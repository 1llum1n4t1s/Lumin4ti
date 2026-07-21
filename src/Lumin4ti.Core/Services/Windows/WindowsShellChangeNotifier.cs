using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lumin4ti.Core.Services.Windows;

/// <summary>スタートメニューの変更をWindows Shellへ通知する。</summary>
[SupportedOSPlatform("windows")]
public static class WindowsShellChangeNotifier
{
    private const int ShellChangeNotifyUpdateDirectory = 0x00001000;
    private const uint ShellChangeNotifyPathUnicode = 0x0005;
    private const uint ShellChangeNotifyFlushNoWait = 0x3000;
    private const uint ShellChangeNotifyRecursive = 0x10000;

    public static void RefreshStartMenu(string programsDirectory)
    {
        if (string.IsNullOrWhiteSpace(programsDirectory) || !Directory.Exists(programsDirectory))
        {
            return;
        }

        var pathPointer = Marshal.StringToHGlobalUni(programsDirectory);
        try
        {
            SHChangeNotify(
                ShellChangeNotifyUpdateDirectory,
                ShellChangeNotifyPathUnicode | ShellChangeNotifyFlushNoWait | ShellChangeNotifyRecursive,
                pathPointer,
                IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(pathPointer);
        }
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
