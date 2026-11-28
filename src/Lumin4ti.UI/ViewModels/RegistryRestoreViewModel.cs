using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;
using Lumin4ti.UI.Services;

namespace Lumin4ti.UI.ViewModels;

/// <summary>
/// 「レジストリ」タブの復元側。バックアップ一覧・復元・バックアップの削除 (確認つき)。復元と削除は lease の中で行い、
/// 復元を始めたら本体の走査結果を失効させる (戻した内容は走査結果と食い違うため。失敗・中断でも安全側に倒す)。
/// </summary>
internal sealed partial class RegistryRestoreViewModel : RegistryOperationViewModel
{
    private readonly IRegistryRestoreBackend _backend;
    private readonly RegistryCleanViewModel _clean;
    private Func<string> _status;
    private RegistryRestoreResult? _lastResult;

    public RegistryRestoreViewModel(
        IRegistryRestoreBackend backend,
        RegistryCleanViewModel clean,
        MaintenanceOperationCoordinator coordinator)
        : base(coordinator)
    {
        _backend = backend;
        _clean = clean;
        _status = () => CleanupText.Get("Cleanup.Registry.Ui.Backup.NotLoaded");
        statusText = _status();
        clean.BackupsChanged += () => PostToUi(() => _ = RefreshAsync());
        App.LocaleChanged += () => PostToUi(OnLocaleChanged);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsRunning))
            {
                OnPropertyChanged(nameof(CanOperate));
            }
        };
        // 削除側の実行中・他の操作の lease 中も、復元・バックアップ削除を無効にする。
        clean.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsRunning))
            {
                PostToUi(() => OnPropertyChanged(nameof(CanOperate)));
            }
        };
        coordinator.StateChanged += (_, _) => PostToUi(() => OnPropertyChanged(nameof(CanOperate)));
    }

    public ObservableCollection<RegistryBackupRowViewModel> Backups { get; } = [];

    /// <summary>復元の結果のうち、成功以外の代表理由 (最大 8 行)。</summary>
    public ObservableCollection<string> ResultReasons { get; } = [];

    [ObservableProperty]
    private string statusText;

    [ObservableProperty]
    private string resultSummary = string.Empty;

    [ObservableProperty]
    private string resultMoreText = string.Empty;

    [ObservableProperty]
    private string resultMessageText = string.Empty;

    [ObservableProperty]
    private RegistryBackupRowViewModel? pendingDelete;

    [ObservableProperty]
    private string deleteConfirmText = string.Empty;

    /// <summary>戻す前の確認の対象 (確認を開いている間だけ設定される)。</summary>
    [ObservableProperty]
    private RegistryBackupRowViewModel? pendingRestore;

    [ObservableProperty]
    private string restoreConfirmText = string.Empty;

    public bool IsConfirmingDelete => PendingDelete is not null;

    public bool IsConfirmingRestore => PendingRestore is not null;

    /// <summary>確認シートを開くか (削除の確認と戻す確認のどちらか。同時には開かない)。</summary>
    public bool IsSheetOpen => IsConfirmingDelete || IsConfirmingRestore;

    public bool HasBackups => Backups.Count > 0;

    public bool HasResult => ResultSummary.Length > 0;

    /// <summary>完全な成功のときだけ成功色にする。</summary>
    public bool IsResultSuccess => _lastResult?.Overall == RegistryOverallState.Success;

    /// <summary>一部だけ戻せた・取消は警告色。</summary>
    public bool IsResultWarning => _lastResult?.Overall is RegistryOverallState.Partial or RegistryOverallState.Canceled;

    public bool IsResultError => _lastResult?.Overall == RegistryOverallState.Failed;

    public bool IsStatusVisible => StatusText.Length > 0;

    public bool CanOperate => !IsRunning && !_clean.IsRunning && Coordinator.ActiveCount == 0;

    /// <summary><see cref="CanOperate"/> の変更を各行のボタンへも伝える (行が自分の ViewModel の値だけを見るため)。</summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(CanOperate))
        {
            foreach (var row in Backups)
            {
                row.RefreshOperate();
            }
        }
    }

    partial void OnPendingDeleteChanged(RegistryBackupRowViewModel? value)
    {
        OnPropertyChanged(nameof(IsConfirmingDelete));
        OnPropertyChanged(nameof(IsSheetOpen));
    }

    partial void OnPendingRestoreChanged(RegistryBackupRowViewModel? value)
    {
        OnPropertyChanged(nameof(IsConfirmingRestore));
        OnPropertyChanged(nameof(IsSheetOpen));
    }

    partial void OnResultSummaryChanged(string value) => OnPropertyChanged(nameof(HasResult));

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(IsStatusVisible));

    private void RefreshResultTone()
    {
        OnPropertyChanged(nameof(IsResultSuccess));
        OnPropertyChanged(nameof(IsResultWarning));
        OnPropertyChanged(nameof(IsResultError));
    }

    /// <summary>一覧を引き直す (読み取りだけ)。取得に失敗しても何も削除・変更しない。</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var summaries = await Task.Run(_backend.List);
            Backups.Clear();
            foreach (var summary in summaries.OrderByDescending(s => s.Id, StringComparer.Ordinal))
            {
                Backups.Add(new RegistryBackupRowViewModel(summary, this));
            }

            SetStatus(() => CleanupText.GetCounted("Cleanup.Registry.Ui.Backup.Loaded", Backups.Count, Backups.Count));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LoggerBootstrap.Log.Error("レジストリのバックアップ一覧を取得できませんでした", ex);
            SetStatus(() => CleanupText.Get("Cleanup.Registry.Ui.Backup.ListFailed"));
        }

        OnPropertyChanged(nameof(HasBackups));
    }

    /// <summary>[戻す…]: 確認シートを開くだけで、何も書き戻さない。実行中・他の確認が開いているときは受け付けない。</summary>
    [RelayCommand]
    private void RequestRestore(RegistryBackupRowViewModel? backup)
    {
        if (backup is null || !CanOperate || IsConfirmingDelete || IsConfirmingRestore)
        {
            return;
        }

        RestoreConfirmText = CleanupText.Get("Cleanup.Registry.Ui.Backup.RestoreConfirm", backup.CreatedText);
        PendingRestore = backup;
    }

    [RelayCommand]
    private void CancelRestore() => PendingRestore = null;

    /// <summary>確認の[戻す]: 確認を閉じ、直ちに戻す既存の経路 (<see cref="RestoreAsync"/>) を 1 回だけ呼ぶ。</summary>
    [RelayCommand]
    private async Task ConfirmRestoreAsync()
    {
        if (PendingRestore is not { } backup || !CanOperate)
        {
            return;
        }

        PendingRestore = null;
        await RestoreAsync(backup);
    }

    /// <summary>Esc で確認シートを閉じる (削除・戻すのどちらの確認でも)。</summary>
    [RelayCommand]
    private void CancelSheet()
    {
        PendingDelete = null;
        PendingRestore = null;
    }

    /// <summary>直ちに戻す。確認を経る画面の操作は <see cref="ConfirmRestoreCommand"/> から呼ばれる。</summary>
    [RelayCommand]
    private async Task RestoreAsync(RegistryBackupRowViewModel? backup)
    {
        if (backup is null || !CanOperate)
        {
            return;
        }

        PendingDelete = null;
        PendingRestore = null;
        ClearOutcome();
        var id = backup.Id;
        var (ended, result) = await RunLeasedAsync(
            true,
            CleanupText.Get("Cleanup.Registry.Ui.Restoring"),
            ct => Task.Run(() => _backend.Restore(id, ct), ct));
        if (ended == Ended.Busy)
        {
            SetStatus(() => CleanupText.Get("Status.Busy"));
            return;
        }

        // 復元は書き込み前に走査結果を失効させる。完了・失敗・中断のどれでも本体の結果を捨てて再走査させる。
        if (ended != Ended.Completed || result is null || result.ScanInvalidated)
        {
            _clean.InvalidateScan();
        }

        if (ended == Ended.Completed && result is not null)
        {
            _lastResult = result;
            RefreshResultTexts();

            // 結果の要約は理由つきの結果欄に出す。状態表示には一覧の件数だけを残し、要約を重ねて出さない。
            SetStatus(() => CleanupText.GetCounted("Cleanup.Registry.Ui.Backup.Loaded", Backups.Count, Backups.Count));
            return;
        }

        var canceled = ended == Ended.Cancelled;
        SetStatus(() => canceled
            ? RegistryResultFormat.OverallText(RegistryOverallState.Canceled, 0, 0)
            : CleanupText.Get("Cleanup.Registry.Ui.Restore.Failed"));
    }

    private void RefreshResultTexts()
    {
        if (_lastResult is not { } result)
        {
            return;
        }

        RefreshResultTone();
        int Count(RegistryItemState state) => result.Items.Count(item => item.State == state);
        ResultSummary = result.Items.Count == 0
            ? RegistryResultFormat.OverallText(result.Overall, 0, 0)
            : CleanupText.Get(
                "Cleanup.Registry.Ui.Restore.Summary",
                Count(RegistryItemState.Restored),
                Count(RegistryItemState.AlreadyPresent),
                Count(RegistryItemState.Conflict),
                Count(RegistryItemState.Skipped),
                Count(RegistryItemState.Failed));
        var (lines, more) = RegistryResultFormat.Reasons(result.Items.Select(item => (item.State, item.Reason)));
        ResultReasons.Clear();
        foreach (var line in lines)
        {
            ResultReasons.Add(line);
        }

        ResultMoreText = more > 0 ? CleanupText.GetCounted("Cleanup.Registry.Ui.ReasonMore", more, more) : string.Empty;
        ResultMessageText = CleanupText.Detail(result.Message);
    }

    [RelayCommand]
    private void RequestDelete(RegistryBackupRowViewModel? backup)
    {
        if (backup is null || !CanOperate || IsConfirmingRestore)
        {
            return;
        }

        DeleteConfirmText = CleanupText.Get("Cleanup.Registry.Ui.Backup.DeleteConfirm", backup.CreatedText);
        PendingDelete = backup;
    }

    [RelayCommand]
    private void CancelDelete() => PendingDelete = null;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (PendingDelete is not { } backup || !CanOperate)
        {
            return;
        }

        PendingDelete = null;
        var id = backup.Id;
        var (ended, _) = await RunLeasedAsync(
            false,
            CleanupText.Get("Cleanup.Registry.Ui.Backup.Deleting"),
            ct => Task.Run(() =>
            {
                _backend.Delete(id);
                return true;
            }, ct));
        if (ended == Ended.Busy)
        {
            SetStatus(() => CleanupText.Get("Status.Busy"));
            return;
        }

        if (ended == Ended.Completed)
        {
            await RefreshAsync();
            SetStatus(() => CleanupText.Get("Cleanup.Registry.Ui.Backup.Deleted"));
        }
        else
        {
            SetStatus(() => CleanupText.Get("Cleanup.Registry.Ui.Backup.DeleteFailed"));
        }
    }

    private void ClearOutcome()
    {
        _lastResult = null;
        RefreshResultTone();
        ResultSummary = string.Empty;
        ResultMoreText = string.Empty;
        ResultMessageText = string.Empty;
        ResultReasons.Clear();
    }

    private void SetStatus(Func<string> text)
    {
        _status = text;
        StatusText = text();
    }

    internal void OnLocaleChanged()
    {
        foreach (var row in Backups)
        {
            row.RefreshLocale();
        }

        RefreshResultTexts();
        StatusText = _status();
        if (PendingRestore is { } restore)
        {
            RestoreConfirmText = CleanupText.Get("Cleanup.Registry.Ui.Backup.RestoreConfirm", restore.CreatedText);
        }
    }
}
