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

    /// <summary>
    /// 指定した機能以外の共有スナップショットを捨てる。MMAgent は機能同士が OS 側で連動するため
    /// (プリフェッチを切るとオペレーションレコーダーも無効になる)、1 つ切り替えたら他は取り直す。
    /// </summary>
    internal void InvalidateOthers(string changedPropertyName)
    {
        lock (_cacheSync)
        {
            // 成功済みスナップショットを破棄し、次回参照で Get-MMAgent を実行し直す。
            _cacheTask = null;
        }

        foreach (var propertyName in _overrides.Keys)
        {
            if (!string.Equals(propertyName, changedPropertyName, StringComparison.OrdinalIgnoreCase))
            {
                _overrides.TryRemove(propertyName, out _);
            }
        }
    }

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

    /// <summary>レジストリ経由で無効化する直前の値 (ON で戻すときに使う)。</summary>
    private int? _valueBeforeDisable;

    public string Id => id;

    public string Label => label;

    public string Description => description;

    public CommandCategory Category => CommandCategory.Performance;

    public bool RequiresReboot => false;

    /// <summary>Get-MMAgent のプロパティ名 = Enable/Disable-MMAgent のパラメーター名。</summary>
    internal string PropertyName => propertyName;

    public Task<bool?> GetStateAsync(CancellationToken ct = default)
    {
        if (stateProvider.IsUnsupported(propertyName))
        {
            return Task.FromResult<bool?>(null);
        }

        // レジストリ経由で無効化した直後は、再起動まで Get-MMAgent が有効のままを報告することがある。
        // 設定として保存されている値 (レジストリ) を優先し、表示が ON へ戻って見えるのを防ぐ。
        if (MmAgentRegistryFallback.CanFallBack(propertyName) &&
            MmAgentRegistryFallback.TryReadState(propertyName) is false)
        {
            return Task.FromResult<bool?>(false);
        }

        return stateProvider.GetAsync(propertyName, ct);
    }

    public async Task<MaintenanceActionResult> SetStateAsync(bool on, CancellationToken ct = default)
    {
        if (stateProvider.IsUnsupported(propertyName))
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
                // 連動する他機能 (プリフェッチ ⇄ オペレーションレコーダー等) の値は古くなるため捨てる。
                stateProvider.InvalidateOthers(propertyName);
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
                // cmdlet が非対応でも、同じ設定を持つレジストリ値から切り替えられる機能がある。
                // 「切り替えられないはず」と決めつけず、代替手段があるならそちらで達成する。
                if (MmAgentRegistryFallback.CanFallBack(propertyName))
                {
                    LoggerBootstrap.Log.Info($"{Id}: {cmdlet} が非対応のためレジストリ経由で切り替えます");
                    return await SetViaRegistryAsync(on, ct);
                }

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

    /// <summary>
    /// cmdlet が非対応を返す機能を、設定の実体であるレジストリ値から切り替える。
    /// 書き込み後は Get-MMAgent の値で反映を確認し、まだ古い値なら再起動が要ることを明示する。
    /// </summary>
    private async Task<MaintenanceActionResult> SetViaRegistryAsync(bool on, CancellationToken ct)
    {
        if (!on)
        {
            // 次に ON へ戻すときのために、無効化直前の値を控える (既に控えがあれば上書きしない)。
            _valueBeforeDisable ??= MmAgentRegistryFallback.TryReadRawValue();
        }

        var error = MmAgentRegistryFallback.TrySetState(
            propertyName,
            on,
            // ON で戻すときは無効化前の値を使う。控えが無ければ Windows 既定 (3) に戻す。
            readPreviousValue: () => _valueBeforeDisable);
        if (error is not null)
        {
            return MaintenanceActionResult.Fail($"{Label} を切り替えられませんでした: {error}");
        }

        stateProvider.SetKnownValue(propertyName, on);
        // プリフェッチを切ると OS 側でオペレーションレコーダーも連動して無効になるため、他機能は取り直す。
        stateProvider.InvalidateOthers(propertyName);
        var applied = $"  - {propertyName} を{(on ? "有効化" : "無効化")}しました (PowerShell が非対応のためレジストリ経由)";

        var fresh = await stateProvider.ReadFreshAsync(propertyName, ct);
        if (fresh is bool freshValue && freshValue != on)
        {
            // レジストリは変わったが MMAgent 側の報告値が追いつかない場合。
            stateProvider.SetKnownValue(propertyName, on);
            return MaintenanceActionResult.Partial(
                $"{applied}{Environment.NewLine}  - 反映には再起動 (または SysMain の再起動) が必要です");
        }

        LoggerBootstrap.Log.Info($"{Id} → {(on ? "有効" : "無効")} (レジストリ経由)");
        return MaintenanceActionResult.Ok(applied);
    }

    /// <summary>非対応エラー以外の失敗に、確認すべき実行環境を添える。</summary>
    private string FailureHint() =>
        " (SysMain サービスの状態または Windows 側の MMAgent 対応状況を確認してください)";

    private MaintenanceActionResult UnsupportedResult() => MaintenanceActionResult.Fail(
        // 「安全のため無効化した」と書くと Lumin4ti の判断に見えるが、実際は OS 側の cmdlet が
        // 変更を拒否している。原因と、利用者が取れる次の手を伝える書き方にする。
        $"{Label} は、この Windows の Enable/Disable-MMAgent が変更を拒否するため切り替えられません " +
        $"(この要求はサポートされていません)。設定は変更していません。{DependencyHint()}");

    /// <summary>
    /// 単独では変更できない機能に、連動で切り替わる前提機能を案内する。
    /// オペレーションレコーダーはプリフェッチ機構の一部なので、前提側を無効にすると一緒に無効になる。
    /// </summary>
    private string DependencyHint() =>
        string.Equals(propertyName, "OperationAPI", StringComparison.OrdinalIgnoreCase)
            ? " この機能はプリフェッチ機構の一部のため、「アプリ起動プリフェッチ」または「アプリ事前起動」を OFF にすると連動して無効になります。"
            : string.Empty;

    internal static bool IsNotSupportedError(string standardError) =>
        standardError.Contains("0x80070032", StringComparison.OrdinalIgnoreCase) ||
        standardError.Contains("この要求はサポートされていません", StringComparison.OrdinalIgnoreCase) ||
        standardError.Contains("The request is not supported", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// MMAgent の全機能項目を生成する (カタログの並び順)。状態取得は共有プロバイダ 1 回に集約。
    /// オペレーションレコーダーだけは ON/OFF に収まらないため選択式 (IMaintenanceChoice) にしている。
    /// </summary>
    public static IReadOnlyList<IMaintenanceItem> CreateAll(ICommandExecutor executor)
    {
        var provider = new MmAgentStateProvider(executor);
        return
        [
            new MmAgentFeatureToggle(executor, provider, "MemoryCompression",
                "mmagent-memory-compression",
                "メモリ圧縮 (Memory Compression)",
                "メモリ不足時、ディスクへスワップアウトする前にページを RAM 内で圧縮して実効容量を増やす機能です。" +
                "わずかな CPU 負荷と引き換えにスワップ由来の遅さと SSD への書き込みを減らせます。推奨は ON (Windows 既定も ON) です。"),
            new MmAgentFeatureToggle(executor, provider, "PageCombining",
                "mmagent-page-combining",
                "ページ結合 (Page Combining)",
                "内容が完全に同一のメモリページを 1 つに共有して重複を取り除く機能です。同種のアプリを多数起動する使い方でメモリ節約効果があります。" +
                "推奨は ON です (エディションによっては既定 OFF)。"),
            // プリフェッチ (親) を先に置く。オペレーションレコーダーは親が OFF だと連動して無効になるため、
            // 「親を ON にしてください」という案内が画面上でも自然に上を指すようにする。
            new MmAgentFeatureToggle(executor, provider, "ApplicationLaunchPrefetching",
                "mmagent-launch-prefetch",
                "アプリ起動プリフェッチ",
                "よく使うアプリの読み込むファイルを学習し、起動時に先読みして起動を高速化する機能 (Prefetch) です。" +
                "SSD でも起動待ちの短縮に寄与します。通常は有効のままを推奨しますが、Windows のバージョンによっては PowerShell から切り替えられません。"),
            new MmAgentFeatureToggle(executor, provider, "ApplicationPreLaunch",
                "mmagent-prelaunch",
                "UWP アプリの事前起動",
                "近いうちに使われそうなストアアプリ (UWP) を予測して、実際に開く前からバックグラウンドで起動しておく機能です。" +
                "体感は速くなりますがメモリを先取りで消費するため、メモリ節約を優先するなら OFF を推奨します。"),
            // 上のプリフェッチ系に連動する子設定なので、親の直後に置く。
            // 無効化を拒否する Windows でも記録ファイル数は変えられるため、選択式で両方を扱う。
            new MmAgentOperationApiChoice(executor, provider),
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
