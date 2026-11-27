using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lumin4ti.UI.ViewModels;

/// <summary>
/// クリーンアップの各画面 (通常・高度・スリム化・設定) の共通の土台。<see cref="IProcessingOperation"/> の
/// 状態を持ち、中身 (走査・削除・lease の取得) は派生側が実装する。
/// 走査・削除は起動時や再アクティブ時に自動で走らせず、利用者の操作でだけ始める。
/// 走査は <c>TryBegin(showProcessing:false)</c>、削除・実行は <c>showProcessing:true</c> の lease を取る。
/// </summary>
public abstract partial class CleanupOperationViewModel : ObservableObject, IProcessingOperation
{
    [ObservableProperty]
    private bool isRunning;

    [ObservableProperty]
    private string label = string.Empty;

    [ObservableProperty]
    private string progressText = string.Empty;

    [ObservableProperty]
    private bool canCancel;

    public IRelayCommand CancelCommand { get; }

    protected CleanupOperationViewModel() => CancelCommand = new RelayCommand(OnCancelRequested, () => CanCancel);

    /// <summary>中断の要求。派生側が実行中の操作の <c>CancellationTokenSource</c> を取り消す。</summary>
    protected virtual void OnCancelRequested()
    {
    }

    partial void OnCanCancelChanged(bool value) => CancelCommand.NotifyCanExecuteChanged();
}
