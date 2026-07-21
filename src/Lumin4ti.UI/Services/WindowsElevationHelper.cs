using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Lumin4ti.UI.Services;

/// <summary>
/// app.manifest は asInvoker (Velopack の Setup.exe/Update.exe が内部フックを CreateProcess で
/// 起動するため、requireAdministrator だと ERROR_ELEVATION_REQUIRED で失敗する)。
/// 実際の管理者権限は起動直後にここで自己再起動して取得する (Shisui と同方式)。
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsElevationHelper
{
    private const string AppUserModelId = "velopack.Lumin4ti";

    /// <summary>
    /// Velopack のショートカットと実行プロセスの AUMID を一致させる。
    /// UAC 自己昇格後もウィンドウ生成前に呼び出し、タスクバーの白紙アイコンを防ぐ。
    /// </summary>
    public static void TrySetCurrentProcessAppUserModelId()
    {
        try { _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
        catch { /* シェル連携の失敗だけで起動を止めない */ }
    }

    public static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// ShellExecute + runas で昇格した自分自身を起動する (CreateProcess は昇格プロンプトを出せない)。
    /// UAC で拒否された場合は false を返す。
    /// </summary>
    public static bool TryRelaunchElevated(string[] args)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Win32Exception)
        {
            // ユーザーが UAC プロンプトで「いいえ」を選択した場合 (ERROR_CANCELLED)
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
