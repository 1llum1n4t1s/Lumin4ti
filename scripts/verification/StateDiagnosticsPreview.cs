using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.Core.Services.Windows.Actions;
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
        private DebuggerService? _debugger;

        public override void OnFrameworkInitializationCompleted()
        {
            // 本番 App の初期化は呼ばない。DI・設定保存・OS 操作を持ち込まない。
            SetLocale("ja_JP");
            _debugger = new DebuggerService(provider =>
            {
                var output = Environment.GetEnvironmentVariable("LUMIN4TI_PREVIEW_LOG");
                var factory = LoggerFactory.Create(builder =>
                {
                    if (!string.IsNullOrEmpty(output)) builder.AddSuperLightFile(output);
                });
                LogManager.Configure(factory);
                var subscription = Lumin4ti.Core.Services.LoggerBootstrap.AttachProvider(factory, provider);
                factory.CreateLogger("Lumin4ti.Preview").LogInformation("CRDebugger の模擬画面接続を確認しました。");
                return subscription;
            });
            _debugger.Initialize();
            var catalog = new MaintenanceActionCatalog(new IMaintenanceItem[]
            {
                new PreviewToggle("vbs-hvci-off", "VBS / HVCI を無効化", false, defaultState: false),
                new PreviewBatchSetting(),
                new PreviewCheckListAction(),
                new PreviewCheckListAction(startEmpty: true),
                new PreviewCheckListAction(failSave: true),
                new FileCleanupAction("preview-real-cleanup", "削除一覧の案内（メモリ内）", "対象は空配列です。OS操作は行いません。", () => []),
                new PreviewShellCacheDescription(),
                new DefenderSignatureUpdateAction(
                    new MockExecutor((_, _, _) => new CommandExecutionResult(true, "preview", 0, "No updates needed", "")),
                    () => @"C:\preview\MpCmdRun.exe"),
                new PreviewToggle("preview-custom-vbs", "VBS カスタム / 一部適用（メモリ内）", false,
                    defaultState: false, customState: true),
                new PreviewToggle("mmagent-memory-compression", "メモリ圧縮", true, defaultState: true, applyDelayMs: 6000),
                new PreviewToggle("preview-parent", "親設定（メモリ内）", true, defaultState: false),
                new PreviewToggle("preview-child", "子設定（メモリ内）", false, defaultState: true, parentId: "preview-parent"),
                new PreviewChoice(),
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
            var unreadableSettings = new Button { Content = "設定読込失敗（模擬）" };
            unreadableSettings.Click += (_, _) => ShowSettingsPreview(catalog, new MemorySettings { LoadStatus = SettingsLoadStatus.Failed });
            controls.Children.Add(unreadableSettings);
            var unwritableSettings = new Button { Content = "設定保存失敗（模擬）" };
            unwritableSettings.Click += (_, _) => ShowSettingsPreview(catalog, new MemorySettings { FailSave = true });
            controls.Children.Add(unwritableSettings);
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
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.MainWindow = window;
                desktop.Exit += (_, _) =>
                {
                    _debugger.Dispose();
                    LogManager.Shutdown();
                };
            }
        }

        private void ShowSettingsPreview(MaintenanceActionCatalog catalog, MemorySettings settings)
        {
            var coordinator = new MaintenanceOperationCoordinator(() => new PreviewLease());
            var version = new VersionViewModel(new UpdateService(() => null), settings, coordinator, _debugger);
            var devices = new DeviceCleanupViewModel(new MockDevices { FailEnumeration = false }, coordinator);
            new MainWindow(coordinator)
            {
                Title = "Lumin4ti — Settings preview (fake state only)",
                DataContext = new MainWindowViewModel(catalog, devices, version, coordinator),
            }.Show();
        }
    }

    private sealed class PreviewShellCacheDescription : IMaintenanceAction
    {
        private readonly FileCleanupAction _metadata = FileCleanupGroups.CreateShellCache(
            new MockExecutor((_, _, _) => throw new InvalidOperationException("metadata only")),
            rebootScheduler: new PreviewRebootScheduler());
        public string Id => _metadata.Id;
        public string Label => _metadata.Label;
        public string Description => _metadata.Description;
        public CommandCategory Category => CommandCategory.Cleanup;
        public bool RequiresReboot => true;
        public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default) =>
            Task.FromResult(MaintenanceActionResult.Ok("説明表示専用の模擬操作です。OS操作・予約は行っていません。"));
    }

    private sealed class PreviewRebootScheduler : IRebootCacheCleanupScheduler
    {
        public Task<RebootCleanupQueueResult> QueueAsync(IReadOnlyList<CleanupTarget> selected,
            IReadOnlyList<CleanupTarget> blocked, CancellationToken ct) =>
            Task.FromResult(new RebootCleanupQueueResult(true, 0, "memory only"));
    }

    private sealed class PreviewCheckListAction(bool startEmpty = false, bool failSave = false) : IMaintenanceAction, IMaintenanceCheckList
    {
        private bool _selected = true;
        private int _queries;
        public string Id => failSave ? "preview-save-failure" : startEmpty ? "preview-empty-checklist" : "preview-checklist";
        public string Label => failSave ? "選択保存失敗（メモリ内）" : startEmpty ? "空の一覧の再確認（メモリ内）" : "実行中のチェック操作（メモリ内）";
        public string Description => "実行中の15秒間はチェックを変更できず、中断後は操作できます。OS変更はありません。";
        public string CheckListCaption => "模擬対象";
        public CommandCategory Category => CommandCategory.Performance;
        public bool RequiresReboot => false;
        public IReadOnlyList<MaintenanceCheckListEntry> GetCheckListEntries() =>
            startEmpty && ++_queries <= 2 ? [] : [new("preview-target", "模擬キャッシュ", _selected)];
        public async Task SetCheckListEntrySelectedAsync(string value, bool selected, CancellationToken ct = default)
        {
            await Task.Delay(3000, ct);
            _selected = selected;
            if (failSave) throw new IOException("preview selection save failure");
        }
        public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default) => ExecuteAsync(null, ct);

        public async Task<MaintenanceActionResult> ExecuteAsync(IProgress<string>? progress, CancellationToken ct = default)
        {
            progress?.Report("OPOP_CANARY: メモリ内の模擬処理を実行しています。OS操作は行いません。");
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                progress?.Report("OPOP_CANARY: 中断後の後処理を確認しています。");
                await Task.Delay(3000, CancellationToken.None);
                throw;
            }
            return MaintenanceActionResult.Ok("OPOP_CANARY simulated cleanup completed");
        }
    }

    private sealed class PreviewBatchSetting : IMaintenanceBatchSetting
    {
        private int _runs;
        public string Id => "uwp-background-off";
        public string Label => "UWP バックグラウンド実行";
        public string Description => "一括OFF／一括ONの繰り返し適用を確認します。変更はメモリ内だけです。";
        public CommandCategory Category => CommandCategory.Performance;
        public bool RequiresReboot => false;
        public bool SupportsCancellation => true;

        public async Task<MaintenanceActionResult> ApplyAsync(bool enabled, CancellationToken ct = default)
        {
            await Task.Delay(300, ct);
            return MaintenanceActionResult.Ok($"  - 一括{(enabled ? "ON" : "OFF")}: メモリ内への実行 {++_runs} 回目");
        }
    }

    private sealed class PreviewToggle(
        string id, string label, bool? state,
        MaintenanceStateDiagnostic? diagnostic = null, string? resourceId = null,
        bool? defaultState = null, string? parentId = null, bool customState = false,
        int applyDelayMs = 0) : IMaintenanceToggle
    {
        private bool? _state = state;
        private bool _customState = customState;
        public string Id => id;
        public string Label => label;
        public string Description => "メモリ内の模擬状態を表示します。";
        public string LabelKey => $"Action.{resourceId ?? id}.Label";
        public string DescriptionKey => $"Action.{resourceId ?? id}.Desc";
        public CommandCategory Category => CommandCategory.Performance;
        public bool RequiresReboot => false;
        public bool? DefaultState => defaultState;
        public bool IsCustomState => _customState;
        public string? ParentId => parentId;
        public MaintenanceStateDiagnostic? StateDiagnostic => diagnostic;
        public Task<bool?> GetStateAsync(CancellationToken ct = default) => Task.FromResult(_state);

        public async Task<MaintenanceActionResult> SetStateAsync(bool on, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (applyDelayMs > 0) await Task.Delay(applyDelayMs, ct);
            _state = on;
            _customState = false;
            return MaintenanceActionResult.Ok();
        }
    }

    private sealed class PreviewChoice : IMaintenanceChoice
    {
        private string _selected = "512";
        public string Id => "preview-choice";
        public string Label => "記録ファイル数（メモリ内）";
        public string Description => "選択肢と既定値の表示確認用。変更はメモリ内だけに保持します。";
        public CommandCategory Category => CommandCategory.Performance;
        public bool RequiresReboot => false;
        public IReadOnlyList<MaintenanceChoiceOption> Options { get; } =
            [new("256", "256"), new("512", "512", IsDefault: true), new("1024", "1024")];
        public Task<string?> GetSelectedValueAsync(CancellationToken ct = default) => Task.FromResult<string?>(_selected);
        public Task<MaintenanceActionResult> SetSelectedValueAsync(string value, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _selected = value;
            return Task.FromResult(MaintenanceActionResult.Ok());
        }
    }

    private sealed class PreviewLease : IDisposable
    {
        public void Dispose() { }
    }
}
