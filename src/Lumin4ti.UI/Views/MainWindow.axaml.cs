using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Lumin4ti.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Lumin4ti.UI.Views;

public partial class MainWindow : Window
{
    private readonly MaintenanceOperationCoordinator _operationCoordinator;
    private bool _allowClose;

    public MainWindow()
        : this(App.Services.GetRequiredService<MaintenanceOperationCoordinator>())
    {
    }

    internal MainWindow(MaintenanceOperationCoordinator operationCoordinator)
    {
        _operationCoordinator = operationCoordinator;
        InitializeComponent();
        Closing += OnClosing;
        Activated += OnActivated;
    }

    /// <summary>
    /// 他のツールや Windows 側の変更を取り込むため、ウィンドウへ戻ってきたら状態を読み直す。
    /// 頻度制限と実行中判定は ViewModel 側が持ち、外部プロセスを無駄に起こさない。
    /// </summary>
    private void OnActivated(object? sender, EventArgs e) =>
        (DataContext as ViewModels.MainWindowViewModel)?.RefreshStatesOnActivated();

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose || _operationCoordinator.ActiveCount == 0)
        {
            return;
        }

        // OS シャットダウンでも、DISM のコンポーネントストア操作のようにキャンセルできない処理が
        // Job Object 経由で強制終了され中途半端に終わらないよう、通常のクローズと同じ補償待ちフローへ
        // 合流させる。Windows へは ShutdownBlockReasonCreate でシャットダウンの一時保留を伝え、
        // 利用者には「アプリが終了処理中」であることが標準の UI で示される。
        var hwnd = e.CloseReason == WindowCloseReason.OSShutdown
            ? TryGetPlatformHandle()?.Handle ?? IntPtr.Zero
            : IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.ShutdownBlockReasonCreate(
                hwnd,
                App.Text("Shutdown.BlockReason", "Lumin4ti がメンテナンス操作の完了を待っています…"));
        }

        // CLRを先に終了すると、各アクションの catch/finally にある補償処理も失われる。
        // いったん閉じる操作を保留し、キャンセル可能な処理へ通知した後、補償完了を待つ。
        e.Cancel = true;
        _operationCoordinator.RequestCancellation();
        // 更新ダイアログは Closing で自身のダウンロード CTS をキャンセルする。
        // 所有ウィンドウを閉じて ShowAsync を完了させ、終了待ちがダイアログ操作待ちにならないようにする。
        foreach (var ownedWindow in OwnedWindows.ToArray())
        {
            ownedWindow.Close();
        }

        await _operationCoordinator.WaitForIdleAsync();

        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.ShutdownBlockReasonDestroy(hwnd);
        }

        _allowClose = true;
        Close();
    }

    /// <summary>OS シャットダウン中の一時保留を Windows へ伝える User32 API。</summary>
    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string pwszReason);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);
    }
}
