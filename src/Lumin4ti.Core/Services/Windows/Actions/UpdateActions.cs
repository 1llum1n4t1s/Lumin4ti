using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// winget によるインストール済みアプリの一括更新。
/// winget はパッケージマネージャー CLI そのものが正規の手段のため外部プロセスで実行し、
/// 出力の要約は C# 側で行う。
/// </summary>
public sealed class WingetUpgradeAction(ICommandExecutor executor) : IMaintenanceAction
{
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
        // プログレスバー・スピナー行を除いた意味のある行だけをライブ表示に流す
        var filtered = progress is null
            ? null
            : new Progress<string>(line =>
            {
                if (IsMeaningfulLine(line))
                {
                    progress.Report(line);
                }
            });

        var result = await executor.RunAsync(
            "winget",
            "upgrade --all --include-unknown --silent --disable-interactivity --accept-source-agreements --accept-package-agreements",
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
            return MaintenanceActionResult.Ok(
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
}

/// <summary>
/// Windows Defender の定義ファイルを削除して再取得する (定義破損時の定番リフレッシュ手順)。
/// MpCmdRun.exe が正規の手段のため外部プロセスで実行する。
/// </summary>
public sealed class DefenderSignatureUpdateAction(ICommandExecutor executor) : IMaintenanceAction
{
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
        var mpCmdRun = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            @"Windows Defender\MpCmdRun.exe");
        if (!File.Exists(mpCmdRun))
        {
            return MaintenanceActionResult.Fail("MpCmdRun.exe が見つかりませんでした (Defender 無効環境の可能性があります)");
        }

        var remove = await executor.RunAsync(mpCmdRun, "-removedefinitions -dynamicsignatures", ct);
        var update = await executor.RunAsync(mpCmdRun, "-SignatureUpdate", ct);

        var lines = new List<string>
        {
            $"  - 動的シグネチャ削除: {(remove.Success ? "OK" : $"失敗 (exit={remove.ExitCode})")}",
            $"  - 定義ファイル更新: {(update.Success ? "OK" : $"失敗 (exit={update.ExitCode})")}",
        };

        LoggerBootstrap.Log.Info($"{Id}: remove={remove.ExitCode} update={update.ExitCode}");
        return update.Success
            ? MaintenanceActionResult.Ok(lines)
            : MaintenanceActionResult.Fail(string.Join(Environment.NewLine, lines));
    }
}

/// <summary>
/// Windows Defender の除外設定・主要オプションを既定値にリセットする。
/// Defender の設定操作は MpPreference cmdlet が唯一の公開手段のため PowerShell を使う。
/// </summary>
public sealed class DefenderResetAction(ICommandExecutor executor) : IMaintenanceAction
{
    public string Id => "defender-reset";

    public string Label => "Defender の設定 (除外・主要オプション) をリセット";

    public string Description =>
        "Windows Defender に登録された除外パス・除外プロセス・除外拡張子などを全て削除し、リアルタイム保護・クラウド保護などの主要オプションを既定値に戻します。" +
        "マルウェアや過去のソフトが勝手に追加した除外設定 (スキャンの抜け穴) を一掃できます。意図的に登録した除外も消えるため、必要なものは実行後に再登録してください。";

    public CommandCategory Category => CommandCategory.Update;

    public bool RequiresReboot => false;

    public async Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default)
    {
        // 削除する除外設定を先に JSON へバックアップし、後から再登録できるようにする。
        var backupDir = Path.Combine(AppPaths.AppDataDirectory, "backups");
        Directory.CreateDirectory(backupDir);
        var backupFile = Path.Combine(backupDir, $"defender-exclusions-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var backupLiteral = backupFile.Replace("'", "''");

        var script =
            "Import-Module ConfigDefender -ErrorAction SilentlyContinue; $ErrorActionPreference = 'SilentlyContinue'; " +
            "$p = Get-MpPreference; " +
            // 削除前の除外一覧を JSON でバックアップし、件数を標準出力へ (C# 側でログ/UI に残す)
            "$backup = [ordered]@{ ExclusionPath = $p.ExclusionPath; ExclusionProcess = $p.ExclusionProcess; ExclusionExtension = $p.ExclusionExtension; ExclusionIpAddress = $p.ExclusionIpAddress; ControlledFolderAccessAllowedApplications = $p.ControlledFolderAccessAllowedApplications }; " +
            $"$backup | ConvertTo-Json -Depth 4 | Out-File -FilePath '{backupLiteral}' -Encoding utf8; " +
            "Write-Host ('  - 除外パス ' + (@($p.ExclusionPath).Where({$_}).Count) + ' 件 / 除外プロセス ' + (@($p.ExclusionProcess).Where({$_}).Count) + ' 件 / 除外拡張子 ' + (@($p.ExclusionExtension).Where({$_}).Count) + ' 件をバックアップしました'); " +
            "if ($p.ExclusionPath) { Remove-MpPreference -ExclusionPath ($p.ExclusionPath | Sort-Object -Unique) -Force }; " +
            "if ($p.ExclusionProcess) { Remove-MpPreference -ExclusionProcess ($p.ExclusionProcess | Sort-Object -Unique) -Force }; " +
            "if ($p.ExclusionExtension) { Remove-MpPreference -ExclusionExtension ($p.ExclusionExtension | Sort-Object -Unique) -Force }; " +
            "if ($p.ExclusionIpAddress) { Remove-MpPreference -ExclusionIpAddress ($p.ExclusionIpAddress | Sort-Object -Unique) -Force }; " +
            "if ($p.ControlledFolderAccessAllowedApplications) { Remove-MpPreference -ControlledFolderAccessAllowedApplications ($p.ControlledFolderAccessAllowedApplications | Sort-Object -Unique) -Force }; " +
            "if ($p.ControlledFolderAccessProtectedFolders) { Remove-MpPreference -ControlledFolderAccessProtectedFolders ($p.ControlledFolderAccessProtectedFolders | Sort-Object -Unique) -Force }; " +
            "if ($p.AttackSurfaceReductionOnlyExclusions) { Remove-MpPreference -AttackSurfaceReductionOnlyExclusions ($p.AttackSurfaceReductionOnlyExclusions | Sort-Object -Unique) -Force }; " +
            "Remove-MpPreference -DisableRealtimeMonitoring -DisableBehaviorMonitoring -DisableIOAVProtection -DisableScriptScanning -DisableArchiveScanning " +
            "-DisableIntrusionPreventionSystem -DisableScanningMappedNetworkDrivesForFullScan -DisableScanningNetworkFiles -DisableBlockAtFirstSeen " +
            "-PUAProtection -EnableNetworkProtection -EnableControlledFolderAccess -CloudBlockLevel -MAPSReporting -SubmitSamplesConsent " +
            "-LowThreatDefaultAction -ModerateThreatDefaultAction -HighThreatDefaultAction -SevereThreatDefaultAction -UnknownThreatDefaultAction -Force";

        var result = await executor.RunAsync("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script}\"", ct);

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
        return MaintenanceActionResult.Fail($"リセットに失敗しました (exit={result.ExitCode})");
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
        var result = await executor.RunAsync(
            "powershell.exe",
            "-NoProfile -NonInteractive -Command \"Get-AppxPackage -Name Microsoft.SecHealthUI | Reset-AppxPackage\"",
            ct);

        if (result.Success)
        {
            LoggerBootstrap.Log.Info($"{Id}: 完了");
            return MaintenanceActionResult.Ok("  - Windows セキュリティ アプリをリセットしました");
        }

        LoggerBootstrap.Log.Error($"{Id}: exit={result.ExitCode}: {result.StandardError}");
        return MaintenanceActionResult.Fail($"リセットに失敗しました (exit={result.ExitCode})");
    }
}
