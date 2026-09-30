using System.Runtime.ExceptionServices;
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
public sealed class NtpConfigAction : IMaintenanceAction
{
    internal const string ParametersKey = @"SYSTEM\CurrentControlSet\Services\W32Time\Parameters";
    internal const string NtpClientKey = @"SYSTEM\CurrentControlSet\Services\W32Time\TimeProviders\NtpClient";
    internal const string PolicyParametersKey = @"SOFTWARE\Policies\Microsoft\W32Time\Parameters";
    internal const string PolicyNtpClientKey = @"SOFTWARE\Policies\Microsoft\W32Time\TimeProviders\NtpClient";
    internal const string NtpServer = "ntp.jst.mfeed.ad.jp";
    internal const string NtpServerConfiguration = NtpServer + ",0x9";

    private readonly ICommandExecutor _executor;
    private readonly Func<bool> _isServiceRunning;
    private readonly INtpConfigurationStore _configuration;
    private readonly Func<string, WindowsServiceState?> _queryState;
    private readonly Func<TimeSpan, Task>? _recoveryDelay;
    private readonly Func<string, bool>? _canStopWithoutDependents;

    public NtpConfigAction(ICommandExecutor executor)
        : this(
            executor,
            IsWindowsTimeRunning,
            new NtpConfigurationStore(WindowsRegistryValueAccessor.Instance),
            WindowsServiceControl.TryQueryState,
            canStopWithoutDependents: WindowsServiceControl.CanStopWithoutDependents)
    {
    }

    internal NtpConfigAction(
        ICommandExecutor executor,
        Func<bool> isServiceRunning,
        Action writeConfiguration,
        Func<string, WindowsServiceState?>? queryState = null,
        Func<TimeSpan, Task>? recoveryDelay = null,
        Func<string, bool>? canStopWithoutDependents = null)
        : this(executor, isServiceRunning, new DelegateNtpConfigurationStore(writeConfiguration), queryState, recoveryDelay, canStopWithoutDependents)
    {
    }

    internal NtpConfigAction(
        ICommandExecutor executor,
        Func<bool> isServiceRunning,
        INtpConfigurationStore configuration,
        Func<string, WindowsServiceState?>? queryState = null,
        Func<TimeSpan, Task>? recoveryDelay = null,
        Func<string, bool>? canStopWithoutDependents = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _isServiceRunning = isServiceRunning ?? throw new ArgumentNullException(nameof(isServiceRunning));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _queryState = queryState ?? (_ => _isServiceRunning() ? WindowsServiceState.Running : WindowsServiceState.Stopped);
        _recoveryDelay = recoveryDelay;
        _canStopWithoutDependents = canStopWithoutDependents;
    }

    public string Id => "ntp-config";

    public string Label => "時刻同期サーバを国内 NTP に設定";

    public string Description =>
        $"Windows の時刻同期先を既定の time.windows.com から、国内の公開 NTP サーバ ({NtpServer}) に変更して時刻同期の精度と安定性を高めます。" +
        "Windows Time サービスが稼働中なら設定後に再起動して即座に反映し、停止中なら元の停止状態を維持します。";

    public CommandCategory Category => CommandCategory.System;

    public bool RequiresReboot => false;

    public async Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var blockReason = _configuration.GetBlockReason();
        if (blockReason is not null)
        {
            LoggerBootstrap.Log.Warn($"{Id}: 適用を見送り: {blockReason}");
            return MaintenanceActionResult.Fail($"NTP サーバを変更できませんでした: {blockReason}");
        }

        var wasRunning = _isServiceRunning();
        ServiceSuspension? suspension = null;
        ExceptionDispatchInfo? operationFailure = null;
        IReadOnlyList<string> resumeFailures = [];
        var applied = false;

        try
        {
            if (wasRunning)
            {
                // 要求前に復帰対象へ登録し、timeout や停止確認失敗でも finally へ返す。
                suspension = await WindowsServiceControl.SuspendAsync(
                    _executor, ["w32time"], null, ct, _queryState, _recoveryDelay, _canStopWithoutDependents);
            }

            // stop 完了直後のキャンセルでも、finally で元の稼働状態へ戻してから伝播する。
            ct.ThrowIfCancellationRequested();
            if (suspension is not { FailedToStop.Count: > 0 })
            {
                _configuration.Apply();
                applied = true;
            }
        }
        catch (Exception ex)
        {
            operationFailure = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            if (suspension is not null)
            {
                resumeFailures = await suspension.ResumeAsync();
            }
        }

        if (resumeFailures.Count > 0)
        {
            var reason = string.Join("; ", suspension!.ResumeFailureDetails);
            LoggerBootstrap.Log.Error($"{Id}: w32time の復旧に失敗: {reason}");
            return MaintenanceActionResult.Fail(
                (operationFailure?.SourceException is OperationCanceledException ? "操作はキャンセルされました。" : string.Empty) +
                (applied ? "NTP サーバは設定しましたが " : "NTP 設定を変更しませんでした。") +
                $"w32time の復帰に失敗しました: {reason}。Windows のサービス管理で Windows Time を開始し、開始できない場合は PC を再起動してください");
        }

        if (operationFailure is not null)
        {
            operationFailure.Throw();
        }

        if (!applied)
        {
            return MaintenanceActionResult.Fail("w32time の停止を確認できなかったため NTP 設定を変更しませんでした");
        }

        LoggerBootstrap.Log.Info(wasRunning
            ? $"{Id}: NTP 設定後に w32time を再起動"
            : $"{Id}: NTP 設定完了 (w32time は元から停止中)");
        return wasRunning
            ? MaintenanceActionResult.Ok($"  - NTP サーバを {NtpServer} に設定し、w32time を再起動しました")
            : MaintenanceActionResult.Ok($"  - NTP サーバを {NtpServer} に設定しました (w32time は元の停止状態を維持)");
    }

    /// <summary>
    /// w32time の稼働状態。SCM の照会は <see cref="WindowsServiceControl"/> と共通化している。
    /// 遷移中は停止・再開の判断ができないため、設定を始めずに中断する。
    /// </summary>
    private static bool IsWindowsTimeRunning() => WindowsServiceControl.QueryState("w32time") switch
    {
        WindowsServiceState.Stopped => false,
        WindowsServiceState.Running => true,
        WindowsServiceState.NotInstalled => throw new InvalidOperationException(
            "この環境には w32time サービスがありません。"),
        _ => throw new InvalidOperationException(
            "w32time が状態遷移中のため NTP 設定を開始できません"),
    };
}

internal interface INtpConfigurationStore
{
    string? GetBlockReason();

    void Apply();
}

internal sealed class DelegateNtpConfigurationStore(Action apply) : INtpConfigurationStore
{
    private readonly Action _apply = apply ?? throw new ArgumentNullException(nameof(apply));

    public string? GetBlockReason() => null;

    public void Apply() => _apply();
}

/// <summary>
/// 手動 NTP ピアだけを変更する。Type と AnnounceFlags は、ドメイン階層や時刻サーバーとしての
/// 役割を決める既存値なので変更しない。
/// </summary>
internal sealed class NtpConfigurationStore(IRegistryValueAccessor registry) : INtpConfigurationStore
{
    private static readonly RegistryToggleSpec TypeSpec = new(
        RegistryHive.LocalMachine,
        NtpConfigAction.ParametersKey,
        "Type",
        RegistryValueKind.String,
        string.Empty);

    private static readonly RegistryToggleSpec PolicyTypeSpec = new(
        RegistryHive.LocalMachine,
        NtpConfigAction.PolicyParametersKey,
        "Type",
        RegistryValueKind.String,
        string.Empty);

    private static readonly RegistryToggleSpec PolicyNtpServerSpec = new(
        RegistryHive.LocalMachine,
        NtpConfigAction.PolicyParametersKey,
        "NtpServer",
        RegistryValueKind.String,
        string.Empty);

    private static readonly RegistryToggleSpec NtpClientEnabledSpec = new(
        RegistryHive.LocalMachine,
        NtpConfigAction.NtpClientKey,
        "Enabled",
        RegistryValueKind.DWord,
        1);

    private static readonly RegistryToggleSpec PolicyNtpClientEnabledSpec = new(
        RegistryHive.LocalMachine,
        NtpConfigAction.PolicyNtpClientKey,
        "Enabled",
        RegistryValueKind.DWord,
        1);

    private static readonly RegistryToggleSpec NtpServerSpec = new(
        RegistryHive.LocalMachine,
        NtpConfigAction.ParametersKey,
        "NtpServer",
        RegistryValueKind.String,
        NtpConfigAction.NtpServerConfiguration);

    private readonly IRegistryValueAccessor _registry =
        registry ?? throw new ArgumentNullException(nameof(registry));

    public string? GetBlockReason()
    {
        if (_registry.Read(PolicyNtpServerSpec).Exists)
        {
            return "グループ ポリシーで NTP サーバが管理されています。管理ポリシー側で変更してください";
        }

        var enabled = _registry.Read(PolicyNtpClientEnabledSpec);
        if (!enabled.Exists)
        {
            enabled = _registry.Read(NtpClientEnabledSpec);
        }

        if (!enabled.Exists || enabled.DwordValue != 1)
        {
            return "Windows NTP クライアントが有効ではありません";
        }

        var type = _registry.Read(PolicyTypeSpec);
        if (!type.Exists)
        {
            type = _registry.Read(TypeSpec);
        }

        return type.StringValue?.Trim() switch
        {
            string value when value.Equals("NTP", StringComparison.OrdinalIgnoreCase) => null,
            string value when value.Equals("AllSync", StringComparison.OrdinalIgnoreCase) =>
                "時刻同期モードが AllSync のため、ドメイン階層が優先される場合は手動 NTP サーバへの切替を確認できません",
            string value when value.Equals("NT5DS", StringComparison.OrdinalIgnoreCase) =>
                "時刻同期モードが NT5DS (ドメイン階層) のため、手動 NTP サーバは使われません",
            string value when value.Equals("NoSync", StringComparison.OrdinalIgnoreCase) =>
                "時刻同期モードが NoSync のため、手動 NTP サーバは使われません",
            null or "" => "時刻同期モードを確認できません",
            var value => $"未対応の時刻同期モードです: {value}",
        };
    }

    public void Apply()
    {
        _registry.Write(
            NtpServerSpec,
            RegistryValueSnapshot.FromRegistry(
                NtpServerSpec.Kind,
                NtpServerSpec.AppliedValue));
    }
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

    public CommandCategory Category => CommandCategory.Repair;

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

        var diagnostic = CommandFailureDiagnostic.Format(result);
        LoggerBootstrap.Log.Error($"{Id}: {diagnostic}");
        return MaintenanceActionResult.Fail($"WSReset が失敗しました ({diagnostic})");
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
            .Select(CommandFailureDiagnostic.Sanitize)
            .Take(12)
            .ToList();

        if (result.Success)
        {
            LoggerBootstrap.Log.Info($"{Id}: 完了");
            return MaintenanceActionResult.Ok(summary.Count > 0
                ? string.Join(Environment.NewLine, summary)
                : "  - TRIM と空き領域の統合を実行しました");
        }

        var diagnostic = CommandFailureDiagnostic.Format(result);
        LoggerBootstrap.Log.Error($"{Id}: {diagnostic}");
        return MaintenanceActionResult.Fail($"defrag が失敗しました ({diagnostic})");
    }
}
