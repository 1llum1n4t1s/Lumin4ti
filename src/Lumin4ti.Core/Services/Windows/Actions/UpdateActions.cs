using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// winget によるインストール済みアプリの一括更新。
/// winget はパッケージマネージャー CLI そのものが正規の手段のため外部プロセスで実行し、
/// 出力の要約は C# 側で行う。
/// </summary>
public sealed partial class WingetUpgradeAction(ICommandExecutor executor) : IMaintenanceAction
{
    /// <summary>更新チャネル判定用の英字の連なり。ビルド時生成で毎回の解釈を省く (パターンは従来と同一)。</summary>
    [GeneratedRegex("[A-Za-z]+")]
    private static partial Regex AsciiLetterRun();

    internal const string SourceExportArguments = "source export winget --disable-interactivity";
    internal const string ListUpgradesArguments =
        "list --upgrade-available --include-unknown --disable-interactivity --source winget --accept-source-agreements";

    private const string CommonExactArguments =
        "--exact --disable-interactivity --source winget --accept-source-agreements";

    private const string OfficialSourceName = "winget";
    private const string OfficialSourceArgument = "https://cdn.winget.microsoft.com/cache";
    private const string OfficialSourceIdentifier = "Microsoft.Winget.Source_8wekyb3d8bbwe";
    private const string OfficialSourceType = "Microsoft.PreIndexed.Package";
    private const int MaxSourceExportLength = 64 * 1024;
    private const int MaxDiagnosticLinesPerStream = 6;
    private const int MaxDiagnosticLineLength = 240;
    private const int MaxStreamSummaryLength = 600;

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
            var failure = DescribeCommandResult("winget 公式ソースの確認に失敗したため、更新を中止しました", sourceExport);
            LoggerBootstrap.Log.Error($"{Id}: {failure}");
            progress?.Report(failure);
            return MaintenanceActionResult.Fail(failure);
        }

        if (!IsOfficialSourceExport(sourceExport.StandardOutput))
        {
            var failure = DescribeCommandResult(
                "winget ソースが Microsoft 公式の既定設定と一致しないため、更新を中止しました",
                sourceExport);
            LoggerBootstrap.Log.Error($"{Id}: {failure}");
            progress?.Report(failure);
            return MaintenanceActionResult.Fail(failure);
        }

        var available = await executor.RunAsync("winget", ListUpgradesArguments, ct);
        if (!available.Success)
        {
            var failure = DescribeCommandResult("winget の更新候補を取得できませんでした", available);
            LoggerBootstrap.Log.Error($"{Id}: {failure}");
            progress?.Report(failure);
            return MaintenanceActionResult.Fail(failure);
        }

        if (!TryParsePackageTable(available.StandardOutput, out var candidates, out var tableFound))
        {
            var failure = DescribeCommandResult(
                "winget の更新候補一覧を安全に確認できなかったため、更新を中止しました",
                available);
            LoggerBootstrap.Log.Error($"{Id}: {failure}");
            progress?.Report(failure);
            return MaintenanceActionResult.Fail(failure);
        }

        if (!tableFound)
        {
            LoggerBootstrap.Log.Info($"{Id}: 更新候補なし");
            return MaintenanceActionResult.Ok("  - 更新可能なパッケージはありませんでした");
        }

        var filtered = progress is null ? null : new MeaningfulLineProgress(progress);
        var details = new List<string>();
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;

        for (var index = 0; index < candidates.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = candidates[index];
            var candidateDisplayName = SanitizeDiagnosticLine(candidate.Name);
            progress?.Report($"  - ({index + 1}/{candidates.Count}) {candidateDisplayName} [{candidate.Id}] を確認しています");

            var catalog = await executor.RunAsync("winget", BuildSearchArguments(candidate.Id), ct);
            var catalogParsed = TryParsePackageTable(
                catalog.StandardOutput,
                out var catalogPackages,
                out var catalogTableFound);
            string? catalogFailureReason = null;
            if (!catalog.Success)
            {
                catalogFailureReason = "公式カタログの照会に失敗したため除外";
            }
            else if (!catalogParsed)
            {
                catalogFailureReason = "公式カタログの応答を安全に解析できないため除外";
            }
            else if (!catalogTableFound || catalogPackages.Count != 1)
            {
                catalogFailureReason = "公式カタログで完全一致する1件を確認できないため除外";
            }
            else if (!string.Equals(catalogPackages[0].Id, candidate.Id, StringComparison.Ordinal))
            {
                catalogFailureReason = "公式カタログのPackage IDが一致しないため除外";
            }

            if (catalogFailureReason is not null)
            {
                skipped++;
                var detail = DescribeCommandResult(
                    $"{candidateDisplayName} [{candidate.Id}]: {catalogFailureReason}",
                    catalog);
                details.Add(detail);
                progress?.Report(detail);
                LoggerBootstrap.Log.Error($"{Id}: {detail}");
                continue;
            }

            var catalogName = catalogPackages[0].Name;
            if (!IsConsistentPackageName(candidate.Name, catalogName))
            {
                skipped++;
                var detail = DescribeCommandResult(
                    $"{candidateDisplayName} [{candidate.Id}]: カタログ名「{SanitizeDiagnosticLine(catalogName)}」と一致しないため除外 (他のパッケージは続行)",
                    catalog);
                details.Add(detail);
                progress?.Report(detail);
                LoggerBootstrap.Log.Error($"{Id}: {detail}");
                continue;
            }

            progress?.Report($"  - ({index + 1}/{candidates.Count}) {candidateDisplayName} を更新しています");
            var result = await executor.RunAsync(
                "winget",
                BuildUpgradeArguments(candidate.Id),
                ct,
                filtered);
            if (result.Success)
            {
                succeeded++;
                var detail = DescribeCommandResult(
                    $"{candidateDisplayName} [{candidate.Id}]: 更新完了",
                    result);
                details.Add(detail);
                progress?.Report(detail);
                LoggerBootstrap.Log.Info($"{Id}: {detail}");
                continue;
            }

            failed++;
            var failure = DescribeCommandResult(
                $"{candidateDisplayName} [{candidate.Id}]: 更新失敗",
                result);
            details.Add(failure);
            progress?.Report(failure);
            LoggerBootstrap.Log.Error($"{Id}: {failure}");
        }

        LoggerBootstrap.Log.Info(
            $"{Id}: 完了 (成功={succeeded}, 失敗={failed}, 安全確認で除外={skipped})");
        details.Insert(0, $"  - 成功 {succeeded} 件 / 失敗 {failed} 件 / 安全確認で除外 {skipped} 件");

        if (failed == 0 && skipped == 0)
        {
            return MaintenanceActionResult.Ok(details);
        }

        // 安全確認による除外は「意図して更新を見送った」であって失敗ではない。
        // 更新に失敗した候補が 1 件も無ければ、除外だけを理由に全体を失敗扱いにしない。
        if (succeeded > 0 || failed == 0)
        {
            return MaintenanceActionResult.Partial(details);
        }

        return MaintenanceActionResult.Fail(string.Join(Environment.NewLine, details));
    }

    internal static string BuildSearchArguments(string packageId) =>
        $"search --id {packageId} {CommonExactArguments}";

    internal static string BuildUpgradeArguments(string packageId) =>
        $"upgrade --id {packageId} {CommonExactArguments} --silent --accept-package-agreements";

    /// <summary>
    /// winget の表形式出力から Name と ID だけを抽出する。全角文字による表示幅の差を避けるため、
    /// ヘッダーの ID より後ろにある列数を数え、各行を右側から特定する。
    /// 切り詰めや不正な ID を含む行は fail closed にする。
    /// </summary>
    internal static bool TryParsePackageTable(
        string output,
        out List<WingetPackage> packages,
        out bool tableFound)
    {
        packages = [];
        tableFound = false;
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        for (var separatorIndex = 1; separatorIndex < lines.Length; separatorIndex++)
        {
            var separator = lines[separatorIndex].Trim();
            if (separator.Length < 3 || separator.Any(character => character != '-'))
            {
                continue;
            }

            var headerTokens = Tokenize(lines[separatorIndex - 1]);
            var idHeaderIndex = headerTokens.FindIndex(token => token.Value == "ID");
            if (idHeaderIndex <= 0 || idHeaderIndex == headerTokens.Count - 1)
            {
                continue;
            }

            tableFound = true;
            var columnsAfterId = headerTokens.Count - idHeaderIndex - 1;
            for (var rowIndex = separatorIndex + 1; rowIndex < lines.Length; rowIndex++)
            {
                var row = lines[rowIndex];
                var rowTokens = Tokenize(row);
                if (rowTokens.Count <= columnsAfterId + 1)
                {
                    break;
                }

                var idToken = rowTokens[rowTokens.Count - columnsAfterId - 1];
                var id = idToken.Value;
                if (!id.Contains('.'))
                {
                    // 表末尾の「3 upgrades available」のような集計行はここで終了する。
                    // 一方、シェルメタ文字を含む ID らしき値は安全側に倒して一覧全体を拒否する。
                    if (id.Contains('…') || id.Any(character => character is ';' or '|' or '&' or '<' or '>'))
                    {
                        return false;
                    }

                    break;
                }

                var name = row[..idToken.Start].TrimEnd();
                if (name.Length == 0 || name.Contains('…') || !IsSafePackageId(id))
                {
                    return false;
                }

                packages.Add(new WingetPackage(name, id));
            }

            return packages.Count > 0;
        }

        return true;
    }

    private static List<TextToken> Tokenize(string line)
    {
        var tokens = new List<TextToken>();
        for (var index = 0; index < line.Length;)
        {
            while (index < line.Length && char.IsWhiteSpace(line[index]))
            {
                index++;
            }

            var start = index;
            while (index < line.Length && !char.IsWhiteSpace(line[index]))
            {
                index++;
            }

            if (index > start)
            {
                tokens.Add(new TextToken(start, line[start..index]));
            }
        }

        return tokens;
    }

    /// <summary>
    /// インストール済み表示名とカタログ名が同じアプリを指しているかを判定する。
    /// winget の list は ARP の表示名、search はカタログ名を返すため、記号・大小文字・
    /// バージョン等の差を許容する。ただし Nightly 等の更新チャネルの差は表記ゆれにしない。
    /// --id --exact でも、winget が別チャネルの ID に誤って紐付けた候補は訂正されない。
    /// </summary>
    internal static bool IsConsistentPackageName(string installedName, string catalogName)
    {
        if (!GetReleaseChannels(installedName).SetEquals(GetReleaseChannels(catalogName)))
        {
            return false;
        }

        var installed = NormalizePackageName(installedName);
        var catalog = NormalizePackageName(catalogName);
        return installed.Length > 0
               && catalog.Length > 0
               && (installed.Contains(catalog, StringComparison.Ordinal)
                   || catalog.Contains(installed, StringComparison.Ordinal));
    }

    private static HashSet<string> GetReleaseChannels(string name)
    {
        var channels = new HashSet<string>(StringComparer.Ordinal);
        // 記号や数字に隣接する「(Nightly)」「Beta2」もチャネルとして扱う。
        foreach (Match match in AsciiLetterRun().Matches(name))
        {
            var token = match.Value.ToLowerInvariant();
            if (token == "insiders")
            {
                token = "insider";
            }
            if (token is "alpha" or "beta" or "dev" or "canary" or "nightly"
                or "preview" or "insider" or "unstable" or "rc")
            {
                channels.Add(token);
            }
        }
        return channels;
    }

    /// <summary>表記ゆれを吸収するため、英数字だけを残して小文字化する。</summary>
    private static string NormalizePackageName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static bool IsSafePackageId(string? packageId) =>
        !string.IsNullOrWhiteSpace(packageId)
        && packageId.Length <= 255
        && packageId.Contains('.')
        && !packageId.Contains('…')
        && packageId.All(character => char.IsAsciiLetterOrDigit(character)
                                          || character is '.' or '-' or '_' or '+');

    private static string DescribeCommandResult(string message, CommandExecutionResult result)
    {
        var diagnostic = new StringBuilder(
            $"exit={result.ExitCode}/0x{unchecked((uint)result.ExitCode):X8}");
        AppendStreamSummary(diagnostic, "stdout", result.StandardOutput);
        AppendStreamSummary(diagnostic, "stderr", result.StandardError);

        var guidance = result.Success ? string.Empty : DescribeWingetFailureGuidance(result);
        return string.IsNullOrEmpty(guidance)
            ? $"  - {message} ({diagnostic})"
            : $"  - {message} ({diagnostic})。{guidance}";
    }

    private static void AppendStreamSummary(StringBuilder diagnostic, string label, string output)
    {
        var summary = SummarizeOutput(output);
        if (summary.Length > 0)
        {
            diagnostic.Append("; ").Append(label).Append('=').Append(summary);
        }
    }

    private static string SummarizeOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        var lines = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeDiagnosticLine)
            .Where(IsMeaningfulLine)
            .TakeLast(MaxDiagnosticLinesPerStream);
        var summary = string.Join(" | ", lines);
        return summary.Length <= MaxStreamSummaryLength
            ? summary
            : summary[..MaxStreamSummaryLength] + "…";
    }

    private static string SanitizeDiagnosticLine(string line)
    {
        // ANSI制御シーケンスとログ表示を崩す制御文字を除き、1行あたりの記録量を制限する。
        var withoutAnsi = CommandFailureDiagnostic.AnsiEscapeSequence().Replace(line, string.Empty);
        var sanitized = new StringBuilder(withoutAnsi.Length);
        foreach (var character in withoutAnsi)
        {
            sanitized.Append(char.IsControl(character) ? ' ' : character);
        }

        var normalized = CommandFailureDiagnostic.WhitespaceRun().Replace(sanitized.ToString(), " ").Trim();
        return normalized.Length <= MaxDiagnosticLineLength
            ? normalized
            : normalized[..MaxDiagnosticLineLength] + "…";
    }

    private static string DescribeWingetFailureGuidance(CommandExecutionResult result)
    {
        var combined = $"{result.StandardOutput}\n{result.StandardError}";
        if (unchecked((uint)result.ExitCode) == 0x8A15008E
            || combined.Contains("0x8a15008e", StringComparison.OrdinalIgnoreCase))
        {
            return "既存のインストール方式と更新方式が異なるため自動変更できません。発行元の公式インストーラーで更新方法を確認してください";
        }

        var installerFailure = (combined.Contains("installer", StringComparison.OrdinalIgnoreCase)
                                && combined.Contains("fail", StringComparison.OrdinalIgnoreCase))
                               || (combined.Contains("インストーラー", StringComparison.Ordinal)
                                   && combined.Contains("失敗", StringComparison.Ordinal));
        return installerFailure
            ? "使用中のアプリがある場合は終了してから再実行してください。解消しない場合は上記のwinget出力と発行元の案内を確認してください"
            : string.Empty;
    }

    internal sealed record WingetPackage(string Name, string Id);

    private sealed record TextToken(int Start, string Value);

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
            var sanitized = SanitizeDiagnosticLine(line);
            if (IsMeaningfulLine(sanitized))
            {
                downstream.Report(sanitized);
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
/// Windows Defender の動的シグネチャを削除し、定義の更新を確認する。
/// MpCmdRun.exe が正規の手段のため外部プロセスで実行する。
/// </summary>
public sealed class DefenderSignatureUpdateAction : IMaintenanceAction
{
    internal const string RemoveDefinitionsArguments = "-RemoveDefinitions -DynamicSignatures";
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

    public string Label => "Defender 定義キャッシュのクリアと更新確認";

    public string Description =>
        "Windows Defender の動的シグネチャを削除して、定義の更新を確認します。" +
        "基本定義全体の削除・巻き戻しは行いません。既に最新の場合、追加のダウンロードは行われません。";

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

        // Microsoft のキャッシュ消去・更新手順。基本定義全体を巻き戻す -All は使わない。
        // https://www.microsoft.com/en-us/wdsi/defenderupdates
        // 動的シグネチャを削除できなくても更新確認は試す。
        CommandExecutionResult remove;
        try
        {
            remove = await _executor.RunAsync(mpCmdRun, RemoveDefinitionsArguments, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 削除処理が途中まで適用された可能性がある。キャンセルを返す前に、独立した
            // トークンで定義更新を必ず試して部分状態のまま残さない。
            var recovery = await _executor.RunAsync(
                mpCmdRun,
                SignatureUpdateArguments,
                CancellationToken.None);
            LoggerBootstrap.Log.Info($"{Id}: キャンセル後の定義更新確認 exit={recovery.ExitCode}");
            if (!recovery.Success)
            {
                LoggerBootstrap.Log.Error(
                    $"{Id}: キャンセル後の定義更新確認にも失敗: {CommandFailureDiagnostic.Format(recovery)}");
                return MaintenanceActionResult.Fail(
                    "動的シグネチャ削除中にキャンセルされ、保護のために試した定義更新確認にも失敗しました。" +
                    Environment.NewLine + DefenderCommandSupport.DescribeFailure(recovery));
            }

            throw;
        }

        if (!remove.Success)
        {
            LoggerBootstrap.Log.Error($"{Id}: 動的シグネチャの削除に失敗: {CommandFailureDiagnostic.Format(remove)}");
        }

        // 削除後の更新確認はクリティカル区間。UI のキャンセルで更新を中断しない。
        var update = await _executor.RunAsync(
            mpCmdRun,
            SignatureUpdateArguments,
            CancellationToken.None);
        if (!update.Success)
        {
            LoggerBootstrap.Log.Error($"{Id}: 定義の更新確認に失敗: {CommandFailureDiagnostic.Format(update)}");
        }

        var lines = new List<string>
        {
            $"  - 動的シグネチャの削除: {(remove.Success ? "OK" : $"失敗 (exit={remove.ExitCode})")}",
            $"  - 定義の更新確認: {(update.Success ? "OK" : $"失敗 (exit={update.ExitCode})")}",
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
    private const int MaximumAvailabilityOutputLength = 64 * 1024;

    private readonly ICommandExecutor _executor;

    public DefenderResetAction(ICommandExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public string Id => "defender-reset";

    public string Label => "Defender の設定 (除外・主要オプション) をリセット";

    public string Description =>
        "Windows Defender に登録された除外パス・除外プロセス・除外拡張子などを全て削除し、リアルタイム保護・クラウド保護などの主要オプションを既定値に戻します。" +
        "マルウェアや過去のソフトが勝手に追加した除外設定 (スキャンの抜け穴) を一掃できます。意図的に登録した除外も消えるため、必要なものは実行後に再登録してください。" +
        "変更前の設定は保存しません。途中失敗では変更が一部残ることがあるため、結果とログを確認してください。" +
        "Defender サービスが完全に無効な環境では実行しません。他社製アンチウイルスによるパッシブモードでも、Defender サービスと管理 cmdlet が利用可能なら実行できます。";

    public CommandCategory Category => CommandCategory.Update;

    public bool RequiresReboot => false;

    public async Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var availabilityResult = await _executor.RunAsync(
            "powershell.exe",
            BuildAvailabilityCheckArguments(),
            ct);
        if (!availabilityResult.Success
            || !TryParseAvailability(availabilityResult.StandardOutput, out var availability))
        {
            LoggerBootstrap.Log.Error(
                $"{Id}: Defender 利用状態を確認できませんでした: {CommandFailureDiagnostic.Format(availabilityResult)}");
            return MaintenanceActionResult.Fail(
                "Microsoft Defender Antivirus の利用状態を確認できないため、設定は変更しませんでした。" +
                Environment.NewLine + DefenderCommandSupport.DescribeFailure(availabilityResult));
        }

        if (!availability.AMServiceEnabled)
        {
            LoggerBootstrap.Log.Info($"{Id}: Defender サービスが無効なため実行しません");
            return MaintenanceActionResult.Fail(
                "Microsoft Defender Antivirus のサービスが無効なため、設定をリセットできません。" +
                "他社製アンチウイルス使用中でも Defender がパッシブモードでサービスを利用できる環境なら実行できます。");
        }

        var arguments = PowerShellArguments.EncodedCommand(BuildResetScript());
        // 複数設定を順次変更するため、開始前だけキャンセルを反映し、開始後は確認まで継続する。
        ct.ThrowIfCancellationRequested();

        var result = await _executor.RunAsync(
            "powershell.exe",
            arguments,
            // 除外を順番に削除するスクリプトを途中で kill すると部分リセットになる。
            // 開始前のキャンセルだけを受け付け、開始後は検証まで一体で完了させる。
            CancellationToken.None);

        if (result.Success)
        {
            LoggerBootstrap.Log.Info($"{Id}: 完了");
            var detail = string.IsNullOrWhiteSpace(result.StandardOutput)
                ? "  - 除外設定を全削除し、主要オプションを既定値に戻しました"
                : result.StandardOutput.Trim();
            return MaintenanceActionResult.Ok(detail);
        }

        LoggerBootstrap.Log.Error($"{Id}: {CommandFailureDiagnostic.Format(result)}");
        return MaintenanceActionResult.Fail(
            $"リセットに失敗しました (exit={result.ExitCode}){Environment.NewLine}" +
            DefenderCommandSupport.DescribeFailure(result) + Environment.NewLine +
            "一部の設定は変更済みの可能性があります。Windows セキュリティで保護状態と除外設定を確認してください。");
    }

    internal static string BuildAvailabilityCheckArguments()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            try {
                Import-Module Defender -ErrorAction Stop
                $status = Get-MpComputerStatus -ErrorAction Stop
                [ordered]@{
                    AMServiceEnabled = [bool]$status.AMServiceEnabled
                    AntivirusEnabled = [bool]$status.AntivirusEnabled
                } | ConvertTo-Json -Compress
            }
            catch {
                [Console]::Error.WriteLine($_.Exception.Message)
                exit 1
            }
            """;
        return PowerShellArguments.EncodedCommand(script);
    }

    internal static bool TryParseAvailability(
        string output,
        out DefenderAvailability availability)
    {
        availability = new(false, false);
        if (string.IsNullOrWhiteSpace(output) || output.Length > MaximumAvailabilityOutputLength)
        {
            return false;
        }

        var jsonLines = output.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith('{'))
            .ToArray();
        if (jsonLines.Length != 1)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(jsonLines[0]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("AMServiceEnabled", out var serviceEnabled)
                || serviceEnabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("AntivirusEnabled", out var antivirusEnabled)
                || antivirusEnabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }

            availability = new(
                serviceEnabled.GetBoolean(),
                antivirusEnabled.GetBoolean());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal sealed record DefenderAvailability(
        bool AMServiceEnabled,
        bool AntivirusEnabled);

    internal static string BuildResetScript() =>
        """
            $ErrorActionPreference = 'Stop'
            try {
                Import-Module Defender -ErrorAction Stop
                $status = Get-MpComputerStatus -ErrorAction Stop
                if (-not [bool]$status.AMServiceEnabled) {
                    throw 'Microsoft Defender Antivirus のサービスが無効なため、設定をリセットできません。'
                }
                if ($null -ne $status.IsTamperProtected -and [bool]$status.IsTamperProtected) {
                    throw 'Tamper Protection が有効なため Defender 設定を変更できません。組織管理端末では管理者へ依頼し、個人端末では Windows セキュリティの「改ざん防止」を確認してください。'
                }
                $p = Get-MpPreference -ErrorAction Stop
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

                Write-Output '  - 変更後に除外 0 件と主要な保護設定の有効状態を確認しました'
            }
            catch {
                [Console]::Error.WriteLine($_.Exception.Message)
                exit 1
            }
        """;
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

        return SelectMpCmdRunPath(
            platformDirectories,
            legacyPath,
            path => IsTrustedMpCmdRun(path, platformRoot, legacyPath));
    }

    internal static string? SelectMpCmdRunPath(
        IEnumerable<string> platformDirectories,
        string legacyPath,
        Func<string, bool> isTrustedCandidate)
    {
        var latest = platformDirectories
            .Select(directory => new
            {
                Path = Path.Combine(directory, "MpCmdRun.exe"),
                Version = ParsePlatformVersion(Path.GetFileName(directory)),
            })
            .Where(candidate => isTrustedCandidate(candidate.Path))
            .OrderByDescending(candidate => candidate.Version)
            .ThenByDescending(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();

        return latest ?? (isTrustedCandidate(legacyPath) ? legacyPath : null);
    }

    private static bool IsTrustedMpCmdRun(string path, string platformRoot, string legacyPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullPlatformRoot = Path.GetFullPath(platformRoot);
            var fullLegacyPath = Path.GetFullPath(legacyPath);
            var candidateDirectory = Path.GetDirectoryName(fullPath);
            var platformParent = candidateDirectory is null ? null : Path.GetDirectoryName(candidateDirectory);
            var isLegacy = fullPath.Equals(fullLegacyPath, StringComparison.OrdinalIgnoreCase);
            var isDirectPlatformChild = platformParent is not null &&
                platformParent.Equals(fullPlatformRoot, StringComparison.OrdinalIgnoreCase);
            if (!isLegacy && !isDirectPlatformChild)
            {
                return false;
            }

            var trustedRoot = isLegacy ? Path.GetDirectoryName(fullLegacyPath)! : fullPlatformRoot;
            if (!File.Exists(fullPath) ||
                HasReparsePoint(trustedRoot) ||
                HasReparsePoint(candidateDirectory!) ||
                HasReparsePoint(fullPath))
            {
                return false;
            }

            return ExecutableTrustVerifier.TryVerify(
                fullPath,
                "Microsoft Windows Publisher",
                out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool HasReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

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
        var sanitized = CommandFailureDiagnostic.Sanitize(lastLine);
        var detail = sanitized.Length <= maxLength ? sanitized : sanitized[..maxLength] + "…";
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
        var result = await executor.RunAsync(
            "powershell.exe",
            PowerShellArguments.EncodedCommand(BuildResetScript()),
            ct);

        if (result.Success)
        {
            LoggerBootstrap.Log.Info($"{Id}: 完了");
            return MaintenanceActionResult.Ok("  - Windows セキュリティ アプリをリセットしました");
        }

        LoggerBootstrap.Log.Error($"{Id}: {CommandFailureDiagnostic.Format(result)}");
        var failure = DescribeFailure(result);
        return MaintenanceActionResult.Fail(
            $"リセットに失敗しました (exit={result.ExitCode})" +
            (failure.Length == 0 ? string.Empty : $"{Environment.NewLine}  - {failure}"));
    }

    internal static string DescribeFailure(CommandExecutionResult result)
    {
        var lines = (result.StandardError + Environment.NewLine + result.StandardOutput)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var failure = lines.LastOrDefault(line =>
            !line.Equals("#< CLIXML", StringComparison.OrdinalIgnoreCase)
            && !line.StartsWith("<Objs", StringComparison.OrdinalIgnoreCase)
            && !line.StartsWith("</Objs", StringComparison.OrdinalIgnoreCase)
            && !line.Contains("System.Management.Automation.ProgressRecord", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(failure))
        {
            return string.Empty;
        }

        const int maximumLength = 500;
        var sanitized = CommandFailureDiagnostic.Sanitize(failure);
        return sanitized.Length <= maximumLength ? sanitized : sanitized[..maximumLength] + "…";
    }

    internal static string BuildResetScript() =>
        """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        try {
            $package = @(Get-AppxPackage -Name Microsoft.SecHealthUI -ErrorAction Stop)
            if ($package.Count -eq 0) {
                throw 'Microsoft.SecHealthUI パッケージが登録されていません。Windows セキュリティを再登録してから再実行してください。'
            }

            $packageFamilyName = $package[0].PackageFamilyName
            $package | Reset-AppxPackage -ErrorAction Stop

            # Reset-AppxPackage の展開完了直後は、現在ユーザーの登録が一時的に
            # Get-AppxPackage へ現れないことがある。同じ family の再登録を待って検証する。
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            $matchingAfter = @()
            do {
                $after = @(Get-AppxPackage -Name Microsoft.SecHealthUI -ErrorAction Stop)
                $matchingAfter = @($after | Where-Object { $_.PackageFamilyName -eq $packageFamilyName })
                if ($matchingAfter.Count -gt 0) { break }
                Start-Sleep -Milliseconds 500
            } while ([DateTime]::UtcNow -lt $deadline)

            if ($matchingAfter.Count -eq 0) {
                throw 'リセット後 30 秒以内に Microsoft.SecHealthUI パッケージの再登録を確認できませんでした。'
            }

            Write-Output '  - Windows セキュリティ アプリをリセットし、パッケージ登録を確認しました'
        }
        catch {
            [Console]::Error.WriteLine($_.Exception.Message)
            exit 1
        }
        """;
}
