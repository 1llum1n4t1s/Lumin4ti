using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>検出した実行中プロセス。<see cref="StartTime"/> は終了時の再照合に使う (FILETIME)。</summary>
internal sealed record RunningProcess(int Pid, string Name, long StartTime);

/// <summary>検出結果。<see cref="UnverifiableCount"/> は、名前とセッションが一致したがハンドルを開けず利用者を確認できなかった数。</summary>
internal sealed record ProcessDetection(IReadOnlyList<RunningProcess> Verified, int UnverifiableCount)
{
    public bool IsRunning => Verified.Count > 0 || UnverifiableCount > 0;
}

internal enum ProcessGuardStatus
{
    /// <summary>対象は残っていない。</summary>
    Clear,

    /// <summary>残っている。その規則だけを Skipped にし、成功と表示しない。</summary>
    Skipped,
}

internal enum ProcessGuardSkipReason
{
    None,

    /// <summary>利用者を確認できないプロセスがある。</summary>
    Unverifiable,

    /// <summary>WM_CLOSE で終わらず、この分類は強制終了の対象外 (browserCache・browserTrace・cookies だけが対象)。</summary>
    ForceNotAllowed,

    /// <summary>強制終了の再確認で同意されなかった。</summary>
    ForceDeclined,

    /// <summary>ハンドルでの再照合 (SID・セッション・名前) が一致しなかった。</summary>
    IdentityMismatch,

    /// <summary>TerminateProcess が拒否・失敗した、または終了を確認できなかった。</summary>
    TerminateFailed,
}

internal sealed record ProcessGuardResult(
    ProcessGuardStatus Status,
    ProcessGuardSkipReason Reason,
    IReadOnlyList<RunningProcess> Remaining)
{
    public static ProcessGuardResult Clear { get; } = new(ProcessGuardStatus.Clear, ProcessGuardSkipReason.None, []);
}

internal readonly record struct ProcessSnapshotEntry(int Pid, string Name, uint SessionId);

/// <summary>開いたプロセスハンドル。保持している間は PID が再利用されない。</summary>
internal interface IProcessLease : IDisposable
{
    int Pid { get; }

    /// <summary>拡張子を除いた実行ファイル名。取得できなければ null。</summary>
    string? ImageName { get; }

    uint SessionId { get; }

    SecurityIdentifier? UserSid { get; }

    /// <summary>作成時刻 (FILETIME)。</summary>
    long StartTime { get; }

    bool HasExited();

    bool WaitForExit(TimeSpan timeout);

    /// <summary>このプロセスの最上位ウィンドウへ WM_CLOSE を送る。送った数を返す。</summary>
    int RequestClose();

    bool Terminate();
}

/// <summary>OS への境界。テストでは差し替え、実プロセスを終了しない。</summary>
internal interface IProcessControl
{
    int CurrentPid { get; }

    uint CurrentSessionId { get; }

    SecurityIdentifier? CurrentUserSid { get; }

    IReadOnlyList<ProcessSnapshotEntry> Snapshot();

    /// <summary>プロセスを開く。終了済み・拒否は null。</summary>
    IProcessLease? Open(int pid);

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>
/// 実行中のアプリを閉じてから掃除するための処理。同一セッション・同一利用者の、規則の <c>processes</c> に一致するものだけを対象にする。
/// 手順は WM_CLOSE (最大 15 秒) → 残れば呼び出し側の再同意の後だけ TerminateProcess。Explorer と自プロセスは対象にしない。
/// </summary>
internal sealed class RunningProcessGuard(IProcessControl control)
{
    internal static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(15);

    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    internal static readonly TimeSpan TerminateWait = TimeSpan.FromSeconds(5);

    private const string ExplorerName = "explorer";

    public RunningProcessGuard() : this(new WindowsProcessControl())
    {
    }

    /// <summary>強制終了を許す分類か。ブラウザーのキャッシュ・痕跡・Cookie の規則だけ。</summary>
    internal static bool AllowsForceTermination(CleanupCategory category) =>
        category is CleanupCategory.BrowserCache or CleanupCategory.BrowserTrace or CleanupCategory.Cookies;

    /// <summary>規則の <c>processes</c> に一致し、同一セッション・同一利用者のプロセスを調べる。何も変更しない。</summary>
    public ProcessDetection Detect(CleanupRule rule)
    {
        var verified = new List<RunningProcess>();
        var unverifiable = 0;
        foreach (var entry in control.Snapshot())
        {
            if (!IsCandidate(rule, entry))
            {
                continue;
            }

            using var lease = control.Open(entry.Pid);
            if (lease is null)
            {
                unverifiable++;
            }
            else if (IsOwnProcess(lease, entry.Name))
            {
                verified.Add(new RunningProcess(entry.Pid, entry.Name, lease.StartTime));
            }

            // 同じセッション内の別利用者のプロセスは対象外 (閉じない)。
        }

        return new ProcessDetection(verified, unverifiable);
    }

    /// <summary>
    /// 検出済みのプロセスを閉じる。<paramref name="confirmForceTermination"/> は WM_CLOSE で残ったときだけ呼ばれ、
    /// 未保存の入力が失われることを示した再確認の結果を返す。呼び出し側は先に「終了する」の同意を得ておくこと。
    /// </summary>
    public async Task<ProcessGuardResult> CloseAsync(
        CleanupRule rule,
        ProcessDetection detection,
        Func<IReadOnlyList<RunningProcess>, CancellationToken, Task<bool>> confirmForceTermination,
        CancellationToken cancellationToken)
    {
        if (detection.UnverifiableCount > 0)
        {
            return new ProcessGuardResult(ProcessGuardStatus.Skipped, ProcessGuardSkipReason.Unverifiable, detection.Verified);
        }

        var leases = new List<(RunningProcess Process, IProcessLease Lease)>();
        try
        {
            var mismatch = OpenAndRequestClose(detection.Verified, leases);
            if (mismatch is not null)
            {
                return Skipped(ProcessGuardSkipReason.IdentityMismatch, [mismatch]);
            }

            await WaitUntilExitedAsync(leases, cancellationToken).ConfigureAwait(false);
            var remaining = RemainingOf(leases);
            if (remaining.Count == 0)
            {
                return ProcessGuardResult.Clear;
            }

            if (!AllowsForceTermination(rule.Category))
            {
                return Skipped(ProcessGuardSkipReason.ForceNotAllowed, remaining);
            }

            var approved = await confirmForceTermination(remaining, cancellationToken).ConfigureAwait(false);
            return approved
                ? TerminateRemaining(leases)
                : Skipped(ProcessGuardSkipReason.ForceDeclined, remaining);
        }
        finally
        {
            foreach (var (_, lease) in leases)
            {
                lease.Dispose();
            }
        }
    }

    private bool IsCandidate(CleanupRule rule, ProcessSnapshotEntry entry) =>
        entry.Pid != control.CurrentPid
        && entry.SessionId == control.CurrentSessionId
        && !IsExplorer(entry.Name)
        && rule.Processes.Contains(entry.Name, StringComparer.OrdinalIgnoreCase);

    private static bool IsExplorer(string? name) =>
        string.Equals(name, ExplorerName, StringComparison.OrdinalIgnoreCase);

    /// <summary>ハンドルから読んだ SID・セッション・名前が、期待する利用者・セッション・名前と一致するか。</summary>
    private bool IsOwnProcess(IProcessLease lease, string expectedName) =>
        lease.SessionId == control.CurrentSessionId
        && UnelevatedCommandExecutor.AreSameUser(lease.UserSid, control.CurrentUserSid)
        && !IsExplorer(lease.ImageName)
        && string.Equals(lease.ImageName, expectedName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// プロセスを全件開き、開始時刻と所有者を再照合する。照合が全件済み、1 件も失敗しなかったときだけ WM_CLOSE を送る
    /// (照合に失敗したプロセスがあれば、他のプロセスにも何も送らない)。照合に失敗したプロセスを返す。
    /// </summary>
    private RunningProcess? OpenAndRequestClose(
        IReadOnlyList<RunningProcess> targets,
        List<(RunningProcess Process, IProcessLease Lease)> leases)
    {
        foreach (var target in targets)
        {
            var lease = control.Open(target.Pid);
            if (lease is null)
            {
                continue;
            }

            if (lease.StartTime != target.StartTime)
            {
                // 検出後に終了し、PID が別のプロセスへ再利用された。元のプロセスは存在しない。
                lease.Dispose();
                continue;
            }

            leases.Add((target, lease));
            if (!IsOwnProcess(lease, target.Name))
            {
                return target;
            }
        }

        foreach (var (_, lease) in leases)
        {
            _ = lease.RequestClose();
        }

        return null;
    }

    private async Task WaitUntilExitedAsync(
        List<(RunningProcess Process, IProcessLease Lease)> leases,
        CancellationToken cancellationToken)
    {
        for (var waited = TimeSpan.Zero; waited < CloseWait && leases.Exists(l => !l.Lease.HasExited()); waited += PollInterval)
        {
            await control.DelayAsync(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static List<RunningProcess> RemainingOf(List<(RunningProcess Process, IProcessLease Lease)> leases) =>
        [.. leases.Where(l => !l.Lease.HasExited()).Select(l => l.Process)];

    private ProcessGuardResult TerminateRemaining(List<(RunningProcess Process, IProcessLease Lease)> leases)
    {
        foreach (var (process, lease) in leases)
        {
            if (lease.HasExited())
            {
                continue;
            }

            // 同意を得るまでに時間が経っているため、終了の直前にもう一度ハンドルで再照合する。
            if (lease.StartTime != process.StartTime || !IsOwnProcess(lease, process.Name))
            {
                return Skipped(ProcessGuardSkipReason.IdentityMismatch, [process]);
            }

            if (!lease.Terminate() || !lease.WaitForExit(TerminateWait))
            {
                return Skipped(ProcessGuardSkipReason.TerminateFailed, RemainingOf(leases));
            }
        }

        return ProcessGuardResult.Clear;
    }

    private static ProcessGuardResult Skipped(ProcessGuardSkipReason reason, IReadOnlyList<RunningProcess> remaining) =>
        new(ProcessGuardStatus.Skipped, reason, remaining);
}

/// <summary>実 OS への境界。単体テストでは使わない。</summary>
internal sealed class WindowsProcessControl : IProcessControl
{
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint TokenQuery = 0x0008;
    private const uint WmClose = 0x0010;
    private const uint WaitObject0 = 0;

    public int CurrentPid => Environment.ProcessId;

    public uint CurrentSessionId { get; } = ProcessIdToSessionId((uint)Environment.ProcessId, out var id) ? id : uint.MaxValue;

    public SecurityIdentifier? CurrentUserSid { get; } = TryGetCurrentUserSid();

    public IReadOnlyList<ProcessSnapshotEntry> Snapshot()
    {
        var entries = new List<ProcessSnapshotEntry>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    entries.Add(new ProcessSnapshotEntry(process.Id, process.ProcessName, (uint)process.SessionId));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // 列挙中に終了したプロセスは無視する。
                }
            }
        }

        return entries;
    }

    public IProcessLease? Open(int pid)
    {
        var handle = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation | Synchronize, false, (uint)pid);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        return ProcessLease.TryCreate(pid, handle);
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);

    /// <summary>終了権限を要求せず、プロセスの利用者 SID だけを読む (対話利用者の判定用)。</summary>
    internal static SecurityIdentifier? TryQueryUserSid(int pid)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        return handle.IsInvalid ? null : ProcessLease.QueryUserSid(handle);
    }

    private static SecurityIdentifier? TryGetCurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User;
    }

    private sealed class ProcessLease : IProcessLease
    {
        private readonly SafeProcessHandle _handle;

        private ProcessLease(int pid, SafeProcessHandle handle, string? imageName, uint sessionId, SecurityIdentifier? userSid, long startTime)
        {
            Pid = pid;
            _handle = handle;
            ImageName = imageName;
            SessionId = sessionId;
            UserSid = userSid;
            StartTime = startTime;
        }

        public int Pid { get; }

        public string? ImageName { get; }

        public uint SessionId { get; }

        public SecurityIdentifier? UserSid { get; }

        public long StartTime { get; }

        public static ProcessLease? TryCreate(int pid, SafeProcessHandle handle)
        {
            if (!GetProcessTimes(handle, out var creation, out _, out _, out _)
                || !ProcessIdToSessionId((uint)pid, out var session))
            {
                handle.Dispose();
                return null;
            }

            return new ProcessLease(pid, handle, QueryImageName(handle), session, QueryUserSid(handle), creation);
        }

        public bool HasExited() => WaitForSingleObject(_handle, 0) != WaitTimeoutResult;

        public bool WaitForExit(TimeSpan timeout) =>
            WaitForSingleObject(_handle, (uint)timeout.TotalMilliseconds) == WaitObject0;

        public int RequestClose()
        {
            var posted = 0;
            _ = EnumWindows(
                (window, _) =>
                {
                    if (GetWindowThreadProcessId(window, out var owner) != 0
                        && owner == (uint)Pid
                        && PostMessage(window, WmClose, 0, 0))
                    {
                        posted++;
                    }

                    return true;
                },
                0);
            return posted;
        }

        public bool Terminate() => TerminateProcess(_handle, 1);

        public void Dispose() => _handle.Dispose();

        private const uint WaitTimeoutResult = 0x102;

        private static string? QueryImageName(SafeProcessHandle handle)
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size)
                ? Path.GetFileNameWithoutExtension(new string(buffer, 0, (int)size))
                : null;
        }

        internal static SecurityIdentifier? QueryUserSid(SafeProcessHandle handle)
        {
            if (!OpenProcessToken(handle, TokenQuery, out var token))
            {
                return null;
            }

            using (token)
            {
                try
                {
                    using var identity = new WindowsIdentity(token.DangerousGetHandle());
                    return identity.User;
                }
                catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException)
                {
                    return null;
                }
            }
        }
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, [Out] char[] name, ref uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
