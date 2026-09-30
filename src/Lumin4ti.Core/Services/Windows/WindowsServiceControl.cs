using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Microsoft.Win32.SafeHandles;

namespace Lumin4ti.Core.Services.Windows;

/// <summary>サービスの現在状態 (SERVICE_STATUS_PROCESS の dwCurrentState)。</summary>
public enum WindowsServiceState
{
    /// <summary>サービス自体が存在しない (機能未搭載・別エディション等)。</summary>
    NotInstalled,
    Stopped,
    Running,
    /// <summary>開始中・停止中などの遷移状態。</summary>
    Transitioning,
}

/// <summary>
/// サービスの状態照会を Service Control Manager から直接行う。
/// 状態取得は C# ネイティブ (advapi32)、停止・開始だけ net.exe を使う
/// (依存サービスの停止は拒否し、指定されたサービスだけを扱う)。
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsServiceControl
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceEnumerateDependents = 0x0008;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceRunning = 0x00000004;
    private const int ScStatusProcessInfo = 0;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorMoreData = 234;

    /// <summary>
    /// net stop 1 件あたりの上限。停止要求自体はキャンセルさせない代わりに、
    /// 応答しないサービスでキャンセル不能な待ちが際限なく延びるのを防ぐ。
    /// </summary>
    internal static readonly TimeSpan ServiceStopTimeout = TimeSpan.FromMinutes(2);

    /// <summary>net start 1 件あたりの上限。</summary>
    internal static readonly TimeSpan ServiceStartTimeout = TimeSpan.FromMinutes(2);

    /// <summary>net start 失敗後に稼働状態への復帰を待つ上限。</summary>
    internal static readonly TimeSpan ServiceStartRecoveryTimeout = TimeSpan.FromSeconds(60);

    /// <summary>SCM で稼働状態への復帰を確認する間隔。</summary>
    internal static readonly TimeSpan ServiceStartRecoveryPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>サービスの状態を取得する。SCM を開けない場合は例外を投げる。</summary>
    public static WindowsServiceState QueryState(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        using var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Service Control Manager を開けませんでした");
        }

        using var service = OpenService(manager, serviceName, ServiceQueryStatus);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorServiceDoesNotExist)
            {
                return WindowsServiceState.NotInstalled;
            }

            throw new Win32Exception(error, $"{serviceName} サービスを開けませんでした");
        }

        if (!QueryServiceStatusEx(
                service,
                ScStatusProcessInfo,
                out var status,
                (uint)Marshal.SizeOf<ServiceStatusProcess>(),
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"{serviceName} の状態を取得できませんでした");
        }

        return status.CurrentState switch
        {
            ServiceStopped => WindowsServiceState.Stopped,
            ServiceRunning => WindowsServiceState.Running,
            _ => WindowsServiceState.Transitioning,
        };
    }

    /// <summary>状態を取得できない環境 (権限不足等) では null を返す非例外版。</summary>
    public static WindowsServiceState? TryQueryState(string serviceName)
    {
        try
        {
            return QueryState(serviceName);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            LoggerBootstrap.Log.Info($"{serviceName} の状態を取得できませんでした: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 稼働中のサービスを止め、<see cref="ServiceSuspension.ResumeAsync"/> で元の稼働状態へ戻す。
    /// 元から停止中・未インストールのサービスは触らず、再開対象にもしない。
    /// </summary>
    public static Task<ServiceSuspension> SuspendAsync(
        ICommandExecutor executor,
        IReadOnlyList<string> serviceNames,
        IProgress<string>? progress,
        CancellationToken ct)
        => SuspendAsync(executor, serviceNames, progress, ct, TryQueryState,
            canStopWithoutDependents: CanStopWithoutDependents);

    /// <summary>稼働中の依存サービスがないことを SCM で確認する。確認不能なら停止しない。</summary>
    internal static bool CanStopWithoutDependents(string serviceName)
    {
        try
        {
            using var manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            using var service = OpenService(manager, serviceName, ServiceEnumerateDependents);
            if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            // SERVICE_ACTIVE は Running だけでなく Paused・遷移中も含む。停止中以外は保護する。
            if (EnumDependentServices(service, 1, 0, 0, out _, out var count)) return count == 0;
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorMoreData) return false;
            throw new Win32Exception(error);
        }
        catch (Exception ex)
        {
            LoggerBootstrap.Log.Error($"{serviceName} の依存サービスを確認できません: {CommandFailureDiagnostic.Sanitize(ex.Message)}");
            return false;
        }
    }

    internal static async Task<ServiceSuspension> SuspendAsync(
        ICommandExecutor executor,
        IReadOnlyList<string> serviceNames,
        IProgress<string>? progress,
        CancellationToken ct,
        Func<string, WindowsServiceState?> queryState,
        Func<TimeSpan, Task>? recoveryDelay = null,
        Func<string, bool>? canStopWithoutDependents = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(serviceNames);
        ArgumentNullException.ThrowIfNull(queryState);

        var stopped = new List<string>();
        var failures = new List<string>();

        foreach (var name in serviceNames)
        {
            // キャンセルされてもここでは例外を投げない。ここで抜けると、既に停止した
            // サービスを再開する手段 (返り値の ServiceSuspension) を呼び出し側が失う。
            if (ct.IsCancellationRequested)
            {
                break;
            }

            WindowsServiceState? initialState;
            try { initialState = queryState(name); }
            catch (Exception ex)
            {
                LoggerBootstrap.Log.Error($"{name} サービスの初期状態を取得できませんでした", ex);
                failures.Add(name);
                continue;
            }
            if (initialState is WindowsServiceState.Stopped or WindowsServiceState.NotInstalled)
            {
                continue;
            }

            if (initialState is not WindowsServiceState.Running)
            {
                LoggerBootstrap.Log.Error($"{name} サービスの停止を確認できません: 初期状態={initialState?.ToString() ?? "取得不能"}");
                failures.Add(name);
                continue;
            }

            // 注入経路の既定は純粋なサービスfixture。製品経路は必ず SCM の依存照会を渡す。
            try
            {
                if (canStopWithoutDependents is not null && !canStopWithoutDependents(name))
                {
                    LoggerBootstrap.Log.Error($"{name}: 稼働中の依存サービスがあるか、依存関係を確認できないため停止を中止しました");
                    failures.Add(name);
                    continue;
                }
            }
            catch (Exception ex)
            {
                LoggerBootstrap.Log.Error($"{name}: 依存サービス照会に失敗: {CommandFailureDiagnostic.Sanitize(ex.Message)}");
                failures.Add(name);
                continue;
            }

            // 停止要求そのものにはキャンセルトークンを渡さない。実行中に net.exe を打ち切ると
            // SCM への停止要求だけが残って「停止したか」が確定せず、stopped から漏れたサービスが
            // 再開されないまま残る。キャンセルはサービスとサービスの間 (ループ先頭) で効かせる。
            // 要求後に照会や実行が失敗しても、停止要求が SCM に届いた可能性がある。
            // 停止確認とは別に復帰対象を先に記録して、補償手段を返す。
            stopped.Add(name);
            try
            {
                progress?.Report($"  - {name} サービスを停止しています…");
                var stop = await executor.RunAsync(
                    "net.exe",
                    $"stop \"{name}\"",
                    CancellationToken.None,
                    timeout: ServiceStopTimeout);
                var state = queryState(name);
                if (state is WindowsServiceState.Stopped)
                {
                    continue;
                }

                var reason = CommandFailureDiagnostic.Format(stop);
                LoggerBootstrap.Log.Error($"{name} サービスの停止を確認できません: 状態={state?.ToString() ?? "取得不能"}, {reason}");
            }
            catch (Exception ex)
            {
                LoggerBootstrap.Log.Error($"{name} サービスの停止に失敗しました: {CommandFailureDiagnostic.Sanitize(ex.Message)}");
            }
            failures.Add(name);
        }

        return new ServiceSuspension(executor, stopped, failures, queryState,
            recoveryDelay ?? (static delay => Task.Delay(delay, CancellationToken.None)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeServiceHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenService(
        SafeServiceHandle serviceManager,
        string serviceName,
        uint desiredAccess);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        SafeServiceHandle service,
        int infoLevel,
        out ServiceStatusProcess buffer,
        uint bufferSize,
        out uint bytesNeeded);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", EntryPoint = "EnumDependentServicesW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDependentServices(
        SafeServiceHandle service, uint state, nint buffer, uint bufferSize,
        out uint bytesNeeded, out uint servicesReturned);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint serviceHandle);
}

/// <summary>
/// <see cref="WindowsServiceControl.SuspendAsync"/> が停止を要求したサービスの復帰手段。
/// 停止を確認できなかったサービスは <see cref="FailedToStop"/> に入り、呼び出し側が結果へ反映する。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceSuspension
{
    private readonly ICommandExecutor _executor;
    private readonly Func<string, WindowsServiceState?> _queryState;
    private readonly Func<TimeSpan, Task> _delay;

    public ServiceSuspension(
        ICommandExecutor executor,
        IReadOnlyList<string> stopped,
        IReadOnlyList<string> failedToStop)
        : this(
            executor,
            stopped,
            failedToStop,
            WindowsServiceControl.TryQueryState,
            static delay => Task.Delay(delay, CancellationToken.None))
    {
    }

    internal ServiceSuspension(
        ICommandExecutor executor,
        IReadOnlyList<string> stopped,
        IReadOnlyList<string> failedToStop,
        Func<string, WindowsServiceState?> queryState,
        Func<TimeSpan, Task> delay)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(stopped);
        ArgumentNullException.ThrowIfNull(failedToStop);
        ArgumentNullException.ThrowIfNull(queryState);
        ArgumentNullException.ThrowIfNull(delay);

        _executor = executor;
        Stopped = stopped;
        FailedToStop = failedToStop;
        _queryState = queryState;
        _delay = delay;
    }

    /// <summary>元が稼働中で、この操作で停止を要求したサービス (停止確認の成否によらず再開対象)。</summary>
    public IReadOnlyList<string> Stopped { get; }

    /// <summary>停止を確認できなかったサービス (初期状態が取得不能・遷移中の場合も含む)。</summary>
    public IReadOnlyList<string> FailedToStop { get; }

    /// <summary>直近の復帰処理で失敗したサービスの、表示用に制限した診断。</summary>
    public IReadOnlyList<string> ResumeFailureDetails { get; private set; } = [];

    /// <summary>
    /// 停止したサービスを開始し直す。削除処理が失敗・キャンセルされた場合でも
    /// 元の稼働状態への復帰は最後まで実行するため、キャンセルトークンは受け取らない。
    /// </summary>
    public async Task<IReadOnlyList<string>> ResumeAsync()
    {
        var failures = new List<string>();
        var details = new List<string>();
        foreach (var name in Stopped)
        {
            try
            {
                if (QueryRecoveryState(name) is WindowsServiceState.Running)
                {
                    continue;
                }

                var start = await _executor.RunAsync(
                    "net.exe",
                    $"start \"{name}\"",
                    CancellationToken.None,
                    timeout: WindowsServiceControl.ServiceStartTimeout);
                if (start.Success)
                {
                    continue;
                }

                LoggerBootstrap.Log.Info(
                    $"{name} サービスの開始コマンド失敗 ({CommandFailureDiagnostic.Format(start)}) 後、稼働状態への復帰を最大 " +
                    $"{WindowsServiceControl.ServiceStartRecoveryTimeout.TotalSeconds:0} 秒待機します");
                if (await WaitForRunningAsync(name))
                {
                    LoggerBootstrap.Log.Info($"{name} サービスの稼働状態への復帰を確認しました");
                }
                else
                {
                    failures.Add(name);
                    var diagnostic = $"{name}: {CommandFailureDiagnostic.Format(start)}";
                    details.Add(diagnostic);
                    LoggerBootstrap.Log.Error($"サービスの再開に失敗: {diagnostic}");
                }
            }
            catch (Exception ex)
            {
                // timeout 等で開始要求だけが届いている場合も、SCM の復帰を確認する。
                if (await WaitForRunningAsync(name)) continue;
                failures.Add(name);
                var diagnostic = $"{name}: {CommandFailureDiagnostic.Sanitize(ex.Message)}";
                details.Add(diagnostic);
                LoggerBootstrap.Log.Error($"サービスの再開に失敗: {diagnostic}");
            }
        }

        ResumeFailureDetails = details;
        return failures;
    }

    private async Task<bool> WaitForRunningAsync(string serviceName)
    {
        var elapsed = TimeSpan.Zero;
        while (true)
        {
            var state = QueryRecoveryState(serviceName);
            if (state is WindowsServiceState.Running)
            {
                return true;
            }

            // 状態を確認できない場合と、サービスが消えた場合は待っても正常復帰を確認できない。
            if (state is null or WindowsServiceState.NotInstalled ||
                elapsed >= WindowsServiceControl.ServiceStartRecoveryTimeout)
            {
                return false;
            }

            var remaining = WindowsServiceControl.ServiceStartRecoveryTimeout - elapsed;
            var delay = remaining < WindowsServiceControl.ServiceStartRecoveryPollInterval
                ? remaining
                : WindowsServiceControl.ServiceStartRecoveryPollInterval;
            await _delay(delay);
            elapsed += delay;
        }
    }

    private WindowsServiceState? QueryRecoveryState(string name)
    {
        try { return _queryState(name); }
        catch (Exception ex)
        {
            LoggerBootstrap.Log.Error($"{name} サービスの復帰状態を照会できません: {CommandFailureDiagnostic.Sanitize(ex.Message)}");
            return null;
        }
    }
}
