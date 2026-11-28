using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.Core.Services.Windows.Cleanup.Slim;
using Lumin4ti.UI.Services;

namespace Lumin4ti.UI.ViewModels;

/// <summary>復元ポイントを利用できないときの判断を、実行中の別ウィンドウで聞く境界 (処理中オーバーレイは入力を塞ぐため)。</summary>
internal interface ISlimRestorePointPrompt
{
    /// <param name="body">理由を含む本文 (ローカライズ済み)。</param>
    /// <param name="canEnableProtection">システムの保護を有効にして再試行する選択肢を出すか。</param>
    /// <param name="ct">中断。取り消されたら <see cref="RestorePointDecision.Abort"/> を返す。</param>
    Task<RestorePointDecision> AskAsync(string body, bool canEnableProtection, CancellationToken ct);
}

/// <summary>
/// 「スリム化」タブ。15 項目の走査 → 項目別の空き容量 → 選択 (既定はすべてオフ) → 確認 (警告・二段階確認・復元ポイント) → 実行 → 項目別の結果。
/// 復元ポイントの作成と判断は <see cref="SlimService"/> の <see cref="SlimRunRequest"/> が持ち、画面は設定の既定値と利用者の判断を渡すだけ。
/// 走査は <c>showProcessing:false</c> (画面内に進捗と中断)、実行は <c>showProcessing:true</c> の lease で行い、
/// サービスの補償を含む後始末が終わるまで lease を手放さない。自動では走査も実行も始めない。
/// </summary>
public sealed partial class SlimViewModel : CleanupOperationViewModel
{
    private readonly Action _refreshState;
    private SlimScanResult? _scan;
    private CancellationTokenSource? _cancelSource;
    private Func<string> _status;
    private SlimRunResult? _lastResult;
    private bool _isInitialStatus = true;
    private bool _statusIsWarning;

    public SlimViewModel(
        SlimService slim,
        CleanupSettingsStore settings,
        MaintenanceOperationCoordinator coordinator)
    {
        Slim = slim;
        Settings = settings;
        Coordinator = coordinator;
        _refreshState = RefreshState;
        Items = [.. SlimService.Definitions.Select(d => new SlimItemRowViewModel(d, _refreshState))];
        createRestorePoint = settings.CreateRestorePoint;
        _status = () => CleanupText.Get("Cleanup.Slim.Ui.NotScanned");
        statusText = _status();
        App.LocaleChanged += () => PostToUi(OnLocaleChanged);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsRunning))
            {
                RefreshState();
            }
            else if (e.PropertyName is nameof(Label) or nameof(ProgressText))
            {
                OnPropertyChanged(nameof(ProgressDisplayText));
            }
        };
    }

    /// <summary>進捗欄に出す文 (操作名と、直近の進捗行)。</summary>
    public string ProgressDisplayText => ProgressText.Length == 0 ? Label : Label + Environment.NewLine + ProgressText;

    internal SlimService Slim { get; }

    internal CleanupSettingsStore Settings { get; }

    internal MaintenanceOperationCoordinator Coordinator { get; }

    /// <summary>復元ポイントの判断を聞く窓口 (View が設定する)。null のときは安全側の中止になる。</summary>
    internal ISlimRestorePointPrompt? RestorePointPrompt { get; set; }

    public ObservableCollection<SlimItemRowViewModel> Items { get; }

    [ObservableProperty]
    private string statusText;

    /// <summary>実行前に復元ポイントを作るか (初期値は設定の既定値)。</summary>
    [ObservableProperty]
    private bool createRestorePoint;

    [ObservableProperty]
    private bool isConfirming;

    [ObservableProperty]
    private string confirmText = string.Empty;

    [ObservableProperty]
    private string confirmWarnings = string.Empty;

    [ObservableProperty]
    private string restorePointNote = string.Empty;

    /// <summary>元に戻せない項目を含み、二段階目の確認 (チェック) が要るか。</summary>
    [ObservableProperty]
    private bool requiresAcknowledgement;

    [ObservableProperty]
    private string irreversibleText = string.Empty;

    [ObservableProperty]
    private bool acknowledged;

    [ObservableProperty]
    private string resultSummary = string.Empty;

    [ObservableProperty]
    private string restorePointResultText = string.Empty;

    [ObservableProperty]
    private string rebootText = string.Empty;

    public int SelectedCount => Items.Count(row => row.IsSelected);

    /// <summary>実行バーに出す選択数の要約 (言語を切り替えると引き直す)。</summary>
    public string SelectionText => CleanupText.Get(SelectedCount == 1 ? "Cleanup.Ui.SelectedCount.One" : "Cleanup.Ui.SelectedCount", SelectedCount);

    /// <summary>実行結果があるか (結果カードを出す条件)。</summary>
    public bool HasResult => ResultSummary.Length > 0;

    /// <summary>実行がすべて成功したときだけ true (成功色の結果カード)。部分失敗・拒否・取消では false。</summary>
    public bool IsResultSuccess => _lastResult?.AllSucceeded == true;

    /// <summary>結果があり完全な成功ではない (警告色の結果カード)。</summary>
    public bool IsResultWarning => HasResult && !IsResultSuccess;

    /// <summary>走査も実行もまだ何も始めていない初期状態 (未走査の案内を出す)。</summary>
    public bool ShowNotScannedNotice => _isInitialStatus && !IsRunning;

    /// <summary>実行後に走査結果が失効しているとき、再走査を案内する警告帯を出すか。</summary>
    public bool ShowRescanNotice => HasResult && !HasScan && !IsRunning;

    /// <summary>実行後の再走査の案内文 (結果カードの中に注記として出す)。</summary>
    public string RescanNoticeText => CleanupText.Get("Cleanup.Ui.RescanAfterRun");

    /// <summary>状態文を警告帯で出すか (失効・取消・失敗・他の操作中)。</summary>
    public bool ShowStatusWarning => _statusIsWarning && StatusText.Length > 0;

    /// <summary>状態文を通常の文として出すか (結果カードと重複する文は出さない)。</summary>
    public bool ShowStatusPlain => !_isInitialStatus && !_statusIsWarning && !HasResult && StatusText.Length > 0;

    public bool CanScan => !IsRunning;

    public bool CanRequestRun => !IsRunning && !IsConfirming && _scan is not null && SelectedCount > 0;

    public bool CanConfirm => IsConfirming && !IsRunning && (!RequiresAcknowledgement || Acknowledged);

    public bool CanEditSelection => !IsRunning && !IsConfirming && _scan is not null;

    public bool HasScan => _scan is not null;

    public string ScanButtonText => CleanupText.Get(HasScan ? "Cleanup.Action.Rescan" : "Cleanup.Action.Scan");

    partial void OnCreateRestorePointChanged(bool value)
    {
        // 確認表示中に切り替えたときは、実行内容と食い違わないよう注記を引き直す。
        if (IsConfirming)
        {
            RefreshConfirmTexts();
        }

        RefreshState();
    }

    partial void OnIsConfirmingChanged(bool value) => RefreshState();

    partial void OnAcknowledgedChanged(bool value) => RefreshState();

    protected override void OnCancelRequested()
    {
        CanCancel = false;
        try
        {
            _cancelSource?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 完了と中断が競合しただけ。
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in Items)
        {
            row.IsSelected = false;
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
        var (ended, scan) = await RunLeasedAsync(
            showProcessing: false,
            CleanupText.Get("Cleanup.Slim.Ui.Scanning"),
            initialProgress: string.Empty,
            (progress, ct) => Slim.ScanAsync(progress, ct));
        if (ended == Ended.Busy)
        {
            // 何も走査していないので、直前の結果と選択はそのまま。
            SetStatus(() => CleanupText.Get("Status.Busy"), warning: true);
            return;
        }

        _scan = null;
        foreach (var row in Items)
        {
            row.ApplyNotScanned();
        }

        switch (ended)
        {
            case Ended.Completed when scan is not null:
                _scan = scan;
                foreach (var row in Items)
                {
                    row.ApplyScan(scan.Item(row.Definition.Id));
                }

                var selectable = scan.Items.Count(i => i.Selectable);
                SetStatus(() => CleanupText.Get("Cleanup.Slim.Ui.ScanDone", selectable));
                break;
            case Ended.Cancelled:
                SetStatus(() => CleanupText.Get("Cleanup.Slim.Ui.Result.Cancelled"), warning: true);
                break;
            default:
                SetStatus(() => CleanupText.Get("Cleanup.Slim.Ui.ScanFailed"), warning: true);
                break;
        }

        RefreshState();
    }

    [RelayCommand]
    private void RequestRun()
    {
        if (!CanRequestRun)
        {
            return;
        }

        var selected = SelectedRows();
        RequiresAcknowledgement = selected.Any(row => row.Definition.RequiresSecondConfirmation);
        RefreshConfirmTexts();
        Acknowledged = false;
        IsConfirming = true;
    }

    /// <summary>確認の本文・警告・復元ポイントの注記を、現在の選択と言語で作る (選択・確認の状態は変えない)。</summary>
    private void RefreshConfirmTexts()
    {
        if (_scan is null)
        {
            return;
        }

        var selected = SelectedRows();
        var bytes = selected.Sum(row => _scan.Item(row.Definition.Id).Bytes ?? 0);
        ConfirmText = CleanupText.Get("Cleanup.Slim.Ui.Confirm.Body", selected.Count, CleanupText.FormatBytes(bytes));
        ConfirmWarnings = string.Join(
            Environment.NewLine,
            selected.Where(row => row.HasWarning).Select(row => $"{row.Name}: {row.WarningText}"));
        RestorePointNote = selected.Any(row => row.Definition.NeedsRestorePoint)
            ? CleanupText.Get(CreateRestorePoint ? "Cleanup.Slim.Ui.Confirm.WithRestorePoint" : "Cleanup.Slim.Ui.Confirm.WithoutRestorePoint")
            : string.Empty;
        IrreversibleText = RequiresAcknowledgement ? CleanupText.Get("Cleanup.Slim.Ui.Confirm.Irreversible.Body") : string.Empty;
    }

    [RelayCommand]
    private void CancelConfirm() => IsConfirming = false;

    [RelayCommand]
    private async Task ConfirmRunAsync()
    {
        if (!CanConfirm || _scan is not { } scan)
        {
            return;
        }

        var selected = SelectedRows();
        var request = new SlimRunRequest(
            scan,
            [.. selected.Select(row => row.Definition.Id)],
            CreateRestorePoint,
            ConfirmedIrreversible: RequiresAcknowledgement && Acknowledged,
            OnRestorePointUnavailable: DecideRestorePointAsync);
        IsConfirming = false;
        ClearOutcome();

        var slowItems = selected.Any(row => row.Definition.Id is SlimItemId.UpdateRollback or SlimItemId.UpdateBackup
            or SlimItemId.LanguageJa or SlimItemId.LanguageKo or SlimItemId.LanguageZhTw or SlimItemId.LanguageZhCn);
        var (ended, result) = await RunLeasedAsync(
            showProcessing: true,
            CleanupText.Get("Cleanup.Slim.Ui.Running", 0, selected.Count, string.Join(", ", selected.Select(row => row.Name))),
            slowItems ? CleanupText.Get("Cleanup.Slim.Ui.LongRunning") : string.Empty,
            (progress, ct) => Slim.RunAsync(request, progress, ct));

        if (ended == Ended.Busy)
        {
            // 何も実行していないので、走査結果と選択はそのまま。
            SetStatus(() => CleanupText.Get("Status.Busy"), warning: true);
            return;
        }

        // 実行を始めた走査結果は失効しているため、結果を見せたうえで再走査を求める。
        _scan = null;
        if (ended == Ended.Completed && result is not null)
        {
            ApplyRunResult(result);
        }
        else
        {
            foreach (var row in Items)
            {
                row.ApplyNotScanned();
            }

            var key = ended == Ended.Cancelled ? "Cleanup.Slim.Ui.Result.Cancelled" : "Cleanup.Slim.Ui.Result.Failed";
            SetStatus(() => CleanupText.Get(key), warning: true);
        }

        RefreshState();
    }

    private void ApplyRunResult(SlimRunResult result)
    {
        foreach (var row in Items)
        {
            row.ApplyNotScanned();
        }

        if (result.Rejected is not null)
        {
            SetStatus(() => CleanupText.Get("Cleanup.Slim.Ui.Stale"), warning: true);
            return;
        }

        foreach (var itemResult in result.Items)
        {
            Items.First(row => row.Definition.Id == itemResult.Id).ApplyResult(itemResult);
        }

        _lastResult = result;
        RefreshResultTexts();
        SetStatus(() => ResultSummary);
    }

    /// <summary>保持している実行結果から、まとめ・復元ポイント・再起動の文言を作る。</summary>
    private void RefreshResultTexts()
    {
        if (_lastResult is not { } result)
        {
            return;
        }

        var freed = result.Items.Where(i => i.Status is SlimItemStatus.Succeeded or SlimItemStatus.Partial).Sum(i => i.FreedBytes);
        ResultSummary = CleanupText.Get(result.AllSucceeded ? "Cleanup.Slim.Ui.Done" : "Cleanup.Slim.Ui.DonePartial", CleanupText.FormatBytes(freed));
        RestorePointResultText = DescribeRestorePoint(result.RestorePoint);
        RebootText = result.RebootRequired ? CleanupText.Get("Cleanup.Slim.Ui.RebootRequired") : string.Empty;
        RefreshResultFlags();
    }

    partial void OnResultSummaryChanged(string value) => RefreshResultFlags();

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
        var body = DescribeRestorePoint(result);
        try
        {
            return await prompt.AskAsync(body, disabled, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return RestorePointDecision.Abort;
        }
    }

    private void SetStatus(Func<string> text, bool warning = false)
    {
        _status = text;
        _isInitialStatus = false;
        _statusIsWarning = warning;
        StatusText = text();
        RefreshStatusFlags();
    }

    private void RefreshStatusFlags()
    {
        OnPropertyChanged(nameof(ShowNotScannedNotice));
        OnPropertyChanged(nameof(ShowRescanNotice));
        OnPropertyChanged(nameof(RescanNoticeText));
        OnPropertyChanged(nameof(ShowStatusWarning));
        OnPropertyChanged(nameof(ShowStatusPlain));
    }

    private void RefreshResultFlags()
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(IsResultSuccess));
        OnPropertyChanged(nameof(IsResultWarning));
        RefreshStatusFlags();
    }

    private static void PostToUi(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) action();
        else Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }

    /// <summary>
    /// 言語を切り替えたとき、表示済みの文言を引き直す。走査結果・選択・確認の状態・実行結果は失わない。
    /// </summary>
    internal void OnLocaleChanged()
    {
        foreach (var row in Items)
        {
            row.RefreshLocale();
        }

        RefreshResultTexts();
        if (IsConfirming)
        {
            RefreshConfirmTexts();
        }

        StatusText = _status();
        RefreshStatusFlags();
        OnPropertyChanged(nameof(ScanButtonText));
        OnPropertyChanged(nameof(SelectionText));
    }

    private List<SlimItemRowViewModel> SelectedRows() => [.. Items.Where(row => row.IsSelected)];

    private void ClearOutcome()
    {
        _lastResult = null;
        ResultSummary = string.Empty;
        RestorePointResultText = string.Empty;
        RebootText = string.Empty;
    }

    private void RefreshState()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(ShowNotScannedNotice));
        OnPropertyChanged(nameof(ShowRescanNotice));
        OnPropertyChanged(nameof(CanScan));
        OnPropertyChanged(nameof(CanRequestRun));
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(CanEditSelection));
        OnPropertyChanged(nameof(HasScan));
        OnPropertyChanged(nameof(ScanButtonText));
        foreach (var row in Items)
        {
            row.RefreshEditable(CanEditSelection);
        }
    }

    private enum Ended
    {
        Busy,
        Completed,
        Cancelled,
        Failed,
    }

    /// <summary>lease を取って操作を実行する。lease は補償を含む <paramref name="work"/> の完了後に手放す。</summary>
    private async Task<(Ended Ended, T? Value)> RunLeasedAsync<T>(
        bool showProcessing,
        string label,
        string initialProgress,
        Func<IProgress<string>, CancellationToken, Task<T>> work)
    {
        if (!Coordinator.TryBegin(out var lease, showProcessing: showProcessing))
        {
            return (Ended.Busy, default);
        }

        using var operation = lease!;
        using var source = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
        _cancelSource = source;
        Label = label;
        ProgressText = initialProgress;
        CanCancel = true;
        IsRunning = true;
        try
        {
            return (Ended.Completed, await work(new Progress<string>(AppendProgress), source.Token));
        }
        catch (OperationCanceledException)
        {
            return (Ended.Cancelled, default);
        }
        catch (Exception ex)
        {
            LoggerBootstrap.Log.Error("スリム化の操作で例外が発生しました", ex);
            return (Ended.Failed, default);
        }
        finally
        {
            _cancelSource = null;
            CanCancel = false;
            IsRunning = false;
            Label = string.Empty;
            ProgressText = string.Empty;
        }
    }

    private void AppendProgress(string line)
    {
        if (!IsRunning)
        {
            return;
        }

        var lines = (ProgressText + Environment.NewLine + line.Trim())
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        ProgressText = string.Join(Environment.NewLine, lines.TakeLast(5));
    }
}
