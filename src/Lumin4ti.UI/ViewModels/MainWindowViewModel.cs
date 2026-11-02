using System;
using System.Diagnostics;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.UI.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lumin4ti.UI.ViewModels;

public class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly CommandItemViewModel[] _items;
    private bool _cancelRequested;
    private bool _disposed;

    public bool IsProcessing => _operationCoordinator.ProcessingState.IsProcessing || Version.IsSavingSettings ||
        _items.Any(item => item.IsSavingSelection);

    private CommandItemViewModel? RunningItem => _items.FirstOrDefault(item => item.IsRunning);

    public bool CanCancelProcessing => !_cancelRequested &&
        _operationCoordinator.ProcessingState.CanCancel &&
        RunningItem is { CanCancel: true };

    public string ProcessingText => _cancelRequested
        ? App.Text("Processing.Cancelling", "キャンセル後の後処理が完了するまでお待ちください。")
        : RunningItem?.Label ?? App.Text("Processing.Wait", "処理が完了するまでお待ちください。");

    public string ProcessingDetail => RunningItem?.ProgressText ?? string.Empty;

    public IRelayCommand CancelProcessingCommand { get; }

    public CommandCategoryViewModel Update { get; }

    public CommandCategoryViewModel Cleanup { get; }

    public CommandCategoryViewModel Repair { get; }

    public CommandCategoryViewModel Performance { get; }

    public CommandCategoryViewModel System { get; }

    public CommandCategoryViewModel Organize { get; }

    public DeviceCleanupViewModel DeviceCleanup { get; }

    public VersionViewModel Version { get; }

    private readonly MaintenanceOperationCoordinator _operationCoordinator;

    public MainWindowViewModel(
        MaintenanceActionCatalog catalog,
        DeviceCleanupViewModel deviceCleanup,
        VersionViewModel version,
        MaintenanceOperationCoordinator operationCoordinator)
    {
        _operationCoordinator = operationCoordinator;
        Update = new CommandCategoryViewModel(
            catalog,
            operationCoordinator,
            CommandCategory.Update,
            "更新・セキュリティ",
            "アプリと Windows Defender の更新、セキュリティ設定の健全化を行います。");
        Cleanup = new CommandCategoryViewModel(
            catalog,
            operationCoordinator,
            CommandCategory.Cleanup,
            "クリーンアップ",
            "ディスク領域の回収と、不要なファイル・登録情報の削除を行います。");
        Repair = new CommandCategoryViewModel(
            catalog,
            operationCoordinator,
            CommandCategory.Repair,
            "修復",
            "壊れた登録状態や表示の不具合を、再登録・再構築して直します。");
        Performance = new CommandCategoryViewModel(
            catalog,
            operationCoordinator,
            CommandCategory.Performance,
            "パフォーマンス",
            "メモリ・プロセス・描画まわりを最適化します。ON = 最適化を適用、OFF = 各項目の解除値へ設定または明示設定を削除します。変更前の値は保存・復元しません。" +
            "（MMAgent の各項目のみ ON = その機能を有効化 で、推奨値は各説明を参照してください）。");
        System = new CommandCategoryViewModel(
            catalog,
            operationCoordinator,
            CommandCategory.System,
            "システム設定",
            "電源・入力・時刻などの Windows 設定を調整します。ON = 調整を適用、OFF = 各項目の解除値へ設定または明示設定を削除します。変更前の値は保存・復元しません。");
        Organize = new CommandCategoryViewModel(
            catalog,
            operationCoordinator,
            CommandCategory.Organize,
            "整理・ソート",
            "ピン留めや環境変数などを整った並び順に揃えます。");

        DeviceCleanup = deviceCleanup;
        Version = version;
        _items = new[] { Update, Cleanup, Repair, Performance, System, Organize }
            .SelectMany(category => category.Items)
            .SelectMany(item => new[] { item }.Concat(item.Children)).ToArray();
        CancelProcessingCommand = new RelayCommand(CancelProcessing, () => CanCancelProcessing);
        _operationCoordinator.StateChanged += OnOperationStateChanged;
        Version.PropertyChanged += OnProcessingPropertyChanged;
        foreach (var item in _items)
        {
            item.PropertyChanged += OnProcessingPropertyChanged;
        }
        App.LocaleChanged += OnLocaleChanged;

        // トグルの現在状態はバックグラウンドで読み込む (DISM/Get-MMAgent 等で外部プロセスを起動しうる)。
        // 初回読込と起動時更新確認は同じ排他制御を使うため、読込完了後に更新確認を投入する。
        // Task.Run で包み、async の同期プレフィックス (Process.Start) が UI スレッド上で走って
        // 起動描画をブロックするのを防ぐ。
        _ = Task.Run(() => RunInitialStateLoadAndUpdateAsync(ReloadAllStatesAsync, Version.Initialize));
    }

    /// <summary>状態を読み直した直近時刻 (再アクティブのたびに外部プロセスを起こさないための間隔管理)。</summary>
    private long _lastStateLoadAt;

    /// <summary>再読込を許す最短間隔。</summary>
    private static readonly TimeSpan StateReloadInterval = TimeSpan.FromMinutes(1);

    private async Task<bool> ReloadAllStatesAsync()
    {
        var loaded = await RunStateReloadAsync(
            _operationCoordinator,
            token => Task.WhenAll(
                Update.LoadToggleStatesAsync(token),
                Cleanup.LoadToggleStatesAsync(token),
                Repair.LoadToggleStatesAsync(token),
                Performance.LoadToggleStatesAsync(token),
                System.LoadToggleStatesAsync(token),
                Organize.LoadToggleStatesAsync(token),
                DeviceCleanup.LoadDisconnectedDevicesAsync(token))).ConfigureAwait(false);

        if (loaded)
        {
            Interlocked.Exchange(ref _lastStateLoadAt, Stopwatch.GetTimestamp());
        }

        return loaded;
    }

    /// <summary>
    /// 状態再読込も変更操作と同じ排他リースへ入れる。再読込の古い結果が、後から始まった
    /// トグル／選択操作の検証結果を上書きしないよう、取得から UI 反映までを一つの操作として扱う。
    /// </summary>
    internal static async Task<bool> RunStateReloadAsync(
        MaintenanceOperationCoordinator coordinator,
        Func<CancellationToken, Task> reload)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(reload);

        // 読み取り専用の初回・再アクティブ時照会は、変更操作のオーバーレイを出さない。
        if (!coordinator.TryBegin(out var operation, showProcessing: false))
        {
            return false;
        }

        using var activeOperation = operation!;
        await reload(activeOperation.Token).ConfigureAwait(false);
        return true;
    }

    internal static async Task RunInitialStateLoadAndUpdateAsync(
        Func<Task<bool>> reload,
        Action startUpdateCheck)
    {
        ArgumentNullException.ThrowIfNull(reload);
        ArgumentNullException.ThrowIfNull(startUpdateCheck);

        try
        {
            await reload().ConfigureAwait(false);
        }
        finally
        {
            // 状態読込が失敗しても、独立した更新確認まで失わせない。
            startUpdateCheck();
        }
    }

    /// <summary>
    /// ウィンドウが再びアクティブになったときに状態を読み直す。設定アプリやポリシー更新など
    /// アプリ外での変更を取り込み、古い表示のまま操作させない。
    /// 実行中の操作があるとき、および前回から間隔が空いていないときは何もしない。
    /// </summary>
    public void RefreshStatesOnActivated()
    {
        var lastStateLoadAt = Interlocked.Read(ref _lastStateLoadAt);
        if (_operationCoordinator.IsStopping || _operationCoordinator.ActiveCount != 0 ||
            (lastStateLoadAt != 0 && Stopwatch.GetElapsedTime(lastStateLoadAt) < StateReloadInterval))
        {
            return;
        }

        _ = Task.Run(ReloadAllStatesAsync);
    }

    private void CancelProcessing()
    {
        if (!CanCancelProcessing || RunningItem is not { CanCancel: true } item)
        {
            return;
        }

        _cancelRequested = true;
        item.CancelCommand.Execute(null);
        RefreshProcessing();
    }

    private void OnOperationStateChanged(object? sender, EventArgs e) => QueueProcessingRefresh();

    private void OnLocaleChanged() => QueueProcessingRefresh();

    private void OnProcessingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CommandItemViewModel.IsRunning) or nameof(CommandItemViewModel.CanCancel)
            or nameof(CommandItemViewModel.Label) or nameof(CommandItemViewModel.ProgressText)
            or nameof(CommandItemViewModel.IsSavingSelection)
            or nameof(VersionViewModel.IsSavingSettings))
        {
            QueueProcessingRefresh();
        }
    }

    private void QueueProcessingRefresh()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshProcessing();
        }
        else
        {
            Dispatcher.UIThread.Post(RefreshProcessing);
        }
    }

    private void RefreshProcessing()
    {
        if (_disposed)
        {
            return;
        }
        if (!_operationCoordinator.ProcessingState.IsProcessing)
        {
            _cancelRequested = false;
        }
        OnPropertyChanged(nameof(IsProcessing));
        OnPropertyChanged(nameof(ProcessingText));
        OnPropertyChanged(nameof(ProcessingDetail));
        OnPropertyChanged(nameof(CanCancelProcessing));
        CancelProcessingCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _disposed = true;
        _operationCoordinator.StateChanged -= OnOperationStateChanged;
        Version.PropertyChanged -= OnProcessingPropertyChanged;
        foreach (var item in _items)
        {
            item.PropertyChanged -= OnProcessingPropertyChanged;
        }
        App.LocaleChanged -= OnLocaleChanged;
        GC.SuppressFinalize(this);
    }
}
