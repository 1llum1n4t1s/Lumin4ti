using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.UI;
using Lumin4ti.UI.Services;
using Lumin4ti.UI.ViewModels;
using Lumin4ti.UI.Views;
using Microsoft.Extensions.Logging;
using SuperLightLogger;

namespace Lumin4ti.Verification;

/// <summary>製品テンプレートの表示確認専用。状態はメモリ内だけで保持する。</summary>
internal static class StateDiagnosticsPreview
{
    public static int Run()
    {
        // 製品 VM のログ呼出しにもファイル出力先を与えない。
        LogManager.Configure(builder => builder.ClearProviders());
        return AppBuilder.Configure<PreviewApp>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime([]);
    }

    public sealed class PreviewApp : App
    {
        public override void OnFrameworkInitializationCompleted()
        {
            // 本番 App の初期化は呼ばない。DI・設定保存・OS 操作を持ち込まない。
            SetLocale("ja_JP");
            var catalog = new MaintenanceActionCatalog(new IMaintenanceItem[]
            {
                new PreviewToggle("vbs-hvci-off", "VBS / HVCI を無効化", false),
                new PreviewToggle("uwp-background-off", "UWP バックグラウンド実行を一括オフ / Windows 既定", false),
                new PreviewToggle("recall-off", "Recall を無効化", null,
                    new MaintenanceStateDiagnostic("Toggle.RecallStateMissing", "Recall の状態が返されませんでした。")),
                new PreviewToggle("scheduled-temp-cleanup", "サインイン時に一時ファイルを掃除", null,
                    new MaintenanceStateDiagnostic("Toggle.ScheduledTaskOwnershipUnknown",
                        "登録済みタスクの所有者を確認できないため操作できません。")),
            });
            var coordinator = new MaintenanceOperationCoordinator(() => new PreviewLease());
            var viewModel = new CommandCategoryViewModel(
                catalog, coordinator, CommandCategory.Performance, "パフォーマンス", "状態診断の表示確認");
            var category = new CommandCategoryView { DataContext = viewModel };
            var controls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(8),
            };
            var japanese = new Button { Content = "日本語" };
            japanese.Click += (_, _) => SetLocale("ja_JP");
            var english = new Button { Content = "English" };
            english.Click += (_, _) => SetLocale("en_US");
            var reload = new Button { Content = "状態再読込 / Reload" };
            reload.Click += async (_, _) =>
            {
                reload.IsEnabled = false;
                try
                {
                    await viewModel.LoadToggleStatesAsync();
                }
                finally
                {
                    reload.IsEnabled = true;
                }
            };
            controls.Children.Add(japanese);
            controls.Children.Add(english);
            controls.Children.Add(reload);
            var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(12) };
            content.Children.Add(controls);
            Grid.SetRow(category, 1);
            content.Children.Add(category);
            var window = new Window
            {
                Title = "Lumin4ti — State diagnostics preview (fake state only)",
                Width = 1000,
                Height = 820,
                Content = content,
            };
            window.Opened += async (_, _) => await viewModel.LoadToggleStatesAsync();
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = window;
            }
        }
    }

    private sealed class PreviewToggle(
        string id, string label, bool? state,
        MaintenanceStateDiagnostic? diagnostic = null, string? resourceId = null) : IMaintenanceToggle
    {
        private bool? _state = state;
        public string Id => id;
        public string Label => label;
        public string Description => "メモリ内の模擬状態を表示します。";
        public string LabelKey => $"Action.{resourceId ?? id}.Label";
        public string DescriptionKey => $"Action.{resourceId ?? id}.Desc";
        public CommandCategory Category => CommandCategory.Performance;
        public bool RequiresReboot => false;
        public bool SupportsExplicitOff => id is "vbs-hvci-off" or "uwp-background-off";
        public MaintenanceStateDiagnostic? StateDiagnostic => diagnostic;
        public Task<bool?> GetStateAsync(CancellationToken ct = default) => Task.FromResult(_state);

        public Task<MaintenanceActionResult> SetStateAsync(bool on, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _state = on;
            return Task.FromResult(MaintenanceActionResult.Ok());
        }
    }

    private sealed class PreviewLease : IDisposable
    {
        public void Dispose() { }
    }
}
