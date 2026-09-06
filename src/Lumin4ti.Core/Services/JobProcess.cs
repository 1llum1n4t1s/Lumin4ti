using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Lumin4ti.Core.Services;

/// <summary>標準入出力を接続し、停止状態で Job に登録してから実行するプロセス。</summary>
internal sealed class JobProcess : IDisposable
{
    private readonly AnonymousPipeServerStream _input = null!;
    private readonly AnonymousPipeServerStream _output = null!;
    private readonly AnonymousPipeServerStream _error = null!;
    private Process? _process;

    private JobProcess()
    {
        try
        {
            _input = new(PipeDirection.Out, HandleInheritability.Inheritable);
            _output = new(PipeDirection.In, HandleInheritability.Inheritable);
            _error = new(PipeDirection.In, HandleInheritability.Inheritable);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public Process Process => _process!;
    public Stream StandardOutput => _output;
    public Stream StandardError => _error;

    public static JobProcess Start(string applicationPath, string arguments)
    {
        var child = new JobProcess();
        try
        {
            child.StartCore(applicationPath, arguments);
            return child;
        }
        catch
        {
            child.Dispose();
            throw;
        }
    }

    private void StartCore(string applicationPath, string arguments)
    {
        // ハンドル継承を標準入出力だけに限定し、他の操作のハンドルを子へ渡さない。
        var handles = new[]
        {
            _input.ClientSafePipeHandle.DangerousGetHandle(),
            _output.ClientSafePipeHandle.DangerousGetHandle(),
            _error.ClientSafePipeHandle.DangerousGetHandle(),
        };
        nuint size = 0;
        _ = InitializeProcThreadAttributeList(0, 1, 0, ref size);
        var attributes = Marshal.AllocHGlobal(checked((nint)size));
        nint handleList = 0;
        var initialized = false;
        try
        {
            handleList = Marshal.AllocHGlobal(handles.Length * nint.Size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            initialized = true;
            Marshal.Copy(handles, 0, handleList, handles.Length);
            if (!UpdateProcThreadAttribute(attributes, 0, 0x00020002, handleList,
                    (nuint)(handles.Length * nint.Size), 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = (uint)Marshal.SizeOf<StartupInfoEx>(),
                    Flags = 0x00000100, // STARTF_USESTDHANDLES
                    StandardInput = handles[0],
                    StandardOutput = handles[1],
                    StandardError = handles[2],
                },
                AttributeList = attributes,
            };
            var commandLine = new StringBuilder($"\"{applicationPath}\" {arguments}");
            const uint flags = 0x08000000 | 0x00080000 | 0x00000004; // NO_WINDOW / EXTENDED_STARTUPINFO / SUSPENDED
            if (!CreateProcessW(applicationPath, commandLine, 0, 0, true, flags, 0,
                    Environment.SystemDirectory, ref startup, out var info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            using var processHandle = new SafeProcessHandle(info.ProcessHandle, ownsHandle: true);
            using var threadHandle = new SafeFileHandle(info.ThreadHandle, ownsHandle: true);
            try
            {
                _process = Process.GetProcessById((int)info.ProcessId);
                // 再開前にハンドルを保持し、即時終了するコマンドの終了コードも回収する。
                _ = _process.Handle;
                _input.DisposeLocalCopyOfClientHandle();
                _output.DisposeLocalCopyOfClientHandle();
                _error.DisposeLocalCopyOfClientHandle();
                _input.Dispose(); // 対話入力には EOF を返す。
                ProcessJobTracker.TrackAndResume(processHandle.DangerousGetHandle(), threadHandle.DangerousGetHandle());
            }
            catch
            {
                // 登録・準備失敗で停止中のプロセスを残さない。
                _ = TerminateProcess(processHandle, 1);
                throw;
            }
        }
        finally
        {
            if (initialized)
            {
                DeleteProcThreadAttributeList(attributes);
            }
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(handleList);
        }
    }

    public void Dispose()
    {
        _input?.Dispose();
        _output?.Dispose();
        _error?.Dispose();
        _process?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Size;
        public nint Reserved2, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint ProcessHandle, ThreadHandle;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnedSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine, nint processAttributes,
        nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, nint environment,
        string workingDirectory, ref StartupInfoEx startup, out ProcessInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}
