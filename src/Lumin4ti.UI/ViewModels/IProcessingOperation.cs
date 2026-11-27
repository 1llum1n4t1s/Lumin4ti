using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lumin4ti.UI.ViewModels;

/// <summary>
/// 実行中表示 (MainWindow の処理中オーバーレイ・中断ボタン) の対象になる操作。
/// <see cref="CommandItemViewModel"/> とクリーンアップの各画面が実装し、MainWindowViewModel は具体型を知らずに
/// <see cref="IsRunning"/> の操作を探して <see cref="Label"/>・<see cref="ProgressText"/>・中断を表示する。
/// 状態を変更する操作は MaintenanceOperationCoordinator の lease (終了時の排他・補償) を必ず通すこと。
/// </summary>
public interface IProcessingOperation : INotifyPropertyChanged
{
    /// <summary>この操作が実行中か。</summary>
    bool IsRunning { get; }

    /// <summary>オーバーレイの見出しに出す操作名 (ローカライズ済み)。</summary>
    string Label { get; }

    /// <summary>オーバーレイの詳細に出す進捗 (直近の数行)。</summary>
    string ProgressText { get; }

    /// <summary>実行中の操作を中断できるか。</summary>
    bool CanCancel { get; }

    /// <summary>実行中の操作へ中断を要求する。補償・再検証を含む後始末は操作側が完了させる。</summary>
    IRelayCommand CancelCommand { get; }
}
