using System;
using Avalonia;
using Avalonia.Controls;
using Lumin4ti.UI.ViewModels;

namespace Lumin4ti.UI.Views;

public partial class RegistryCleanView : UserControl
{
    public RegistryCleanView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachPrompt();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        AttachPrompt();
    }

    // 実行中は処理中オーバーレイが入力を塞ぐため、復元ポイントの判断は Slim と同じ別ウィンドウ (モーダル) で聞く。
    private void AttachPrompt()
    {
        if (DataContext is RegistryCleanViewModel viewModel)
        {
            viewModel.RestorePointPrompt = new SlimRestorePointPrompt(() => TopLevel.GetTopLevel(this) as Window);
        }
    }
}
