using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows;
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
        var settings = Services.GetRequiredService<ISettingsService>().Current;
        var localeKey = string.IsNullOrWhiteSpace(settings.Locale) ? DetectDefaultLocale() : settings.Locale;
        SetLocale(localeKey);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>(),
            };
            desktop.Exit += (_, _) => LoggerBootstrap.Shutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServices(ServiceCollection services)
    {
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ICommandExecutor, ProcessCommandExecutor>();
        services.AddSingleton<MaintenanceActionCatalog>();

        services.AddSingleton(sp => new Services.UpdateService(sp.GetRequiredService<ISettingsService>().Current));

        services.AddSingleton<VersionViewModel>();
        services.AddSingleton<MainWindowViewModel>();
    }
}
