using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Lumin4ti.Core.Services;

/// <summary>
/// 起動した子プロセスを Windows Job Object (KILL_ON_JOB_CLOSE) に紐付ける。
/// アプリ (親) プロセスが終了すると Job ハンドルが閉じ、OS が配下の子プロセスを自動的に
/// 終了させる。これにより、長時間コマンド (dism/defrag 等) の実行中にウィンドウを閉じても
/// 子プロセスが孤児化して DISM グローバルロックを握ったまま残ることを防ぐ。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProcessJobTracker
{
    // WindowsApps の winget は生成時点で別の Job に入ることがある。
    // 一度ほかの子を登録した Job を再利用すると、異なる Job 階層との競合で登録が拒否される。
    // 子ごとに独立した Job を持ち、アプリ終了までハンドルを保持する。
    private static readonly ConcurrentBag<SafeFileHandle> JobHandles = new();

    internal static bool ContainsProcess(nint processHandle) =>
        JobHandles.Any(job => IsProcessInJob(processHandle, job.DangerousGetHandle(), out var belongs) && belongs);

    // Explorer broker は生成時のスレッドを取得できないため、既存プロセスの登録だけを行う。
    // 生成を制御できる経路には使わず、TrackAndResume で実行前に登録する。
    public static void TrackExistingBrokerProcess(nint processHandle)
    {
        if (processHandle == nint.Zero)
        {
            return;
        }

        var job = CreateKillOnCloseJob();
        if (job is null)
        {
            LoggerBootstrap.Log.Error("Explorer broker の終了を管理する Job を作成できませんでした");
            return;
        }

        if (!AssignProcessToJobObject(job.DangerousGetHandle(), processHandle))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            LoggerBootstrap.Log.Error(DescribeAssignmentFailure(error, processHandle));
            return;
        }

        JobHandles.Add(job);
    }

    /// <summary>CREATE_SUSPENDED で生成したプロセスだけを登録し、成功後に再開する。</summary>
    public static void TrackAndResume(nint processHandle, nint threadHandle)
    {
        var job = CreateKillOnCloseJob();
        if (job is null)
            throw new InvalidOperationException("子プロセスの終了を管理する Job を作成できませんでした");
        if (!AssignProcessToJobObject(job.DangerousGetHandle(), processHandle))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error, DescribeAssignmentFailure(error, processHandle));
        }
        JobHandles.Add(job);
        if (ResumeThread(threadHandle) == uint.MaxValue)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"子プロセスを再開できませんでした: Win32 {error} ({new Win32Exception(error).Message})");
        }
    }

    private static string DescribeAssignmentFailure(int error, nint processHandle) =>
        $"子プロセスの Job 登録に失敗しました: Win32 {error} ({new Win32Exception(error).Message}); " +
        $"親Job={DescribeJobMembership(GetCurrentProcess())}; 子Job={DescribeJobMembership(processHandle)}";

    private static string DescribeJobMembership(nint processHandle) =>
        IsProcessInJob(processHandle, nint.Zero, out var belongs)
            ? belongs ? "所属" : "未所属"
            : $"照会失敗 Win32 {Marshal.GetLastWin32Error()}";

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(nint threadHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(nint processHandle, nint jobHandle, [MarshalAs(UnmanagedType.Bool)] out bool belongs);

    private static SafeFileHandle? CreateKillOnCloseJob()
    {
        var handle = CreateJobObject(nint.Zero, null);
        if (handle == nint.Zero)
        {
            return null;
        }
        var job = new SafeFileHandle(handle, ownsHandle: true);
        try
        {
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                },
            };

            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, ptr, fDeleteOld: false);
                if (!SetInformationJobObject(job.DangerousGetHandle(), JobObjectExtendedLimitInformation, ptr, (uint)length))
                {
                    job.Dispose();
                    return null;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObject(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint hJob, int jobObjectInfoClass, nint lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);
}
