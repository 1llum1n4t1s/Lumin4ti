using Avalonia;
using Lumin4ti.Core.Services;
using Lumin4ti.UI.Services;
using Velopack;

namespace Lumin4ti.UI;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Services.WindowsElevationHelper.TrySetCurrentProcessAppUserModelId();

        // Velopack のブートストラップを最初に走らせる
        // (--veloapp-install / --veloapp-updated 等の internal hook を捌くため、Avalonia 起動・多重起動ガードより前に必須)
        var velopackApp = VelopackApp.Build();
        if (OperatingSystem.IsWindows())
        {
            velopackApp
                .OnAfterInstallFastCallback(_ => WindowsLegacyStartMenuShortcutMigrator.MigrateForCurrentUser())
                .OnAfterUpdateFastCallback(_ => WindowsLegacyStartMenuShortcutMigrator.MigrateForCurrentUser());
        }

        velopackApp.Run();

        // 更新フックが一時的なファイルロック等で移行できなかった場合も、通常起動時に再試行する。
        if (OperatingSystem.IsWindows())
        {
            WindowsLegacyStartMenuShortcutMigrator.MigrateForCurrentUser();
        }

        // HKLM への reg add / dism / regsvr32 を実行するため管理者権限が必要。
        // app.manifest は asInvoker のまま、ここで自己再起動して昇格する (詳細は app.manifest のコメント参照)。
        // SingleInstanceGuard より前に行う: 非昇格プロセスがロックを握ったまま昇格版を起動すると
        // 昇格版が二重起動判定で弾かれてしまうため。
        // デバッガ接続時は自己再起動するとデバッグセッションが切れてしまうため昇格せずそのまま続行する
        // (HKLM 系アクションは失敗するが、UI・HKCU 系のデバッグは可能)。
        if (!System.Diagnostics.Debugger.IsAttached
            && !Services.WindowsElevationHelper.IsRunningAsAdministrator())
        {
            return Services.WindowsElevationHelper.TryRelaunchElevated(args) ? 0 : 1;
        }

        using var singleInstance = new SingleInstanceGuard("Lumin4ti");
        if (!singleInstance.TryAcquire())
        {
            return 1;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
