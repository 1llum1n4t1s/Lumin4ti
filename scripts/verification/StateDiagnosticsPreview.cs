using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Layout;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.Core.Services.Windows.Actions;
using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;
using Lumin4ti.Core.Services.Windows.Cleanup.Slim;
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
        var builder = AppBuilder.Configure<PreviewApp>();
        var noWindows = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PreviewCapture.EnvName))
                        || Environment.GetEnvironmentVariable("LUMIN4TI_PREVIEW_SELFCHECK") == "1";
        // 書き出しと自己検査は OS の窓・ポップアップ・タスクバー項目を一切作らない (Avalonia.Headless + Skia 描画)。
        // 手で見る実窓のプレビュー (環境変数なし) だけが UsePlatformDetect の実窓を使う。
        return (noWindows
                ? builder.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                : builder.UsePlatformDetect())
            .StartWithClassicDesktopLifetime([]);
    }

    public sealed class PreviewApp : App
    {
#if DEBUG
        private DebuggerService? _debugger;
#endif

        public override void OnFrameworkInitializationCompleted()
        {
            // 本番 App の初期化は呼ばない。DI・設定保存・OS 操作を持ち込まない。
            SetLocale("ja_JP");
#if DEBUG
            // 書き出し・自己検査 (窓を作らない方式) ではデバッガーを初期化しない。
            var headlessRun = Environment.GetEnvironmentVariable(PreviewCapture.EnvName) is { Length: > 0 }
                              || Environment.GetEnvironmentVariable("LUMIN4TI_PREVIEW_SELFCHECK") == "1";
            if (!headlessRun) _debugger = new DebuggerService(provider =>
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
            _debugger?.Initialize();
#endif
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
            var german = new Button { Content = "Deutsch" };
            german.Click += (_, _) => SetLocale("de_DE");
            controls.Children.Add(german);
            var cleanup = new Button { Content = "クリーンアップ画面（偽データ）" };
            cleanup.Click += (_, _) => OpenCleanupWindow(catalog);
            controls.Children.Add(cleanup);
            var autoOpen = Environment.GetEnvironmentVariable("LUMIN4TI_PREVIEW_OPEN");
            var selfCheck = Environment.GetEnvironmentVariable("LUMIN4TI_PREVIEW_SELFCHECK") == "1";
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
            if (Environment.GetEnvironmentVariable(PreviewCapture.EnvName) is { Length: > 0 } || selfCheck)
            {
                // 書き出し・自己検査では利用者の画面の前面に出さない。
                PreviewCapture.MakeBackground(window);
            }

            window.Opened += async (_, _) => await viewModel.LoadToggleStatesAsync();
            var captureDir = Environment.GetEnvironmentVariable(PreviewCapture.EnvName);
            var capturing = !string.IsNullOrWhiteSpace(captureDir);
#if DEBUG
            // デバッガー窓は前面に出て活性になるため、書き出し・自己検査では開かない。
            if (!capturing && !selfCheck) window.Opened += (_, _) => _debugger?.Show();
#endif
            if (capturing)
            {
                // 書き出しモード: クリーンアップ窓の各状態を PNG にして自動で終了する。
                window.Opened += (_, _) => _ = RunCaptureAsync(catalog, captureDir!);
            }
            else if (string.Equals(autoOpen, "cleanup", StringComparison.OrdinalIgnoreCase) || selfCheck)
            {
                window.Opened += (_, _) => OpenCleanupWindow(catalog, selfCheck);
            }

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.MainWindow = window;
                desktop.Exit += (_, _) =>
                {
#if DEBUG
                    _debugger?.Dispose();
#endif
                    LogManager.Shutdown();
                    // 合成フォルダーは終了時に消す (自分が作った名前・場所のときだけ)。
                    _sandbox.Dispose();
                };
            }
        }

        private readonly PreviewSandbox _sandbox = PreviewSandbox.Create();
        private readonly PreviewToggles _toggles = new();

        /// <summary>本物の <see cref="CleanupView"/> と <see cref="CleanupViewModel"/> を、偽のサービスだけで開く。</summary>
        private void OpenCleanupWindow(MaintenanceActionCatalog catalog, bool selfCheck = false)
        {
            var coordinator = new MaintenanceOperationCoordinator(() => new PreviewLease());
            var viewModel = PreviewCleanupFactory.Create(catalog, new MemorySettings(), coordinator, _sandbox, _toggles, background: selfCheck);
            var view = new CleanupView { DataContext = viewModel };

            // MainWindow の処理中オーバーレイの代わり (削除・実行の lease の間だけ表示する)。
            var overlayLabel = new TextBlock { FontSize = 16, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            var overlayDetail = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxLines = 6 };
            var overlayCancel = new Button { Content = "中断 / Cancel", HorizontalAlignment = HorizontalAlignment.Center };
            overlayCancel.Click += (_, _) =>
                viewModel.Operations.FirstOrDefault(o => o.IsRunning)?.CancelCommand.Execute(null);
            var overlay = new Border
            {
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0xB0, 0, 0, 0)),
                IsVisible = false,
                Child = new StackPanel
                {
                    Spacing = 8,
                    MaxWidth = 520,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children = { overlayLabel, overlayDetail, overlayCancel },
                },
            };
            void Refresh()
            {
                var state = coordinator.ProcessingState;
                var running = viewModel.Operations.FirstOrDefault(o => o.IsRunning);
                overlay.IsVisible = state.IsProcessing;
                view.IsEnabled = !state.IsProcessing;
                overlayLabel.Text = running?.Label ?? string.Empty;
                overlayDetail.Text = running?.ProgressText ?? string.Empty;
                overlayCancel.IsEnabled = state.CanCancel && running is { CanCancel: true };
            }

            void Post() => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
            coordinator.StateChanged += (_, _) => Post();
            foreach (var operation in viewModel.Operations) operation.PropertyChanged += (_, _) => Post();

            var window = new Window
            {
                Title = "Lumin4ti — Cleanup preview (fake data only)",
                Width = 880,
                Height = 700,
            };
            var bar = new WrapPanel { Margin = new Thickness(8, 4) };
            void AddButton(string text, Action action)
            {
                var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 4) };
                button.Click += (_, _) => action();
                bar.Children.Add(button);
            }

            void Resize(double width, double height)
            {
                window.Width = width;
                window.Height = height + 40 + bar.Bounds.Height;
            }

            AddButton("760×500", () => Resize(760, 500));
            AddButton("880×620", () => Resize(880, 620));
            AddButton("日本語", () => SetLocale("ja_JP"));
            AddButton("English", () => SetLocale("en_US"));
            AddButton("Deutsch", () => SetLocale("de_DE"));
            AddButton("ライト", () => RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light);
            AddButton("ダーク", () => RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark);
            AddButton("HKCU 走査可/不可", () => _toggles.HkcuAvailable = !_toggles.HkcuAvailable);
            AddButton("復元ポイント: 作成可→保護無効→失敗", () =>
                _toggles.RestoreMode = (PreviewRestoreMode)(((int)_toggles.RestoreMode + 1) % 3));

            var body = new Grid { Children = { view, overlay } };
            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
            root.Children.Add(body);
            window.Content = root;
            if (selfCheck) PreviewCapture.MakeBackground(window);
            window.Show();
            if (selfCheck) _ = RunSelfCheckAsync(catalog, viewModel, window);
        }

        private async Task RunCaptureAsync(MaintenanceActionCatalog catalog, string dir)
        {
            // 想定外のダイアログなどで止まっても、一定時間で報告を残して終了する。
            using var watchdog = new CancellationTokenSource();
            _ = Task.Delay(TimeSpan.FromMinutes(12), watchdog.Token).ContinueWith(
                t =>
                {
                    if (t.IsCanceled) return;
                    Console.Error.WriteLine("PREVIEW capture watchdog: 12 分で終わらなかったため強制終了します");
                    _sandbox.Dispose();
                    Environment.Exit(2);
                },
                TaskScheduler.Default);
            try
            {
                var coordinator = new MaintenanceOperationCoordinator(() => new PreviewLease());
                var viewModel = PreviewCleanupFactory.Create(catalog, new MemorySettings(), coordinator, _sandbox, _toggles, background: true);
                var capture = new PreviewCapture(dir, viewModel, _toggles, SetLocale, theme => RequestedThemeVariant = theme);
                using var foreground = PreviewForeground.ForApplication();
                await capture.RunAsync();
                Console.WriteLine($"PREVIEW foreground {foreground.Summary} clean={foreground.Clean}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("PREVIEW capture failed: " + ex);
            }
            finally
            {
                watchdog.Cancel();
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
            }
        }

        /// <summary>LUMIN4TI_PREVIEW_SELFCHECK=1 のときだけ、各タブの走査を実行して結果を標準出力へ書き、終了する。</summary>
        private async Task RunSelfCheckAsync(MaintenanceActionCatalog catalog, CleanupViewModel viewModel, Window window)
        {
            using var foreground = PreviewForeground.ForApplication();
            try
            {
                Console.OutputEncoding = System.Text.Encoding.UTF8;
                await Task.Delay(1000);
                var before = Directory.EnumerateFiles(_sandbox.Root, "*", SearchOption.AllDirectories).Count();
                await viewModel.Common.ScanCommand.ExecuteAsync(null);
                Console.WriteLine($"PREVIEW common rows={viewModel.Common.Rows.Count}");
                await viewModel.Advanced.ScanCommand.ExecuteAsync(null);
                Console.WriteLine($"PREVIEW advanced hasScan={viewModel.Advanced.HasScan} status={viewModel.Advanced.StatusText} notices={string.Join(" / ", viewModel.Advanced.Notices)}");
                await viewModel.Slim.ScanCommand.ExecuteAsync(null);
                Console.WriteLine($"PREVIEW slim status={viewModel.Slim.StatusText}");
                await viewModel.Registry.ScanCommand.ExecuteAsync(null);
                Console.WriteLine($"PREVIEW registry groups={viewModel.Registry.Groups.Count} status={viewModel.Registry.StatusText} scope={viewModel.Registry.UserScopeNote}");
                // 高度タブの削除も、合成フォルダーの中だけで完結することを確かめる。
                viewModel.Advanced.SelectRecommendedCommand.Execute(null);
                viewModel.Advanced.RequestDeleteCommand.Execute(null);
                await viewModel.Advanced.ConfirmDeleteCommand.ExecuteAsync(null);
                Console.WriteLine($"PREVIEW advanced delete status={viewModel.Advanced.StatusText} success={viewModel.Advanced.IsResultSuccess} warning={viewModel.Advanced.IsResultWarning} error={viewModel.Advanced.IsResultError}");
                var after = Directory.EnumerateFiles(_sandbox.Root, "*", SearchOption.AllDirectories).Count();
                Console.WriteLine($"PREVIEW sandbox={_sandbox.Root} files before={before} after={after}");
                Console.WriteLine($"PREVIEW in-use file kept={File.Exists(_sandbox.LockedAdvancedFile)} (partial 期待: warning=True)");
                var escaped = await PreviewEscapeCheck.RunAsync(
                    catalog, _sandbox, _toggles, () => new MaintenanceOperationCoordinator(() => new PreviewLease()));
                if (!escaped) Environment.ExitCode = 1;
                Console.WriteLine($"PREVIEW foreground {foreground.Summary} clean={foreground.Clean}");
                if (!foreground.Clean) Environment.ExitCode = 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("PREVIEW selfcheck failed: " + ex);
            }
            finally
            {
                window.Close();
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
            }
        }

        private void ShowSettingsPreview(MaintenanceActionCatalog catalog, MemorySettings settings)
        {
            var coordinator = new MaintenanceOperationCoordinator(() => new PreviewLease());
            var version = new VersionViewModel(new UpdateService(() => null), settings, coordinator);
            var devices = new DeviceCleanupViewModel(new MockDevices { FailEnumeration = false }, coordinator);
            new MainWindow(coordinator)
            {
                Title = "Lumin4ti — Settings preview (fake state only)",
                DataContext = new MainWindowViewModel(
                    catalog, PreviewCleanupFactory.Create(catalog, settings, coordinator, _sandbox, _toggles), devices, version, coordinator),
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
