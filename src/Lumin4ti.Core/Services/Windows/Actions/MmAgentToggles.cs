using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.Json;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// Get-MMAgent の成功結果を取得・キャッシュする共有プロバイダ。
/// 並行取得を 1 プロセスへ集約し、失敗時だけ次回取得で再試行する。
/// </summary>
public sealed class MmAgentStateProvider(ICommandExecutor executor)
{
    private readonly object _cacheSync = new();
    private Task<Dictionary<string, bool>?>? _cacheTask;
    private readonly ConcurrentDictionary<string, StateOverride> _overrides =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _unsupported =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<bool?> GetAsync(string propertyName, CancellationToken ct = default)
    {
        if (_unsupported.ContainsKey(propertyName))
        {
            return null;
        }

        if (_overrides.TryGetValue(propertyName, out var stateOverride))
        {
            return stateOverride.IsKnown
                ? stateOverride.Value
                : await RefreshInvalidatedAsync(propertyName, ct);
        }

        // 共有ロード自体は最初の呼び出しのキャンセルで汚染せず、各呼び出しは個別に待機を中断できる。
        // 成功結果だけを保持し、プロセス失敗・不正 JSON の null・Task 例外は次回呼び出しで再試行する。
        // 実プロセスの上限時間は ProcessCommandExecutor が保証する。
        var all = await GetOrCreateSharedLoad().WaitAsync(ct);

        // 共有ロード中に SetStateAsync が完了・中断した場合は、ロード開始時の古い値より
        // その後の既知値または再取得要求を優先する。共有ロード失敗 (null) 時も先に確認する。
        if (_overrides.TryGetValue(propertyName, out stateOverride))
        {
            return stateOverride.IsKnown
                ? stateOverride.Value
                : await RefreshInvalidatedAsync(propertyName, ct);
        }

        return all is not null && all.TryGetValue(propertyName, out var value) ? value : null;
    }

    private Task<Dictionary<string, bool>?> GetOrCreateSharedLoad()
    {
        lock (_cacheSync)
        {
            // 完了前は同じ Task を全呼び出しで共有する。完了後も成功値は保持するが、
            // null・fault・cancel は新しい Task へ置換して一時的な取得失敗を固定化しない。
            var shouldRetry = _cacheTask is null ||
                _cacheTask.IsCanceled ||
                (_cacheTask.IsCompletedSuccessfully && _cacheTask.Result is null);
            if (_cacheTask?.IsFaulted == true)
            {
                // 待機側が先にキャンセルされていた場合も、破棄する fault を未観測のまま残さない。
                _ = _cacheTask.Exception;
                shouldRetry = true;
            }

            if (shouldRetry)
            {
                _cacheTask = LoadAsync(executor);
            }

            return _cacheTask!;
        }
    }

    /// <summary>切り替え成功後の既知値を、共有ロードを起動・待機せずスレッドセーフに反映する。</summary>
    internal void SetKnownValue(string propertyName, bool value) =>
        _overrides[propertyName] = new StateOverride(IsKnown: true, Value: value);

    /// <summary>
    /// キャンセル等でコマンドが適用済みか判定できない場合、古い共有値へ戻らず次回取得を強制する。
    /// </summary>
    internal void Invalidate(string propertyName) =>
        _overrides[propertyName] = new StateOverride(IsKnown: false, Value: false);

    /// <summary>OS が切り替え要求を拒否した機能を、以後の操作対象から外す。</summary>
    internal void MarkUnsupported(string propertyName) => _unsupported[propertyName] = 0;

    internal bool IsUnsupported(string propertyName) => _unsupported.ContainsKey(propertyName);

    /// <summary>
    /// キャッシュを介さず Get-MMAgent を今この場で実行して 1 プロパティの現在値を読む。
    /// SetStateAsync の失敗後に「本当に目的の状態に達していないか」を判定するために使う
    /// (起動時キャッシュは古い可能性があるため冪等判定にはフレッシュ値が要る)。
    /// </summary>
    public async Task<bool?> ReadFreshAsync(string propertyName, CancellationToken ct = default)
    {
        var all = await LoadAsync(executor, ct);
        return all is not null && all.TryGetValue(propertyName, out var value) ? value : null;
    }

    private async Task<bool?> RefreshInvalidatedAsync(string propertyName, CancellationToken ct)
    {
        var fresh = await ReadFreshAsync(propertyName, ct);
        if (fresh is not bool value)
        {
            // 再取得中に別の成功した Set が既知値を置いた場合は、失敗した再取得よりそちらを優先する。
            return _overrides.TryGetValue(propertyName, out var latest) && latest.IsKnown
                ? latest.Value
                : null;
        }

        // Unknown のままなら新鮮な値へ置換する。並行 Set が既知値を置いていれば上書きしない。
        _overrides.TryUpdate(
            propertyName,
            new StateOverride(IsKnown: true, Value: value),
            new StateOverride(IsKnown: false, Value: false));
        return _overrides.TryGetValue(propertyName, out var current) && current.IsKnown
            ? current.Value
            : value;
    }

    private readonly record struct StateOverride(bool IsKnown, bool Value);

    private static async Task<Dictionary<string, bool>?> LoadAsync(ICommandExecutor executor, CancellationToken ct = default)
    {
        var result = await executor.RunAsync(
            "powershell.exe",
            "-NoProfile -NonInteractive -Command \"Get-MMAgent | ConvertTo-Json -Compress\"",
            ct);
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
    private readonly SemaphoreSlim _setGate = new(1, 1);

    public string Id => id;

    public string Label => label;

    public string Description => description;

    public CommandCategory Category => CommandCategory.Performance;

    public bool RequiresReboot => false;

    /// <summary>Get-MMAgent のプロパティ名 = Enable/Disable-MMAgent のパラメーター名。</summary>
    internal string PropertyName => propertyName;

    public Task<bool?> GetStateAsync(CancellationToken ct = default) =>
        MmAgentFeatureSupport.IsKnownUnsupportedOnCurrentWindows(propertyName) ||
        stateProvider.IsUnsupported(propertyName)
            ? Task.FromResult<bool?>(null)
            : stateProvider.GetAsync(propertyName, ct);

    public async Task<MaintenanceActionResult> SetStateAsync(bool on, CancellationToken ct = default)
    {
        if (MmAgentFeatureSupport.IsKnownUnsupportedOnCurrentWindows(propertyName) ||
            stateProvider.IsUnsupported(propertyName))
        {
            return UnsupportedResult();
        }

        // 同一機能への並行 Set は完了順と実状態が逆転し得るため直列化する。
        await _setGate.WaitAsync(ct);
        try
        {
            var cmdlet = on ? "Enable-MMAgent" : "Disable-MMAgent";
            var result = await executor.RunAsync(
                "powershell.exe",
                $"-NoProfile -NonInteractive -Command \"{cmdlet} -{propertyName} -ErrorAction Stop\"",
                ct);

            if (result.Success)
            {
                stateProvider.SetKnownValue(propertyName, on);
                LoggerBootstrap.Log.Info($"{Id} → {(on ? "有効" : "無効")}");
                return MaintenanceActionResult.Ok($"  - {propertyName} を{(on ? "有効化" : "無効化")}しました");
            }

            // cmdlet が失敗しても、目的の状態に既に一致していれば成功扱い (冪等)。
            // 例: OperationAPI は前提機能のプリフェッチが無効だと「この要求はサポートされていません」で
            // 失敗するが、既定で無効なら OFF 目標は既に達成済み。フレッシュ値で確認する。
            var current = await stateProvider.ReadFreshAsync(propertyName, ct);
            if (current is bool currentValue)
            {
                // 目的値と違う失敗でも、確認できた実状態を以後の UI に反映して古い共有値を残さない。
                stateProvider.SetKnownValue(propertyName, currentValue);
            }

            if (current == on)
            {
                LoggerBootstrap.Log.Info($"{Id}: 既に{(on ? "有効" : "無効")} (cmdlet は失敗したが目標状態に一致)");
                return MaintenanceActionResult.Ok($"  - {propertyName} は既に{(on ? "有効" : "無効")}です");
            }

            var reason = result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
            if (IsNotSupportedError(result.StandardError))
            {
                stateProvider.MarkUnsupported(propertyName);
                LoggerBootstrap.Log.Error($"{Id}: {cmdlet} はこの Windows でサポートされていません");
                return UnsupportedResult();
            }

            LoggerBootstrap.Log.Error($"{Id}: {cmdlet} (exit={result.ExitCode}): {reason}");
            return MaintenanceActionResult.Fail(
                $"{cmdlet} が失敗しました{(reason.Length > 0 ? $": {reason}" : string.Empty)}{FailureHint()}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // プロセス終了直前に OS 側だけ変更済みの可能性があるため、古い値を返さず次回再取得する。
            stateProvider.Invalidate(propertyName);
            throw;
        }
        finally
        {
            _setGate.Release();
        }
    }

    /// <summary>非対応エラー以外の失敗に、確認すべき実行環境を添える。</summary>
    private string FailureHint() =>
        " (SysMain サービスの状態または Windows 側の MMAgent 対応状況を確認してください)";

    private MaintenanceActionResult UnsupportedResult() => MaintenanceActionResult.Fail(
        $"{Label} は、このバージョンの Windows では安全に切り替えられないため操作を無効化しました。");

    internal static bool IsNotSupportedError(string standardError) =>
        standardError.Contains("0x80070032", StringComparison.OrdinalIgnoreCase) ||
        standardError.Contains("この要求はサポートされていません", StringComparison.OrdinalIgnoreCase) ||
        standardError.Contains("The request is not supported", StringComparison.OrdinalIgnoreCase);

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
                "Windows のバージョンによっては PowerShell からの切り替えがサポートされないため、その場合は操作できません。"),
            new(executor, provider, "ApplicationLaunchPrefetching",
                "mmagent-launch-prefetch",
                "アプリ起動プリフェッチ",
                "よく使うアプリの読み込むファイルを学習し、起動時に先読みして起動を高速化する機能 (Prefetch) です。" +
                "SSD でも起動待ちの短縮に寄与します。通常は有効のままを推奨しますが、Windows のバージョンによっては PowerShell から切り替えられません。"),
            new(executor, provider, "ApplicationPreLaunch",
                "mmagent-prelaunch",
                "UWP アプリの事前起動",
                "近いうちに使われそうなストアアプリ (UWP) を予測して、実際に開く前からバックグラウンドで起動しておく機能です。" +
                "体感は速くなりますがメモリを先取りで消費するため、メモリ節約を優先するなら OFF を推奨します。"),
        ];
    }
}

internal static class MmAgentFeatureSupport
{
    private const int Windows11Version25H2Build = 26200;

    internal static bool IsKnownUnsupportedOnCurrentWindows(string propertyName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        string? installationType = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            installationType = key?.GetValue("InstallationType") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            LoggerBootstrap.Log.Warn($"Windows の InstallationType を取得できませんでした: {ex.Message}");
        }

        return IsKnownUnsupported(propertyName, Environment.OSVersion.Version.Build, installationType);
    }

    internal static bool IsKnownUnsupported(string propertyName, int build, string? installationType) =>
        build == Windows11Version25H2Build &&
        string.Equals(installationType, "Client", StringComparison.OrdinalIgnoreCase) &&
        (propertyName.Equals("OperationAPI", StringComparison.OrdinalIgnoreCase) ||
         propertyName.Equals("ApplicationLaunchPrefetching", StringComparison.OrdinalIgnoreCase));
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
