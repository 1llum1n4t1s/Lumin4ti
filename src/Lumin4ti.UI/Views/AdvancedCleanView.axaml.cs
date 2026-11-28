using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Lumin4ti.UI.ViewModels;

namespace Lumin4ti.UI.Views;

public partial class AdvancedCleanView : UserControl
{
    public AdvancedCleanView()
    {
        InitializeComponent();
    }

    private AdvancedCleanViewModel? ViewModel => DataContext as AdvancedCleanViewModel;

    /// <summary>表示のたびにドライブ一覧と結果の世代を見直す。走査・削除は始めない。</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ViewModel?.Activate();
    }

    // 接続の時点で DataContext が未設定 (バインドの適用が後) のことがあるため、接続済みで設定・変更されたときも見直す。
    // Activate は繰り返し呼んでも安全 (購読は 1 回、ドライブ一覧は空のときだけ・同時読み込みは 1 回にまとまる)。
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (TopLevel.GetTopLevel(this) is not null) ViewModel?.Activate();
    }

    // メニューはクリックのたびに作る。フライアウトの中身は DataContext を引き継ぐ保証がないため、
    // コマンドと引数を直接渡し、文言もそのときの言語の辞書から読む。

    private void OnSelectMenuClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not Control anchor) return;
        ShowMenu(anchor,
            Item("Text.Cleanup.Select.Recommended", vm.SelectRecommendedCommand),
            Item("Text.Cleanup.Advanced.SelectSafe", vm.SelectSafeCommand),
            Item("Text.Cleanup.Select.All", vm.SelectAllCommand),
            Item("Text.Cleanup.Select.None", vm.SelectNoneCommand));
    }

    private void OnSortMenuClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not Control anchor) return;
        ShowMenu(anchor,
            Item("Text.Cleanup.Column.Name", vm.SortByCommand, nameof(AdvancedCleanSortColumn.Name)),
            Item("Text.Cleanup.Column.Folder", vm.SortByCommand, nameof(AdvancedCleanSortColumn.Folder)),
            Item("Text.Cleanup.Column.Size", vm.SortByCommand, nameof(AdvancedCleanSortColumn.Size)),
            Item("Text.Cleanup.Column.Modified", vm.SortByCommand, nameof(AdvancedCleanSortColumn.Modified)),
            Item("Text.Cleanup.Column.Rule", vm.SortByCommand, nameof(AdvancedCleanSortColumn.Rule)));
    }

    private void OnRowMenuClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not Control { DataContext: AdvancedCleanRowViewModel row } anchor) return;
        ShowMenu(anchor,
            Item("Text.Cleanup.Action.OpenLocation", vm.OpenLocationCommand, row),
            Item("Text.Cleanup.Action.Ignore", vm.IgnoreFileCommand, row),
            new MenuItem { Header = vm.IgnoreFolderText, Command = vm.IgnoreFolderCommand, CommandParameter = row });
    }

    private MenuItem Item(string resourceKey, System.Windows.Input.ICommand command, object? parameter = null) => new()
    {
        Header = this.TryFindResource(resourceKey, ActualThemeVariant, out var text) ? text : resourceKey,
        Command = command,
        CommandParameter = parameter,
    };

    private static void ShowMenu(Control anchor, params MenuItem[] items)
    {
        var flyout = new MenuFlyout();
        foreach (var item in items) flyout.Items.Add(item);
        flyout.ShowAt(anchor);
    }
}
