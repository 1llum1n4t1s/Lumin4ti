using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// 時刻同期 (w32time) の NTP サーバを国内の ntp.jst.mfeed.ad.jp に設定する。
/// レジストリは C# で直接書き、サービス再起動のみ net コマンドを使う。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NtpConfigAction(ICommandExecutor executor) : IMaintenanceAction
{
    private const string ParametersKey = @"SYSTEM\CurrentControlSet\Services\W32Time\Parameters";
    private const string ConfigKey = @"SYSTEM\CurrentControlSet\Services\W32Time\Config";
    private const string NtpServer = "ntp.jst.mfeed.ad.jp";

    public string Id => "ntp-config";

    public string Label => "時刻同期サーバを国内 NTP に設定";

    public string Description =>
        $"Windows の時刻同期先を既定の time.windows.com から、国内の公開 NTP サーバ ({NtpServer}) に変更して時刻同期の精度と安定性を高めます。" +
        "設定後に Windows Time サービスを再起動して即座に反映します。";

    public CommandCategory Category => CommandCategory.System;

    public bool RequiresReboot => false;

    public async Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default)
    {
        // 停止は「起動していない」ケースがあるため失敗を無視し、開始の成否だけを見る
        await executor.RunAsync("net.exe", "stop w32time", ct);

        using (var parameters = Registry.LocalMachine.CreateSubKey(ParametersKey))
        {
            parameters.SetValue("NtpServer", NtpServer, RegistryValueKind.String);
            parameters.SetValue("Type", "NTP", RegistryValueKind.String);
        }

        using (var config = Registry.LocalMachine.CreateSubKey(ConfigKey))
        {
            config.SetValue("AnnounceFlags", 5, RegistryValueKind.DWord);
        }

        var start = await executor.RunAsync("net.exe", "start w32time", ct);

        LoggerBootstrap.Log.Info($"{Id}: w32time start exit={start.ExitCode}");
        return start.Success
            ? MaintenanceActionResult.Ok($"  - NTP サーバを {NtpServer} に設定し、w32time を再起動しました")
            : MaintenanceActionResult.Fail($"NTP サーバは設定しましたが w32time の起動に失敗しました: {start.StandardError}");
    }
}

/// <summary>
/// イベントビューアーの全ログをクリアする。
/// wevtutil を使わず EventLogSession (Windows Event Log API) で C# ネイティブに実装する。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EventLogClearAction : IMaintenanceAction
{
    public string Id => "event-log-clear";

    public string Label => "イベントビューアーの全ログを削除";

    public string Description =>
        "イベントビューアーに蓄積された全チャンネルのログを削除して、ログ格納領域 (通常数百 MB) を解放します。" +
        "過去のトラブルシューティング情報も消えるため、直近の不具合を調査中の場合は実行しないでください。一部の保護されたシステムログは削除できずスキップされます。";

    public CommandCategory Category => CommandCategory.Cleanup;

    public bool RequiresReboot => false;

    public bool IsLongRunning => true;

    public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var session = EventLogSession.GlobalSession;
            var cleared = 0;
            var accessDenied = 0;
            var protectedLogs = 0;

            foreach (var logName in session.GetLogNames())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    session.ClearLog(logName);
                    cleared++;
                }
                catch (UnauthorizedAccessException)
                {
                    accessDenied++;
                }
                catch (EventLogException ex) when (ex.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("アクセスが拒否", StringComparison.Ordinal))
                {
                    accessDenied++;
                }
                catch (EventLogException)
                {
                    // 有効化された Analytic/Debug チャンネル等はクリア不可
                    protectedLogs++;
                }
            }

            LoggerBootstrap.Log.Info($"{Id}: {cleared} 件クリア / 権限不足 {accessDenied} 件 / 保護 {protectedLogs} 件");

            var lines = new List<string> { $"  - {cleared} 件のログをクリアしました" };
            if (protectedLogs > 0)
            {
                lines.Add($"  - 保護されたログ {protectedLogs} 件はスキップしました (有効化中の Analytic/Debug チャンネル等)");
            }

            if (accessDenied == 0)
            {
                return MaintenanceActionResult.Ok(lines);
            }

            lines.Add($"  - 権限不足で {accessDenied} 件をスキップしました");
            // 権限不足が支配的なら非昇格実行 (デバッグ起動等) の可能性が高いので失敗として案内する
            if (accessDenied > cleared)
            {
                lines.Add("  - 管理者権限で起動し直してから再実行してください (通常起動なら UAC 昇格されます)");
                return MaintenanceActionResult.Fail(string.Join(Environment.NewLine, lines));
            }

            return MaintenanceActionResult.Ok(lines);
        }, ct);
}

/// <summary>
/// Microsoft Store のキャッシュをリセットする (WSReset.exe)。
/// Store キャッシュの正規のリセット手段は WSReset のみ。
/// </summary>
public sealed class StoreCacheResetAction(ICommandExecutor executor) : IMaintenanceAction
{
    public string Id => "store-cache-reset";

    public string Label => "Microsoft Store のキャッシュをクリア";

    public string Description =>
        "Microsoft Store のキャッシュを WSReset でリセットします。ストアが開かない・ダウンロードが進まない・「再試行してください」が続くといった不調の定番対処です。" +
        "アプリ本体やアカウント情報は消えません。完了時に Store アプリが自動的に開くことがあります。";

    public CommandCategory Category => CommandCategory.Cleanup;

    public bool RequiresReboot => false;

    public bool IsLongRunning => true;

    public async Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default)
    {
        var result = await executor.RunAsync("WSReset.exe", string.Empty, ct);

        if (result.Success)
        {
            LoggerBootstrap.Log.Info($"{Id}: 完了");
            return MaintenanceActionResult.Ok("  - Store キャッシュをクリアしました");
        }

        LoggerBootstrap.Log.Error($"{Id}: exit={result.ExitCode}");
        return MaintenanceActionResult.Fail($"WSReset が失敗しました (exit={result.ExitCode})");
    }
}

/// <summary>
/// SSD の TRIM と空き領域の統合を全ボリュームに実行する (defrag /C /L /X)。
/// ボリューム最適化は defrag.exe が正規の手段。出力の要約は C# 側で行う。
/// </summary>
public sealed class TrimOptimizeAction(ICommandExecutor executor) : IMaintenanceAction
{
    public string Id => "ssd-trim";

    public string Label => "SSD TRIM と空き領域の統合";

    public string Description =>
        "全ボリュームに対して SSD への TRIM 通知 (/L) と空き領域の統合 (/X) を実行し、SSD の書き込み性能維持と空き領域の断片化解消を行います。" +
        "ドライブ構成によっては完了まで数分〜数十分かかります。実行中も PC は使用できます。";

    public CommandCategory Category => CommandCategory.Cleanup;

    public bool RequiresReboot => false;

    public bool IsLongRunning => true;

    public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default) => ExecuteAsync(null, ct);

    public async Task<MaintenanceActionResult> ExecuteAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        var result = await executor.RunAsync("defrag.exe", "/C /L /X", ct, progress);

        // defrag はレポートを大量に吐くため、サイズ・TRIM 関連の行だけ抽出する
        var summary = result.StandardOutput.Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Contains("trimmed", StringComparison.OrdinalIgnoreCase)
                     || l.Contains("Free space", StringComparison.OrdinalIgnoreCase)
                     || l.Contains("Volume size", StringComparison.OrdinalIgnoreCase)
                     || l.Contains("トリミング")
                     || l.Contains("空き領域")
                     || l.Contains("ボリューム サイズ"))
            .Select(l => $"  {l}")
            .ToList();

        if (result.Success)
        {
            LoggerBootstrap.Log.Info($"{Id}: 完了");
            return MaintenanceActionResult.Ok(summary.Count > 0
                ? string.Join(Environment.NewLine, summary)
                : "  - TRIM と空き領域の統合を実行しました");
        }

        LoggerBootstrap.Log.Error($"{Id}: exit={result.ExitCode}");
        return MaintenanceActionResult.Fail($"defrag が失敗しました (exit={result.ExitCode}): {result.StandardError}");
    }
}

/// <summary>
/// 電源プラン (Balanced) と電源モード (最適なパフォーマンス) を推奨構成にまとめて設定する。
/// 電源スキームの操作は powercfg が正規の手段。DC 側の電源モード Overlay のみ
/// powercfg が書けないためレジストリを直接書く。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PowerPlanSetupAction(ICommandExecutor executor) : IMaintenanceAction
{
    /// <summary>電源モード Overlay「最適なパフォーマンス」の GUID。</summary>
    private const string MaxPerformanceOverlay = "ded574b5-45a0-4f42-8737-46345c09c238";

    public string Id => "power-plan-setup";

    public string Label => "電源プラン / 電源モードを推奨構成に設定";

    public string Description =>
        "電源プランを既定の「バランス」ベースに再構成し、電源モードを「最適なパフォーマンス」(AC/バッテリー両方) に設定します。あわせて次の値を書き込みます: " +
        "ディスプレイ消灯 = 電源接続時 180 分 / バッテリー 30 分、スリープ = なし、電源ボタン = シャットダウン、カバーを閉じたとき = 何もしない (隠し項目の表示属性も解除)。" +
        "既存の電源プランのカスタマイズは既定値に戻るため、独自プランを使っている場合は注意してください。";

    public CommandCategory Category => CommandCategory.System;

    public bool RequiresReboot => false;

    public async Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default)
    {
        // IsDcOnly=true の DC (バッテリー) 系ステップはデスクトップ PC では対象がなく失敗しうるが正常。
        // それ以外 (既定スキーム復元・AC 系・overlay・setactive) の失敗は実際の設定失敗として扱う。
        (string Arguments, string Label, bool IsDcOnly)[] steps =
        [
            ("/restoredefaultschemes", "既定スキームの復元", false),
            ("/attributes SUB_BUTTONS 7648efa3-dd9c-4e3e-b566-50f929386280 -ATTRIB_HIDE", "電源ボタン設定の表示", false),
            ("/attributes SUB_BUTTONS 5ca83367-6e45-459f-a27b-476b1d01c936 -ATTRIB_HIDE", "カバー設定の表示", false),
            ("/setacvalueindex SCHEME_BALANCED SUB_VIDEO VIDEOIDLE 10800", "ディスプレイ消灯 (AC 180分)", false),
            ("/setdcvalueindex SCHEME_BALANCED SUB_VIDEO VIDEOIDLE 1800", "ディスプレイ消灯 (DC 30分)", true),
            ("/setacvalueindex SCHEME_BALANCED SUB_SLEEP STANDBYIDLE 0", "スリープなし (AC)", false),
            ("/setdcvalueindex SCHEME_BALANCED SUB_SLEEP STANDBYIDLE 0", "スリープなし (DC)", true),
            ("/setacvalueindex SCHEME_BALANCED SUB_BUTTONS PBUTTONACTION 3", "電源ボタン = シャットダウン (AC)", false),
            ("/setdcvalueindex SCHEME_BALANCED SUB_BUTTONS PBUTTONACTION 3", "電源ボタン = シャットダウン (DC)", true),
            ("/setacvalueindex SCHEME_BALANCED SUB_BUTTONS LIDACTION 0", "カバー = 何もしない (AC)", false),
            ("/setdcvalueindex SCHEME_BALANCED SUB_BUTTONS LIDACTION 0", "カバー = 何もしない (DC)", true),
            ($"/overlaysetactive {MaxPerformanceOverlay}", "電源モード = 最適なパフォーマンス (AC)", false),
            ("/setactive SCHEME_CURRENT", "設定の適用", false),
        ];

        var dcSkipped = new List<string>();
        var criticalFailed = new List<string>();
        foreach (var (arguments, label, isDcOnly) in steps)
        {
            ct.ThrowIfCancellationRequested();
            var result = await executor.RunAsync("powercfg.exe", arguments, ct);
            if (result.Success)
            {
                continue;
            }

            LoggerBootstrap.Log.Error($"{Id}: powercfg {arguments} (exit={result.ExitCode})");
            if (isDcOnly)
            {
                dcSkipped.Add(label);
            }
            else
            {
                criticalFailed.Add(label);
            }
        }

        // /overlaysetactive は AC 側しか書かないため、DC 側の電源モードはレジストリ直接書き
        using (var key = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes"))
        {
            key.SetValue("ActiveOverlayDcPowerScheme", MaxPerformanceOverlay, RegistryValueKind.String);
        }

        var dcNote = dcSkipped.Count == 0
            ? string.Empty
            : $"（DC/バッテリー系 {dcSkipped.Count} 件はデスクトップ PC では対象がなく正常にスキップ）";

        // AC 系・既定スキーム復元・overlay 等の実失敗があれば、既存プランを破壊済みの可能性があるため Fail
        if (criticalFailed.Count > 0)
        {
            LoggerBootstrap.Log.Error($"{Id}: 重要ステップ {criticalFailed.Count} 件失敗");
            return MaintenanceActionResult.Fail(
                $"  - 一部の設定に失敗しました: {string.Join("、", criticalFailed)}{dcNote}");
        }

        LoggerBootstrap.Log.Info($"{Id}: 完了");
        return MaintenanceActionResult.Ok(
            $"  - 電源プラン (バランス) / 電源モード (最適なパフォーマンス) を設定しました{dcNote}");
    }
}
