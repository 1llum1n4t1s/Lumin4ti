using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;
using Lumin4ti.Core.Services.Windows.Cleanup.Slim;
using Lumin4ti.UI.Services;

namespace Lumin4ti.UI.ViewModels;

/// <summary>結果の代表理由 (状態と理由ごとの件数) を作る共通処理。</summary>
internal static class RegistryResultFormat
{
    /// <summary>代表理由の最大件数 (docs/design/registry-cleaner.md)。</summary>
    public const int MaxReasons = 8;

    /// <summary>成功以外の状態を (状態, 理由) ごとにまとめ、多い順に最大 <see cref="MaxReasons"/> 件の行にする。残りの種類数も返す。</summary>
    public static (IReadOnlyList<string> Lines, int More) Reasons(IEnumerable<(RegistryItemState State, string? Reason)> items)
    {
        var groups = items
            .Where(item => item.State is not (RegistryItemState.Deleted or RegistryItemState.Restored or RegistryItemState.AlreadyPresent))
            .GroupBy(item => (item.State, Reason: CleanupText.Detail(item.Reason)))
            .OrderByDescending(group => group.Count())
            .ToList();
        var lines = groups
            .Take(MaxReasons)
            .Select(group => CleanupText.GetCounted("Cleanup.Registry.Ui.ReasonLine", group.Count(), CleanupText.Get($"Cleanup.Registry.Ui.State.{group.Key.State}"), group.Key.Reason, group.Count()))
            .ToList();
        return (lines, Math.Max(0, groups.Count - MaxReasons));
    }

    public static string OverallText(RegistryOverallState overall, int done, int notDone) => overall == RegistryOverallState.Success
        ? CleanupText.GetCounted("Cleanup.Registry.Ui.Result.Success", done, done, notDone)
        : CleanupText.Get($"Cleanup.Registry.Ui.Result.{overall}", done, notDone);
}

/// <summary>
/// 「レジストリ」タブの削除側。走査 → カテゴリー別選択 → 確認 (アンインストールは項目ごとの承認) → 実行 → 結果。
/// 走査は <c>showProcessing:false</c>、実行は <c>showProcessing:true</c> の lease で行う。実行した走査結果は使用済みで、
/// 結果を見せたうえで再走査を求める。動作が速くなる機能ではなく、バックアップから戻せる旨を <see cref="PurposeText"/> で常に示す。
/// </summary>
internal sealed partial class RegistryCleanViewModel : RegistryOperationViewModel
{
    private readonly IRegistryCleanBackend _backend;
    private RegistryScanResult? _scan;
    private Func<string> _status;
    private RegistryCleanRunResult? _lastResult;
    private bool _statusIsPlaceholder = true;
    private Func<string>? _userScopeNote;

    public RegistryCleanViewModel(IRegistryCleanBackend backend, MaintenanceOperationCoordinator coordinator)
        : base(coordinator)
    {
        _backend = backend;
        Groups = [.. RegistryCleanCategories.All.Select(definition => new RegistryCleanGroupViewModel(definition) { ExpandedChanged = OnGroupExpandedChanged })];
        _status = () => CleanupText.Get("Cleanup.Registry.Ui.NotScanned");
        statusText = _status();
        App.LocaleChanged += () => PostToUi(OnLocaleChanged);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsRunning))
            {
                RefreshState();
            }
        };
    }

    /// <summary>実行 (バックアップを書いた) の後に呼ぶ。復元側が一覧を引き直すために購読する。</summary>
    internal event Action? BackupsChanged;

    /// <summary>復元ポイントの判断を聞く窓口 (View が設定する)。null のときは安全側の中止になる。</summary>
    internal ISlimRestorePointPrompt? RestorePointPrompt { get; set; }

    public ObservableCollection<RegistryCleanGroupViewModel> Groups { get; }

    /// <summary>
    /// 一覧に出す行。各カテゴリーの見出し (<see cref="RegistryCleanGroupViewModel"/>) と、開いているカテゴリーの項目行
    /// (<see cref="RegistryCleanRowViewModel"/>) をカテゴリー順に並べる。走査前・再走査で結果を捨てたときは空。
    /// </summary>
    public RangeObservableCollection<object> DisplayItems { get; } = [];

    /// <summary>確認画面で承認を求める行 (確認を始めた時点の、承認が要るカテゴリーの選択行)。</summary>
    public ObservableCollection<RegistryCleanRowViewModel> ApprovalRows { get; } = [];

    /// <summary>実行結果の代表理由 (最大 8 行)。</summary>
    public ObservableCollection<string> ResultReasons { get; } = [];

    [ObservableProperty]
    private string statusText;

    /// <summary>実行前に復元ポイントを作るか。既定オンで、確認を始めるたびにオンへ戻る (その回だけの選択)。</summary>
    [ObservableProperty]
    private bool createRestorePoint = true;

    [ObservableProperty]
    private bool isConfirming;

    [ObservableProperty]
    private string confirmText = string.Empty;

    /// <summary>実行中の操作が削除か (走査と中断ボタンの文言を分ける)。</summary>
    [ObservableProperty]
    private bool isDeleting;

    /// <summary>HKCU 系を走査できなかった理由 (走査できたときは空)。</summary>
    [ObservableProperty]
    private string userScopeNote = string.Empty;

    [ObservableProperty]
    private string resultSummary = string.Empty;

    [ObservableProperty]
    private string resultMoreText = string.Empty;

    [ObservableProperty]
    private string resultMessageText = string.Empty;

    [ObservableProperty]
    private string backupText = string.Empty;

    [ObservableProperty]
    private string restorePointResultText = string.Empty;

    /// <summary>走査結果が無く、再走査が要る (実行後・古い・復元後)。</summary>
    [ObservableProperty]
    private bool needsRescan;

    /// <summary>この機能の位置付け (速くなるものではなく、バックアップから戻せる)。</summary>
    public string PurposeText => CleanupText.Get("Cleanup.Registry.Ui.Purpose");

    public bool HasScan => _scan is not null;

    /// <summary>走査結果が無い (走査ボタンを強調する)。</summary>
    public bool IsNotScanned => !HasScan;

    /// <summary>実行バーの選択の要約 (言語切り替えで引き直す)。</summary>
    public string SelectionText => CleanupText.GetCounted("Cleanup.Ui.SelectedCount", SelectedCount, SelectedCount);

    public bool HasResult => ResultSummary.Length > 0;

    /// <summary>完全な成功のときだけ成功色にする。</summary>
    public bool IsResultSuccess => _lastResult?.Overall == RegistryOverallState.Success;

    /// <summary>部分失敗・取消は警告色。</summary>
    public bool IsResultWarning => _lastResult?.Overall is RegistryOverallState.Partial or RegistryOverallState.Canceled;

    public bool IsResultError => _lastResult?.Overall == RegistryOverallState.Failed;

    /// <summary>状態文を通知として出すか。初期の「まだ走査していません」は空状態が示すので出さず、再走査の警告帯が出ている間は帯に出す。</summary>
    public bool IsStatusVisible => !_statusIsPlaceholder && StatusText.Length > 0 && !NeedsRescan;

    /// <summary>再走査の警告帯の文。状態文が空 (実行後) のときは「走査結果が無い」旨を出す (実行後は帯を出さず <see cref="RescanAfterRunText"/> を結果カードに出す)。</summary>
    public string RescanNoticeText => StatusText.Length > 0 ? StatusText : CleanupText.Get("Cleanup.Registry.Ui.NotScanned");

    /// <summary>表示用: 再走査の警告帯を出すか。状態文 (失効・中止・失敗・復元後) があるときだけで、通常の実行後は結果カードの案内に任せる。</summary>
    public bool ShowRescanBand => NeedsRescan && StatusText.Length > 0;

    /// <summary>表示用: 空状態「まだ走査していません」を出すか。走査結果も結果カードも無いときだけ。</summary>
    public bool ShowEmptyState => !HasScan && !HasResult;

    /// <summary>表示用: 実行したため走査結果が使えなくなった旨 (結果カードの下に出す)。</summary>
    public string RescanAfterRunText => CleanupText.Get("Cleanup.Ui.RescanAfterRun");

    public bool HasUserScopeNote => UserScopeNote.Length > 0;

    public int SelectedCount => Groups.Sum(group => group.SelectedCount);

    public bool CanScan => !IsRunning;

    public bool CanEditSelection => !IsRunning && !IsConfirming && _scan is not null;

    public bool CanRequestRun => !IsRunning && !IsConfirming && _scan is not null && SelectedCount > 0;

    /// <summary>承認が要る選択行がすべて承認済みか。承認が要る行が無ければ真。</summary>
    public bool IsApprovalComplete => SelectedRows().Where(row => row.RequiresApproval).All(row => row.IsApproved);

    public bool CanConfirm => IsConfirming && !IsRunning && IsApprovalComplete;

    public bool HasApprovalRows => ApprovalRows.Count > 0;

    public bool IsScanning => IsRunning && !IsDeleting;

    public bool IsDeletingRunning => IsRunning && IsDeleting;

    partial void OnIsDeletingChanged(bool value) => RefreshRunKind();

    private void RefreshRunKind()
    {
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(IsDeletingRunning));
    }

    public string ScanButtonText => CleanupText.Get(HasScan ? "Cleanup.Action.Rescan" : "Cleanup.Action.Scan");

    partial void OnCreateRestorePointChanged(bool value)
    {
        if (IsConfirming)
        {
            RefreshConfirmTexts();
        }
    }

    partial void OnIsConfirmingChanged(bool value) => RefreshState();

    partial void OnUserScopeNoteChanged(string value) => OnPropertyChanged(nameof(HasUserScopeNote));

    partial void OnResultSummaryChanged(string value)
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    partial void OnStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsStatusVisible));
        OnPropertyChanged(nameof(RescanNoticeText));
        OnPropertyChanged(nameof(ShowRescanBand));
    }

    partial void OnNeedsRescanChanged(bool value)
    {
        OnPropertyChanged(nameof(IsStatusVisible));
        OnPropertyChanged(nameof(ShowRescanBand));
    }

    private void RefreshResultTone()
    {
        OnPropertyChanged(nameof(IsResultSuccess));
        OnPropertyChanged(nameof(IsResultWarning));
        OnPropertyChanged(nameof(IsResultError));
    }

    /// <summary>表示用の一覧を組み直す (走査結果の適用・破棄のとき)。開閉だけの変更は <see cref="OnGroupExpandedChanged"/> が差分で直す。</summary>
    private void RebuildDisplayItems() =>
        DisplayItems.Replace(_scan is null ? Enumerable.Empty<object>() : Groups.SelectMany(VisibleItems));

    private static IEnumerable<object> VisibleItems(RegistryCleanGroupViewModel group)
    {
        yield return group;
        if (!group.IsExpanded)
        {
            yield break;
        }

        foreach (var row in group.Rows)
        {
            yield return row;
        }
    }

    private void OnGroupExpandedChanged(RegistryCleanGroupViewModel group)
    {
        var index = DisplayItems.IndexOf(group);
        if (index < 0)
        {
            return;
        }

        if (group.IsExpanded)
        {
            DisplayItems.InsertRange(index + 1, group.Rows);
        }
        else
        {
            DisplayItems.RemoveRange(index + 1, group.Rows.Count);
        }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (!CanScan)
        {
            return;
        }

        IsConfirming = false;
        ClearOutcome();
        var (ended, scan) = await RunLeasedAsync(false, CleanupText.Get("Cleanup.Registry.Ui.Scanning"), _backend.ScanAsync);
        if (ended == Ended.Busy)
        {
            SetStatus(() => CleanupText.Get("Status.Busy"));
            return;
        }

        ClearScan();
        if (ended == Ended.Completed && scan is not null)
        {
            ApplyScan(scan);
        }
        else
        {
            // 走査が終わらなかったときは、古い世代を残さない。
            _backend.Invalidate();
            var canceled = ended == Ended.Cancelled;
            SetStatus(() => canceled
                ? RegistryResultFormat.OverallText(RegistryOverallState.Canceled, 0, 0)
                : CleanupText.Get("Cleanup.Registry.Ui.ScanFailed"));
        }

        RefreshState();
    }

    private void ApplyScan(RegistryScanResult scan)
    {
        _scan = scan;
        NeedsRescan = false;
        foreach (var group in Groups)
        {
            var rows = scan.Items
                .Where(item => item.Category == group.Definition.Category)
                .Select(item => new RegistryCleanRowViewModel(item, group.Definition, group.Definition.DefaultSelected, OnSelectionChanged))
                .ToList();
            group.Apply(rows, scan.Retained.Where(entry => entry.Category == group.Definition.Category));
        }

        _userScopeNote = scan.UserScope.Available ? null : () => DescribeUserScope(scan.UserScope);
        UserScopeNote = _userScopeNote?.Invoke() ?? string.Empty;
        var candidates = scan.Items.Count;
        SetStatus(() => CleanupText.Get("Cleanup.Registry.Ui.ScanDone", candidates, scan.Retained.Count));
        RebuildDisplayItems();
    }

    /// <summary>「すべて選択」。含めないカテゴリー (アンインストール・共有 DLL) の行は触らない。</summary>
    [RelayCommand]
    private void SelectAll()
    {
        if (!CanEditSelection)
        {
            return;
        }

        foreach (var row in Groups.Where(group => group.Definition.IncludedInSelectAll).SelectMany(group => group.Rows))
        {
            row.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        if (!CanEditSelection)
        {
            return;
        }

        foreach (var row in Groups.SelectMany(group => group.Rows))
        {
            row.IsSelected = false;
        }
    }

    [RelayCommand]
    private void RequestRun()
    {
        if (!CanRequestRun)
        {
            return;
        }

        CreateRestorePoint = true;
        ApprovalRows.Clear();
        foreach (var row in SelectedRows().Where(row => row.RequiresApproval))
        {
            row.IsApproved = false;
            ApprovalRows.Add(row);
        }

        RefreshConfirmTexts();
        IsConfirming = true;
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        IsConfirming = false;
        ApprovalRows.Clear();
        OnPropertyChanged(nameof(HasApprovalRows));
    }

    private void RefreshConfirmTexts()
    {
        var selected = SelectedRows();
        ConfirmText = CleanupText.GetCounted("Cleanup.Registry.Ui.Confirm.Body", selected.Count, selected.Count);
        OnPropertyChanged(nameof(HasApprovalRows));
    }

    [RelayCommand]
    private async Task ConfirmRunAsync()
    {
        if (!CanConfirm || _scan is not { } scan)
        {
            return;
        }

        var selected = SelectedRows();
        var request = new RegistryCleanRequest(scan, [.. selected.Select(row => row.Item)], CreateRestorePoint, DecideRestorePointAsync);
        IsConfirming = false;
        ApprovalRows.Clear();
        ClearOutcome();

        IsDeleting = true;
        (Ended Ended, RegistryCleanRunResult? Value) outcome;
        try
        {
            outcome = await RunLeasedAsync(
                true,
                CleanupText.GetCounted("Cleanup.Registry.Ui.Running", selected.Count, selected.Count),
                ct => _backend.CleanAsync(request, ct));
        }
        finally
        {
            IsDeleting = false;
        }

        var (ended, result) = outcome;
        if (ended == Ended.Busy)
        {
            SetStatus(() => CleanupText.Get("Status.Busy"));
            return;
        }

        // 実行を始めた走査結果は使用済み。結果を見せたうえで再走査を求める。
        ClearScan();
        NeedsRescan = true;
        if (ended == Ended.Completed && result is not null)
        {
            ApplyRunResult(result);
        }
        else
        {
            _backend.Invalidate();
            var canceled = ended == Ended.Cancelled;
            SetStatus(() => canceled
                ? RegistryResultFormat.OverallText(RegistryOverallState.Canceled, 0, selected.Count)
                : CleanupText.Get("Cleanup.Registry.Ui.RunFailed"));
        }

        RefreshState();
    }

    private void ApplyRunResult(RegistryCleanRunResult result)
    {
        if (result.BackupId is not null)
        {
            BackupsChanged?.Invoke();
        }

        if (result.Stale)
        {
            SetStatus(() => CleanupText.Get("Cleanup.Registry.Ui.Stale"));
            return;
        }

        _lastResult = result;
        RefreshResultTexts();

        // 結果の要約は結果欄に出す。状態表示にも同じ文を出さない。
        SetStatus(() => string.Empty);
    }

    private void RefreshResultTexts()
    {
        if (_lastResult is not { } result)
        {
            return;
        }

        RefreshResultTone();
        var deleted = result.Items.Count(item => item.State == RegistryItemState.Deleted);
        ResultSummary = RegistryResultFormat.OverallText(result.Overall, deleted, result.Items.Count - deleted);
        var (lines, more) = RegistryResultFormat.Reasons(result.Items.Select(item => (item.State, item.Reason)));
        ResultReasons.Clear();
        foreach (var line in lines)
        {
            ResultReasons.Add(line);
        }

        ResultMoreText = more > 0 ? CleanupText.GetCounted("Cleanup.Registry.Ui.ReasonMore", more, more) : string.Empty;
        ResultMessageText = CleanupText.Detail(result.Message);
        BackupText = result.BackupId is null ? string.Empty : CleanupText.Get("Cleanup.Registry.Ui.BackupSaved", result.BackupId);
        RestorePointResultText = DescribeRestorePoint(result.RestorePoint);
    }

    private static string DescribeRestorePoint(RestorePointResult? restorePoint) => restorePoint switch
    {
        null => string.Empty,
        { Outcome: RestorePointOutcome.Created } => CleanupText.Get("Cleanup.Slim.Ui.RestorePoint.Created"),
        { Outcome: RestorePointOutcome.ReusedExisting, Point: { } point } =>
            CleanupText.Get("Cleanup.Slim.Ui.RestorePoint.Reused", point.CreatedAtUtc.ToLocalTime().ToString("g")),
        { Outcome: RestorePointOutcome.ProtectionDisabled } => CleanupText.Get("Cleanup.Slim.Ui.RestorePoint.Disabled.Body"),
        _ => CleanupText.Get("Cleanup.Slim.Ui.RestorePoint.Unavailable.Body", CleanupText.Detail(restorePoint.Message)),
    };

    private async Task<RestorePointDecision> DecideRestorePointAsync(RestorePointResult result, CancellationToken ct)
    {
        if (RestorePointPrompt is not { } prompt)
        {
            return RestorePointDecision.Abort;
        }

        var disabled = result.Outcome == RestorePointOutcome.ProtectionDisabled;
        try
        {
            return await prompt.AskAsync(DescribeRestorePoint(result), disabled, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return RestorePointDecision.Abort;
        }
    }

    /// <summary>復元などで走査結果が食い違うとき、結果を捨てて再走査を求める (復元側から呼ぶ)。</summary>
    internal void InvalidateScan()
    {
        _backend.Invalidate();
        if (_scan is null)
        {
            return;
        }

        IsConfirming = false;
        ApprovalRows.Clear();
        ClearScan();
        NeedsRescan = true;
        SetStatus(() => CleanupText.Get("Cleanup.Registry.Ui.ScanInvalidated"));
        RefreshState();
    }

    /// <summary>
    /// HKCU を走査できなかった注記。Core の符号の引数は列挙値の名前 (DifferentUser など) なので、そのまま出さず、
    /// 既存の理由の文言 (Cleanup.Reason.UserScope*) に置き換える。
    /// </summary>
    private static string DescribeUserScope(RegistryUserScopeStatus scope)
    {
        var reasonKey = scope.Reason switch
        {
            InteractiveScopeReason.DifferentUser => "Cleanup.Reason.UserScopeDifferentUser",
            InteractiveScopeReason.UserUnknown => "Cleanup.Reason.UserScopeUnknown",
            _ => null,
        };
        return reasonKey is null
            ? CleanupText.Detail(scope.ReasonMessage)
            : CleanupText.Get("Cleanup.Msg.RegistryScanUserScopeUnavailable", CleanupText.Get(reasonKey));
    }

    private void ClearScan()
    {
        _scan = null;
        _userScopeNote = null;
        UserScopeNote = string.Empty;
        foreach (var group in Groups)
        {
            group.Clear();
        }

        RebuildDisplayItems();
    }

    private void SetStatus(Func<string> text)
    {
        _statusIsPlaceholder = false;
        _status = text;
        StatusText = text();
    }

    internal void OnLocaleChanged()
    {
        foreach (var group in Groups)
        {
            group.RefreshLocale();
        }

        RefreshResultTexts();
        if (IsConfirming)
        {
            RefreshConfirmTexts();
        }

        StatusText = _status();
        if (_userScopeNote is { } userScopeNote)
        {
            UserScopeNote = userScopeNote();
        }

        OnPropertyChanged(nameof(RescanNoticeText));
        OnPropertyChanged(nameof(RescanAfterRunText));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(PurposeText));
        OnPropertyChanged(nameof(ScanButtonText));
    }

    private List<RegistryCleanRowViewModel> SelectedRows() =>
        [.. Groups.SelectMany(group => group.Rows).Where(row => row.IsSelected)];

    private void ClearOutcome()
    {
        _lastResult = null;
        RefreshResultTone();
        ResultSummary = string.Empty;
        ResultMoreText = string.Empty;
        ResultMessageText = string.Empty;
        BackupText = string.Empty;
        RestorePointResultText = string.Empty;
        ResultReasons.Clear();
    }

    /// <summary>選択・承認の変更だけを通知する (行ごとの編集可否は引き直さない)。</summary>
    private void OnSelectionChanged()
    {
        foreach (var group in Groups)
        {
            group.RefreshCounts();
        }

        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(CanRequestRun));
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(IsApprovalComplete));
    }

    private void RefreshState()
    {
        foreach (var group in Groups)
        {
            group.RefreshCounts();
            foreach (var row in group.Rows)
            {
                row.RefreshEditable(CanEditSelection);
            }
        }

        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(IsNotScanned));
        OnPropertyChanged(nameof(ShowEmptyState));
        RefreshRunKind();
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CanRequestRun));
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(IsApprovalComplete));
        OnPropertyChanged(nameof(CanEditSelection));
        OnPropertyChanged(nameof(HasScan));
        OnPropertyChanged(nameof(ScanButtonText));
    }
}
