using Avalonia;
using Avalonia.Controls;
using Lumin4ti.UI.ViewModels;

namespace Lumin4ti.UI.Views;

public partial class RegistryRestoreView : UserControl
{
    public RegistryRestoreView() => InitializeComponent();

    // 初回の一覧取得は自動で走らないため、タブが表示されたとき (読み取りだけ) に引く。実行中は触らない。
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RefreshIfIdle();
    }

    // 接続の時点で DataContext が未設定のことがあるため、接続済みで設定・変更されたときも引く。
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (TopLevel.GetTopLevel(this) is not null) RefreshIfIdle();
    }

    private void RefreshIfIdle()
    {
        if (DataContext is RegistryRestoreViewModel { IsRunning: false } viewModel)
        {
            viewModel.RefreshCommand.Execute(null);
        }
    }
}
