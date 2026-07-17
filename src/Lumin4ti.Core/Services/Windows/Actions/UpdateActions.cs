using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using System.Security;
using System.Text;
using System.Text.Json;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// winget によるインストール済みアプリの一括更新。
/// winget はパッケージマネージャー CLI そのものが正規の手段のため外部プロセスで実行し、
/// 出力の要約は C# 側で行う。
/// </summary>
public sealed class WingetUpgradeAction(ICommandExecutor executor) : IMaintenanceAction
{
    internal const string SourceExportArguments = "source export winget --disable-interactivity";
    internal const string UpgradeArguments =
        "upgrade --all --include-unknown --silent --disable-interactivity --source winget --accept-source-agreements --accept-package-agreements";

    private const string OfficialSourceName = "winget";
    private const string OfficialSourceArgument = "https://cdn.winget.microsoft.com/cache";
    private const string OfficialSourceIdentifier = "Microsoft.Winget.Source_8wekyb3d8bbwe";
    private const string OfficialSourceType = "Microsoft.PreIndexed.Package";
    private const int MaxSourceExportLength = 64 * 1024;

    public string Id => "winget-upgrade-all";

    public string Label => "インストール済みアプリを一括更新 (winget)";

    public string Description =>
        "winget パッケージマネージャーで、インストール済みアプリ全てを最新バージョンへ一括アップデートします。" +
        "更新対象が多いとダウンロード・インストールに時間がかかります。起動中・使用中のアプリは実行ファイルがロックされていて更新に失敗することがありますが、" +
        "その場合も他のパッケージの更新は続行され、結果に成功/失敗の内訳が表示されます。";

    public CommandCategory Category => CommandCategory.Update;

    public bool RequiresReboot => false;

    public bool IsLongRunning => true;

    public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default) => ExecuteAsync(null, ct);

    public async Task<MaintenanceActionResult> ExecuteAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        var sourceExport = await executor.RunAsync("winget", SourceExportArguments, ct);
        if (!sourceExport.Success)
        {
            LoggerBootstrap.Log.Error($"{Id}: 公式ソース確認に失敗 (exit={sourceExport.ExitCode})");
            return MaintenanceActionResult.Fail(
                $"winget 公式ソースの確認に失敗したため、更新を中止しました (exit={sourceExport.ExitCode})");
        }

        if (!IsOfficialSourceExport(sourceExport.StandardOutput))
        {
            LoggerBootstrap.Log.Error($"{Id}: winget ソースが Microsoft 公式設定と不一致");
            return MaintenanceActionResult.Fail(
                "winget ソースが Microsoft 公式の既定設定と一致しないため、更新を中止しました。");
        }

        // プログレスバー・スピナー行を除いた意味のある行だけをライブ表示に流す
        var filtered = progress is null
            ? null
            : new MeaningfulLineProgress(progress);

        var result = await executor.RunAsync(
            "winget",
            UpgradeArguments,
            ct,
            filtered);

        // winget はプログレスバー行を大量に吐くため、意味のある行だけ残して末尾 15 行に要約する
        var lines = result.StandardOutput.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(IsMeaningfulLine)
            .ToList();
        var summary = string.Join(Environment.NewLine, lines.TakeLast(15).Select(l => $"  {l.Trim()}"));

        if (result.Success)
        {
            LoggerBootstrap.Log.Info($"{Id}: 完了");
            return MaintenanceActionResult.Ok(summary);
        }

        // 一部パッケージの失敗 (使用中の実行ファイル・インストール技術の変更等) では
        // winget 全体が非 0 で終わるが、成功した更新はある。1 件でも成功していれば部分成功として扱う。
        var succeeded = CountSuccessfulInstalls(lines);
        if (succeeded > 0)
        {
            LoggerBootstrap.Log.Info($"{Id}: 部分成功 ({succeeded} 件成功 / exit={result.ExitCode})");
            return MaintenanceActionResult.Partial(
                $"  - {succeeded} 件の更新に成功しました (一部のパッケージは更新できませんでした。使用中のアプリは閉じてから再実行してください)" +
                Environment.NewLine + summary);
        }

        LoggerBootstrap.Log.Error($"{Id}: exit={result.ExitCode}");
        return MaintenanceActionResult.Fail(summary.Length > 0 ? summary : $"winget が失敗しました (exit={result.ExitCode})");
    }

    /// <summary>winget 出力から更新に成功したパッケージ数を数える (日英両ロケール対応)。</summary>
    internal static int CountSuccessfulInstalls(IEnumerable<string> lines) =>
        lines.Count(l => l.Contains("正常にインストールされました") || l.Contains("Successfully installed"));

    /// <summary>プログレスバー (█▒)・スピナー (-\|/)・罫線だけの行を除外する。</summary>
    internal static bool IsMeaningfulLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 0
               && !trimmed.Contains('█')
               && !trimmed.Contains('▒')
               && trimmed.TrimStart('-', '\\', '|', '/', ' ').Length > 0;
    }

    /// <summary>
    /// 外部プロセスの pipe 読み取りスレッド上で軽量フィルタだけを行い、意味のある行だけを
    /// 呼び出し側の Progress へ渡す。Progress&lt;T&gt; をここで生成して UI context を捕捉しない。
    /// </summary>
    private sealed class MeaningfulLineProgress(IProgress<string> downstream) : IProgress<string>
    {
        public void Report(string line)
        {
            if (IsMeaningfulLine(line))
            {
                downstream.Report(line);
            }
        }
    }

    /// <summary>
    /// winget が 1 行で出力するソース JSON を検証する。診断メッセージなどの非 JSON 行は
    /// 許容するが、JSON 候補の破損・複数出現・必須値の欠損や重複は fail closed にする。
    /// </summary>
    internal static bool IsOfficialSourceExport(string output)
    {
        if (string.IsNullOrWhiteSpace(output) || output.Length > MaxSourceExportLength)
        {
            return false;
        }

        JsonDocument? sourceDocument = null;
        try
        {
            foreach (var line in output.Split(
                         ['\r', '\n'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // 公式出力は 1 行の JSON object。通常の診断行は読み飛ばす一方、
                // JSON らしい行が壊れている場合は別の行へフォールバックしない。
                if (!line.StartsWith('{') && !line.StartsWith('['))
                {
                    continue;
                }

                JsonDocument candidate;
                try
                {
                    candidate = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    return false;
                }

                if (sourceDocument is not null || candidate.RootElement.ValueKind != JsonValueKind.Object)
                {
                    candidate.Dispose();
                    return false;
                }

                sourceDocument = candidate;
            }

            if (sourceDocument is null)
            {
                return false;
            }

            return HasUniqueProperties(sourceDocument.RootElement)
                   && HasExactString(sourceDocument.RootElement, "Name", OfficialSourceName)
                   && HasExactString(sourceDocument.RootElement, "Arg", OfficialSourceArgument)
                   && HasExactString(sourceDocument.RootElement, "Data", OfficialSourceIdentifier)
                   && HasExactString(sourceDocument.RootElement, "Identifier", OfficialSourceIdentifier)
                   && HasExactString(sourceDocument.RootElement, "Type", OfficialSourceType)
                   && HasExactTrustLevels(sourceDocument.RootElement);
        }
        finally
        {
            sourceDocument?.Dispose();
        }
    }

    private static bool HasUniqueProperties(JsonElement source)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return source.EnumerateObject().All(property => names.Add(property.Name));
    }

    private static bool HasExactString(JsonElement source, string propertyName, string expected) =>
        source.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
        && string.Equals(property.GetString(), expected, StringComparison.Ordinal);

    private static bool HasExactTrustLevels(JsonElement source)
    {
        if (!source.TryGetProperty("TrustLevel", out var trustLevels)
            || trustLevels.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trustLevel in trustLevels.EnumerateArray())
        {
            if (trustLevel.ValueKind != JsonValueKind.String
                || trustLevel.GetString() is not { } value
                || !actual.Add(value))
            {
                return false;
            }
        }

        return actual.Count == 2
               && actual.Contains("Trusted")
               && actual.Contains("StoreOrigin");
    }
}

/// <summary>
/// Windows Defender の定義ファイルを削除して再取得する (定義破損時の定番リフレッシュ手順)。
/// MpCmdRun.exe が正規の手段のため外部プロセスで実行する。
/// </summary>
public sealed class DefenderSignatureUpdateAction : IMaintenanceAction
{
    internal const string RemoveDefinitionsArguments = "-RemoveDefinitions -All";
    internal const string SignatureUpdateArguments = "-SignatureUpdate";

    private readonly ICommandExecutor _executor;
    private readonly Func<string?> _mpCmdRunResolver;

    public DefenderSignatureUpdateAction(ICommandExecutor executor)
        : this(executor, DefenderCommandSupport.FindMpCmdRun)
    {
    }

    internal DefenderSignatureUpdateAction(ICommandExecutor executor, Func<string?> mpCmdRunResolver)
    {
        _executor = executor;
        _mpCmdRunResolver = mpCmdRunResolver;
    }

    public string Id => "defender-signature-update";

    public string Label => "Defender 定義ファイルの再取得";

    public string Description =>
        "Windows Defender のウイルス定義ファイルと動的シグネチャを一度削除してから最新版を再取得します。" +
        "定義の破損による誤検知・更新エラーを解消する定番のリフレッシュ手順で、実行してもリアルタイム保護は動作し続けます。";

    public CommandCategory Category => CommandCategory.Update;

    public bool RequiresReboot => false;

    public bool IsLongRunning => true;

    public async Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var mpCmdRun = _mpCmdRunResolver();
        if (mpCmdRun is null)
        {
            return MaintenanceActionResult.Fail("MpCmdRun.exe が見つかりませんでした (Defender 無効環境の可能性があります)");
        }

        // 公式手順どおり定義全体を以前のセットまたは inbox 版へ戻し、最新版を再取得する。
        // 最新化はロールバックに失敗しても試し、定義が古いまま残るリスクを減らす。
        CommandExecutionResult remove;
        try
        {
            remove = await _executor.RunAsync(mpCmdRun, RemoveDefinitionsArguments, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 削除処理が途中まで適用された可能性がある。キャンセルを返す前に、独立した
            // トークンで定義更新を必ず試して inbox 版・部分状態のまま残さない。
            var recovery = await _executor.RunAsync(
                mpCmdRun,
                SignatureUpdateArguments,
                CancellationToken.None);
            LoggerBootstrap.Log.Info($"{Id}: キャンセル後の定義再取得 exit={recovery.ExitCode}");
            if (!recovery.Success)
            {
                LoggerBootstrap.Log.Error(
                    $"{Id}: キャンセル後の定義再取得にも失敗 exit={recovery.ExitCode}: {recovery.StandardError}");
                return MaintenanceActionResult.Fail(
                    "定義ファイル削除中にキャンセルされ、保護のために試した定義再取得にも失敗しました。" +
                    Environment.NewLine + DefenderCommandSupport.DescribeFailure(recovery));
            }

            throw;
        }

        // 定義を戻した後はクリティカル区間。UI のキャンセルで更新を中断しない。
        var update = await _executor.RunAsync(
            mpCmdRun,
            SignatureUpdateArguments,
            CancellationToken.None);
        var lines = new List<string>
        {
            $"  - 定義ファイルを既定状態へ戻す: {(remove.Success ? "OK" : $"失敗 (exit={remove.ExitCode})")}",
            $"  - 定義ファイル更新: {(update.Success ? "OK" : $"失敗 (exit={update.ExitCode})")}",
        };

        LoggerBootstrap.Log.Info($"{Id}: remove={remove.ExitCode} update={update.ExitCode}");
        if (remove.Success && update.Success)
        {
            if (ct.IsCancellationRequested)
            {
                lines.Add("  - キャンセル要求は定義更新開始後だったため、安全な完了まで継続しました");
            }

            return MaintenanceActionResult.Ok(lines);
        }

        var failed = !remove.Success ? remove : update;
        lines.Add(DefenderCommandSupport.DescribeFailure(failed));
        return MaintenanceActionResult.Fail(string.Join(Environment.NewLine, lines));
    }
}

/// <summary>
/// Windows Defender の除外設定・主要オプションを既定値にリセットする。
/// Defender の設定操作は MpPreference cmdlet が唯一の公開手段のため PowerShell を使う。
/// </summary>
public sealed class DefenderResetAction : IMaintenanceAction
{
    private const string PendingBackupJson = "{\"Lumin4tiPending\":true}";
    private static readonly string[] BackupPropertyNames =
    [
        "ExclusionPath",
        "ExclusionProcess",
        "ExclusionExtension",
        "ExclusionIpAddress",
        "ControlledFolderAccessAllowedApplications",
        "ControlledFolderAccessProtectedFolders",
        "AttackSurfaceReductionOnlyExclusions",
    ];

    private readonly ICommandExecutor _executor;
    private readonly ProtectedBackupStorage _backupStorage;

    public DefenderResetAction(ICommandExecutor executor)
        : this(executor, ProtectedBackupStorage.Default)
    {
    }

    internal DefenderResetAction(ICommandExecutor executor, ProtectedBackupStorage backupStorage)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _backupStorage = backupStorage ?? throw new ArgumentNullException(nameof(backupStorage));
    }

    public string Id => "defender-reset";

    public string Label => "Defender の設定 (除外・主要オプション) をリセット";

    public string Description =>
        "Windows Defender に登録された除外パス・除外プロセス・除外拡張子などを全て削除し、リアルタイム保護・クラウド保護などの主要オプションを既定値に戻します。" +
        "マルウェアや過去のソフトが勝手に追加した除外設定 (スキャンの抜け穴) を一掃できます。意図的に登録した除外も消えるため、必要なものは実行後に再登録してください。";

    public CommandCategory Category => CommandCategory.Update;

    public bool RequiresReboot => false;

    public async Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // 昇格 PowerShell にユーザー書き込み可能な AppData のパスを渡すと、再解析ポイント等で
        // 任意ファイルを上書きされ得る。ACL を固定した ProgramData 配下へファイルを先に作り、
        // PowerShell にはその既存ファイルの内容だけを更新させる。
        var backupName = Path.Combine(
            "defender",
            $"defender-exclusions-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        string backupFile;
        try
        {
            _backupStorage.WriteNewAtomically(backupName, stream =>
            {
                var pendingBytes = Encoding.UTF8.GetBytes(PendingBackupJson);
                stream.Write(pendingBytes);
            });
            backupFile = _backupStorage.GetFullPath(backupName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            LoggerBootstrap.Log.Error($"{Id}: 保護バックアップを準備できませんでした", ex);
            return MaintenanceActionResult.Fail(
                "Defender 設定の保護バックアップを準備できないため、リセットを開始しませんでした。" +
                $"{Environment.NewLine}  - {ex.Message}");
        }

        var script = BuildResetScript(backupFile);
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        // ここから先は複数設定を順次変更するクリティカル区間。開始前の最後の境界で
        // キャンセルを反映し、開始後だけ CancellationToken.None で完了まで継続する。
        if (ct.IsCancellationRequested)
        {
            TryDeleteIncompleteBackup(backupName);
            ct.ThrowIfCancellationRequested();
        }

        var result = await _executor.RunAsync(
            "powershell.exe",
            $"-NoProfile -NonInteractive -EncodedCommand {encodedScript}",
            // 除外を順番に削除するスクリプトを途中で kill すると部分リセットになる。
            // 開始前のキャンセルだけを受け付け、開始後は検証まで一体で完了させる。
            CancellationToken.None);

        string backupValidationFailure;
        var backupIsValid = false;
        var backupReadSucceeded = false;
        try
        {
            var backupJson = _backupStorage.ReadAllText(backupName);
            backupReadSucceeded = true;
            backupIsValid = TryValidateBackupJson(backupJson, out backupValidationFailure);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            backupValidationFailure = ex.Message;
        }

        if (!backupIsValid)
        {
            // 内容を実際に読めて不正と確認できた placeholder だけを削除する。
            // 一時的な読取エラー時は、唯一の復元データかもしれないため必ず保持する。
            if (backupReadSucceeded)
            {
                TryDeleteIncompleteBackup(backupName);
            }

            LoggerBootstrap.Log.Error($"{Id}: バックアップ検証失敗: {backupValidationFailure}");
            var commandFailure = result.Success
                ? string.Empty
                : Environment.NewLine + DefenderCommandSupport.DescribeFailure(result);
            return MaintenanceActionResult.Fail(
                "Defender 設定のバックアップを確認できなかったため、処理を成功扱いにできません。" +
                $"{Environment.NewLine}  - {backupValidationFailure}{commandFailure}");
        }

        if (result.Success)
        {
            LoggerBootstrap.Log.Info($"{Id}: 完了 (バックアップ: {backupFile})");
            var detail = string.IsNullOrWhiteSpace(result.StandardOutput)
                ? "  - 除外設定を全削除し、主要オプションを既定値に戻しました"
                : result.StandardOutput.Trim();
            return MaintenanceActionResult.Ok(
                $"{detail}{Environment.NewLine}  - 削除前の除外設定を保存しました: {backupFile}{Environment.NewLine}  - 除外設定を全削除し、主要オプションを既定値に戻しました");
        }

        LoggerBootstrap.Log.Error($"{Id}: exit={result.ExitCode}: {result.StandardError}");
        return MaintenanceActionResult.Fail(
            $"リセットに失敗しました (exit={result.ExitCode}){Environment.NewLine}" +
            DefenderCommandSupport.DescribeFailure(result) +
            $"{Environment.NewLine}  - 変更前の除外設定は保存済みです: {backupFile}");
    }

    internal static bool TryValidateBackupJson(string json, out string failureReason)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4 * 1024 * 1024)
        {
            failureReason = "バックアップが空か、許容サイズを超えています。";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                failureReason = "バックアップのルートが JSON オブジェクトではありません。";
                return false;
            }

            foreach (var propertyName in BackupPropertyNames)
            {
                if (!document.RootElement.TryGetProperty(propertyName, out var value) ||
                    !IsValidBackupValue(value))
                {
                    failureReason = $"バックアップの {propertyName} が欠落または不正です。";
                    return false;
                }
            }

            failureReason = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            failureReason = $"バックアップ JSON を解析できません: {ex.Message}";
            return false;
        }
    }

    private static bool IsValidBackupValue(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.String)
        {
            return true;
        }

        return value.ValueKind == JsonValueKind.Array &&
               value.EnumerateArray().All(item =>
                   item.ValueKind is JsonValueKind.Null or JsonValueKind.String);
    }

    private void TryDeleteIncompleteBackup(string backupName)
    {
        try
        {
            _backupStorage.Delete(backupName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            LoggerBootstrap.Log.Error($"{Id}: 未完成バックアップを削除できませんでした", ex);
        }
    }

    internal static string BuildResetScript(string backupFile)
    {
        var backupLiteral = backupFile.Replace("'", "''", StringComparison.Ordinal);
        const string template = """
            $ErrorActionPreference = 'Stop'
            try {
                Import-Module Defender -ErrorAction Stop
                $status = Get-MpComputerStatus -ErrorAction Stop
                if ($null -ne $status.IsTamperProtected -and [bool]$status.IsTamperProtected) {
                    throw 'Tamper Protection が有効なため Defender 設定を変更できません。組織管理端末では管理者へ依頼し、個人端末では Windows セキュリティの「改ざん防止」を確認してください。'
                }
                $p = Get-MpPreference -ErrorAction Stop
                $backup = [ordered]@{
                    ExclusionPath = $p.ExclusionPath
                    ExclusionProcess = $p.ExclusionProcess
                    ExclusionExtension = $p.ExclusionExtension
                    ExclusionIpAddress = $p.ExclusionIpAddress
                    ControlledFolderAccessAllowedApplications = $p.ControlledFolderAccessAllowedApplications
                    ControlledFolderAccessProtectedFolders = $p.ControlledFolderAccessProtectedFolders
                    AttackSurfaceReductionOnlyExclusions = $p.AttackSurfaceReductionOnlyExclusions
                }
                $backupJson = $backup | ConvertTo-Json -Depth 4
                [System.IO.File]::WriteAllText('__BACKUP_FILE__', $backupJson, [System.Text.UTF8Encoding]::new($false))

                $removeCommand = Get-Command Remove-MpPreference -ErrorAction Stop
                function Remove-ConfiguredValues([string] $name, [object[]] $values) {
                    $configuredValues = @($values | Where-Object { $_ } | Sort-Object -Unique)
                    if ($configuredValues.Count -eq 0 -or -not $removeCommand.Parameters.ContainsKey($name)) {
                        return
                    }

                    $parameters = @{ Force = $true; ErrorAction = 'Stop' }
                    $parameters[$name] = $configuredValues
                    Remove-MpPreference @parameters
                }

                Remove-ConfiguredValues -name 'ExclusionPath' -values @($p.ExclusionPath)
                Remove-ConfiguredValues -name 'ExclusionProcess' -values @($p.ExclusionProcess)
                Remove-ConfiguredValues -name 'ExclusionExtension' -values @($p.ExclusionExtension)
                Remove-ConfiguredValues -name 'ExclusionIpAddress' -values @($p.ExclusionIpAddress)
                Remove-ConfiguredValues -name 'ControlledFolderAccessAllowedApplications' -values @($p.ControlledFolderAccessAllowedApplications)
                Remove-ConfiguredValues -name 'ControlledFolderAccessProtectedFolders' -values @($p.ControlledFolderAccessProtectedFolders)
                Remove-ConfiguredValues -name 'AttackSurfaceReductionOnlyExclusions' -values @($p.AttackSurfaceReductionOnlyExclusions)

                # 実効値をOS版に依存せず検証できる boolean Disable* だけを対象にする。
                # enum値や脅威既定動作はWindows/Defender版で既定値が変わり得るため、
                # 未検証のまま「既定に戻した」と成功表示しない。
                $resetNames = @(
                    'DisableRealtimeMonitoring', 'DisableBehaviorMonitoring', 'DisableIOAVProtection',
                    'DisableScriptScanning', 'DisableArchiveScanning', 'DisableIntrusionPreventionSystem',
                    'DisableScanningMappedNetworkDrivesForFullScan', 'DisableScanningNetworkFiles',
                    'DisableBlockAtFirstSeen'
                )
                $resetParameters = @{ Force = $true; ErrorAction = 'Stop' }
                $resetAppliedNames = @()
                $verifiableNames = @()
                foreach ($name in $resetNames) {
                    if ($removeCommand.Parameters.ContainsKey($name)) {
                        $verifiableNames += $name
                        if ([bool]$p.$name) {
                            $resetParameters[$name] = $true
                            $resetAppliedNames += $name
                        }
                    }
                }
                if ($resetParameters.Count -gt 2) {
                    Remove-MpPreference @resetParameters
                }

                # Tamper Protection や組織ポリシーは、cmdlet を成功終了させたまま変更を
                # ブロックする場合がある。変更後の実効状態を再取得して偽成功を防ぐ。
                $after = Get-MpPreference -ErrorAction Stop
                $remainingExclusions = @(
                    $after.ExclusionPath
                    $after.ExclusionProcess
                    $after.ExclusionExtension
                    $after.ExclusionIpAddress
                    $after.ControlledFolderAccessAllowedApplications
                    $after.ControlledFolderAccessProtectedFolders
                    $after.AttackSurfaceReductionOnlyExclusions
                ) | Where-Object { $_ }
                if ($remainingExclusions.Count -gt 0) {
                    throw ('除外設定が ' + $remainingExclusions.Count + ' 件残っています。Tamper Protection または組織ポリシーにより変更がブロックされました。')
                }

                $notReset = @()
                foreach ($name in $verifiableNames) {
                    if ([bool]$after.$name) { $notReset += $name }
                }
                if ($notReset.Count -gt 0) {
                    throw ('保護設定が既定の有効状態へ戻っていません: ' + ($notReset -join ', ') + '。Tamper Protection または組織ポリシーを確認してください。')
                }

                $pathCount = @($p.ExclusionPath | Where-Object { $_ }).Count
                $processCount = @($p.ExclusionProcess | Where-Object { $_ }).Count
                $extensionCount = @($p.ExclusionExtension | Where-Object { $_ }).Count
                Write-Output ('  - 除外パス ' + $pathCount + ' 件 / 除外プロセス ' + $processCount + ' 件 / 除外拡張子 ' + $extensionCount + ' 件をバックアップしました')
                Write-Output '  - 変更後に除外 0 件と主要な保護設定の有効状態を確認しました'
            }
            catch {
                [Console]::Error.WriteLine($_.Exception.Message)
                exit 1
            }
            """;

        return template.Replace("__BACKUP_FILE__", backupLiteral, StringComparison.Ordinal);
    }
}

internal static class DefenderCommandSupport
{
    private const int DefenderServiceDisabled = unchecked((int)0x800106BA);
    private const string DefenderUnavailableMessage =
        "  - Microsoft Defender Antivirus が無効なため実行できません。" +
        "別のウイルス対策ソフトが有効な場合は Windows によって自動的に無効化されます。" +
        "Defender を有効にしてから再実行してください。";

    internal static string? FindMpCmdRun()
    {
        var platformRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Microsoft\Windows Defender\Platform");
        var platformDirectories = Directory.Exists(platformRoot)
            ? Directory.EnumerateDirectories(platformRoot)
            : [];
        var legacyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            @"Windows Defender\MpCmdRun.exe");

        return SelectMpCmdRunPath(platformDirectories, legacyPath, File.Exists);
    }

    internal static string? SelectMpCmdRunPath(
        IEnumerable<string> platformDirectories,
        string legacyPath,
        Func<string, bool> fileExists)
    {
        var latest = platformDirectories
            .Select(directory => new
            {
                Path = Path.Combine(directory, "MpCmdRun.exe"),
                Version = ParsePlatformVersion(Path.GetFileName(directory)),
            })
            .Where(candidate => fileExists(candidate.Path))
            .OrderByDescending(candidate => candidate.Version)
            .ThenByDescending(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();

        return latest ?? (fileExists(legacyPath) ? legacyPath : null);
    }

    internal static string DescribeFailure(CommandExecutionResult result)
    {
        if (IsDefenderUnavailable(result))
        {
            return DefenderUnavailableMessage;
        }

        var output = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        var lastLine = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        if (string.IsNullOrWhiteSpace(lastLine))
        {
            return $"  - Defender コマンドが失敗しました (exit={result.ExitCode})";
        }

        const int maxLength = 300;
        var detail = lastLine.Length <= maxLength ? lastLine : lastLine[..maxLength] + "…";
        return $"  - {detail}";
    }

    internal static bool IsDefenderUnavailable(CommandExecutionResult result)
    {
        if (result.ExitCode == DefenderServiceDisabled)
        {
            return true;
        }

        var output = result.StandardOutput + Environment.NewLine + result.StandardError;
        return output.Contains("800106BA", StringComparison.OrdinalIgnoreCase);
    }

    private static Version ParsePlatformVersion(string directoryName)
    {
        var versionText = directoryName.Split('-', 2)[0];
        return Version.TryParse(versionText, out var version) ? version : new Version(0, 0);
    }
}

/// <summary>
/// Windows セキュリティ アプリ (Microsoft.SecHealthUI) を初期状態にリセットする。
/// Reset-AppxPackage は PowerShell cmdlet が唯一の公開手段。
/// </summary>
public sealed class SecurityAppResetAction(ICommandExecutor executor) : IMaintenanceAction
{
    public string Id => "security-app-reset";

    public string Label => "Windows セキュリティ アプリをリセット";

    public string Description =>
        "「Windows セキュリティ」アプリ (設定画面) が開かない・表示が壊れた・項目が消えたなどの不調時に、アプリのデータを初期状態に戻して再登録します。" +
        "Defender 本体の保護状態や定義ファイルには影響しません。";

    public CommandCategory Category => CommandCategory.Update;

    public bool RequiresReboot => false;

    public async Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default)
    {
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildResetScript()));
        var result = await executor.RunAsync(
            "powershell.exe",
            $"-NoProfile -NonInteractive -EncodedCommand {encodedScript}",
            ct);

        if (result.Success)
        {
            LoggerBootstrap.Log.Info($"{Id}: 完了");
            return MaintenanceActionResult.Ok("  - Windows セキュリティ アプリをリセットしました");
        }

        LoggerBootstrap.Log.Error($"{Id}: exit={result.ExitCode}: {result.StandardError}");
        var failure = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        return MaintenanceActionResult.Fail(
            $"リセットに失敗しました (exit={result.ExitCode})" +
            (failure.Length == 0 ? string.Empty : $"{Environment.NewLine}  - {failure}"));
    }

    internal static string BuildResetScript() =>
        """
        $ErrorActionPreference = 'Stop'
        try {
            $package = @(Get-AppxPackage -Name Microsoft.SecHealthUI -ErrorAction Stop)
            if ($package.Count -eq 0) {
                throw 'Microsoft.SecHealthUI パッケージが登録されていません。Windows セキュリティを再登録してから再実行してください。'
            }

            $package | Reset-AppxPackage -ErrorAction Stop

            $after = @(Get-AppxPackage -Name Microsoft.SecHealthUI -ErrorAction Stop)
            if ($after.Count -eq 0) {
                throw 'リセット後に Microsoft.SecHealthUI パッケージを確認できませんでした。'
            }

            Write-Output '  - Windows セキュリティ アプリをリセットし、パッケージ登録を確認しました'
        }
        catch {
            [Console]::Error.WriteLine($_.Exception.Message)
            exit 1
        }
        """;
}
