using System.Runtime.Versioning;
using System.Text.Json;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// Get-MMAgent を 1 回だけ実行して全プロパティを取得・キャッシュする共有プロバイダ。
/// 5 つの MmAgentFeatureToggle が各々 PowerShell を起動する冗長 (起動時 5 プロセス) を防ぐ。
/// </summary>
public sealed class MmAgentStateProvider(ICommandExecutor executor)
{
    private readonly Lazy<Task<Dictionary<string, bool>?>> _cache =
        new(() => LoadAsync(executor), LazyThreadSafetyMode.ExecutionAndPublication);

    public async Task<bool?> GetAsync(string propertyName, CancellationToken ct = default)
    {
        var all = await _cache.Value;
        if (all is null)
        {
            return null;
        }

        return all.TryGetValue(propertyName, out var value) ? value : null;
    }

    private static async Task<Dictionary<string, bool>?> LoadAsync(ICommandExecutor executor)
    {
        var result = await executor.RunAsync(
            "powershell.exe",
            "-NoProfile -NonInteractive -Command \"Get-MMAgent | ConvertTo-Json -Compress\"");
        if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StandardOutput);
            var dict = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    dict[prop.Name] = prop.Value.GetBoolean();
                }
            }

            return dict;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// メモリ管理エージェント (MMAgent) の機能 1 つ分のトグル。
/// Enable/Disable-MMAgent は非公開の NT API をラップした PowerShell cmdlet で C# から直接呼べないため
/// PowerShell を項目単位で実行し、状態は共有プロバイダ (Get-MMAgent 1 回) から読む。
/// このトグルは例外的に ON = 機能が有効 / OFF = 機能が無効 を表す (推奨値は説明文に記載)。
/// </summary>
public sealed class MmAgentFeatureToggle(
    ICommandExecutor executor,
    MmAgentStateProvider stateProvider,
    string propertyName,
    string id,
    string label,
    string description) : IMaintenanceToggle
{
    public string Id => id;

    public string Label => label;

    public string Description => description;

    public CommandCategory Category => CommandCategory.Performance;

    public bool RequiresReboot => false;

    /// <summary>Get-MMAgent のプロパティ名 = Enable/Disable-MMAgent のパラメーター名。</summary>
    internal string PropertyName => propertyName;

    public Task<bool?> GetStateAsync(CancellationToken ct = default) => stateProvider.GetAsync(propertyName, ct);

    public async Task<MaintenanceActionResult> SetStateAsync(bool on, CancellationToken ct = default)
    {
        var cmdlet = on ? "Enable-MMAgent" : "Disable-MMAgent";
        var result = await executor.RunAsync(
            "powershell.exe",
            $"-NoProfile -NonInteractive -Command \"{cmdlet} -{propertyName} -ErrorAction Stop\"",
            ct);

        if (result.Success)
        {
            LoggerBootstrap.Log.Info($"{Id} → {(on ? "有効" : "無効")}");
            return MaintenanceActionResult.Ok($"  - {propertyName} を{(on ? "有効化" : "無効化")}しました");
        }

        var reason = result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        LoggerBootstrap.Log.Error($"{Id}: {cmdlet} (exit={result.ExitCode}): {reason}");
        return MaintenanceActionResult.Fail(
            $"{cmdlet} が失敗しました{(reason.Length > 0 ? $": {reason}" : string.Empty)}" +
            " (SysMain サービスが停止していると設定できない項目があります)");
    }

    /// <summary>MMAgent の全機能トグルを生成する (カタログの並び順)。状態取得は共有プロバイダ 1 回に集約。</summary>
    public static IReadOnlyList<MmAgentFeatureToggle> CreateAll(ICommandExecutor executor)
    {
        var provider = new MmAgentStateProvider(executor);
        return
        [
            new(executor, provider, "MemoryCompression",
                "mmagent-memory-compression",
                "メモリ圧縮 (Memory Compression)",
                "メモリ不足時、ディスクへスワップアウトする前にページを RAM 内で圧縮して実効容量を増やす機能です。" +
                "わずかな CPU 負荷と引き換えにスワップ由来の遅さと SSD への書き込みを減らせます。推奨は ON (Windows 既定も ON) です。"),
            new(executor, provider, "PageCombining",
                "mmagent-page-combining",
                "ページ結合 (Page Combining)",
                "内容が完全に同一のメモリページを 1 つに共有して重複を取り除く機能です。同種のアプリを多数起動する使い方でメモリ節約効果があります。" +
                "推奨は ON です (エディションによっては既定 OFF)。"),
            new(executor, provider, "OperationAPI",
                "mmagent-operation-api",
                "Operation Recorder API",
                "SysMain (旧 Superfetch) の動作を外部ツールから記録・再生するための API です。ベンチマークや性能解析ツール向けの機能で、通常の利用では使われません。" +
                "推奨は OFF (Windows 既定も OFF) です。"),
            new(executor, provider, "ApplicationLaunchPrefetching",
                "mmagent-launch-prefetch",
                "アプリ起動プリフェッチ",
                "よく使うアプリの読み込むファイルを学習し、起動時に先読みして起動を高速化する機能 (Prefetch) です。" +
                "SSD でも起動待ちの短縮に寄与します。推奨は ON (Windows 既定も ON) です。"),
            new(executor, provider, "ApplicationPreLaunch",
                "mmagent-prelaunch",
                "UWP アプリの事前起動",
                "近いうちに使われそうなストアアプリ (UWP) を予測して、実際に開く前からバックグラウンドで起動しておく機能です。" +
                "体感は速くなりますがメモリを先取りで消費するため、メモリ節約を優先するなら OFF を推奨します。"),
        ];
    }
}

/// <summary>
/// カーネルページング抑止設定 (DisablePagingExecutive) を削除して Windows 既定に戻す。
/// 過去の最適化ツールが書き込んだ非推奨 tweak の復旧手順。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PagingExecutiveResetAction : IMaintenanceAction
{
    private const string MemoryManagementKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

    public string Id => "paging-executive-reset";

    public string Label => "カーネルページング設定を既定に戻す";

    public string Description =>
        "過去の最適化ツールが書き込みがちな DisablePagingExecutive (カーネルコードを常に RAM に置く設定) を削除して、Windows 既定のページング動作に戻します。" +
        "この設定は現代の Windows ではメリットがほぼなく、メモリを無駄に占有する原因になります。設定が無い PC では何も変更しません。";

    public CommandCategory Category => CommandCategory.Performance;

    public bool RequiresReboot => true;

    public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default)
    {
        using var key = Registry.LocalMachine.OpenSubKey(MemoryManagementKey, writable: true);
        if (key?.GetValue("DisablePagingExecutive") is null)
        {
            return Task.FromResult(MaintenanceActionResult.Ok("  - DisablePagingExecutive は設定されていません (既定のままです)"));
        }

        key.DeleteValue("DisablePagingExecutive", throwOnMissingValue: false);
        LoggerBootstrap.Log.Info($"{Id}: 削除しました");
        return Task.FromResult(MaintenanceActionResult.Ok("  - DisablePagingExecutive を削除しました (既定に戻す)"));
    }
}
