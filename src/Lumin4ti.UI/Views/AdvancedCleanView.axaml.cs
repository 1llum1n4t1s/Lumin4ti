using Avalonia;
using Avalonia.Controls;
using Lumin4ti.UI.ViewModels;

namespace Lumin4ti.UI.Views;

public partial class AdvancedCleanView : UserControl
{
    /// <summary>固定幅の列 (選択・サイズ・更新・規則・安全性) だけで 480px 近くあるため、一覧の最小幅を決める。</summary>
    private const double ResultsMinWidth = 780;

    public AdvancedCleanView()
    {
        InitializeComponent();
        // 横スクロールの ScrollViewer は内容を無限幅で測るため、星取り列が長いパスの自然幅まで伸びる。
        // 幅を「表示幅 (最小は ResultsMinWidth)」に固定し、名前・フォルダー列は省略表示のまま収める。
        ResultsScroll.SizeChanged += (_, e) => ResultsGrid.Width = System.Math.Max(ResultsMinWidth, e.NewSize.Width);
    }

    /// <summary>表示のたびにドライブ一覧と結果の世代を見直す。走査・削除は始めない。</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as AdvancedCleanViewModel)?.Activate();
    }
}
