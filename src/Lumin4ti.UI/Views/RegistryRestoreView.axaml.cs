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
        if (DataContext is RegistryRestoreViewModel { IsRunning: false } viewModel)
        {
            viewModel.RefreshCommand.Execute(null);
        }
    }
}
