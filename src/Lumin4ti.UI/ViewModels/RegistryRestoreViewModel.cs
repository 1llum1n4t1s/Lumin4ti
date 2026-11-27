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

    public bool IsConfirmingDelete => PendingDelete is not null;

    public bool HasBackups => Backups.Count > 0;

    public bool CanOperate => !IsRunning && !_clean.IsRunning && Coordinator.ActiveCount == 0;

    partial void OnPendingDeleteChanged(RegistryBackupRowViewModel? value) => OnPropertyChanged(nameof(IsConfirmingDelete));

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
                Backups.Add(new RegistryBackupRowViewModel(summary));
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

    [RelayCommand]
    private async Task RestoreAsync(RegistryBackupRowViewModel? backup)
    {
        if (backup is null || !CanOperate)
        {
            return;
        }

        PendingDelete = null;
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
        if (backup is null || !CanOperate)
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
    }
}
