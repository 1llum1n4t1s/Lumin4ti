using System.Diagnostics;
using System.Runtime.Versioning;

namespace Lumin4ti.Core.Services.Windows;

/// <summary>
/// エクスプローラー (シェル) を再起動して、シェルキャッシュに依存する変更を反映させる。
/// </summary>
[SupportedOSPlatform("windows")]
public static class ExplorerRestarter
{
    public static void Restart()
    {
        LoggerBootstrap.Log.Info("Explorer を再起動します");
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            try
            {
                process.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 既に終了している / アクセス不可は無視 (シェルは自動再起動する)
            }
            finally
            {
                process.Dispose();
            }
        }

        // シェルとしての explorer は通常自動再起動するが、保険として明示起動する
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
    }
}
