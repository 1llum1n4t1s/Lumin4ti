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
    internal const string ConfigKey = @"SYSTEM\CurrentControlSet\Services\W32Time\Config";
    internal const string NtpServer = "ntp.jst.mfeed.ad.jp";

    private readonly ICommandExecutor _executor;
    private readonly Func<bool> _isServiceRunning;
    private readonly INtpConfigurationStore _configuration;

    public NtpConfigAction(ICommandExecutor executor)
        : this(
            executor,
            IsWindowsTimeRunning,
            new TransactionalNtpConfigurationStore(WindowsRegistryValueAccessor.Instance))
    {
    }

    internal NtpConfigAction(
        ICommandExecutor executor,
        Func<bool> isServiceRunning,
        Action writeConfiguration)
        : this(executor, isServiceRunning, new DelegateNtpConfigurationStore(writeConfiguration))
    {
    }

    internal NtpConfigAction(
        ICommandExecutor executor,
        Func<bool> isServiceRunning,
        INtpConfigurationStore configuration)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _isServiceRunning = isServiceRunning ?? throw new ArgumentNullException(nameof(isServiceRunning));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
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
        var wasRunning = _isServiceRunning();
        var stoppedByThisAction = false;
        ExceptionDispatchInfo? operationFailure = null;
        CommandExecutionResult? restartResult = null;
        Exception? restartException = null;

        try
        {
            if (wasRunning)
            {
                // 停止要求にはキャンセルトークンを渡さない。実行中に打ち切ると停止できたかが
                // 確定せず、stoppedByThisAction が false のまま finally の再起動補償から漏れて
                // w32time が停止したまま残る。キャンセル境界は停止の前後 (下の判定) に置く。
                var stop = await _executor.RunAsync(
                    "net.exe",
                    "stop w32time",
                    CancellationToken.None,
                    timeout: WindowsServiceControl.ServiceStopTimeout);
                if (!stop.Success)
                {
                    var reason = FailureReason(stop);
                    LoggerBootstrap.Log.Error($"{Id}: w32time stop exit={stop.ExitCode}: {reason}");
                    return MaintenanceActionResult.Fail($"w32time の停止に失敗したため NTP 設定を変更しませんでした: {reason}");
                }

                stoppedByThisAction = true;
            }

            // stop 完了直後のキャンセルでも、finally で元の稼働状態へ戻してから伝播する。
            ct.ThrowIfCancellationRequested();
            _configuration.Apply();
        }
        catch (Exception ex)
        {
            operationFailure = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            if (stoppedByThisAction)
            {
                try
                {
                    // 設定失敗や利用者キャンセル後も、元の稼働状態への補償は最後まで完了させる。
                    restartResult = await _executor.RunAsync(
                        "net.exe",
                        "start w32time",
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    restartException = ex;
                }
            }
        }

        if (restartException is not null || restartResult is { Success: false })
        {
            var reason = restartException?.Message ?? FailureReason(restartResult!);
            LoggerBootstrap.Log.Error($"{Id}: w32time の復旧に失敗: {reason}");
            if (operationFailure is not null)
            {
                throw new InvalidOperationException(
                    $"NTP 設定中に失敗し、w32time の再起動にも失敗しました: {reason}",
                    operationFailure.SourceException);
            }

            return MaintenanceActionResult.Fail(
                $"NTP サーバは設定しましたが w32time の起動に失敗しました: {reason}");
        }

        if (operationFailure is not null)
        {
            operationFailure.Throw();
        }

        LoggerBootstrap.Log.Info(wasRunning
            ? $"{Id}: NTP 設定後に w32time を再起動"
            : $"{Id}: NTP 設定完了 (w32time は元から停止中)");
        return wasRunning
            ? MaintenanceActionResult.Ok($"  - NTP サーバを {NtpServer} に設定し、w32time を再起動しました")
            : MaintenanceActionResult.Ok($"  - NTP サーバを {NtpServer} に設定しました (w32time は元の停止状態を維持)");
    }

    private static string FailureReason(CommandExecutionResult result) =>
        string.IsNullOrWhiteSpace(result.StandardError)
            ? $"exit={result.ExitCode}"
            : result.StandardError.Trim();

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
    void Apply();
}

internal sealed class DelegateNtpConfigurationStore(Action apply) : INtpConfigurationStore
{
    private readonly Action _apply = apply ?? throw new ArgumentNullException(nameof(apply));

    public void Apply() => _apply();
}

/// <summary>NTP の3値を一つのトランザクションとして扱い、途中失敗時は全値を開始前へ戻す。</summary>
internal sealed class TransactionalNtpConfigurationStore(IRegistryValueAccessor registry) : INtpConfigurationStore
{
    private static readonly RegistryToggleSpec[] Specs =
    [
        new(
            RegistryHive.LocalMachine,
            NtpConfigAction.ParametersKey,
            "NtpServer",
            RegistryValueKind.String,
            NtpConfigAction.NtpServer),
        new(
            RegistryHive.LocalMachine,
            NtpConfigAction.ParametersKey,
            "Type",
            RegistryValueKind.String,
            "NTP"),
        new(
            RegistryHive.LocalMachine,
            NtpConfigAction.ConfigKey,
            "AnnounceFlags",
            RegistryValueKind.DWord,
            5),
    ];

    private readonly IRegistryValueAccessor _registry =
        registry ?? throw new ArgumentNullException(nameof(registry));

    public void Apply()
    {
        var before = Specs.Select(spec => (Spec: spec, Value: _registry.Read(spec))).ToArray();
        try
        {
            foreach (var spec in Specs)
            {
                _registry.Write(
                    spec,
                    RegistryValueSnapshot.FromRegistry(spec.Kind, spec.AppliedValue));
            }
        }
        catch (Exception applyError)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var (spec, value) in before.Reverse())
            {
                try
                {
                    _registry.Write(spec, value);
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
            }

            if (rollbackErrors.Count > 0)
            {
                throw new InvalidOperationException(
                    "NTP レジストリ設定に失敗し、開始前の値へのロールバックにも失敗しました。",
                    new AggregateException([applyError, .. rollbackErrors]));
            }

            throw new InvalidOperationException(
                "NTP レジストリ設定に失敗したため、開始前の値へロールバックしました。",
                applyError);
        }
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
