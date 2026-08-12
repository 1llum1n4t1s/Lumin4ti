using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lumin4ti.Core.Services;
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

    /// <summary>ERROR_CANCELLED。UAC プロンプトで「いいえ」が選ばれた場合。</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// Shisui と同じく製品版プロセスへ Velopack の AUMID を設定する。
    /// ショートカット側へ AUMID や明示アイコンは追記せず、埋め込みアイコンの解決は Windows に任せる。
    /// </summary>
    public static void TrySetCurrentProcessAppUserModelId()
    {
        try
        {
            var hresult = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            if (hresult != 0)
            {
                // タスクバーのアイコン・ピン留めが崩れる原因になるため、失敗を残す。
                LoggerBootstrap.Log.Error($"AppUserModelID を設定できませんでした (HRESULT=0x{hresult:X8})");
            }
        }
        catch (Exception ex)
        {
            // シェル連携の失敗だけで起動は止めないが、理由は残す。
            LoggerBootstrap.Log.Error("AppUserModelID の設定に失敗しました", ex);
        }
    }

    public static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// 起動時に自己昇格すべきか。昇格は「自分自身を起動し直して元プロセスは終了する」方式のため、
    /// デバッガが付いた状態でこれを行うとデバッグセッションがその場で切れてしまう
    /// (昇格した子プロセスにはデバッガが付かない)。そのためデバッガ接続中は昇格せずに続行する。
    /// 非昇格のままなので HKLM 系の操作と Explorer トークン経由の実行は権限エラーになる。
    /// そこまで含めて確認したいときは IDE 自体を管理者として起動する (最初から昇格済みで起動される)。
    /// </summary>
    public static bool ShouldRelaunchElevated(bool isElevated, bool isDebuggerAttached) =>
        !isElevated && !isDebuggerAttached;

    /// <summary>
    /// ShellExecute + runas で昇格した自分自身を起動する (CreateProcess は昇格プロンプトを出せない)。
    /// UAC で拒否された場合は false を返す。
    /// </summary>
    public static bool TryRelaunchElevated(string[] args)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            LoggerBootstrap.Log.Error("自分自身の実行ファイルパスを取得できないため昇格できませんでした");
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
        catch (Win32Exception ex)
        {
            // ERROR_CANCELLED (1223) は利用者が UAC で「いいえ」を選んだ想定内の分岐。
            // それ以外はポリシーや環境の問題で、原因が分からないと調べようがないため残す。
            if (ex.NativeErrorCode == ErrorCancelled)
            {
                LoggerBootstrap.Log.Info("UAC の昇格プロンプトで拒否されたため起動を中止しました");
            }
            else
            {
                LoggerBootstrap.Log.Error($"管理者権限での再起動に失敗しました (Win32={ex.NativeErrorCode})", ex);
            }

            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
