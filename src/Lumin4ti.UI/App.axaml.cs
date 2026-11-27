using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.Core.Services.Windows.Cleanup.Slim;
using Lumin4ti.UI.Services;
using Lumin4ti.UI.ViewModels;
using Lumin4ti.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Lumin4ti.UI;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LoggerBootstrap.Log.Error("UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LoggerBootstrap.Log.Error("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        LoggerBootstrap.Initialize();

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        // 表示言語を適用する (設定に保存済みならそれを、無ければ OS ロケールから自動判定)。
        var settingsService = Services.GetRequiredService<ISettingsService>();
        var settings = settingsService.Current;
        var localeKey = ResolveLocaleKey(settings.Locale);
        if (!string.Equals(settings.Locale, localeKey, StringComparison.Ordinal))
        {
            settings.Locale = localeKey;
            // 読めなかった既存ファイルは SettingsService 側でも保護するが、ここでも
            // 起動直後の自動正規化を明示的に止め、破損・ロック中の原本を上書きしない。
            if (settingsService.LoadStatus != SettingsLoadStatus.Failed)
            {
                try
                {
                    settingsService.SaveAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    LoggerBootstrap.Log.Error("未対応ロケールの正規化を保存できませんでした", ex);
                }
            }
        }

        SetLocale(localeKey);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // デバッグ画面を開いたままでも、メイン画面の終了・補償完了後に終了する。
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
#if DEBUG
            Services.GetRequiredService<DebuggerService>().Initialize();
#endif
            desktop.MainWindow = new MainWindow(
                Services.GetRequiredService<MaintenanceOperationCoordinator>())
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>(),
            };
#if DEBUG
            desktop.MainWindow.Opened += (_, _) => Services.GetRequiredService<DebuggerService>().Show();
#endif
            desktop.Exit += (_, _) =>
            {
                try
                {
                    Services.GetRequiredService<ISettingsService>().FlushAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    LoggerBootstrap.Log.Error("終了時の設定保存に失敗しました", ex);
                }
                finally
                {
                    (Services as IDisposable)?.Dispose();
                    LoggerBootstrap.Shutdown();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>レジストリクリーナーの削除側と復元側の境界 (同じ世代を共有するため 1 組で作る)。</summary>
    private sealed record RegistryBackends(IRegistryCleanBackend Clean, IRegistryRestoreBackend Restore);

    private static void ConfigureServices(ServiceCollection services)
    {
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ICommandExecutor, ProcessCommandExecutor>();
        services.AddSingleton<ICleanupPreferences, CleanupPreferences>();
        services.AddSingleton<IRebootCacheCleanupScheduler, RebootCacheCleanupService>();
        services.AddSingleton<IDisconnectedDeviceService, WindowsDisconnectedDeviceService>();
        services.AddSingleton<MaintenanceActionCatalog>();
        services.AddSingleton<MaintenanceOperationCoordinator>();
#if DEBUG
        services.AddSingleton<DebuggerService>();
#endif

        services.AddSingleton(sp => new Services.UpdateService(sp.GetRequiredService<ISettingsService>().Current));

        // クリーンアップ (通常・高度・スリム化・設定)。走査・削除は画面の操作でだけ始まり、構築時には何も実行しない。
        services.AddSingleton<CleanupSettingsStore>();
        services.AddSingleton<CleanupScanService>();
        services.AddSingleton<CleanupRunService>();
        services.AddSingleton<AdvancedScanLedger>();
        services.AddSingleton<AdvancedCleanScanner>();
        services.AddSingleton<AdvancedCleaner>();
        services.AddSingleton<SlimService>();

        services.AddSingleton<VersionViewModel>();
        services.AddSingleton<DeviceCleanupViewModel>();
        services.AddSingleton<CommonCleanViewModel>();
        services.AddSingleton<AdvancedCleanViewModel>();
        services.AddSingleton<SlimViewModel>();
        services.AddSingleton<CleanupSettingsViewModel>();

        // レジストリ (削除側と復元側は同じ世代を共有する境界から作る)。構築しても走査・削除・復元は始めない。
        services.AddSingleton(sp =>
        {
            var (clean, restore) = RegistryCleanBackends.CreateDefault(sp.GetRequiredService<ICommandExecutor>());
            return new RegistryBackends(clean, restore);
        });
        services.AddSingleton(sp => new RegistryCleanViewModel(
            sp.GetRequiredService<RegistryBackends>().Clean,
            sp.GetRequiredService<MaintenanceOperationCoordinator>()));
        services.AddSingleton(sp => new RegistryRestoreViewModel(
            sp.GetRequiredService<RegistryBackends>().Restore,
            sp.GetRequiredService<RegistryCleanViewModel>(),
            sp.GetRequiredService<MaintenanceOperationCoordinator>()));
        services.AddSingleton(sp => new CleanupViewModel(
            sp.GetRequiredService<MaintenanceActionCatalog>(),
            sp.GetRequiredService<MaintenanceOperationCoordinator>(),
            sp.GetRequiredService<CommonCleanViewModel>(),
            sp.GetRequiredService<AdvancedCleanViewModel>(),
            sp.GetRequiredService<SlimViewModel>(),
            sp.GetRequiredService<RegistryCleanViewModel>(),
            sp.GetRequiredService<RegistryRestoreViewModel>(),
            sp.GetRequiredService<CleanupSettingsViewModel>()));
        services.AddSingleton<MainWindowViewModel>();
    }
}
