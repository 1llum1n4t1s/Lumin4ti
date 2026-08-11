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
/// (SCM の制御要求は依存サービスの連鎖停止を自前で解決する必要があり、net.exe が唯一の簡潔な手段)。
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsServiceControl
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceRunning = 0x00000004;
    private const int ScStatusProcessInfo = 0;
    private const int ErrorServiceDoesNotExist = 1060;

    /// <summary>
    /// net stop 1 件あたりの上限。停止要求自体はキャンセルさせない代わりに、
    /// 応答しないサービスでキャンセル不能な待ちが際限なく延びるのを防ぐ。
    /// </summary>
    internal static readonly TimeSpan ServiceStopTimeout = TimeSpan.FromMinutes(2);

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
    public static async Task<ServiceSuspension> SuspendAsync(
        ICommandExecutor executor,
        IReadOnlyList<string> serviceNames,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(serviceNames);

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

            if (TryQueryState(name) is not WindowsServiceState.Running)
            {
                continue;
            }

            progress?.Report($"  - {name} サービスを停止しています…");
            // 停止要求そのものにはキャンセルトークンを渡さない。実行中に net.exe を打ち切ると
            // SCM への停止要求だけが残って「停止したか」が確定せず、stopped から漏れたサービスが
            // 再開されないまま残る。キャンセルはサービスとサービスの間 (ループ先頭) で効かせる。
            var stop = await executor.RunAsync(
                "net.exe",
                $"stop \"{name}\" /y",
                CancellationToken.None,
                timeout: ServiceStopTimeout);
            if (stop.Success)
            {
                stopped.Add(name);
                continue;
            }

            // 停止直後に SCM 側で完了した場合は成功として扱う (net.exe の戻り値より実状態を優先)。
            if (TryQueryState(name) is WindowsServiceState.Stopped)
            {
                stopped.Add(name);
                continue;
            }

            var reason = string.IsNullOrWhiteSpace(stop.StandardError)
                ? $"exit={stop.ExitCode}"
                : stop.StandardError.Trim();
            LoggerBootstrap.Log.Error($"{name} サービスの停止に失敗: {reason}");
            failures.Add(name);
        }

        return new ServiceSuspension(executor, stopped, failures);
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
    [DllImport("advapi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint serviceHandle);
}

/// <summary>
/// <see cref="WindowsServiceControl.SuspendAsync"/> が止めたサービスの一覧。
/// 停止に失敗したサービスは <see cref="FailedToStop"/> に入り、呼び出し側が結果へ反映する。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServiceSuspension(
    ICommandExecutor executor,
    IReadOnlyList<string> stopped,
    IReadOnlyList<string> failedToStop)
{
    /// <summary>この操作で停止したサービス (再開対象)。</summary>
    public IReadOnlyList<string> Stopped { get; } = stopped;

    /// <summary>稼働中だが停止できなかったサービス。</summary>
    public IReadOnlyList<string> FailedToStop { get; } = failedToStop;

    /// <summary>
    /// 停止したサービスを開始し直す。削除処理が失敗・キャンセルされた場合でも
    /// 元の稼働状態への復帰は最後まで実行するため、キャンセルトークンは受け取らない。
    /// </summary>
    public async Task<IReadOnlyList<string>> ResumeAsync()
    {
        var failures = new List<string>();
        foreach (var name in Stopped)
        {
            try
            {
                var start = await executor.RunAsync("net.exe", $"start \"{name}\"", CancellationToken.None);
                if (!start.Success && WindowsServiceControl.TryQueryState(name) is not WindowsServiceState.Running)
                {
                    failures.Add(name);
                    LoggerBootstrap.Log.Error($"{name} サービスの再開に失敗: exit={start.ExitCode}");
                }
            }
            catch (Exception ex)
            {
                failures.Add(name);
                LoggerBootstrap.Log.Error($"{name} サービスの再開に失敗しました", ex);
            }
        }

        return failures;
    }
}
