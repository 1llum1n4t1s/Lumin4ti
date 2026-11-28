using System;
using System.Collections.Generic;
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

/// <summary>
/// 「通常」タブ。走査 → 分類・規則・項目のツリー (件数・サイズ) → 選択 → 確認 → 削除 → 結果。
/// 走査は <see cref="Coordinator"/> の lease を <c>showProcessing:false</c> で (画面内に進捗と中断を出す)、
/// 削除は <c>true</c> で取る。走査・削除は利用者の操作でだけ始め、自動では走らせない。
/// 結果一覧は展開状態を持つ平らな行の一覧で、ListBox の仮想化に載せる (代表パスは規則ごとに数件だけで、規模は規則数)。
/// </summary>
public sealed partial class CommonCleanViewModel : CleanupOperationViewModel
{
    private readonly CommonCleanServices _services;
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private Dictionary<string, CommonCleanRowViewModel> _ruleRows = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<CommonCleanRowViewModel>> _pathRows = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<CleanupRuleCategory, List<CommonCleanRowViewModel>> _rulesByCategory = [];
    private List<CommonCleanRowViewModel> _categoryRows = [];
    private CleanupScanSnapshot? _snapshot;
    private CleanupRunResult? _lastRun;
    private bool _runFailed;
    private CancellationTokenSource? _cts;
    private Func<string> _status = () => CleanupText.Get("Cleanup.Common.NotScanned");

    public CommonCleanViewModel(
        CleanupScanService scan,
        CleanupRunService run,
        CleanupSettingsStore settings,
        ICommandExecutor executor,
        MaintenanceOperationCoordinator coordinator)
        : this(scan, run, settings, executor, coordinator, CommonCleanServices.Create(scan, run))
    {
    }

    internal CommonCleanViewModel(
        CleanupScanService scan,
        CleanupRunService run,
        CleanupSettingsStore settings,
        ICommandExecutor executor,
        MaintenanceOperationCoordinator coordinator,
        CommonCleanServices services)
    {
        Scan = scan;
        Run = run;
        Settings = settings;
        Executor = executor;
        Coordinator = coordinator;
        _services = services;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsRunning))
            {
                NotifyState();
            }
        };
        App.LocaleChanged += () => _services.Post(OnLocaleChanged);
    }

    internal CleanupScanService Scan { get; }

    internal CleanupRunService Run { get; }

    internal CleanupSettingsStore Settings { get; }

    /// <summary>外部プロセスの起動用。「場所を開く」は対話利用者の権限で開く <c>WindowsExplorerLauncher</c> を使う。</summary>
    internal ICommandExecutor Executor { get; }

    internal MaintenanceOperationCoordinator Coordinator { get; }

    [ObservableProperty]
    private IReadOnlyList<CommonCleanRowViewModel> rows = [];

    [ObservableProperty]
    private string filterText = string.Empty;

    [ObservableProperty]
    private bool hideEmpty;

    [ObservableProperty]
    private bool isConfirming;

    [ObservableProperty]
    private bool isConfirmingUserFiles;

    [ObservableProperty]
    private string confirmBody = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<string> confirmNotes = [];

    [ObservableProperty]
    private IReadOnlyList<CommonCleanUserFileEntryViewModel> userFileEntries = [];

    [ObservableProperty]
    private bool isStale;

    [ObservableProperty]
    private double progressValue;

    [ObservableProperty]
    private double progressMaximum = 1;

    [ObservableProperty]
    private bool hasResult;

    [ObservableProperty]
    private bool resultIsSuccess;

    [ObservableProperty]
    private string resultBanner = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<string> resultNotes = [];

    [ObservableProperty]
    private IReadOnlyList<CommonCleanResultLineViewModel> resultLines = [];

    public string StatusText => _status();

    public bool CanScan => !IsRunning && !IsConfirming && !IsConfirmingUserFiles;

    public bool HasSnapshot => _snapshot is not null && !IsStale;

    public bool CanChangeSelection => HasSnapshot && CanScan;

    public bool CanRun => CanChangeSelection && _selected.Count > 0;

    public bool CanExport => CanScan && (_snapshot is not null || _lastRun is not null);

    public bool HasRows => Rows.Count > 0;

    /// <summary>表示用: まだ走査していない (または実行後に走査結果が消えた) とき true。</summary>
    public bool IsNotScanned => _snapshot is null;

    /// <summary>表示用: 走査前の空状態を出すか (走査中と実行結果の表示中は出さない)。</summary>
    public bool ShowNotScannedState => IsNotScanned && !HasResult && !IsRunning;

    /// <summary>
    /// 表示用: 通知欄に状態文を出すか。未走査の案内文は空状態と重複するため出さず、走査中は出さない。
    /// 失効は警告帯が文を出すため、ここでは出さない。
    /// </summary>
    public bool ShowStatusLine => !IsStale && !IsRunning && !(IsNotScanned && StatusText == CleanupText.Get("Cleanup.Common.NotScanned"));

    /// <summary>表示用: 走査結果の列見出しと一覧を出すか (実行結果の表示中は出さない)。</summary>
    public bool ShowScanRows => HasRows && !HasResult;

    /// <summary>表示用: 実行が失敗したときの結果カード (赤)。</summary>
    public bool ResultIsError => HasResult && _runFailed;

    /// <summary>表示用: 成功でも失敗でもない結果 (部分失敗・失効・取消) の結果カード (警告色)。</summary>
    public bool ResultIsWarning => HasResult && !_runFailed && !ResultIsSuccess;

    public string UserFilesWarningText => CleanupText.Get("Cleanup.Impact.UserFiles.Warning");

    /// <summary>表示用: 実行後は走査結果が使えない旨の案内 (結果カードの下の注記)。</summary>
    public string RescanAfterRunText => CleanupText.Get("Cleanup.Ui.RescanAfterRun");

    public string ScanButtonText => CleanupText.Get(_snapshot is null ? "Cleanup.Action.Scan" : "Cleanup.Action.Rescan");

    public string SelectionText
    {
        get
        {
            if (_snapshot is null)
            {
                return string.Empty;
            }

            var chosen = SelectedItems().ToList();
            var selectable = _snapshot.Items.Count(i => i.CanSelect);
            return CleanupText.Get("Cleanup.Common.Selection", chosen.Count, selectable, CleanupText.FormatBytes(_snapshot.Measure(chosen.Select(i => i.Rule.Id)).Bytes));
        }
    }

    protected override void OnCancelRequested()
    {
        CanCancel = false;
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    partial void OnFilterTextChanged(string value) => RefreshRows();

    partial void OnHideEmptyChanged(bool value) => RefreshRows();

    partial void OnIsConfirmingChanged(bool value) => NotifyState();

    partial void OnIsConfirmingUserFilesChanged(bool value) => NotifyState();

    partial void OnIsStaleChanged(bool value) => NotifyState();

    // ----- 走査 -----

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (!CanScan || !TryBegin(showProcessing: false, out var lease))
        {
            return;
        }

        using var operation = lease;
        ClearTree();
        ClearResult();
        BeginOperation(CleanupText.Get("Cleanup.Action.Scan"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(operation!.Token);
        _cts = cts;
        try
        {
            var progress = new CommonCleanProgress<CleanupScanProgress>(OnScanProgress, _services.Post);
            var snapshot = await Task.Run(() => _services.Scan(progress, cts.Token));
            ApplySnapshot(snapshot);
        }
        catch (OperationCanceledException)
        {
            SetStatus(() => CleanupText.Get("Cleanup.Common.ScanCanceled"));
        }
        catch (Exception ex)
        {
            LoggerBootstrap.Log.Error("通常クリーンの走査に失敗しました", ex);
            SetStatus(() => CleanupText.Get("Cleanup.Common.ScanFailed"));
        }
        finally
        {
            _cts = null;
            EndOperation();
        }
    }

    private bool TryBegin(bool showProcessing, out MaintenanceOperationCoordinator.OperationLease? lease)
    {
        if (Coordinator.TryBegin(out lease, showProcessing: showProcessing))
        {
            return true;
        }

        SetStatus(() => CleanupText.Get("Status.Busy"));
        return false;
    }

    private void BeginOperation(string label)
    {
        Label = label;
        ProgressText = string.Empty;
        ProgressValue = 0;
        ProgressMaximum = 1;
        CanCancel = true;
        IsRunning = true;
    }

    private void EndOperation()
    {
        IsRunning = false;
        CanCancel = false;
        ProgressText = string.Empty;
        Label = string.Empty;
    }

    private void OnScanProgress(CleanupScanProgress progress)
    {
        if (!IsRunning || progress.CurrentName is null)
        {
            return;
        }

        ProgressMaximum = Math.Max(1, progress.Total);
        ProgressValue = progress.Completed;
        ProgressText = CleanupText.Get(
            "Cleanup.Common.Scanning", Math.Min(progress.Completed + 1, progress.Total), progress.Total, progress.CurrentName);
    }

    private void ApplySnapshot(CleanupScanSnapshot snapshot)
    {
        _snapshot = snapshot;
        OnPropertyChanged(nameof(IsNotScanned));
        IsStale = false;
        _selected.Clear();
        foreach (var item in snapshot.Items.Where(i => i.CanSelect && i.DefaultSelected && i.Rule.Impact != CleanupRuleImpact.UserFiles))
        {
            _selected.Add(item.Rule.Id);
        }

        _expanded.Clear();
        foreach (var category in snapshot.Items.Where(i => i.State != CleanupItemState.NotDetected).Select(i => i.Rule.Category).Distinct())
        {
            _expanded.Add(CategoryKey(category));
        }

        BuildRowCache(snapshot);
        RefreshRows();
        RefreshSelection();
        var found = snapshot.Items.Count(i => i.CanSelect);
        SetStatus(found == 0
            ? () => CleanupText.Get("Cleanup.Common.NothingFound")
            : () => CleanupText.Get(
                "Cleanup.Common.ScanDone", found, snapshot.TotalFiles + snapshot.TotalItems, CleanupText.FormatBytes(snapshot.TotalBytes)));
    }

    private void ClearTree()
    {
        _snapshot = null;
        OnPropertyChanged(nameof(IsNotScanned));
        _selected.Clear();
        _ruleRows = new(StringComparer.OrdinalIgnoreCase);
        _pathRows = new(StringComparer.OrdinalIgnoreCase);
        _rulesByCategory = [];
        _categoryRows = [];
        IsStale = false;
        Rows = [];
        RefreshSelection();
        OnPropertyChanged(nameof(HasRows));
        NotifyDisplay();
    }

    private void MarkStale(Func<string> status)
    {
        IsStale = true;
        SetStatus(status);
    }

    private bool EnsureCurrent()
    {
        if (_snapshot is not null && _services.IsCurrent(_snapshot))
        {
            return true;
        }

        MarkStale(() => CleanupText.Get("Cleanup.Common.Stale"));
        return false;
    }

    // ----- ツリー -----

    private static string CategoryKey(CleanupRuleCategory category) => "cat:" + category;

    private void BuildRowCache(CleanupScanSnapshot snapshot)
    {
        var items = snapshot.Items.Where(i => i.State != CleanupItemState.NotDetected).ToList();
        _ruleRows = new(StringComparer.OrdinalIgnoreCase);
        _pathRows = new(StringComparer.OrdinalIgnoreCase);
        _rulesByCategory = [];
        foreach (var item in items)
        {
            var row = CreateRuleRow(item);
            if (!_ruleRows.TryAdd(item.Rule.Id, row))
            {
                continue;
            }

            row.SetExpandedSilently(_expanded.Contains(item.Rule.Id));
            _pathRows[item.Rule.Id] = [.. item.Representatives.Select(p => CreatePathRow(item, p))];
            if (!_rulesByCategory.TryGetValue(item.Rule.Category, out var list))
            {
                _rulesByCategory[item.Rule.Category] = list = [];
            }

            list.Add(row);
        }

        _categoryRows = [.. _rulesByCategory.OrderBy(p => p.Key).Select(p => CreateCategoryRow(p.Key, p.Value))];
    }

    private CommonCleanRowViewModel CreateRuleRow(CleanupScanItem item)
    {
        var name = CleanupText.RuleName(item.Rule);
        var found = item.State == CleanupItemState.Found;
        var size = found ? CleanupText.FormatBytes(item.Bytes) : string.Empty;
        var state = CleanupText.Get($"Cleanup.State.{item.State}");
        return new CommonCleanRowViewModel(this, CommonCleanRowKind.Rule, item.Rule.Id, name)
        {
            Item = item,
            StateText = state,
            FilesText = found ? (item.Files + item.Items).ToString("N0") : string.Empty,
            SizeText = size,
            ImpactText = CleanupText.Get($"Cleanup.Impact.{item.Rule.Impact}"),
            NoteText = string.Join('\n', BuildNote(item)),
            AutomationName = CleanupText.Get("Cleanup.A11y.Row", name, state, size.Length > 0 ? size : state),
            SelectAutomationName = CleanupText.Get("Cleanup.A11y.SelectRow", name),
            ExpandAutomationName = CleanupText.Get("Cleanup.A11y.Expand", name),
            CanSelect = item.CanSelect,
            IsExpandable = item.Representatives.Count > 0,
        };
    }

    private CommonCleanRowViewModel CreatePathRow(CleanupScanItem item, string path) =>
        new(this, CommonCleanRowKind.Path, path, path)
        {
            Item = item,
            AutomationName = path,
        };

    private CommonCleanRowViewModel CreateCategoryRow(CleanupRuleCategory category, List<CommonCleanRowViewModel> rules)
    {
        var found = rules.Select(r => r.Item!).Where(i => i.State == CleanupItemState.Found).ToList();
        var name = CleanupText.Get($"Cleanup.Category.{category}");
        var totals = _snapshot?.Measure(found.Select(i => i.Rule.Id)) ?? default;
        var size = CleanupText.FormatBytes(totals.Bytes);
        var row = new CommonCleanRowViewModel(this, CommonCleanRowKind.Category, CategoryKey(category), name)
        {
            Category = category,
            FilesText = (totals.Files + totals.Items).ToString("N0"),
            SizeText = size,
            AutomationName = CleanupText.Get("Cleanup.A11y.Row", name, rules.Count.ToString("N0"), size),
            SelectAutomationName = CleanupText.Get("Cleanup.A11y.SelectRow", name),
            ExpandAutomationName = CleanupText.Get("Cleanup.A11y.Expand", name),
            CanSelect = SelectableByCategory(rules).Any(),
            IsExpandable = true,
        };
        row.SetExpandedSilently(_expanded.Contains(row.Key));
        return row;
    }

    /// <summary>規則の行に添える警告・理由・補足。成功と偽らず、除外・失敗・実行中の製品も示す。</summary>
    internal static IReadOnlyList<string> BuildNote(CleanupScanItem item)
    {
        var rule = item.Rule;
        var lines = new List<string>();
        if (item.Reason != CleanupUnavailableReason.None)
        {
            lines.Add(CleanupText.Get($"Cleanup.Reason.{item.Reason}"));
        }

        lines.AddRange(item.Detail.Select(CleanupText.Detail));
        if (rule.WarningKey is { } warningKey)
        {
            lines.Add(CleanupText.GetByCoreKey(warningKey));
        }

        if (rule.Impact != CleanupRuleImpact.Junk)
        {
            lines.Add(CleanupText.Get($"Cleanup.Impact.{rule.Impact}.Warning"));
        }

        if (!rule.Recommended && item.CanSelect)
        {
            lines.Add(CleanupText.Get("Cleanup.Warning.NotRecommended"));
        }

        AddCounts(lines, item);
        if (item.RunningProcesses.Count > 0)
        {
            lines.Add(CleanupText.Get("Cleanup.Common.RunningProcesses", string.Join(", ", item.RunningProcesses)));
        }

        if (rule.Services.Count > 0)
        {
            lines.Add(CleanupText.Get("Cleanup.Common.ServiceNote", string.Join(", ", rule.Services)));
        }

        return lines;
    }

    private static void AddCounts(List<string> lines, CleanupScanItem item)
    {
        if (item.State == CleanupItemState.Unmeasurable)
        {
            lines.Add(CleanupText.Get("Cleanup.Common.Unmeasurable"));
        }

        if (item.Retained > 0)
        {
            lines.Add(CleanupText.Get("Cleanup.Common.Retained", item.Retained));
        }

        if (item.Unreadable > 0)
        {
            lines.Add(CleanupText.Get("Cleanup.Common.Unreadable", item.Unreadable));
        }

        if (item.SkippedLinks > 0)
        {
            lines.Add(CleanupText.Get("Cleanup.Common.SkippedLinks", item.SkippedLinks));
        }
    }

    private void RefreshRows()
    {
        var needle = FilterText.Trim();
        var list = new List<CommonCleanRowViewModel>();
        foreach (var category in _categoryRows)
        {
            var visible = _rulesByCategory[category.Category!.Value]
                .Where(r => IsVisible(r, needle))
                .ToList();
            if (visible.Count == 0)
            {
                continue;
            }

            list.Add(category);
            if (category.IsExpanded)
            {
                AddRuleRows(list, visible);
            }
        }

        Rows = list;
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(ShowScanRows));
    }

    private void AddRuleRows(List<CommonCleanRowViewModel> list, List<CommonCleanRowViewModel> rules)
    {
        foreach (var rule in rules)
        {
            list.Add(rule);
            if (rule.IsExpanded)
            {
                list.AddRange(_pathRows[rule.Key]);
            }
        }
    }

    private bool IsVisible(CommonCleanRowViewModel rule, string needle) =>
        (needle.Length == 0 || rule.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase)) &&
        !(HideEmpty && rule.Item!.State == CleanupItemState.Empty);

    internal void OnExpandedChanged(CommonCleanRowViewModel row)
    {
        if (row.IsExpanded)
        {
            _expanded.Add(row.Key);
        }
        else
        {
            _expanded.Remove(row.Key);
        }

        RefreshRows();
    }

    // ----- 選択 -----

    /// <summary>分類のチェックで一括して選べる規則。受信ファイルなどの <c>UserFiles</c> は含めない (個別選択と再確認が必要)。</summary>
    private static IEnumerable<CommonCleanRowViewModel> SelectableByCategory(IEnumerable<CommonCleanRowViewModel> rules) =>
        rules.Where(r => r.CanSelect && r.Item!.Rule.Impact != CleanupRuleImpact.UserFiles);

    internal bool? CheckStateOf(CommonCleanRowViewModel row)
    {
        if (row.IsRule)
        {
            return _selected.Contains(row.Key);
        }

        if (!row.IsCategory)
        {
            return false;
        }

        var rules = SelectableByCategory(_rulesByCategory.GetValueOrDefault(row.Category!.Value) ?? []).ToList();
        var chosen = rules.Count(r => _selected.Contains(r.Key));
        return chosen == 0 ? false : chosen == rules.Count ? true : null;
    }

    internal void SetChecked(CommonCleanRowViewModel row, bool value)
    {
        if (!CanChangeSelection || !row.CanSelect)
        {
            return;
        }

        var ids = row.IsRule
            ? [row.Key]
            : SelectableByCategory(_rulesByCategory[row.Category!.Value]).Select(r => r.Key).ToList();
        foreach (var id in ids)
        {
            if (value)
            {
                _selected.Add(id);
            }
            else
            {
                _selected.Remove(id);
            }
        }

        RefreshSelection();
    }

    private void RefreshSelection()
    {
        foreach (var row in _categoryRows.Concat(_ruleRows.Values))
        {
            row.RefreshCheck();
        }

        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(CanRun));
    }

    private IEnumerable<CleanupScanItem> SelectedItems() =>
        _snapshot?.Items.Where(i => i.CanSelect && _selected.Contains(i.Rule.Id)) ?? [];

    private void ReplaceSelection(IEnumerable<string> ids)
    {
        if (!CanChangeSelection)
        {
            return;
        }

        _selected.Clear();
        _selected.UnionWith(ids);
        RefreshSelection();
    }

    [RelayCommand]
    private void SelectRecommended() => ReplaceSelection(_snapshot is null ? [] : CleanupSelection.Recommended(_snapshot));

    [RelayCommand]
    private void SelectAll() => ReplaceSelection(_snapshot is null ? [] : CleanupSelection.All(_snapshot));

    [RelayCommand]
    private void SelectNone() => ReplaceSelection([]);

    // ----- 確認 -----

    [RelayCommand]
    private void RequestRun()
    {
        if (!CanRun || !EnsureCurrent())
        {
            return;
        }

        RefreshConfirmTexts();
        IsConfirming = true;
    }

    /// <summary>確認の本文と注意書きを、現在の選択と言語で作る (確認の状態は変えない)。</summary>
    private void RefreshConfirmTexts()
    {
        var chosen = SelectedItems().ToList();
        var totals = _snapshot?.Measure(chosen.Select(i => i.Rule.Id)) ?? default;
        ConfirmBody = CleanupText.Get(
            "Cleanup.Confirm.Common.Body",
            chosen.Count,
            totals.Files + totals.Items,
            CleanupText.FormatBytes(totals.Bytes));
        ConfirmNotes = BuildConfirmNotes(chosen, Settings.SecureDelete);
    }

    /// <summary>確認欄の注意書き。推奨外・サインアウト・利用者のファイル・影響・安全な削除・起動中の製品・停止するサービスを示す。</summary>
    internal static IReadOnlyList<string> BuildConfirmNotes(IReadOnlyList<CleanupScanItem> chosen, bool secureDelete)
    {
        var notes = new List<string>();
        AddCountNote(notes, "Cleanup.Confirm.NotRecommended", chosen.Count(i => !i.Rule.Recommended));
        AddCountNote(notes, "Cleanup.Confirm.Signout", chosen.Count(i => i.Rule.Impact == CleanupRuleImpact.Signout));
        AddCountNote(notes, "Cleanup.Confirm.UserFiles", chosen.Count(i => i.Rule.Impact == CleanupRuleImpact.UserFiles));
        foreach (var impact in new[] { CleanupRuleImpact.Trace, CleanupRuleImpact.Signout, CleanupRuleImpact.Rebuild })
        {
            if (chosen.Any(i => i.Rule.Impact == impact))
            {
                notes.Add(CleanupText.Get($"Cleanup.Impact.{impact}.Warning"));
            }
        }

        notes.AddRange(chosen.Select(i => i.Rule.WarningKey).OfType<string>().Distinct().Select(k => CleanupText.GetByCoreKey(k)));
        if (secureDelete)
        {
            notes.Add(CleanupText.Get("Cleanup.Confirm.SecureDelete"));
        }

        if (chosen.Any(i => i.RunningProcesses.Count > 0))
        {
            notes.Add(CleanupText.Get("Cleanup.Confirm.RunningProcess"));
        }

        var services = chosen.SelectMany(i => i.Rule.Services).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (services.Count > 0)
        {
            notes.Add(CleanupText.Get("Cleanup.Common.ServiceNote", string.Join(", ", services)));
        }

        return notes;
    }

    private static void AddCountNote(List<string> notes, string key, int count)
    {
        if (count > 0)
        {
            notes.Add(CleanupText.Get(key, count));
        }
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        IsConfirming = false;
        IsConfirmingUserFiles = false;
    }

    [RelayCommand]
    private async Task ConfirmRunAsync()
    {
        if (!IsConfirming)
        {
            return;
        }

        if (!EnsureCurrent())
        {
            IsConfirming = false;
            return;
        }

        var userFiles = SelectedItems().Where(i => i.Rule.Impact == CleanupRuleImpact.UserFiles).ToList();
        IsConfirming = false;
        if (userFiles.Count > 0)
        {
            UserFileEntries =
            [
                .. userFiles.Select(i =>
                {
                    var name = CleanupText.RuleName(i.Rule);
                    return new CommonCleanUserFileEntryViewModel(
                        i.Rule.Id, name, CleanupText.FormatBytes(i.Bytes), CleanupText.Get("Cleanup.A11y.SelectRow", name));
                }),
            ];
            IsConfirmingUserFiles = true;
            return;
        }

        await ExecuteRunAsync(SelectedItems().Select(i => i.Rule.Id).ToList(), []);
    }

    /// <summary>利用者のファイルの再確認。チェックを入れた規則だけを実行し、入れなかった規則は選択から外す。</summary>
    [RelayCommand]
    private async Task ConfirmUserFilesAsync()
    {
        if (!IsConfirmingUserFiles)
        {
            return;
        }

        IsConfirmingUserFiles = false;
        if (!EnsureCurrent())
        {
            return;
        }

        var confirmed = UserFileEntries.Where(e => e.IsConfirmed).Select(e => e.RuleId).ToList();
        var declined = UserFileEntries.Where(e => !e.IsConfirmed).Select(e => e.RuleId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = SelectedItems().Select(i => i.Rule.Id).Where(id => !declined.Contains(id)).ToList();
        await ExecuteRunAsync(ids, confirmed);
    }

    // ----- 削除 -----

    private async Task ExecuteRunAsync(IReadOnlyCollection<string> ids, IReadOnlyCollection<string> confirmedUserFiles)
    {
        if (_snapshot is null || ids.Count == 0 || !TryBegin(showProcessing: true, out var lease))
        {
            return;
        }

        using var operation = lease;
        var snapshot = _snapshot;
        ClearResult();
        BeginOperation(CleanupText.Get("Cleanup.Action.Clean"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(operation!.Token);
        _cts = cts;
        CleanupRunResult? result = null;
        var failed = false;
        try
        {
            var request = new CleanupRunRequest(snapshot, ids)
            {
                ConfirmedUserFileRuleIds = confirmedUserFiles,
                Prompts = _services.Prompts,
            };
            var progress = new CommonCleanProgress<string>(OnRunProgress, _services.Post);
            result = await Task.Run(() => _services.Run(request, progress, cts.Token));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LoggerBootstrap.Log.Error("通常クリーンの削除に失敗しました", ex);
            failed = true;
        }
        finally
        {
            _cts = null;
            EndOperation();
        }

        // 実行は走査結果を 1 回で使い切る (取消・失敗・部分成功でも同じ)。最新の状態は再走査で確かめる。
        ClearTree();
        SetStatus(() => CleanupText.Get("Cleanup.Common.NotScanned"));
        ApplyRunResult(result, failed);
    }

    private void OnRunProgress(string line)
    {
        if (IsRunning)
        {
            ProgressText = line;
        }
    }

    private void ApplyRunResult(CleanupRunResult? result, bool failed)
    {
        _lastRun = result;
        _runFailed = failed;
        HasResult = true;
        RefreshResultTexts();
        NotifyState();
    }

    private void ClearResult()
    {
        HasResult = false;
        _lastRun = null;
        _runFailed = false;
        OnPropertyChanged(nameof(ResultIsError));
        OnPropertyChanged(nameof(ResultIsWarning));
        ResultLines = [];
        ResultNotes = [];
        ResultBanner = string.Empty;
        NotifyDisplay();
    }

    private void RefreshResultTexts()
    {
        if (!HasResult)
        {
            return;
        }

        (ResultBanner, ResultIsSuccess) = BuildBanner(_lastRun, _runFailed);
        ResultNotes = _lastRun is null ? [] : BuildResultNotes(_lastRun);
        ResultLines = _lastRun is null ? [] : [.. _lastRun.Items.Where(i => i.Status != CleanupRunStatus.NothingToDo).Select(BuildResultLine)];
    }

    /// <summary>実行結果の見出し。全規則が成功したときだけ成功とし、取消・失効・部分失敗を成功と偽らない。</summary>
    internal static (string Text, bool Success) BuildBanner(CleanupRunResult? result, bool failed)
    {
        if (failed)
        {
            return (CleanupText.Get("Cleanup.Run.Error"), false);
        }

        if (result is null)
        {
            return (CleanupText.Get("Cleanup.Run.Canceled", 0, CleanupText.FormatBytes(0)), false);
        }

        var files = result.FilesDeleted;
        var size = CleanupText.FormatBytes(result.BytesFreed);
        if (result.StaleScan)
        {
            return (CleanupText.Get("Cleanup.Run.Stale"), false);
        }

        if (result.Canceled)
        {
            return (CleanupText.Get("Cleanup.Run.Canceled", files, size), false);
        }

        return result.IsComplete
            ? (CleanupText.Get("Cleanup.Run.Done", files, size), true)
            : (CleanupText.Get("Cleanup.Run.DonePartial", files, size), false);
    }

    internal static IReadOnlyList<string> BuildResultNotes(CleanupRunResult result)
    {
        var notes = new List<string>();
        AddCountNote(notes, "Cleanup.Run.Retained", (int)Math.Min(int.MaxValue, result.Items.Sum(i => i.Retained)));
        AddCountNote(notes, "Cleanup.Run.Blocked", (int)Math.Min(int.MaxValue, result.Items.Sum(i => i.Blocked)));
        var wiped = result.Items.Sum(i => i.WipedFiles);
        if (wiped > 0)
        {
            notes.Add(CleanupText.Get("Cleanup.Run.Wiped", wiped, result.Items.Sum(i => i.WipeSkipped)));
        }

        return notes;
    }

    internal static CommonCleanResultLineViewModel BuildResultLine(CleanupRuleRunResult item)
    {
        var detail = new List<string>();
        if (item.SkipReason != CleanupSkipReason.None)
        {
            detail.Add(CleanupText.Get($"Cleanup.Run.Skip.{item.SkipReason}"));
        }

        detail.AddRange(item.Details.Select(CleanupText.Detail));
        if (item.IsSuccess)
        {
            detail.Add($"{item.Files + item.Items:N0} / {CleanupText.FormatBytes(item.Bytes)}");
        }

        return new CommonCleanResultLineViewModel(
            CleanupText.RuleName(item.Rule),
            CleanupText.Get($"Cleanup.Run.Status.{item.Status}"),
            string.Join(" / ", detail),
            item.IsSuccess);
    }

    // ----- 書き出し・場所を開く・除外 -----

    /// <summary>書き出す内容。走査結果があればそれを、無ければ直近の実行結果を返す。</summary>
    internal string? BuildExportText()
    {
        var labels = new CleanupTextLabels(
            CleanupText.Get("Cleanup.Export.Header.Name"),
            CleanupText.Get("Cleanup.Export.Header.Category"),
            CleanupText.Get("Cleanup.Export.Header.State"),
            CleanupText.Get("Cleanup.Export.Header.Files"),
            CleanupText.Get("Cleanup.Export.Header.Items"),
            CleanupText.Get("Cleanup.Export.Header.Bytes"),
            CleanupText.Get("Cleanup.Export.Header.Retained"),
            CleanupText.Get("Cleanup.Export.Header.Status"),
            CleanupText.Get("Cleanup.Export.Header.Details"),
            CleanupText.Get("Cleanup.Export.Header.Total"))
        {
            DescribeMessage = CleanupText.Detail,
        };
        return _snapshot is not null
            ? CleanupResultsText.FromScan(_snapshot, labels)
            : _lastRun is not null ? CleanupResultsText.FromRun(_lastRun, labels) : null;
    }

    /// <summary>利用者が選んだ絶対パスへテキストを書き出す (保存先の選択は View が行う)。</summary>
    public async Task ExportAsync(string path)
    {
        var text = BuildExportText();
        if (text is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => CleanupResultsText.Save(path, text));
            SetStatus(() => CleanupText.Get("Cleanup.Export.Done", path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus(() => CleanupText.Get("Cleanup.Export.Failed", CleanupText.Detail(ex.Message)));
        }
    }

    /// <summary>項目を開くのではなく、含まれるフォルダーを対話利用者の権限で開く (ファイルを実行しない)。</summary>
    internal async Task OpenLocationAsync(string path)
    {
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            SetStatus(() => CleanupText.Get("Cleanup.OpenLocation.Failed", path));
            return;
        }

        try
        {
            await _services.OpenFolder(folder);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LoggerBootstrap.Log.Error("場所を開けませんでした", ex);
            SetStatus(() => CleanupText.Get("Cleanup.OpenLocation.Failed", path));
        }
    }

    /// <summary>代表パスを今後の走査・削除から除外する。設定が変わるため、走査結果は失効して再走査が必要になる。</summary>
    internal async Task IgnorePathAsync(string path)
    {
        if (IsRunning)
        {
            return;
        }

        var kind = Directory.Exists(path) ? CleanupExclusionKind.Folder : CleanupExclusionKind.File;
        switch (Settings.AddExclusion(kind, path))
        {
            case CleanupAddResult.Added:
                await SaveExclusionAsync(path);
                break;
            case CleanupAddResult.Duplicate:
                SetStatus(() => CleanupText.Get("Cleanup.Ignore.Duplicate", path));
                break;
            default:
                SetStatus(() => CleanupText.Get("Cleanup.Ignore.Failed", path));
                break;
        }
    }

    private async Task SaveExclusionAsync(string path)
    {
        var saved = true;
        try
        {
            await Settings.SaveAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LoggerBootstrap.Log.Warn($"除外を保存できませんでした ({ex.GetType().Name})");
            saved = false;
        }

        MarkStale(saved
            ? () => CleanupText.Get("Cleanup.Ignore.Done", path)
            : () => CleanupText.Get("Cleanup.Ignore.SaveFailed", path));
    }

    // ----- 状態の通知 -----

    private void SetStatus(Func<string> status)
    {
        _status = status;
        OnPropertyChanged(nameof(StatusText));
        NotifyDisplay();
    }

    private void NotifyDisplay()
    {
        OnPropertyChanged(nameof(ShowNotScannedState));
        OnPropertyChanged(nameof(ShowStatusLine));
        OnPropertyChanged(nameof(ShowScanRows));
    }

    private void NotifyState()
    {
        NotifyDisplay();
        OnPropertyChanged(nameof(ResultIsError));
        OnPropertyChanged(nameof(ResultIsWarning));
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(CanChangeSelection));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(ScanButtonText));
    }

    private void OnLocaleChanged()
    {
        if (_snapshot is not null)
        {
            BuildRowCache(_snapshot);
            RefreshRows();
            RefreshSelection();
        }

        if (IsConfirming && _snapshot is not null)
        {
            RefreshConfirmTexts();
        }

        RefreshResultTexts();
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ScanButtonText));
        OnPropertyChanged(nameof(RescanAfterRunText));
    }
}
