using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.UI.Services;

namespace Lumin4ti.UI.ViewModels;

/// <summary>結果一覧の並べ替えの列。</summary>
public enum AdvancedCleanSortColumn
{
    None,
    Name,
    Folder,
    Size,
    Modified,
    Rule,
}

/// <summary>
/// 「高度」タブ。ドライブ全体のパターン走査 → 5 列の結果 (並べ替え・仮想化) → 選択 → 確認 → 削除。
/// 走査は <c>showProcessing:false</c>、削除は <c>true</c> の lease を取り、起動時や再表示時に自動では走らせない。
/// 一覧の組み立てと並べ替えは UI スレッドの外で行い、20 万件でも画面を塞がない。
/// <see cref="Ledger"/> は <see cref="Scanner"/> と <see cref="Cleaner"/> が共有する世代台帳 (DI の単一インスタンス)。
/// </summary>
public sealed partial class AdvancedCleanViewModel(
    AdvancedCleanScanner scanner,
    AdvancedCleaner cleaner,
    AdvancedScanLedger ledger,
    CleanupSettingsStore settings,
    ICommandExecutor executor,
    MaintenanceOperationCoordinator coordinator) : CleanupOperationViewModel
{
    private CancellationTokenSource? _cts;
    private AdvancedScanResult? _scan;
    private List<AdvancedCleanRowViewModel> _rows = [];
    private int _viewVersion;
    private bool _bulk;
    private bool _localeHooked;
    private Func<string>? _status;
    private Func<List<string>>? _notices;
    private Task? _driveLoad;

    [ObservableProperty]
    private bool includeZeroByte;

    [ObservableProperty]
    private bool includeInvalidShortcuts;

    [ObservableProperty]
    private bool includeOlder;

    [ObservableProperty]
    private decimal? olderThanDays = 180;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool hasScan;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool isStale;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool isConfirming;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<string> notices = [];

    [ObservableProperty]
    private IReadOnlyList<string> confirmLines = [];

    [ObservableProperty]
    private string selectionText = string.Empty;

    [ObservableProperty]
    private string filterText = string.Empty;

    [ObservableProperty]
    private AdvancedCleanGroupViewModel? selectedGroup;

    [ObservableProperty]
    private IReadOnlyList<AdvancedCleanRowViewModel> visibleRows = [];

    [ObservableProperty]
    private IReadOnlyList<AdvancedCleanGroupViewModel> groups = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentRow))]
    private AdvancedCleanRowViewModel? currentRow;

    [ObservableProperty]
    private AdvancedCleanSortColumn sortColumn;

    [ObservableProperty]
    private bool sortDescending;

    internal AdvancedCleanScanner Scanner { get; } = scanner;

    internal AdvancedCleaner Cleaner { get; } = cleaner;

    internal AdvancedScanLedger Ledger { get; } = ledger;

    internal CleanupSettingsStore Settings { get; } = settings;

    internal ICommandExecutor Executor { get; } = executor;

    internal MaintenanceOperationCoordinator Coordinator { get; } = coordinator;

    /// <summary>走査できるドライブの取得。テストが差し替える。</summary>
    internal Func<IReadOnlyList<AdvancedDrive>> DriveProvider { get; set; } = AdvancedCleanScanner.GetDrives;

    /// <summary>「場所を開く」の実体 (対話中ユーザーの権限で開く)。テストが差し替える。</summary>
    internal Func<string, Task> OpenFolder { get; set; } = static path => WindowsExplorerLauncher.OpenAsync(path);

    /// <summary>枝刈りと実行時検査の環境。省略時は実環境。テストが差し替える。</summary>
    internal AdvancedProtectedPaths? Protection { get; set; }

    internal CustomFolderGuardContext? GuardContext { get; set; }

    /// <summary>直近の削除の結果 (画面には文言で出す)。</summary>
    internal AdvancedCleanOutcome? LastOutcome { get; private set; }

    public ObservableCollection<AdvancedCleanDriveViewModel> Drives { get; } = [];

    public ObservableCollection<AdvancedCleanFolderViewModel> CustomFolders { get; } = [];

    public long SelectedFileCount { get; private set; }

    public long SelectedBytes { get; private set; }

    public int SelectedFolderCount { get; private set; }

    public long TotalFileCount => _rows.Count;

    public bool CanScan => !IsRunning && !IsConfirming;

    /// <summary>「場所を開く」「無視」の対象 (一覧で選んだ行) があるか。</summary>
    public bool HasCurrentRow => CurrentRow is not null && !IsRunning && !IsConfirming;

    public bool CanDelete => HasScan && !IsStale && !IsRunning && !IsConfirming && (SelectedFileCount > 0 || SelectedFolderCount > 0);

    public bool HasCustomFolders => CustomFolders.Count > 0;

    public bool HasResults => HasScan && (_rows.Count > 0 || CustomFolders.Count > 0);

    public string ScanButtonText => CleanupText.Get(HasScan ? "Cleanup.Action.Rescan" : "Cleanup.Action.Scan");

    public string ShowAllText => CleanupText.Get("Cleanup.Advanced.Filter.All");

    public string IgnoreFolderText => CleanupText.Get("Cleanup.Advanced.Action.IgnoreFolder");

    public string KindsHeaderText => CleanupText.Get("Cleanup.Advanced.Kinds");

    public string SortStateText => SortColumn == AdvancedCleanSortColumn.None
        ? string.Empty
        : CleanupText.Get($"Cleanup.Column.{SortColumn}") + (SortDescending ? " ▼" : " ▲");

    /// <summary>画面の表示時に呼ぶ。ドライブ一覧を (空なら) 読み、走査後に世代が失効していないか見直す。走査・削除は始めない。</summary>
    public void Activate()
    {
        if (!_localeHooked)
        {
            _localeHooked = true;
            App.LocaleChanged += () => PostToUi(OnLocaleChanged);
        }

        if (Drives.Count == 0) _ = EnsureDrivesSafeAsync();
        RefreshStaleness();
    }

    private async Task EnsureDrivesSafeAsync()
    {
        try
        {
            await EnsureDrivesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LoggerBootstrap.Log.Error("高度なクリーンのドライブ一覧の取得に失敗しました", ex);
        }
    }

    /// <summary>ドライブ一覧が空なら読み込む。ドライブの状態取得 (準備完了・ボリューム名) は遅いことがあるため UI スレッドの外で行い、同時の呼び出しは 1 回にまとめる。</summary>
    private Task EnsureDrivesAsync() => Drives.Count > 0 ? Task.CompletedTask : _driveLoad ??= LoadDrivesAsync();

    private async Task LoadDrivesAsync()
    {
        try
        {
            var drives = await Task.Run(() => DriveProvider()).ConfigureAwait(true);
            Drives.Clear();
            foreach (var drive in drives) Drives.Add(new AdvancedCleanDriveViewModel(drive));
        }
        finally
        {
            _driveLoad = null;
        }
    }

    /// <summary>走査結果がまだ有効か。失効していたら削除を無効にして知らせる (設定の変更や別の走査で世代が進む)。</summary>
    internal bool RefreshStaleness()
    {
        if (_scan is { IsCurrent: false } && !IsStale)
        {
            IsStale = true;
            SetStatus(() => CleanupText.Get("Cleanup.Common.Stale"));
        }

        return !IsStale;
    }

    protected override void OnCancelRequested() => _cts?.Cancel();

    // ----- 走査 -----

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsRunning || IsConfirming) return;
        await EnsureDrivesAsync().ConfigureAwait(true);
        if (IsRunning || IsConfirming) return;
        var roots = Drives.Where(static d => d.IsSelected).Select(static d => d.Drive.Root).ToArray();
        var folders = Settings.GetCustomFolders(GuardContext);
        if (roots.Length == 0 && folders.Count == 0)
        {
            SetStatus(() => CleanupText.Get("Cleanup.Advanced.NoTarget"));
            return;
        }

        if (!Coordinator.TryBegin(out var lease, default, showProcessing: false))
        {
            ReportBusy();
            return;
        }

        using var activeLease = lease!;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(activeLease.Token);
        BeginOperation(cts, CleanupText.Get("Cleanup.Tab.Advanced"));
        ClearResults();
        var options = new AdvancedScanOptions(roots)
        {
            SavedPatterns = Settings.GetPatterns(),
            IncludeZeroByte = IncludeZeroByte,
            IncludeInvalidShortcuts = IncludeInvalidShortcuts,
            OlderThanDays = IncludeOlder && OlderThanDays is >= 1 ? (int)Math.Min(OlderThanDays.Value, 36_500) : null,
            Filter = Settings.CreateExclusionMatcher(),
            CustomFolders = folders,
            Protection = Protection,
        };
        var progress = new Progress<AdvancedScanProgress>(p =>
        {
            // 終了後に遅れて届いた通知で、消した進捗を書き戻さない。
            if (!IsRunning) return;
            ProgressText = CleanupText.Get(
                "Cleanup.Advanced.Progress", p.Directories.ToString("N0"), p.Files.ToString("N0"), p.Matched.ToString("N0"), CleanupText.FormatBytes(p.Bytes), p.CurrentDirectory);
        });
        try
        {
            var (result, rows) = await Task.Run(() =>
            {
                var scanned = Scanner.Scan(options, progress, cts.Token);
                return (scanned, scanned.Items.Select(item => new AdvancedCleanRowViewModel(item, OnRowChanged)).ToList());
            }).ConfigureAwait(true);
            await ApplyScanAsync(result, rows).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            SetStatus(() => CleanupText.Get("Cleanup.Common.ScanCanceled"));
        }
        catch (Exception ex)
        {
            LoggerBootstrap.Log.Error("高度なクリーンの走査に失敗しました", ex);
            SetStatus(() => CleanupText.Get("Cleanup.Common.ScanFailed"));
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task ApplyScanAsync(AdvancedScanResult result, List<AdvancedCleanRowViewModel> rows)
    {
        _scan = result;
        _rows = rows;
        IsStale = false;
        HasScan = true;
        CustomFolders.Clear();
        foreach (var folder in result.CustomFolders) CustomFolders.Add(new AdvancedCleanFolderViewModel(folder, RecomputeSelection));
        Groups = BuildGroups(rows);
        SelectedGroup = null;
        RecomputeSelection();
        await RebuildViewAsync().ConfigureAwait(true);
        SetNotices(() => BuildScanNotices(result));
        SetStatus(() => result.TotalMatched == 0 && result.CustomFolders.Count == 0
            ? CleanupText.Get("Cleanup.Advanced.Empty")
            : CleanupText.Get("Cleanup.Advanced.ScanDone", result.TotalMatched.ToString("N0"), CleanupText.FormatBytes(result.TotalBytes), result.FilesScanned.ToString("N0")));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasCustomFolders));
        OnPropertyChanged(nameof(ScanButtonText));
        OnPropertyChanged(nameof(TotalFileCount));
    }

    internal static List<string> BuildScanNotices(AdvancedScanResult result)
    {
        var lines = new List<string>();
        if (result.Truncated) lines.Add(CleanupText.Get("Cleanup.Advanced.Truncated", result.Items.Count.ToString("N0"), result.OmittedFiles.ToString("N0"), CleanupText.FormatBytes(result.OmittedBytes)));
        if (result.PrunedDirectories > 0) lines.Add(CleanupText.Get("Cleanup.Advanced.Pruned", result.PrunedDirectories.ToString("N0")));
        lines.AddRange(result.UnavailableRoots.Select(static f => CleanupText.Get("Cleanup.Advanced.RootFailure", f.Root, CleanupText.Detail(f.Reason))));
        if (result.Unreadable > 0) lines.Add(CleanupText.Get("Cleanup.Common.Unreadable", result.Unreadable.ToString("N0")));
        if (result.SkippedLinks > 0) lines.Add(CleanupText.Get("Cleanup.Common.SkippedLinks", result.SkippedLinks.ToString("N0")));
        if (result.Retained > 0) lines.Add(CleanupText.Get("Cleanup.Common.Retained", result.Retained.ToString("N0")));
        return lines;
    }

    private void ClearResults()
    {
        _scan = null;
        _rows = [];
        _viewVersion++;
        HasScan = false;
        IsStale = false;
        CustomFolders.Clear();
        Groups = [];
        SelectedGroup = null;
        CurrentRow = null;
        VisibleRows = [];
        _notices = null;
        Notices = [];
        RecomputeSelection();
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasCustomFolders));
        OnPropertyChanged(nameof(ScanButtonText));
        OnPropertyChanged(nameof(TotalFileCount));
    }

    // ----- 一覧 (種別・絞り込み・並べ替え) -----

    internal static List<AdvancedCleanGroupViewModel> BuildGroups(IReadOnlyList<AdvancedCleanRowViewModel> rows)
    {
        var groups = new Dictionary<string, (AdvancedCleanItem Sample, long Count, long Bytes)>();
        foreach (var row in rows)
        {
            groups.TryGetValue(row.GroupKey, out var entry);
            groups[row.GroupKey] = (entry.Sample ?? row.Item, entry.Count + 1, entry.Bytes + row.Item.Size);
        }

        return [.. groups
            .Select(static g => new AdvancedCleanGroupViewModel(g.Key, g.Value.Sample, g.Value.Count, g.Value.Bytes))
            .OrderBy(static g => g.Group)
            .ThenByDescending(static g => g.Bytes)
            .ThenBy(static g => g.Title, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>種別・文字の絞り込みと並べ替えを適用した一覧。元の並び (パス順) を保つ安定な並べ替え。</summary>
    internal static List<AdvancedCleanRowViewModel> Arrange(
        IReadOnlyList<AdvancedCleanRowViewModel> rows, string? groupKey, string? filter, AdvancedCleanSortColumn column, bool descending)
    {
        IEnumerable<AdvancedCleanRowViewModel> query = rows;
        if (groupKey is not null) query = query.Where(row => row.GroupKey == groupKey);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var text = filter.Trim();
            query = query.Where(row =>
                row.Item.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                row.Item.Folder.Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        if (column != AdvancedCleanSortColumn.None)
        {
            query = descending ? query.OrderByDescending(row => SortKey(row, column), SortComparer) : query.OrderBy(row => SortKey(row, column), SortComparer);
        }

        return [.. query];
    }

    private static readonly IComparer<object> SortComparer = Comparer<object>.Create(static (a, b) =>
        a is string x && b is string y ? StringComparer.OrdinalIgnoreCase.Compare(x, y) : Comparer<long>.Default.Compare((long)a, (long)b));

    private static object SortKey(AdvancedCleanRowViewModel row, AdvancedCleanSortColumn column) => column switch
    {
        AdvancedCleanSortColumn.Name => row.Item.Name,
        AdvancedCleanSortColumn.Folder => row.Item.Folder,
        AdvancedCleanSortColumn.Size => row.Item.Size,
        AdvancedCleanSortColumn.Modified => row.Item.LastWriteFileTime,
        _ => row.Item.Rule,
    };

    private async Task RebuildViewAsync()
    {
        var version = Interlocked.Increment(ref _viewVersion);
        var rows = _rows;
        var key = SelectedGroup?.Key;
        var text = FilterText;
        var column = SortColumn;
        var descending = SortDescending;
        var view = await Task.Run(() => Arrange(rows, key, text, column, descending)).ConfigureAwait(true);
        if (version == Volatile.Read(ref _viewVersion)) VisibleRows = view;
    }

    private async void RebuildViewSafe()
    {
        try
        {
            await RebuildViewAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LoggerBootstrap.Log.Error("高度なクリーンの一覧の更新に失敗しました", ex);
        }
    }

    partial void OnSelectedGroupChanged(AdvancedCleanGroupViewModel? value) => RebuildViewSafe();

    partial void OnFilterTextChanged(string value) => RebuildViewSafe();

    partial void OnSortColumnChanged(AdvancedCleanSortColumn value) => OnPropertyChanged(nameof(SortStateText));

    partial void OnSortDescendingChanged(bool value) => OnPropertyChanged(nameof(SortStateText));

    [RelayCommand]
    private void SortBy(string? column)
    {
        if (!Enum.TryParse<AdvancedCleanSortColumn>(column, out var next) || next == AdvancedCleanSortColumn.None) return;
        if (SortColumn == next)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortDescending = false;
            SortColumn = next;
        }

        RebuildViewSafe();
    }

    [RelayCommand]
    private void ShowAllKinds() => SelectedGroup = null;

    // ----- 選択 -----

    [RelayCommand]
    private void SelectRecommended() => ApplySelection(static row => row.Item.DefaultSelected);

    [RelayCommand]
    private void SelectSafe() => ApplySelection(static row => row.Item.Verdict == AdvancedVerdict.Safe && row.Item.DefaultSelected);

    [RelayCommand]
    private void SelectAll() => ApplySelection(static _ => true);

    [RelayCommand]
    private void SelectNone() => ApplySelection(static _ => false);

    private void ApplySelection(Func<AdvancedCleanRowViewModel, bool> choose)
    {
        if (IsRunning || IsConfirming) return;
        _bulk = true;
        try
        {
            foreach (var row in VisibleRows) row.SetSelectedQuietly(choose(row));
        }
        finally
        {
            _bulk = false;
        }

        RecomputeSelection();
    }

    private void OnRowChanged(AdvancedCleanRowViewModel row)
    {
        if (!_bulk) RecomputeSelection();
    }

    private void RecomputeSelection()
    {
        long files = 0, bytes = 0;
        foreach (var row in _rows)
        {
            if (!row.IsSelected) continue;
            files++;
            bytes += row.Item.Size;
        }

        SelectedFileCount = files;
        SelectedBytes = bytes;
        SelectedFolderCount = CustomFolders.Count(static f => f.IsSelected);
        RefreshSelectionText();
        OnPropertyChanged(nameof(SelectedFileCount));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedFolderCount));
        OnPropertyChanged(nameof(CanDelete));
    }

    private void RefreshSelectionText() =>
        SelectionText = CleanupText.Get("Cleanup.Advanced.Selection", SelectedFileCount.ToString("N0"), _rows.Count.ToString("N0"), CleanupText.FormatBytes(SelectedBytes));

    // ----- 状態の文言 (言語を切り替えたら引き直す) -----

    private void SetStatus(Func<string> text)
    {
        _status = text;
        StatusText = text();
    }

    private void SetNotices(Func<List<string>> notices)
    {
        _notices = notices;
        Notices = notices();
    }

    private static void PostToUi(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) action();
        else Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }

    /// <summary>
    /// 言語を切り替えたとき、表示済みの文言を引き直す。走査結果・各行の選択・並べ替え・絞り込みは変えない。
    /// 行の文言は読まれたときに作るため、通知だけを出す。
    /// </summary>
    internal void OnLocaleChanged()
    {
        foreach (var drive in Drives) drive.RefreshLocale();
        foreach (var folder in CustomFolders) folder.RefreshLocale();
        foreach (var group in Groups) group.RefreshLocale();
        foreach (var row in _rows) row.RefreshLocale();
        RefreshSelectionText();
        if (_notices is { } notices) Notices = notices();
        if (IsConfirming) ConfirmLines = BuildConfirmLines();
        if (_status is { } status) StatusText = status();
        OnPropertyChanged(nameof(ScanButtonText));
        OnPropertyChanged(nameof(ShowAllText));
        OnPropertyChanged(nameof(IgnoreFolderText));
        OnPropertyChanged(nameof(KindsHeaderText));
        OnPropertyChanged(nameof(SortStateText));
    }

    // ----- 場所を開く・無視 -----

    [RelayCommand]
    private async Task OpenLocationAsync(AdvancedCleanRowViewModel? row)
    {
        if (row is null) return;
        try
        {
            await OpenFolder(row.Item.Folder).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LoggerBootstrap.Log.Error("結果の場所を開けませんでした", ex);
        }
    }

    [RelayCommand]
    private Task IgnoreFileAsync(AdvancedCleanRowViewModel? row) =>
        row is null ? Task.CompletedTask : IgnoreAsync(CleanupExclusionKind.File, row.Item.Path, path => row.Item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private Task IgnoreFolderAsync(AdvancedCleanRowViewModel? row) =>
        row is null ? Task.CompletedTask : IgnoreAsync(CleanupExclusionKind.Folder, row.Item.Folder, IsUnder(row.Item.Folder));

    private static Func<string, bool> IsUnder(string folder)
    {
        var prefix = folder.TrimEnd('\\') + "\\";
        return path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 除外を保存し、該当する行を一覧と選択から外す。除外は削除の対象を減らす方向にしか働かず、
    /// 削除の時にも保存済みの除外から作り直した照合 (<see cref="AdvancedCleanOptions.Filter"/>) が再び適用されるため、
    /// 走査結果は失効させず、そのまま削除に使える。設定画面は設定の変更全般で失効させるが、除外を足すだけなら矛盾しない。
    /// 設定ファイルを読み込めず保存を抑止している場合 (<see cref="InvalidOperationException"/>) も、変更はこの起動中だけ有効として続ける。
    /// </summary>
    private async Task IgnoreAsync(CleanupExclusionKind kind, string value, Func<string, bool> matches)
    {
        if (IsRunning || IsConfirming) return;
        var added = Settings.AddExclusion(kind, value);
        if (added is not (CleanupAddResult.Added or CleanupAddResult.Duplicate)) return;
        try
        {
            await Settings.SaveAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LoggerBootstrap.Log.Error("除外設定を保存できませんでした", ex);
        }

        _rows = [.. _rows.Where(row => !matches(row.Item.Path))];
        CurrentRow = null;
        Groups = BuildGroups(_rows);
        if (SelectedGroup is { } current && Groups.All(g => g.Key != current.Key)) SelectedGroup = null;
        RecomputeSelection();
        await RebuildViewAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasCustomFolders));
        OnPropertyChanged(nameof(TotalFileCount));
    }

    // ----- 削除 -----

    [RelayCommand]
    private void RequestDelete()
    {
        if (!CanDelete) return;
        if (!RefreshStaleness()) return;
        ConfirmLines = BuildConfirmLines();
        IsConfirming = true;
        OnPropertyChanged(nameof(CanScan));
    }

    [RelayCommand]
    private void CancelDelete()
    {
        IsConfirming = false;
        ConfirmLines = [];
        OnPropertyChanged(nameof(CanScan));
    }

    internal List<string> BuildConfirmLines()
    {
        var selected = _rows.Where(static r => r.IsSelected).ToList();
        var lines = new List<string>
        {
            CleanupText.Get("Cleanup.Advanced.Confirm.Body", SelectedFileCount.ToString("N0"), CleanupText.FormatBytes(SelectedBytes), SelectedFolderCount.ToString("N0")),
        };
        var undeterminable = selected.Count(static r => r.Item.Verdict == AdvancedVerdict.Undeterminable);
        if (undeterminable > 0) lines.Add(CleanupText.Get("Cleanup.Advanced.Confirm.Undeterminable", undeterminable.ToString("N0")));
        var recent = selected.Count(static r => r.Item.Hint == AdvancedHint.RecentlyModified);
        if (recent > 0) lines.Add(CleanupText.Get("Cleanup.Advanced.Confirm.Recent", recent.ToString("N0")));
        if (Settings.SecureDelete) lines.Add(CleanupText.Get("Cleanup.Confirm.SecureDelete"));
        return lines;
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (!IsConfirming || _scan is not { } scan) return;
        IsConfirming = false;
        ConfirmLines = [];
        var selection = new AdvancedCleanSelection(
            [.. _rows.Where(static r => r.IsSelected).Select(static r => r.Item)],
            [.. CustomFolders.Where(static f => f.IsSelected).Select(static f => f.Scan)]);
        if (!Coordinator.TryBegin(out var lease, default, showProcessing: true))
        {
            ReportBusy();
            return;
        }

        using var activeLease = lease!;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(activeLease.Token);
        BeginOperation(cts, CleanupText.Get("Cleanup.Action.Clean"));
        var options = new AdvancedCleanOptions
        {
            Filter = Settings.CreateExclusionMatcher(),
            Wipe = Settings.SecureDelete,
            GuardContext = GuardContext,
            Protection = Protection,
        };
        var total = selection.Items.Count + selection.Folders.Count;
        // 削除はファイルごとに通知が来るため、走査と同じく間引いて UI へ渡す (件数は間引き前に数える)。
        var progress = new ThrottledProgress(
            ProgressInterval,
            new Progress<(long Done, string Text)>(update =>
            {
                if (!IsRunning) return;
                ProgressText = CleanupText.Get("Cleanup.Run.Running", Math.Min(update.Done, total).ToString("N0"), total.ToString("N0"), update.Text);
            }));
        try
        {
            var outcome = await Task.Run(() => Cleaner.Clean(scan, selection, options, progress, cts.Token)).ConfigureAwait(true);
            await FinishDeleteAsync(outcome).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 削除の開始で世代は進んでいるため、結果は使えない。成功とは表示しない。
            LoggerBootstrap.Log.Error("高度なクリーンの削除に失敗しました", ex);
            ClearResults();
            SetStatus(() => CleanupText.Get("Cleanup.Advanced.Result.Failed"));
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task FinishDeleteAsync(AdvancedCleanOutcome outcome)
    {
        LastOutcome = outcome;
        ClearResults();
        SetStatus(() => DescribeOutcome(outcome));
        SetNotices(() => BuildOutcomeNotices(outcome));
        if (outcome.Expired) return;
        Settings.RecordRun(outcome.DeletedFiles, outcome.FreedBytes, outcome.Blocked + outcome.Retained + outcome.Rejected.Count, outcome.IsPartial, DateTimeOffset.Now);
        try
        {
            await Settings.SaveAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LoggerBootstrap.Log.Error("削除の実績を保存できませんでした", ex);
        }
    }

    internal static string DescribeOutcome(AdvancedCleanOutcome outcome)
    {
        var files = outcome.DeletedFiles.ToString("N0");
        var size = CleanupText.FormatBytes(outcome.FreedBytes);
        if (outcome.Expired) return CleanupText.Get("Cleanup.Advanced.Result.Expired");
        if (outcome.Cancelled) return CleanupText.Get("Cleanup.Advanced.Result.Cancelled", files, size);
        return CleanupText.Get(
            outcome.IsPartial ? "Cleanup.Advanced.Result.Partial" : "Cleanup.Advanced.Result.Done", files, outcome.DeletedDirectories.ToString("N0"), size);
    }

    /// <summary>部分失敗・残した・既に無かった・拒否を件数で示し、削除できなかった代表例を添える。</summary>
    internal static List<string> BuildOutcomeNotices(AdvancedCleanOutcome outcome)
    {
        var lines = new List<string>();
        if (outcome.Blocked > 0) lines.Add(CleanupText.Get("Cleanup.Advanced.Result.Blocked", outcome.Blocked.ToString("N0")));
        if (outcome.Retained > 0) lines.Add(CleanupText.Get("Cleanup.Advanced.Result.Retained", outcome.Retained.ToString("N0")));
        if (outcome.Gone > 0) lines.Add(CleanupText.Get("Cleanup.Advanced.Result.Gone", outcome.Gone.ToString("N0")));
        if (outcome.CoveredByFolders > 0) lines.Add(CleanupText.Get("Cleanup.Advanced.Result.Covered", outcome.CoveredByFolders.ToString("N0")));
        if (outcome.Rejected.Count > 0) lines.Add(CleanupText.Get("Cleanup.Advanced.Result.Rejected", outcome.Rejected.Count.ToString("N0")));
        if (outcome.WipedFiles + outcome.WipeSkipped > 0) lines.Add(CleanupText.Get("Cleanup.Run.Wiped", outcome.WipedFiles.ToString("N0"), outcome.WipeSkipped.ToString("N0")));
        lines.AddRange(outcome.BlockedExamples.Select(static example => CleanupText.Get("Cleanup.Common.Representatives", example)));
        return lines;
    }

    // ----- 実行状態 -----

    private void BeginOperation(CancellationTokenSource cts, string label)
    {
        _cts = cts;
        Label = label;
        ProgressText = string.Empty;
        IsRunning = true;
        CanCancel = true;
        NotifyBusyChanged();
    }

    private void EndOperation()
    {
        _cts = null;
        CanCancel = false;
        IsRunning = false;
        ProgressText = string.Empty;
        Label = string.Empty;
        NotifyBusyChanged();
    }

    private void NotifyBusyChanged()
    {
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(HasCurrentRow));
    }

    private void ReportBusy() =>
        SetStatus(() => CleanupText.Get("Status.Busy"));

    /// <summary>削除の進捗を UI へ渡す間隔 (走査の <see cref="AdvancedScanOptions.ProgressInterval"/> と同じ)。</summary>
    internal static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// 通知の件数は毎回数え、UI へは一定間隔に 1 回だけ渡す。呼び出しは削除のスレッドで行われるため、
    /// 間隔の判定はそこで済ませ、UI への投稿そのものを減らす。
    /// </summary>
    internal sealed class ThrottledProgress(TimeSpan interval, IProgress<(long Done, string Text)> target) : IProgress<string>
    {
        private readonly long _intervalMs = (long)interval.TotalMilliseconds;
        private long _count;
        private long _lastReport = long.MinValue;

        public void Report(string value)
        {
            var count = Interlocked.Increment(ref _count);
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastReport);
            if (last != long.MinValue && now - last < _intervalMs) return;
            if (Interlocked.CompareExchange(ref _lastReport, now, last) != last) return;
            target.Report((count, value));
        }
    }
}
