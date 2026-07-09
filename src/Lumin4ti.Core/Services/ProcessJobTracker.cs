using System.Runtime.InteropServices;
using System.Runtime.Versioning;

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
    private static readonly nint JobHandle = CreateKillOnCloseJob();

    public static void Track(nint processHandle)
    {
        if (JobHandle != nint.Zero && processHandle != nint.Zero)
        {
            // 失敗しても致命的でない (孤児化防止はベストエフォート) ため戻り値は無視する
            AssignProcessToJobObject(JobHandle, processHandle);
        }
    }

    private static nint CreateKillOnCloseJob()
    {
        var handle = CreateJobObject(nint.Zero, null);
        if (handle == nint.Zero)
        {
            return nint.Zero;
        }

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
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ptr, (uint)length))
            {
                CloseHandle(handle);
                return nint.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        return handle;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);
}
