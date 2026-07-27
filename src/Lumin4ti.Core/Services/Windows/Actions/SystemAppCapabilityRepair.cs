using System.Diagnostics;
using System.Text.RegularExpressions;
using Lumin4ti.Core.Interfaces;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// Windows がシステムアプリとして保護していて登録解除できないゴーストを、
/// 本体 (オプション機能 / Feature on Demand) の入れ直しで修復する。
/// 「システムアプリなら本来入っているべき」ので、削除できないものは消すより戻す方が正しい状態になる。
/// FoD の追加は DISM が唯一の手段のため Dism.exe を使う。
/// </summary>
internal static partial class SystemAppCapabilityRepair
{
    /// <summary>
    /// PackageFamilyName → 本体を提供するオプション機能の既定 ID。
    /// まずこの ID をそのまま追加し、OS 側でバージョン部が違って失敗したときだけ
    /// DISM の一覧から解決し直す (一覧取得は Windows Update への問い合わせで数分かかるため後回しにする)。
    /// </summary>
    private static readonly Dictionary<string, string> CapabilityIds = new(StringComparer.OrdinalIgnoreCase)
    {
        // 「接続 (ワイヤレスディスプレイ受信)」= スタートに ms-resource:ProductNameWindowsStore で出る典型例
        ["Microsoft.PPIProjection_cw5n1h2txyewy"] = "App.WirelessDisplay.Connect~~~~0.0.1.0",
        ["MicrosoftCorporationII.QuickAssist_8wekyb3d8bbwe"] = "App.Support.QuickAssist~~~~0.0.1.0",
    };

    /// <summary>FoD の取得を待つ上限。DISM は取得元へ到達できないと終了しないため必ず打ち切る。</summary>
    private static readonly TimeSpan AddCapabilityTimeout = TimeSpan.FromMinutes(15);

    /// <summary>進捗が無いときでも生存を示す通知の間隔。</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    [GeneratedRegex(@"[A-Za-z0-9._]+~[~A-Za-z0-9._-]*", RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityIdPattern();

    /// <summary>
    /// オプション機能を Windows Update から取得できない構成なら理由を返す (取得できるなら null)。
    /// WSUS 運用の PC では FoD が WSUS に無く、DISM が取得元を探し続けて終わらないため事前に弾く。
    /// </summary>
    private static string? GetFeatureOnDemandBlockedReason()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        // 再起動保留のままでは FoD の取得が途中 (実測では 75%) で進まなくなり、DISM が延々と
        // ポーリングし続ける。取得を始める前に再起動を促す方が待たせずに済む。
        if (Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") is { } pending)
        {
            pending.Dispose();
            return "Windows の更新が再起動待ちのため、オプション機能を取得できません " +
                "(PC を再起動してから、もう一度実行してください)";
        }

        using var au = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU");
        using var windowsUpdatePolicy = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate");
        using var servicing = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Servicing");
        using var wuauserv = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\wuauserv");

        return EvaluateFeatureOnDemandPolicy(
            au?.GetValue("UseWUServer") as int?,
            servicing?.GetValue("RepairContentServerSource") as int?,
            servicing?.GetValue("LocalSourcePath") as string,
            windowsUpdatePolicy?.GetValue("DoNotConnectToWindowsUpdateInternetLocations") as int?,
            wuauserv?.GetValue("Start") as int?);
    }

    /// <summary>
    /// ポリシー値から FoD 取得の可否を判定する。WSUS 強制かつ「WU から直接ダウンロード」でもなく
    /// ローカルソースも無い場合だけ「取得不能」とする。
    /// </summary>
    /// <param name="useWuServer">WSUS を使う設定 (1 で WSUS 強制)。</param>
    /// <param name="repairContentServerSource">2 なら WSUS 設定を無視して Windows Update から直接取得する。</param>
    /// <param name="localSourcePath">ローカルの取得元 (指定があれば WU が使えなくても取得できる)。</param>
    /// <param name="doNotConnectToWindowsUpdateInternetLocations">1 なら WU のインターネット接続が禁止されている。</param>
    /// <param name="windowsUpdateServiceStart">wuauserv の Start 値 (4 = 無効)。</param>
    internal static string? EvaluateFeatureOnDemandPolicy(
        int? useWuServer,
        int? repairContentServerSource,
        string? localSourcePath,
        int? doNotConnectToWindowsUpdateInternetLocations = null,
        int? windowsUpdateServiceStart = null)
    {
        // ローカルの取得元があれば Windows Update の状態に関係なく取得できる。
        if (!string.IsNullOrWhiteSpace(localSourcePath))
        {
            return null;
        }

        if (windowsUpdateServiceStart == 4)
        {
            return "Windows Update サービス (wuauserv) が無効化されているため、オプション機能を取得できません " +
                "(サービスのスタートアップの種類を「手動」に戻してから、もう一度実行してください)";
        }

        if (doNotConnectToWindowsUpdateInternetLocations == 1)
        {
            return "グループポリシー「Windows Update のインターネット上の場所に接続しない」が有効なため、" +
                "オプション機能を取得できません (このポリシーを無効にするか、ローカルの取得元を指定してください)";
        }

        if (useWuServer == 1 && repairContentServerSource != 2)
        {
            return "この PC は更新プログラムの取得先が WSUS に固定されているため、オプション機能を Windows Update から取得できません " +
                "(グループポリシー「オプション コンポーネントのインストールとコンポーネント修復の設定」で " +
                "「Windows Update から直接ダウンロードする」を有効にするか、ローカルの取得元を指定してください)";
        }

        return null;
    }

    /// <summary>この パッケージを入れ直しで修復できるか。</summary>
    public static bool CanRepair(string packageFamilyName) =>
        CapabilityIds.ContainsKey(packageFamilyName);

    /// <summary>
    /// 本体のオプション機能を追加する。成功したら null、失敗したら利用者向けの理由を返す。
    /// </summary>
    public static async Task<string?> TryRepairAsync(
        string packageFamilyName,
        ICommandExecutor executor,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (!CapabilityIds.TryGetValue(packageFamilyName, out var capabilityId))
        {
            return "この項目に対応するオプション機能が分かりません";
        }

        // WU から取得できない構成では DISM が延々と待ち続けるので、走らせる前に弾く。
        if (GetFeatureOnDemandBlockedReason() is { } blockedReason)
        {
            LoggerBootstrap.Log.Info($"remove-ghost-packages: FoD 取得が構成で塞がれているため中止: {blockedReason}");
            return blockedReason;
        }

        var add = await AddCapabilityAsync(capabilityId, executor, progress, ct);
        if (add.Success)
        {
            return null;
        }

        // 取得そのものが進まない状態で一覧取得へ降りても同じだけ待たされるので、そこで打ち切る。
        if (add.TimedOut)
        {
            return add.Error;
        }

        // 既定 ID のバージョン部が OS と食い違う場合だけ、時間のかかる一覧取得へ降りる。
        progress?.Report("既定の機能 ID で追加できなかったため、Windows Update から一覧を取得します (数分かかることがあります)...");
        var resolvedId = await ResolveCapabilityIdAsync(CapabilityPrefixOf(capabilityId), executor, progress, ct);
        if (resolvedId is null || resolvedId.Equals(capabilityId, StringComparison.OrdinalIgnoreCase))
        {
            return add.Error;
        }

        var retry = await AddCapabilityAsync(resolvedId, executor, progress, ct);
        return retry.Success ? null : retry.Error;
    }

    /// <summary>
    /// オプション機能の取得結果。タイムアウトは「もう一度別 ID で試しても無駄」と判断するために区別する。
    /// </summary>
    private readonly record struct CapabilityAddOutcome(bool Success, bool TimedOut, string? Error);

    private static async Task<CapabilityAddOutcome> AddCapabilityAsync(
        string capabilityId,
        ICommandExecutor executor,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report($"オプション機能を追加しています: {capabilityId} (Windows Update から取得するため数分かかることがあります)");
        // 長時間かかる工程なので、開始と所要時間をログに残して「無反応」と「本当に遅い」を切り分けられるようにする。
        LoggerBootstrap.Log.Info($"remove-ghost-packages: /Add-Capability 開始 {capabilityId}");
        var started = Stopwatch.GetTimestamp();

        // DISM は取得元へ到達できないと終了せずに待ち続ける。既定のコマンドタイムアウト (1 時間) は
        // 対話操作には長すぎるので、この工程だけ短い上限で打ち切る。
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attemptCts.CancelAfter(AddCapabilityTimeout);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(attemptCts.Token);
        var heartbeat = ReportHeartbeatAsync(progress, started, heartbeatCts.Token);

        try
        {
            var result = await executor.RunAsync(
                "dism.exe",
                $"/online /Add-Capability /CapabilityName:{capabilityId} /NoRestart",
                attemptCts.Token,
                progress);

            var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
            LoggerBootstrap.Log.Info(
                $"remove-ghost-packages: /Add-Capability 完了 {capabilityId} (exit={result.ExitCode} / {elapsed:F0} 秒)");

            if (DismExitCode.IsSuccessOrRebootRequired(result))
            {
                return new CapabilityAddOutcome(Success: true, TimedOut: false, Error: null);
            }

            LoggerBootstrap.Log.Error(
                $"remove-ghost-packages: {capabilityId} の追加に失敗 (exit={result.ExitCode}): {result.StandardError}");
            return new CapabilityAddOutcome(
                Success: false,
                TimedOut: false,
                Error: $"オプション機能 {capabilityId} の追加に失敗しました (exit={result.ExitCode})");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 上限に到達。executor がプロセスツリーごと終了させている。
            LoggerBootstrap.Log.Error(
                $"remove-ghost-packages: /Add-Capability が {AddCapabilityTimeout.TotalMinutes:F0} 分で完了せず打ち切り {capabilityId}");
            return new CapabilityAddOutcome(
                Success: false,
                TimedOut: true,
                Error:
                    $"オプション機能 {capabilityId} の取得が {AddCapabilityTimeout.TotalMinutes:F0} 分で終わらないため中止しました " +
                    "(Windows Update に到達できていない可能性があります。ネットワークと更新設定を確認してください)");
        }
        finally
        {
            await heartbeatCts.CancelAsync();
            await heartbeat;
        }
    }

    /// <summary>
    /// DISM が無出力のまま待ち続けても止まって見えないよう、経過時間を定期的に通知する。
    /// </summary>
    private static async Task ReportHeartbeatAsync(IProgress<string>? progress, long started, CancellationToken ct)
    {
        if (progress is null)
        {
            return;
        }

        try
        {
            using var timer = new PeriodicTimer(HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                progress.Report(
                    $"オプション機能を取得しています... ({Stopwatch.GetElapsedTime(started).TotalMinutes:F0} 分経過 / 上限 {AddCapabilityTimeout.TotalMinutes:F0} 分)");
            }
        }
        catch (OperationCanceledException)
        {
            // 本処理の完了・打ち切りに追随して終わるだけなので通知は不要。
        }
    }

    /// <summary>"App.WirelessDisplay.Connect~~~~0.0.1.0" → "App.WirelessDisplay.Connect"。</summary>
    private static string CapabilityPrefixOf(string capabilityId)
    {
        var separator = capabilityId.IndexOf('~');
        return separator > 0 ? capabilityId[..separator] : capabilityId;
    }

    /// <summary>
    /// DISM の機能一覧から接頭辞に一致する完全な機能 ID を取り出す。
    /// 一覧の見出しは OS の表示言語で変わるため、ID そのものを正規表現で拾って言語非依存にする。
    /// </summary>
    private static async Task<string?> ResolveCapabilityIdAsync(
        string prefix,
        ICommandExecutor executor,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        LoggerBootstrap.Log.Info($"remove-ghost-packages: /Get-Capabilities 開始 (接頭辞 {prefix})");
        var started = Stopwatch.GetTimestamp();
        var list = await executor.RunAsync("dism.exe", "/online /Get-Capabilities", ct, progress);
        LoggerBootstrap.Log.Info(
            $"remove-ghost-packages: /Get-Capabilities 完了 (exit={list.ExitCode} / {Stopwatch.GetElapsedTime(started).TotalSeconds:F0} 秒)");
        if (!list.Success)
        {
            return null;
        }

        foreach (Match match in CapabilityIdPattern().Matches(list.StandardOutput))
        {
            if (match.Value.StartsWith(prefix + "~", StringComparison.OrdinalIgnoreCase))
            {
                return match.Value;
            }
        }

        return null;
    }
}
