using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;
using Lumin4ti.UI.Controls;
using Lumin4ti.UI.ViewModels;
using Lumin4ti.UI.Views;

namespace Lumin4ti.Verification;

/// <summary>
/// LUMIN4TI_PREVIEW_CAPTURE=&lt;出力フォルダー&gt; のときだけ使う。クリーンアップ窓の各状態を PNG に書き出し、
/// 横スクロール・縦スクロール・DataContext・フォーカスと Esc・メニュー・バインドエラーを検査して report.txt に書く。
/// 状態は偽データの ViewModel のコマンドだけで作る (実ファイル・実レジストリには届かない)。
/// </summary>
internal sealed class PreviewCapture
{
    public const string EnvName = "LUMIN4TI_PREVIEW_CAPTURE";

    private readonly string _dir;
    private readonly CleanupViewModel _vm;
    private readonly PreviewToggles _toggles;
    private readonly Action<string> _setLocale;
    private readonly Action<ThemeVariant> _setTheme;
    private readonly StringBuilder _report = new();
    private readonly List<string> _issues = [];
    private readonly List<string> _written = [];
    private readonly List<string> _skipped = [];
    private readonly List<string> _listRows = [];
    private int _hwndTotal;
    private int _tabRowsMax;
    private readonly CaptureLogSink _sink = new();
    private Window _window = null!;
    private CleanupView _view = null!;
    private int _serial;
    private int _width = 760;
    private string _lang = "ja";
    private string _theme = "light";

    public PreviewCapture(
        string dir, CleanupViewModel vm, PreviewToggles toggles, Action<string> setLocale, Action<ThemeVariant> setTheme)
    {
        _dir = dir;
        _vm = vm;
        _toggles = toggles;
        _setLocale = setLocale;
        _setTheme = setTheme;
    }

    /// <summary>書き出し・自己検査用の窓を、前面に出さず・アクティブにせず・タスクバーに出さず、画面外へ置く。</summary>
    public static void MakeBackground(Window window)
    {
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Topmost = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(-32000, -32000);
    }

    public async Task RunAsync()
    {
        Directory.CreateDirectory(_dir);
        Logger.Sink = _sink;
        try
        {
            _view = new CleanupView { DataContext = _vm };
            _window = new Window { Title = "Lumin4ti capture", Width = 760, Height = 500, Content = _view };
            MakeBackground(_window);
            _window.Show();
            _window.Position = new PixelPoint(-32000, -32000);
            await Settle(500);
            Note($"窓の位置={_window.Position} ShowActivated={_window.ShowActivated} ShowInTaskbar={_window.ShowInTaskbar} Topmost={_window.Topmost} IsActive={_window.IsActive}");
            await CaptureAllAsync();
            Note($"終了時 IsActive={_window.IsActive}");
        }
        catch (Exception ex)
        {
            Skip("RunAsync", ex.ToString());
        }
        finally
        {
            WriteReport();
            try { _window?.Close(); } catch { /* 終了処理 */ }
        }
    }

    // ----- 全体の流れ -----

    private async Task CaptureAllAsync()
    {
        _setLocale("ja_JP");
        _setTheme(ThemeVariant.Light);
        await Settle(400);

        await Step("tabs-ja", () => Snap("tabs", "headers", tabsCheck: true));
        await Step("tabs-de", async () =>
        {
            await Configure(760, 500, "de", "light");
            await Snap("tabs", "headers", tabsCheck: true);
            await Configure(760, 500, "ja", "light");
        });

        await CommonAsync();
        await AdvancedAsync();
        await SlimAsync();
        await RegistryAsync();
        await SettingsAsync();
        await OtherAsync();
    }

    private async Task CommonAsync()
    {
        var c = _vm.Common;
        await Step("common-select-tab", () => SelectTab(0));
        await Step("common-prescan", () => Snap("common", "prescan"));
        Task? scan = null;
        await Step("common-scanning", async () =>
        {
            scan = c.ScanCommand.ExecuteAsync(null);
            await Task.Delay(1200);
            await Snap("common", "scanning");
        });
        await Step("common-scan-wait", async () =>
        {
            if (scan is not null) await Wait(scan, 40, "common scan");
            await Settle();
            foreach (var category in c.Rows.Where(r => r.IsCategory).ToList()) category.IsExpanded = true;
        });
        await Step("common-scanned", () => Snap("common", "scanned"));
        await Step("common-scanned-en-dark-880", async () =>
        {
            await Configure(880, 620, "en", "dark");
            await Snap("common", "scanned");
            await Configure(760, 500, "ja", "light");
        });
        await Step("common-scanned-de", async () =>
        {
            await Configure(760, 500, "de", "light");
            await Snap("common", "scanned");
            await Configure(760, 500, "ja", "light");
        });

        // 実行中の製品を持たない規則だけを選ぶ (起動中の製品の確認ダイアログを出さないため)。
        void PickSafeRules(params string[] extra)
        {
            c.SelectNoneCommand.Execute(null);
            foreach (var id in new[] { "win-temp", "win-logs", "shader-cache", "fabrikam-cache" }.Concat(extra))
            {
                var row = c.Rows.FirstOrDefault(r => r.IsRule && r.Key == id);
                if (row is not null) row.IsChecked = true;
            }
        }

        await Step("common-confirm", async () =>
        {
            PickSafeRules();
            c.RequestRunCommand.Execute(null);
            Task Reopen()
            {
                c.RequestRunCommand.Execute(null);
                return Task.CompletedTask;
            }

            await Snap("common", "confirm", sheet: true, reopen: Reopen);
            await Configure(760, 500, "de", "light");
            await Snap("common", "confirm", sheet: true, reopen: Reopen);
            await Configure(760, 500, "ja", "light");
        });
        await Step("common-userfiles", async () =>
        {
            async Task OpenUserFiles()
            {
                if (c.IsConfirming || c.IsConfirmingUserFiles) c.CancelConfirmCommand.Execute(null);
                PickSafeRules("downloads-old");
                c.RequestRunCommand.Execute(null);
                await Wait(c.ConfirmRunCommand.ExecuteAsync(null), 20, "common confirm run");
            }

            await OpenUserFiles();
            await Snap("common", "userfiles-confirm", sheet: true, reopen: OpenUserFiles);
            foreach (var entry in c.UserFileEntries) entry.IsConfirmed = true;
            await Wait(c.ConfirmUserFilesCommand.ExecuteAsync(null), 90, "common run");
            await Settle(400);
            await Snap("common", "result-partial", info: () =>
                $"status={c.StatusText}; banner={c.ResultBanner}; lines={c.ResultLines.Count}; success={c.ResultIsSuccess}; warning={c.ResultIsWarning}");
        });
        await Step("common-stale", async () =>
        {
            await Wait(c.ScanCommand.ExecuteAsync(null), 40, "common rescan");
            foreach (var category in c.Rows.Where(r => r.IsCategory).ToList()) category.IsExpanded = true;
            c.SelectRecommendedCommand.Execute(null);
            c.Scan.Invalidate();
            c.RequestRunCommand.Execute(null);
            await Snap("common", "stale", info: () => $"IsStale={c.IsStale}; status={c.StatusText}");
        });
    }

    private async Task AdvancedAsync()
    {
        var a = _vm.Advanced;
        await Step("advanced-select-tab", async () =>
        {
            await SelectTab(1);
            // Activate() はドライブの取得を非同期で始める。一覧が出るまで待ってから撮る。
            for (var i = 0; i < 40 && a.Drives.Count == 0; i++) await Settle(150);

            Note($"advanced: ドライブ一覧 {a.Drives.Count} 件");
            if (a.Drives.Count == 0) throw new InvalidOperationException("走査するドライブの一覧が空のまま");
        });
        await Step("advanced-prescan", () => Snap("advanced", "prescan"));
        await Step("advanced-scan", async () =>
        {
            await Wait(a.ScanCommand.ExecuteAsync(null), 60, "advanced scan");
            await Snap("advanced", "scanned");
        });
        await Step("advanced-scanned-de", async () =>
        {
            await Configure(760, 500, "de", "light");
            await Snap("advanced", "scanned");
            await Configure(760, 500, "ja", "light");
        });
        await Step("advanced-scanned-dark", async () =>
        {
            await Configure(760, 500, "ja", "dark");
            await Snap("advanced", "scanned");
            await Configure(760, 500, "ja", "light");
        });
        await Step("advanced-filtered", async () =>
        {
            var group = a.Groups.Count > 1 ? a.Groups[1] : a.Groups.FirstOrDefault();
            if (group is null) throw new InvalidOperationException("種別の一覧が空です");
            a.SelectedGroup = group;
            await Snap("advanced", "filtered");
            a.ShowAllKindsCommand.Execute(null);
            await Settle();
        });
        await Step("advanced-confirm", async () =>
        {
            a.SelectRecommendedCommand.Execute(null);
            a.RequestDeleteCommand.Execute(null);
            await Snap("advanced", "confirm", sheet: true);
        });
        await Step("advanced-result", async () =>
        {
            if (a.IsConfirming) a.CancelDeleteCommand.Execute(null);
            // 使用中のファイル (合成フォルダー内) も含め、部分失敗を作る。
            a.SelectAllCommand.Execute(null);
            a.RequestDeleteCommand.Execute(null);
            await Wait(a.ConfirmDeleteCommand.ExecuteAsync(null), 90, "advanced delete");
            await Settle(400);
            await Snap("advanced", "result-partial", info: () =>
                $"status={a.StatusText}; success={a.IsResultSuccess}; warning={a.IsResultWarning}; error={a.IsResultError}");
        });
    }

    private async Task SlimAsync()
    {
        var s = _vm.Slim;
        await Step("slim-select-tab", () => SelectTab(2));
        await Step("slim-prescan", () => Snap("slim", "prescan"));
        await Step("slim-scan", async () =>
        {
            await Wait(s.ScanCommand.ExecuteAsync(null), 60, "slim scan");
            await Snap("slim", "scanned");
        });
        await Step("slim-confirm", async () =>
        {
            foreach (var row in s.Items.Where(r => r.Definition.RequiresSecondConfirmation)) row.IsSelected = true;
            s.RequestRunCommand.Execute(null);
            if (!s.IsConfirming) throw new InvalidOperationException("確認シートが開きません (選択可能な項目が無い)");
            Note($"slim: RequiresAcknowledgement={s.RequiresAcknowledgement}");
            await Snap("slim", "confirm-unacknowledged");
            s.Acknowledged = true;
            await Snap("slim", "confirm-acknowledged", sheet: true, reopen: () =>
            {
                s.RequestRunCommand.Execute(null);
                return Task.CompletedTask;
            });
        });
        await Step("slim-result", async () =>
        {
            if (!s.IsConfirming) s.RequestRunCommand.Execute(null);
            s.Acknowledged = true;
            var picked = s.Items.Where(r => r.IsSelected).Select(r => r.Definition.Id).ToHashSet();
            await Wait(s.ConfirmRunCommand.ExecuteAsync(null), 120, "slim run");
            await Settle(400);
            // 選んでいない行に結果が付いていないか (実行結果は選んだ項目の分だけのはず)。
            var extra = s.Items.Where(r => r.HasResult && !picked.Contains(r.Definition.Id)).Select(r => r.Definition.Id.ToString()).ToList();
            Note($"slim: 選択 {picked.Count} 行 / 選択外で結果が付いた行 {extra.Count} ({string.Join(", ", extra)})");
            if (extra.Count > 0) _issues.Add($"slim-result: 選択していない行に結果が付いた: {string.Join(", ", extra)}");
            await Snap("slim", "result", info: () => $"summary={s.ResultSummary}; success={s.IsResultSuccess}; warning={s.IsResultWarning}");
        });
    }

    private async Task RegistryAsync()
    {
        var r = _vm.Registry;
        var restore = _vm.RegistryRestore;
        _toggles.HkcuAvailable = false;
        await Step("registry-select-tab", async () =>
        {
            await SelectTab(3);
            await SelectSegment(0);
        });
        await Step("registry-prescan", () => Snap("registry-clean", "prescan"));
        await Step("registry-scan", async () =>
        {
            await Wait(r.ScanCommand.ExecuteAsync(null), 40, "registry scan");
            await Snap("registry-clean", "scanned", info: () => $"status={r.StatusText}; userScopeNote={r.UserScopeNote}; groups={r.Groups.Count}");
        });
        await Step("registry-scanned-de", async () =>
        {
            await Configure(760, 500, "de", "light");
            await Snap("registry-clean", "scanned");
            await Configure(760, 500, "ja", "light");
        });
        await Step("registry-scanned-dark", async () =>
        {
            await Configure(760, 500, "ja", "dark");
            await Snap("registry-clean", "scanned");
            await Configure(760, 500, "ja", "light");
        });
        await Step("registry-collapsed", async () =>
        {
            var group = r.Groups.First(g => g.Definition.Category == RegistryCleanCategory.AppPaths);
            group.IsExpanded = false;
            await Snap("registry-clean", "category-collapsed");
            group.IsExpanded = true;
            await Settle();
        });
        await Step("registry-scrolled", async () =>
        {
            var shared = r.Groups.First(g => g.Definition.Category == RegistryCleanCategory.SharedDlls);
            var list = _view.GetVisualDescendants().OfType<ListBox>()
                .FirstOrDefault(l => l.IsEffectivelyVisible && l.Classes.Contains("cleanup-list"))
                ?? throw new InvalidOperationException("レジストリの一覧 (ListBox.cleanup-list) が見つかりません");
            var target = shared.Rows.Count > 40 ? shared.Rows[40] : (object)shared;
            list.ScrollIntoView(target);
            await Settle(400);
            await Snap("registry-clean", "scrolled-320rows");
            list.ScrollIntoView(r.DisplayItems[0]);
            await Settle();
        });
        await Step("registry-confirm", async () =>
        {
            SelectForRun();
            r.RequestRunCommand.Execute(null);
            Task Reopen()
            {
                r.RequestRunCommand.Execute(null);
                return Task.CompletedTask;
            }

            await Snap("registry-clean", "confirm", sheet: true, reopen: Reopen);
            await Configure(760, 500, "de", "light");
            await Snap("registry-clean", "confirm", sheet: true, reopen: Reopen);
            await Configure(760, 500, "ja", "light");
            // 承認を一部だけ付けた状態 (確認ボタンの活性が変わる)。
            if (!r.IsConfirming) r.RequestRunCommand.Execute(null);
            var first = r.ApprovalRows.FirstOrDefault();
            if (first is not null) first.IsApproved = true;
            await Snap("registry-clean", "confirm-partially-approved");
        });

        void SelectForRun()
        {
            if (r.IsConfirming) r.CancelConfirmCommand.Execute(null);
            r.SelectAllCommand.Execute(null);
            foreach (var row in r.Groups.First(g => g.Definition.Category == RegistryCleanCategory.Uninstall).Rows) row.IsSelected = true;
            foreach (var row in r.Groups.First(g => g.Definition.Category == RegistryCleanCategory.SharedDlls).Rows.Take(3)) row.IsSelected = true;
        }

        // バックアップ側 (削除の実行より前に、2 件の一覧と確認シートを撮る)。
        await Step("registry-backups-list", async () =>
        {
            if (r.IsConfirming) r.CancelConfirmCommand.Execute(null);
            await SelectSegment(1);
            await Wait(restore.RefreshCommand.ExecuteAsync(null), 15, "backup refresh");
            await Snap("registry-backups", "list");
            await Configure(760, 500, "de", "light");
            await Snap("registry-backups", "list");
            await Configure(760, 500, "ja", "light");
        });
        await Step("registry-backups-restore-confirm", async () =>
        {
            var backup = restore.Backups.First(b => b.Id.StartsWith("20260301", StringComparison.Ordinal));
            restore.RequestRestoreCommand.Execute(backup);
            await Snap("registry-backups", "restore-confirm", sheet: true);
        });
        await Step("registry-backups-delete-confirm", async () =>
        {
            restore.CancelSheetCommand.Execute(null);
            var backup = restore.Backups.Last();
            restore.RequestDeleteCommand.Execute(backup);
            await Snap("registry-backups", "delete-confirm", sheet: true);
            restore.CancelSheetCommand.Execute(null);
            await Settle();
        });

        // 削除の実行 → 結果。
        await Step("registry-result", async () =>
        {
            await SelectSegment(0);
            SelectForRun();
            r.RequestRunCommand.Execute(null);
            foreach (var row in r.ApprovalRows.ToList()) row.IsApproved = true;
            await Wait(r.ConfirmRunCommand.ExecuteAsync(null), 120, "registry run");
            await Settle(400);
            await Snap("registry-clean", "result", info: () =>
                $"summary={r.ResultSummary}; success={r.IsResultSuccess}; warning={r.IsResultWarning}; error={r.IsResultError}");
        });

        // 復元の実行 → Partial。
        await Step("registry-backups-restore-result", async () =>
        {
            await SelectSegment(1);
            await Wait(restore.RefreshCommand.ExecuteAsync(null), 15, "backup refresh");
            var backup = restore.Backups.First(b => b.Id.StartsWith("20260301", StringComparison.Ordinal));
            restore.RequestRestoreCommand.Execute(backup);
            await Wait(restore.ConfirmRestoreCommand.ExecuteAsync(null), 40, "registry restore");
            await Settle(400);
            await Snap("registry-backups", "result-partial", info: () =>
                $"summary={restore.ResultSummary}; success={restore.IsResultSuccess}; warning={restore.IsResultWarning}; error={restore.IsResultError}");
        });
    }

    private async Task SettingsAsync()
    {
        await Step("settings-top", async () =>
        {
            await SelectTab(4);
            await Snap("settings", "top");
        });
        await Step("settings-bottom", async () =>
        {
            var sv = _view.GetVisualDescendants().OfType<ScrollViewer>()
                .Where(s => s.IsEffectivelyVisible && s.Extent.Height - s.Viewport.Height > 0.5)
                .OrderByDescending(s => s.Extent.Height - s.Viewport.Height)
                .FirstOrDefault();
            if (sv is null)
            {
                Note("settings: 縦にスクロールできる ScrollViewer がありません (末尾の状態は先頭と同じ)");
            }
            else
            {
                sv.ScrollToEnd();
                await Settle(400);
            }

            await Snap("settings", "bottom");
        });
    }

    private async Task OtherAsync()
    {
        await Step("other", async () =>
        {
            await SelectTab(5);
            await Wait(_vm.LoadToggleStatesAsync(), 20, "other load states");
            await Snap("other", "default");
        });
    }

    // ----- 状態を作る補助 -----

    private async Task Step(string name, Func<Task> body)
    {
        _sink.Context = name;
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            Skip(name, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static async Task Wait(Task task, int seconds, string what)
    {
        var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds)));
        if (done != task) throw new TimeoutException($"{what} が {seconds} 秒で終わりませんでした");
        await task;
    }

    private async Task SelectTab(int index)
    {
        var tabs = (TabControl)_view.Content!;
        tabs.SelectedIndex = index;
        await Settle(400);
    }

    private async Task SelectSegment(int index)
    {
        var segment = _view.GetVisualDescendants().OfType<TabControl>().FirstOrDefault(t => t.Classes.Contains("segment"))
            ?? throw new InvalidOperationException("レジストリの内側のタブ (TabControl.segment) が見つかりません");
        segment.SelectedIndex = index;
        await Settle(400);
    }

    private async Task Configure(int width, int height, string lang, string theme)
    {
        _width = width;
        _window.Width = width;
        _window.Height = height;
        if (lang != _lang)
        {
            _lang = lang;
            _setLocale(lang switch { "en" => "en_US", "de" => "de_DE", _ => "ja_JP" });
            // 製品は App.SetLocale → App.LocaleChanged → 各 ViewModel の OnLocaleChanged (UI スレッドへの投稿)。
            // 投稿の完了を待ち、念のため同じ引き直しを各 ViewModel に直接も呼ぶ (何度呼んでも状態は変わらない)。
            await Settle(300);
            RefreshViewModels();
        }

        if (theme != _theme)
        {
            _theme = theme;
            _setTheme(theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light);
        }

        await Settle(450);
    }

    private void RefreshViewModels()
    {
        object[] models = [_vm.Common, _vm.Advanced, _vm.Slim, _vm.Registry, _vm.RegistryRestore, _vm.Settings];
        foreach (var model in models)
        {
            var method = model.GetType().GetMethod(
                "OnLocaleChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (method is null)
            {
                Note($"言語の引き直し: {model.GetType().Name} に OnLocaleChanged が無い");
                continue;
            }

            try { method.Invoke(model, null); }
            catch (Exception ex) { Note($"言語の引き直しに失敗 {model.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}"); }
        }
    }

    private async Task Settle(int ms = 280)
    {
        for (var i = 0; i < 3; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            await Task.Delay(ms / 3);
            _window.UpdateLayout();
        }
    }

    // ----- 書き出しと検査 -----

    private async Task Snap(
        string tab, string state, bool sheet = false, Func<Task>? reopen = null, bool tabsCheck = false, Func<string>? info = null)
    {
        await Settle();
        var name = $"{++_serial:000}-{tab}-{state}-{_width}-{_lang}-{_theme}.png";
        var path = Path.Combine(_dir, name);
        var bindStart = _sink.Count;
        _report.AppendLine().AppendLine("## " + name);
        try
        {
            var size = _window.ClientSize;
            using (var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)), new Vector(96, 96)))
            {
                bitmap.Render(_window);
                bitmap.Save(path, new PngBitmapEncoderOptions());
            }

            var length = new FileInfo(path).Length;
            _report.AppendLine($"file: {length} bytes, client {size.Width:0}x{size.Height:0}");
            if (length == 0) Skip(name, "ファイルの大きさが 0");
            else _written.Add(name);
        }
        catch (Exception ex)
        {
            Skip(name, "書き出しに失敗: " + ex.Message);
        }

        if (info is not null) SafeInspect(name, "info", () => _report.AppendLine("info: " + info()));
        SafeInspect(name, "scroll", () => InspectScroll(name));
        SafeInspect(name, "list", () => InspectListViewport(name, tab, state));
        SafeInspect(name, "hwnd", () => CheckHwnd(name));
        SafeInspect(name, "datacontext", () => InspectDataContext(name));
        SafeInspect(name, "language", () => InspectLanguageMix(name));
        SafeInspect(name, "tabs", () => InspectTabs(name));
        if (sheet) await SafeInspectAsync(name, "sheet", () => InspectSheetAsync(name, reopen));
        else await SafeInspectAsync(name, "menus", () => InspectMenusAsync(name));

        var entries = _sink.Since(bindStart);
        _report.AppendLine($"(f) 実行時の警告以上のログ: {entries.Count} 件");
        foreach (var entry in entries.Take(8)) _report.AppendLine("    " + entry);
        if (entries.Count > 0) _issues.Add($"{name}: ログ {entries.Count} 件 (先頭: {entries[0]})");
    }

    private void SafeInspect(string name, string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { _report.AppendLine($"[{what}] 検査に失敗: {ex.Message}"); }
    }

    private async Task SafeInspectAsync(string name, string what, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { _report.AppendLine($"[{what}] 検査に失敗: {ex.Message}"); }
    }

    private Visual? SheetHost() =>
        _window.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "PART_SheetHost" && c.IsEffectivelyVisible);

    private void InspectScroll(string name)
    {
        var host = SheetHost();
        var all = _window.GetVisualDescendants().OfType<ScrollViewer>()
            .Where(s => s.IsEffectivelyVisible && s.Bounds.Width > 0 && s.Bounds.Height > 0).ToList();
        var horizontal = all.Where(s => s.Extent.Width - s.Viewport.Width > 0.5).ToList();
        var vertical = all.Where(s => s.Extent.Height - s.Viewport.Height > 0.5).ToList();
        bool InSheet(Visual v) => host is not null && host.IsVisualAncestorOf(v);

        _report.AppendLine($"(a) 横スクロール可能な ScrollViewer: {horizontal.Count}");
        foreach (var s in horizontal)
        {
            var line = $"    {Where(s)} Extent.W={s.Extent.Width:0.#} Viewport.W={s.Viewport.Width:0.#}{(InSheet(s) ? " [シート内]" : string.Empty)}";
            _report.AppendLine(line);
            _issues.Add($"{name}: 横スクロール {Where(s)} (Extent.W={s.Extent.Width:0.#} > Viewport.W={s.Viewport.Width:0.#})");
        }

        var inSheet = vertical.Count(InSheet);
        _report.AppendLine($"(b) 縦スクロール可能な ScrollViewer: {vertical.Count} (うちシート内 {inSheet}, 表示中の ScrollViewer 全体 {all.Count})");
        foreach (var s in vertical)
        {
            _report.AppendLine($"    {Where(s)} Extent.H={s.Extent.Height:0.#} Viewport.H={s.Viewport.Height:0.#}{(InSheet(s) ? " [シート内]" : string.Empty)}");
        }

        if (vertical.Count > 1) _issues.Add($"{name}: 縦スクロールが {vertical.Count} 個 ({string.Join(" / ", vertical.Select(Where))})");
    }

    /// <summary>このプロセスの可視のトップレベル HWND の数 (Headless なら常に 0)。</summary>
    private void CheckHwnd(string name)
    {
        var count = PreviewForeground.VisibleWindowCount();
        _hwndTotal += count;
        _report.AppendLine($"(j) このプロセスの可視の HWND: {count}");
        if (count > 0) _issues.Add($"{name}: 可視の HWND が {count} 個ある (窓を作らない方式のはず)");
    }

    /// <summary>
    /// 一覧 (ListBox.cleanup-list) の表示範囲の高さと、表示範囲に入っている行数 (完全に入る行 / 一部だけ見える行を含めた小数) を書く。
    /// 窓が 760 幅の走査後の状態は、末尾の集計表にも載せる。
    /// </summary>
    private void InspectListViewport(string name, string tab, string state)
    {
        var lists = _window.GetVisualDescendants().OfType<ListBox>()
            .Where(l => l.IsEffectivelyVisible && l.Classes.Contains("cleanup-list") && l.Bounds.Height > 0)
            .ToList();
        _report.AppendLine($"(i) 一覧 {lists.Count} 個 (窓 {_window.ClientSize.Width:0}x{_window.ClientSize.Height:0})");
        foreach (var segment in _window.GetVisualDescendants().OfType<TabControl>().Where(t => t.IsEffectivelyVisible && t.Classes.Contains("segment")))
        {
            var wrap = segment.GetVisualDescendants().OfType<WrapPanel>().FirstOrDefault();
            if (wrap is not null)
                _report.AppendLine($"    segment WrapPanel bounds={wrap.Bounds.Width:0}x{wrap.Bounds.Height:0} 希望={wrap.DesiredSize.Width:0}x{wrap.DesiredSize.Height:0} 測定有効={wrap.IsMeasureValid} 配置有効={wrap.IsArrangeValid} 親={wrap.GetVisualParent()?.GetType().Name} {(wrap.GetVisualParent() as Visual)?.Bounds.Width:0}");
            foreach (var item in segment.Items.OfType<TabItem>())
            {
                var text = item.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text));
                _report.AppendLine(
                    $"    segment 項目 w={item.Bounds.Width:0} h={item.Bounds.Height:0} 希望 w={item.DesiredSize.Width:0} h={item.DesiredSize.Height:0} 測定有効={item.IsMeasureValid} 文字 w={text?.Bounds.Width:0} h={text?.Bounds.Height:0} 希望 w={text?.DesiredSize.Width:0} h={text?.DesiredSize.Height:0} 折返={text?.TextWrapping} '{text?.Text}' 親={segment.Bounds.Width:0}x{segment.Bounds.Height:0}");
            }
        }

        foreach (var page in _window.GetVisualDescendants().OfType<CleanupPage>().Where(p => p.IsEffectivelyVisible))
        {
            var y = page.TranslatePoint(default, _window)?.Y ?? 0;
            string H(object? slot) => slot is Control { IsEffectivelyVisible: true } c ? $"{c.Bounds.Height:0}" : "-";
            _report.AppendLine($"    ページ y={y:0} h={page.Bounds.Height:0}: 操作バー={H(page.Toolbar)} 通知={H(page.Notices)} 結果={H(page.Result)} 実行バー={H(page.Footer)}");
        }

        foreach (var list in lists)
        {
            var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroller is null) continue;
            var viewport = scroller.Viewport.Height;
            var full = 0;
            var partial = 0;
            var fraction = 0d;
            foreach (var item in list.GetVisualDescendants().OfType<ListBoxItem>())
            {
                if (item.Bounds.Height <= 0) continue;
                var y = item.TranslatePoint(default, scroller)?.Y ?? 0;
                var visible = Math.Max(0, Math.Min(viewport, y + item.Bounds.Height) - Math.Max(0, y));
                if (visible <= 0.5) continue;
                if (visible >= item.Bounds.Height - 0.5) full++;
                else partial++;
                fraction += visible / item.Bounds.Height;
            }

            var line = $"{Label(list)} 表示範囲の高さ={viewport:0}px, 完全に入る行={full}, 一部だけ見える行={partial}, 行数(小数)={fraction:0.0}";
            _report.AppendLine("    " + line);
            if (_width == 760 && state == "scanned" && tab is "common" or "advanced" or "slim" or "registry-clean")
            {
                _listRows.Add($"{tab}/{state} {_lang}/{_theme} {_width}x{_window.ClientSize.Height:0}: {line}");
            }
        }
    }

    /// <summary>確認シートの下端がページの下端より上か、実行・キャンセルのボタンが表示範囲か、シート内の縦スクロールが 1 つ以下か。</summary>
    private void InspectSheetLayout(string name, CleanupPage page, Control? host)
    {
        if (host is null)
        {
            _report.AppendLine("(g) シートの配置: PART_SheetHost が無い");
            _issues.Add($"{name}: PART_SheetHost が見つからない");
            return;
        }

        var pageTop = page.TranslatePoint(default, _window) ?? default;
        var pageBottom = pageTop.Y + page.Bounds.Height;
        var hostTop = host.TranslatePoint(default, _window) ?? default;
        var hostBottom = hostTop.Y + host.Bounds.Height;
        var bottomOk = hostBottom <= pageBottom + 0.5 && hostTop.Y >= pageTop.Y - 0.5;
        _report.AppendLine($"(g) シートの配置: シート y={hostTop.Y:0.#}..{hostBottom:0.#}, ページ y={pageTop.Y:0.#}..{pageBottom:0.#} → 収まる {(bottomOk ? "はい" : "いいえ")}");
        if (!bottomOk) _issues.Add($"{name}: 確認シート (y={hostTop.Y:0.#}..{hostBottom:0.#}) がページ (y={pageTop.Y:0.#}..{pageBottom:0.#}) からはみ出す");

        // スクロール領域の外 (下端に固定された側) にあるボタンが、ページと窓の表示範囲に入っているか。
        var scrollers = host.GetVisualDescendants().OfType<ScrollViewer>().ToList();
        var buttons = host.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && b.Bounds.Width > 0 && b.Bounds.Height > 0 && !scrollers.Any(s => s.IsVisualAncestorOf(b)))
            .ToList();
        _report.AppendLine($"(g) シートの固定ボタン: {buttons.Count} 個");
        if (buttons.Count < 2) _issues.Add($"{name}: シートに固定された実行・キャンセルのボタンが 2 個未満 ({buttons.Count})");
        foreach (var button in buttons)
        {
            var origin = button.TranslatePoint(default, _window) ?? default;
            var bottom = origin.Y + button.Bounds.Height;
            var right = origin.X + button.Bounds.Width;
            var inside = origin.Y >= pageTop.Y - 0.5 && bottom <= pageBottom + 0.5 &&
                         origin.X >= -0.5 && right <= _window.ClientSize.Width + 0.5 && origin.Y >= -0.5 && bottom <= _window.ClientSize.Height + 0.5;
            _report.AppendLine($"    {Label(button)} '{button.Content}' y={origin.Y:0.#}..{bottom:0.#} x={origin.X:0.#}..{right:0.#} 表示範囲 {(inside ? "内" : "外")}");
            if (!inside) _issues.Add($"{name}: シートのボタン '{button.Content}' (y={origin.Y:0.#}..{bottom:0.#}) が表示範囲の外");
        }

        var vertical = scrollers.Where(s => s.IsEffectivelyVisible && s.Extent.Height - s.Viewport.Height > 0.5).ToList();
        _report.AppendLine($"(g) シート内の縦スクロールできる ScrollViewer: {vertical.Count}");
        if (vertical.Count > 1) _issues.Add($"{name}: シート内の縦スクロールが {vertical.Count} 個");
    }

    /// <summary>日本語以外の表示なのに日本語の文字が残っている TextBlock (言語の引き直し漏れ、または偽データの日本語)。</summary>
    private void InspectLanguageMix(string name)
    {
        if (_lang == "ja") return;
        var stale = _window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text) && Regex.IsMatch(t.Text, @"[぀-ヿ一-鿿]"))
            .ToList();
        _report.AppendLine($"(h) {_lang} で日本語の文字を含む TextBlock: {stale.Count}");
        foreach (var t in stale.Take(6)) _report.AppendLine($"    {Where(t)} \"{(t.Text!.Length > 60 ? t.Text[..60] : t.Text)}\"");
        if (stale.Count > 0) _issues.Add($"{name}: {_lang} なのに日本語を含む表示 {stale.Count} 件 (例: {Where(stale[0])})");
    }

    private void InspectDataContext(string name)
    {
        var pages = _window.GetVisualDescendants().OfType<CleanupPage>().Where(p => p.IsEffectivelyVisible).ToList();
        _report.AppendLine($"(c) CleanupPage {pages.Count} 個の各スロットの DataContext");
        foreach (var page in pages)
        {
            var owner = page.GetVisualAncestors().OfType<UserControl>().FirstOrDefault()?.GetType().Name ?? "?";
            var expected = owner + "Model";
            var slots = new (string Name, object? Value)[]
            {
                ("Toolbar", page.Toolbar), ("Notices", page.Notices), ("Result", page.Result),
                ("Body", page.Body), ("Footer", page.Footer), ("Sheet", page.Sheet),
            };
            _report.AppendLine($"  page in {owner} (期待 {expected}, page.DataContext={page.DataContext?.GetType().Name ?? "null"})");
            foreach (var (slot, value) in slots)
            {
                if (value is null)
                {
                    _report.AppendLine($"    {slot}: (未設定)");
                    continue;
                }

                if (value is not Control control)
                {
                    _report.AppendLine($"    {slot}: Control ではない ({value.GetType().Name})");
                    continue;
                }

                if (TopLevel.GetTopLevel(control) is null)
                {
                    _report.AppendLine($"    {slot}: 未接続 (閉じている/非表示) {control.GetType().Name}");
                    continue;
                }

                var dc = control.DataContext;
                var ok = dc is not null && dc.GetType().Name == expected;
                var nullChildren = control.GetVisualDescendants().OfType<Control>()
                    .Where(x => x is Button or ListBox or TextBox or CheckBox or ComboBox or NumericUpDown or ProgressBar or ItemsControl)
                    .Where(x => x.DataContext is null).ToList();
                _report.AppendLine($"    {slot}: {control.GetType().Name} DataContext={dc?.GetType().Name ?? "null"} {(ok ? "OK" : "NG")}; DataContext が null の主要コントロール {nullChildren.Count}");
                if (!ok) _issues.Add($"{name}: {owner} の {slot} の DataContext が {dc?.GetType().Name ?? "null"} (期待 {expected})");
                if (nullChildren.Count > 0)
                {
                    _issues.Add($"{name}: {owner} の {slot} 内に DataContext が null のコントロール {nullChildren.Count} 個 ({Where(nullChildren[0])})");
                }
            }
        }
    }

    private void InspectTabs(string name)
    {
        var tabs = _window.GetVisualDescendants().OfType<TabControl>()
            .FirstOrDefault(t => t.IsEffectivelyVisible && t.Classes.Contains("cleanup-tabs"));
        if (tabs is null)
        {
            _report.AppendLine("(tabs) TabControl.cleanup-tabs が見つからない");
            _issues.Add($"{name}: 内側タブ (TabControl.cleanup-tabs) が見つからない");
            return;
        }

        var items = tabs.Items.OfType<TabItem>().ToList();
        var rows = new SortedSet<int>();
        var clipped = false;
        foreach (var item in items)
        {
            var origin = item.TranslatePoint(default, tabs) ?? default;
            rows.Add((int)Math.Round(origin.Y));
            var right = origin.X + item.Bounds.Width;
            if (item.Bounds.Width <= 0 || right > tabs.Bounds.Width + 1 || origin.X < -1) clipped = true;
            _report.AppendLine($"    tab '{item.Header}' x={origin.X:0} y={origin.Y:0} w={item.Bounds.Width:0} h={item.Bounds.Height:0}");
        }

        _report.AppendLine($"(tabs) 見出し {items.Count} 個, 行数 {rows.Count}, 切れ/はみ出し {(clipped ? "あり" : "なし")}");
        if (clipped) _issues.Add($"{name}: タブ見出しが領域からはみ出している");
        if (rows.Count > 1) _issues.Add($"{name}: 内側タブの見出しが {rows.Count} 行に折り返している");
        _tabRowsMax = Math.Max(_tabRowsMax, rows.Count);
    }

    private async Task InspectSheetAsync(string name, Func<Task>? reopen)
    {
        var page = _window.GetVisualDescendants().OfType<CleanupPage>().FirstOrDefault(p => p.IsSheetOpen && p.IsEffectivelyVisible);
        if (page is null)
        {
            _report.AppendLine("(d) 確認シート: 開いているページが見つかりません");
            _issues.Add($"{name}: 確認シートを開いている CleanupPage が無い");
            return;
        }

        var host = page.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "PART_SheetHost");
        var focused = TopLevel.GetTopLevel(page)?.FocusManager?.GetFocusedElement() as Visual;
        var inside = focused is not null && host is not null && (focused == host || host.IsVisualAncestorOf(focused));
        _report.AppendLine($"(d) シート表示直後のフォーカス (Avalonia の FocusManager。窓は非アクティブ・画面外): {(focused is null ? "なし" : Where(focused))} → シート内 {(inside ? "はい" : "いいえ")}");
        if (focused is null)
        {
            _report.AppendLine("(d) フォーカス判定: 未確認 (非アクティブな窓では FocusManager が要素を返さない。前面化して確かめない)");
            _issues.Add($"{name}: シートのフォーカスは未確認 (非アクティブ窓)");
        }
        else if (!inside) _issues.Add($"{name}: シートを開いた直後のフォーカスがシートの外 ({(focused is null ? "なし" : Where(focused))})");

        InspectSheetLayout(name, page, host);

        var target = (focused as InputElement) ?? page;
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = target });
        await Settle(300);
        var closed = !page.IsSheetOpen;
        _report.AppendLine($"(d) Esc の後にシートが閉じる: {(closed ? "はい" : "いいえ")}");
        if (!closed) _issues.Add($"{name}: Esc でシートが閉じない");
        if (closed && reopen is not null)
        {
            await reopen();
            await Settle(300);
        }
    }

    private async Task InspectMenusAsync(string name)
    {
        var owners = new List<(Control Owner, FlyoutBase Flyout)>();
        var suspicious = new List<string>();
        foreach (var control in _window.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible))
        {
            var flyout = control switch
            {
                Button button => button.Flyout,
                SplitButton split => split.Flyout,
                _ => null,
            } ?? FlyoutBase.GetAttachedFlyout(control);
            if (flyout is not null) owners.Add((control, flyout));
            else if (control is DropDownButton or SplitButton) suspicious.Add(Where(control));
        }

        _report.AppendLine($"(e) Flyout を持つボタン: {owners.Count}{(suspicious.Count > 0 ? $" / Flyout が null の DropDown/SplitButton: {string.Join(", ", suspicious)}" : string.Empty)}");
        foreach (var s in suspicious) _issues.Add($"{name}: Flyout が null のメニューボタン {s}");
        foreach (var (owner, flyout) in owners.Take(4))
        {
            var count = flyout is MenuFlyout menu ? menu.Items.Count : -1;
            var opened = false;
            try
            {
                flyout.ShowAt(owner);
                await Settle(300);
                opened = flyout.IsOpen;
            }
            finally
            {
                flyout.Hide();
                await Settle(150);
            }

            var ok = opened && (count != 0);
            _report.AppendLine($"    {Where(owner)} enabled={owner.IsEffectivelyEnabled} {flyout.GetType().Name} 項目数={count} 開く={(opened ? "はい" : "いいえ")} {(ok ? "OK" : "NG")}");
            if (!ok) _issues.Add($"{name}: メニュー {Where(owner)} が開かない/項目が無い (項目数={count}, 開く={opened})");
        }
    }

    private static string Label(Visual v)
    {
        var text = v.GetType().Name;
        if (v is StyledElement element)
        {
            if (!string.IsNullOrEmpty(element.Name)) text += "#" + element.Name;
            var classes = element.Classes.Where(c => !c.StartsWith(':')).Take(2).ToList();
            if (classes.Count > 0) text += "." + string.Join('.', classes);
        }

        return text;
    }

    private static string Where(Visual v)
    {
        var parts = new List<string> { Label(v) };
        foreach (var ancestor in v.GetVisualAncestors())
        {
            if (parts.Count >= 5) break;
            if (ancestor is UserControl)
            {
                parts.Add(ancestor.GetType().Name);
                break;
            }

            if (ancestor is ListBox or ItemsControl or CleanupPage or Expander or TabControl or Popup) parts.Add(Label(ancestor));
        }

        return string.Join(" < ", parts);
    }

    private void Note(string text) => _report.AppendLine("note: " + text);

    private void Skip(string name, string reason)
    {
        _skipped.Add($"{name}: {reason}");
        _report.AppendLine($"SKIP {name}: {reason}");
    }

    private void WriteReport()
    {
        var head = new StringBuilder();
        head.AppendLine("# Lumin4ti クリーンアップ画面 キャプチャ報告");
        head.AppendLine($"書き出し: {_written.Count} 枚 / 飛ばした状態: {_skipped.Count} / 問題の候補: {_issues.Count}");
        head.AppendLine().AppendLine("## 問題の候補 (View のファイルと要素は各項の場所を参照)");
        foreach (var issue in _issues) head.AppendLine("- " + issue);
        head.AppendLine($"可視の HWND (各状態の合計): {_hwndTotal}");
        head.AppendLine($"内側タブの見出しの行数 (全状態の最大): {_tabRowsMax}");
        head.AppendLine().AppendLine("## 一覧の表示範囲 (760 幅・走査後)");
        foreach (var row in _listRows) head.AppendLine("- " + row);
        head.AppendLine().AppendLine("## 飛ばした状態");
        foreach (var skipped in _skipped) head.AppendLine("- " + skipped);
        head.AppendLine().AppendLine("## 書き出したファイル");
        foreach (var file in _written) head.AppendLine("- " + file);
        head.AppendLine().AppendLine("## 実行時ログ (警告以上) の集計");
        foreach (var group in _sink.Since(0).GroupBy(e => e).OrderByDescending(g => g.Count()).Take(30))
        {
            head.AppendLine($"- x{group.Count()} {group.Key}");
        }

        head.AppendLine().AppendLine("# 状態ごとの検査");
        File.WriteAllText(Path.Combine(_dir, "report.txt"), head + _report.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Avalonia のバインドエラーなど、警告以上のログを溜める。</summary>
    private sealed class CaptureLogSink : ILogSink
    {
        private readonly List<string> _entries = [];

        /// <summary>いま実行中の手順名 (ログの出どころの目安)。</summary>
        public string Context { get; set; } = string.Empty;

        public int Count
        {
            get { lock (_entries) return _entries.Count; }
        }

        public List<string> Since(int start)
        {
            lock (_entries) return [.. _entries.Skip(start)];
        }

        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            Add(level, area, source, messageTemplate, []);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
            Add(level, area, source, messageTemplate, propertyValues);

        private void Add(LogEventLevel level, string area, object? source, string template, object?[] values)
        {
            var index = 0;
            var text = Regex.Replace(template, @"\{[^}]*\}", _ => index < values.Length ? values[index++]?.ToString() ?? "null" : "{}");
            lock (_entries) _entries.Add($"[{level}/{area}] {text} (source={source?.GetType().Name ?? "null"}, step={Context})");
        }
    }
}
