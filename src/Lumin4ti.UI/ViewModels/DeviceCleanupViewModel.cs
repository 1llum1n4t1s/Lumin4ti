using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Services;
using Lumin4ti.UI.Services;

namespace Lumin4ti.UI.ViewModels;

/// <summary>未接続デバイスの選択・削除を扱う専用タブ。</summary>
public partial class DeviceCleanupViewModel : ObservableObject
{
    private readonly IDisconnectedDeviceService _deviceService;
    private readonly MaintenanceOperationCoordinator _operationCoordinator;
    private readonly Func<Action, Task> _dispatchAsync;
    private bool _suppressSelectionUpdates;

    public ObservableCollection<DisconnectedDeviceItemViewModel> Devices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(CanRefresh))]
    [NotifyPropertyChangedFor(nameof(CanChangeSelection))]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    private bool isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(CanRefresh))]
    [NotifyPropertyChangedFor(nameof(CanChangeSelection))]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    private bool isRemoving;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmationText))]
    private bool isConfirmingRemoval;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText))]
    [NotifyPropertyChangedFor(nameof(ConfirmationText))]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    private int selectedCount;

    public bool IsBusy => IsLoading || IsRemoving;

    public bool HasDevices => Devices.Count > 0;

    public bool IsEmpty => !IsLoading && !HasDevices;

    public bool CanRefresh => !IsBusy;

    public bool CanChangeSelection => HasDevices && !IsBusy;

    public bool CanRemove => SelectedCount > 0 && !IsBusy;

    public string SelectionText => App.Text(
        "DeviceCleanup.Selection",
        "{0} / {1} 件を選択中",
        SelectedCount,
        Devices.Count);

    public string ConfirmationText => App.Text(
        "DeviceCleanup.ConfirmText",
        "選択した {0} 件のデバイスを削除しますか？ 再接続するとドライバーは再検出されますが、デバイス固有の設定は初期化される場合があります。",
        SelectedCount);

    public DeviceCleanupViewModel(
        IDisconnectedDeviceService deviceService,
        MaintenanceOperationCoordinator operationCoordinator)
        : this(deviceService, operationCoordinator, DispatchToUiAsync)
    {
    }

    internal DeviceCleanupViewModel(
        IDisconnectedDeviceService deviceService,
        MaintenanceOperationCoordinator operationCoordinator,
        Func<Action, Task> dispatchAsync)
    {
        _deviceService = deviceService;
        _operationCoordinator = operationCoordinator;
        _dispatchAsync = dispatchAsync;

        App.LocaleChanged += () =>
        {
            OnPropertyChanged(nameof(SelectionText));
            OnPropertyChanged(nameof(ConfirmationText));
        };
    }

    /// <summary>
    /// 起動時およびウィンドウ再アクティブ時の一括状態再読込から呼ばれる。
    /// 呼び出し側が MaintenanceOperationCoordinator のリースを保持する。
    /// </summary>
    public async Task LoadDisconnectedDevicesAsync(CancellationToken ct = default)
    {
        await _dispatchAsync(() =>
        {
            IsLoading = true;
            StatusText = App.Text("DeviceCleanup.Loading", "未接続デバイスを検索しています…");
        }).ConfigureAwait(false);

        try
        {
            var devices = await _deviceService
                .GetDisconnectedDevicesAsync(ct)
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            await _dispatchAsync(() =>
            {
                ReplaceDevices(devices);
                StatusText = App.Text(
                    "DeviceCleanup.Loaded",
                    "未接続デバイスを {0} 件検出しました。",
                    devices.Count);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await _dispatchAsync(() =>
                StatusText = App.Text("Status.Cancelled", "キャンセルされました")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LoggerBootstrap.Log.Error("未接続デバイスの列挙に失敗しました", ex);
            await _dispatchAsync(() =>
                StatusText = App.Text(
                    "DeviceCleanup.LoadFailed",
                    "未接続デバイスを読み込めませんでした (ログを確認してください)。")).ConfigureAwait(false);
        }
        finally
        {
            await _dispatchAsync(() => IsLoading = false).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!_operationCoordinator.TryBegin(out var operation))
        {
            StatusText = App.Text(
                "Status.Busy",
                "別のメンテナンス操作が実行中です。完了後にもう一度お試しください。");
            return;
        }

        using var activeOperation = operation!;
        await LoadDisconnectedDevicesAsync(activeOperation.Token).ConfigureAwait(false);
    }

    [RelayCommand]
    private void SelectAll()
    {
        if (!CanChangeSelection)
        {
            return;
        }

        _suppressSelectionUpdates = true;
        try
        {
            foreach (var device in Devices)
            {
                device.IsSelected = true;
            }
        }
        finally
        {
            _suppressSelectionUpdates = false;
        }

        RecountSelection();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        if (IsBusy)
        {
            return;
        }

        _suppressSelectionUpdates = true;
        try
        {
            foreach (var device in Devices)
            {
                device.IsSelected = false;
            }
        }
        finally
        {
            _suppressSelectionUpdates = false;
        }

        RecountSelection();
        IsConfirmingRemoval = false;
    }

    [RelayCommand]
    private void RequestRemoval()
    {
        if (CanRemove)
        {
            IsConfirmingRemoval = true;
        }
    }

    [RelayCommand]
    private void CancelRemoval() => IsConfirmingRemoval = false;

    [RelayCommand]
    private async Task ConfirmRemovalAsync()
    {
        var selected = Devices.Where(device => device.IsSelected).ToArray();
        if (!IsConfirmingRemoval || selected.Length == 0)
        {
            return;
        }

        if (!_operationCoordinator.TryBegin(out var operation))
        {
            StatusText = App.Text(
                "Status.Busy",
                "別のメンテナンス操作が実行中です。完了後にもう一度お試しください。");
            return;
        }

        using var activeOperation = operation!;
        var removedInstanceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        var skipped = 0;
        var failed = 0;
        var requiresRestart = false;
        var completed = false;

        await _dispatchAsync(() =>
        {
            IsConfirmingRemoval = false;
            IsRemoving = true;
        }).ConfigureAwait(false);

        try
        {
            for (var index = 0; index < selected.Length; index++)
            {
                activeOperation.Token.ThrowIfCancellationRequested();
                var item = selected[index];
                await _dispatchAsync(() =>
                    StatusText = App.Text(
                        "DeviceCleanup.Removing",
                        "削除中 {0} / {1}: {2}",
                        index + 1,
                        selected.Length,
                        item.DisplayName)).ConfigureAwait(false);

                DisconnectedDeviceRemovalResult result;
                try
                {
                    result = await _deviceService
                        .RemoveDisconnectedDeviceAsync(item.InstanceId, activeOperation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (activeOperation.Token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    LoggerBootstrap.Log.Error($"未接続デバイスを削除できませんでした: {item.InstanceId}", ex);
                    continue;
                }

                switch (result.Status)
                {
                    case DisconnectedDeviceRemovalStatus.Removed:
                        removed++;
                        removedInstanceIds.Add(item.InstanceId);
                        requiresRestart |= result.RequiresRestart;
                        LoggerBootstrap.Log.Info($"未接続デバイスを削除しました: {item.InstanceId}");
                        break;
                    case DisconnectedDeviceRemovalStatus.AlreadyAbsent:
                        skipped++;
                        removedInstanceIds.Add(item.InstanceId);
                        LoggerBootstrap.Log.Info($"未接続デバイスは既に削除済みです: {item.InstanceId}");
                        break;
                    case DisconnectedDeviceRemovalStatus.Reconnected:
                        skipped++;
                        LoggerBootstrap.Log.Info($"再接続されたため削除を見送りました: {item.InstanceId}");
                        break;
                    case DisconnectedDeviceRemovalStatus.Protected:
                        skipped++;
                        LoggerBootstrap.Log.Info($"保護対象のため削除を見送りました: {item.InstanceId}");
                        break;
                    default:
                        failed++;
                        LoggerBootstrap.Log.Error(
                            $"未接続デバイスを削除できませんでした: {item.InstanceId} " +
                            $"(Win32={result.ErrorCode}: {result.ErrorMessage})");
                        break;
                }
            }

            IReadOnlyList<DisconnectedDevice>? refreshedDevices = null;
            try
            {
                refreshedDevices = await _deviceService
                    .GetDisconnectedDevicesAsync(activeOperation.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LoggerBootstrap.Log.Error("削除後の未接続デバイス再読込に失敗しました", ex);
            }

            var summaryKey = requiresRestart
                ? "DeviceCleanup.CompletedRestart"
                : "DeviceCleanup.Completed";
            var summaryFallback = requiresRestart
                ? "{0} 件を削除、{1} 件をスキップ、{2} 件失敗しました。反映には PC の再起動が必要です。"
                : "{0} 件を削除、{1} 件をスキップ、{2} 件失敗しました。";

            await _dispatchAsync(() =>
            {
                if (refreshedDevices is not null)
                {
                    ReplaceDevices(refreshedDevices);
                }
                else
                {
                    RemoveCompletedDevices(removedInstanceIds);
                }

                StatusText = App.Text(summaryKey, summaryFallback, removed, skipped, failed);
            }).ConfigureAwait(false);
            completed = true;
        }
        catch (OperationCanceledException) when (activeOperation.Token.IsCancellationRequested)
        {
            await _dispatchAsync(() =>
                StatusText = App.Text("Status.Cancelled", "キャンセルされました")).ConfigureAwait(false);
        }
        finally
        {
            await _dispatchAsync(() =>
            {
                IsRemoving = false;
                if (!completed)
                {
                    RecountSelection();
                }
            }).ConfigureAwait(false);
        }
    }

    private void ReplaceDevices(IEnumerable<DisconnectedDevice> devices)
    {
        Devices.Clear();
        foreach (var device in devices)
        {
            Devices.Add(new DisconnectedDeviceItemViewModel(device, OnDeviceSelectionChanged));
        }

        SelectedCount = 0;
        IsConfirmingRemoval = false;
        NotifyDeviceCollectionChanged();
    }

    private void RemoveCompletedDevices(IReadOnlySet<string> removedInstanceIds)
    {
        for (var index = Devices.Count - 1; index >= 0; index--)
        {
            if (removedInstanceIds.Contains(Devices[index].InstanceId))
            {
                Devices.RemoveAt(index);
            }
        }

        RecountSelection();
        NotifyDeviceCollectionChanged();
    }

    private void RecountSelection()
    {
        SelectedCount = Devices.Count(device => device.IsSelected);
        if (SelectedCount == 0)
        {
            IsConfirmingRemoval = false;
        }
    }

    private void OnDeviceSelectionChanged()
    {
        if (!_suppressSelectionUpdates)
        {
            RecountSelection();
        }
    }

    private void NotifyDeviceCollectionChanged()
    {
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanChangeSelection));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(SelectionText));
    }

    private static async Task DispatchToUiAsync(Action action) =>
        await Dispatcher.UIThread.InvokeAsync(action);
}

/// <summary>未接続デバイス一覧の 1 行。</summary>
public sealed partial class DisconnectedDeviceItemViewModel : ObservableObject
{
    private readonly Action _selectionChanged;

    public DisconnectedDevice Device { get; }

    public string InstanceId => Device.InstanceId;

    public string DisplayName => Device.DisplayName;

    public string Details => string.Join(
        " · ",
        new[] { Device.ClassName, Device.Manufacturer }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    [ObservableProperty]
    private bool isSelected;

    public DisconnectedDeviceItemViewModel(
        DisconnectedDevice device,
        Action selectionChanged)
    {
        Device = device;
        _selectionChanged = selectionChanged;
    }

    partial void OnIsSelectedChanged(bool value) => _selectionChanged();
}
