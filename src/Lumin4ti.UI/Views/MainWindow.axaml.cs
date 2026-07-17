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
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose || e.CloseReason == WindowCloseReason.OSShutdown ||
            _operationCoordinator.ActiveCount == 0)
        {
            return;
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

        _allowClose = true;
        Close();
    }
}
