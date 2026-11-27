using System.Globalization;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services.Windows.Cleanup.Slim;

/// <summary>/AnalyzeComponentStore の解析結果 (DISM /English の出力から読み取った値)。</summary>
/// <param name="ActualSizeBytes">Actual Size of Component Store。</param>
/// <param name="BackupsAndDisabledFeaturesBytes">Backups and Disabled Features。</param>
/// <param name="CacheAndTemporaryDataBytes">Cache and Temporary Data。</param>
/// <param name="ReclaimablePackages">Number of Reclaimable Packages (出力に無ければ null)。</param>
/// <param name="CleanupRecommended">Component Store Cleanup Recommended (出力に無ければ null)。</param>
public sealed record ComponentStoreAnalysis(
    long ActualSizeBytes,
    long BackupsAndDisabledFeaturesBytes,
    long CacheAndTemporaryDataBytes,
    int? ReclaimablePackages,
    bool? CleanupRecommended)
{
    /// <summary>StartComponentCleanup で減る見込みの合計 (推定。DISM が保証する値ではない)。</summary>
    public long EstimatedReclaimableBytes => BackupsAndDisabledFeaturesBytes + CacheAndTemporaryDataBytes;
}

/// <summary>解析の結果。<see cref="Analysis"/> が null のときは <see cref="Error"/> に理由がある。</summary>
public sealed record ComponentStoreAnalysisResult(ComponentStoreAnalysis? Analysis, string? Error);

/// <summary>DISM 操作 1 回の結果。<see cref="Success"/> が false なら成功として扱わない。</summary>
/// <param name="Success">終了コードと出力の両方が成功と解釈できた。</param>
/// <param name="RebootRequired">終了コード 3010 (成功・要再起動)。</param>
/// <param name="Message">診断メッセージ。</param>
public sealed record DismOperationResult(bool Success, bool RebootRequired, string Message);

/// <summary>
/// コンポーネントストア (WinSxS) の解析とクリーンアップを DISM で行う。WinSxS のファイルは直接削除しない。
/// 出力は /English で固定し、非 0 終了・出力の解釈不能はどちらも失敗として扱う。
/// </summary>
public sealed class ComponentStore(ICommandExecutor executor)
{
    internal const string SuccessMarker = "The operation completed successfully";

    private static readonly TimeSpan AnalyzeTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromHours(3);

    /// <summary>/AnalyzeComponentStore を実行して結果を読み取る (読み取りだけで何も変更しない)。</summary>
    public async Task<ComponentStoreAnalysisResult> AnalyzeAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var result = await executor.RunAsync(
            "dism.exe",
            "/Online /Cleanup-Image /AnalyzeComponentStore /English",
            ct,
            progress,
            AnalyzeTimeout).ConfigureAwait(false);
        return ParseAnalysis(result);
    }

    /// <summary>
    /// /StartComponentCleanup を実行する。<paramref name="resetBase"/> が true のときは /ResetBase を付け、
    /// 既存の更新プログラムをアンインストールできなくなる (呼び出し側が二段階確認を済ませてから呼ぶ)。
    /// </summary>
    public async Task<DismOperationResult> CleanupAsync(bool resetBase, IProgress<string>? progress, CancellationToken ct)
    {
        var arguments = resetBase
            ? "/Online /Cleanup-Image /StartComponentCleanup /ResetBase /English"
            : "/Online /Cleanup-Image /StartComponentCleanup /English";
        var result = await executor.RunAsync("dism.exe", arguments, ct, progress, CleanupTimeout).ConfigureAwait(false);
        return InterpretOperation(result, CleanupMessage.Of("ComponentStoreCleanup"));
    }

    /// <summary>終了コードと出力から DISM 操作の成否を決める。解釈できない出力は失敗にする。</summary>
    internal static DismOperationResult InterpretOperation(CommandExecutionResult result, string operation)
    {
        var reboot = result.ExitCode == DismRebootRequired;
        if (result.ExitCode != 0 && !reboot)
        {
            return new(false, false, CleanupMessage.Of("OperationFailed", operation, result.ExitCode, Truncate(result.StandardError)));
        }

        if (!result.StandardOutput.Contains(SuccessMarker, StringComparison.OrdinalIgnoreCase))
        {
            return new(false, false, CleanupMessage.Of("OperationUninterpretable", operation, result.ExitCode));
        }

        return new(true, reboot, CleanupMessage.Of(reboot ? "OperationDoneReboot" : "OperationDone", operation));
    }

    internal static ComponentStoreAnalysisResult ParseAnalysis(CommandExecutionResult result)
    {
        if (result.ExitCode != 0)
        {
            return new(null, $"コンポーネントストアの解析が失敗しました (終了コード {result.ExitCode})。{Truncate(result.StandardError)}");
        }

        if (!result.StandardOutput.Contains(SuccessMarker, StringComparison.OrdinalIgnoreCase))
        {
            return new(null, "コンポーネントストアの解析結果を解釈できませんでした (成功の出力がありません)。");
        }

        var values = ReadKeyValues(result.StandardOutput);
        if (!TryGetSize(values, "Actual Size of Component Store", out var actual) ||
            !TryGetSize(values, "Backups and Disabled Features", out var backups) ||
            !TryGetSize(values, "Cache and Temporary Data", out var cache))
        {
            return new(null, "コンポーネントストアの解析結果の項目を解釈できませんでした。");
        }

        int? packages = null;
        if (values.TryGetValue("Number of Reclaimable Packages", out var packageText))
        {
            if (!int.TryParse(packageText, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            {
                return new(null, "再利用可能なパッケージ数を解釈できませんでした。");
            }

            packages = count;
        }

        bool? recommended = values.TryGetValue("Component Store Cleanup Recommended", out var recommendedText)
            ? string.Equals(recommendedText, "Yes", StringComparison.OrdinalIgnoreCase)
            : null;
        return new(new ComponentStoreAnalysis(actual, backups, cache, packages, recommended), null);
    }

    /// <summary>"7.12 GB" のような DISM の容量表記をバイト数へ読む。単位が不明なら false。</summary>
    internal static bool TryParseSize(string text, out long bytes)
    {
        bytes = 0;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2 ||
            !double.TryParse(parts[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) ||
            number < 0)
        {
            return false;
        }

        var unit = parts.Length == 2 ? parts[1].ToUpperInvariant() : "BYTES";
        var factor = unit switch
        {
            "BYTES" or "BYTE" or "B" => 1d,
            "KB" => 1024d,
            "MB" => 1024d * 1024,
            "GB" => 1024d * 1024 * 1024,
            "TB" => 1024d * 1024 * 1024 * 1024,
            _ => 0d,
        };
        if (factor == 0d)
        {
            return false;
        }

        bytes = (long)Math.Round(number * factor);
        return true;
    }

    private const int DismRebootRequired = 3010;

    private static bool TryGetSize(Dictionary<string, string> values, string key, out long bytes)
    {
        bytes = 0;
        return values.TryGetValue(key, out var text) && TryParseSize(text, out bytes);
    }

    private static Dictionary<string, string> ReadKeyValues(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in output.Split('\n'))
        {
            var separator = raw.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = raw[..separator].Trim();
            if (key.Length > 0)
            {
                values[key] = raw[(separator + 1)..].Trim();
            }
        }

        return values;
    }

    private static string Truncate(string text) =>
        text.Length <= 200 ? text.Trim() : text[..200].Trim() + "…";
}
