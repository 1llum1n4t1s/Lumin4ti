using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Lumin4ti.Core.Services;

/// <summary>
/// 画面を操作している利用者 (explorer.exe の所有者) を調べる。
/// HKCU を扱う設定は「このプロセスのユーザー = 対話ユーザー」を前提にしているため、
/// 別の管理者資格で昇格された場合にその前提が崩れていることを検知するために使う。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class InteractiveUserResolver
{
    /// <summary>対話ユーザー名 (DOMAIN\User)。特定できない場合は null。</summary>
    public static string? TryGetInteractiveUserName()
    {
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            using (process)
            {
                if (TryGetProcessUserName(process) is { } userName)
                {
                    return userName;
                }
            }
        }

        return null;
    }

    private static string? TryGetProcessUserName(Process process)
    {
        var token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process.Handle, TokenQuery, out token))
            {
                return null;
            }

            using var identity = new WindowsIdentity(token);
            return identity.Name;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or
                                       System.ComponentModel.Win32Exception or ArgumentException or
                                       System.Security.SecurityException)
        {
            return null;
        }
        finally
        {
            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }
        }
    }

    private const uint TokenQuery = 0x0008;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
