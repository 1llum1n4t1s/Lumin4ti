using Avalonia;
using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.UI.Services;
using Velopack;

namespace Lumin4ti.UI;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 製品版では Velopack がショートカットへ設定する AUMID と実行プロセスを一致させる。
        // Debug版まで製品版のAUMIDを名乗ると、Windowsがインストール済みショートカットの
        // アイコン情報を参照し、開発用EXEのタスクバーアイコンが白紙になるため設定しない。
#if !DEBUG
        if (OperatingSystem.IsWindows())
        {
            Services.WindowsElevationHelper.TrySetCurrentProcessAppUserModelId();
        }
#endif

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

            // 旧PerUser版は、管理者実行するバイナリがユーザー書き込み可能な場所にあるため、
            // 通常の自己昇格より先に署名済みPerMachine MSIへ移行する。
            var migrationExitCode = WindowsPerMachineMigration.HandleStartupAsync(args).GetAwaiter().GetResult();
            if (migrationExitCode is not null)
            {
                return migrationExitCode.Value;
            }
        }

        // HKLM への reg add / dism / regsvr32 を実行するため管理者権限が必要。
        // app.manifest は asInvoker のまま、ここで自己再起動して昇格する (詳細は app.manifest のコメント参照)。
        // SingleInstanceGuard より前に行う: 非昇格プロセスがロックを握ったまま昇格版を起動すると
        // 昇格版が二重起動判定で弾かれてしまうため。
        // Debug版もHKLM操作と、Explorerのmedium tokenから安全な子プロセスを生成するため昇格する。
        // デバッガを接続したまま確認する場合は、IDE自体を管理者として起動する。
        if (!Services.WindowsElevationHelper.IsRunningAsAdministrator())
        {
            return Services.WindowsElevationHelper.TryRelaunchElevated(args) ? 0 : 1;
        }

        if (OperatingSystem.IsWindows())
        {
            WindowsLegacyStartMenuShortcutMigrator.RepairInstalledShortcutMetadata();
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
