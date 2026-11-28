using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Lumin4ti.UI.Controls;

/// <summary>
/// クリーンアップの全タブが載る共通の入れ物。見出し → 操作バー → 進捗 → 通知 → 結果 → 本体 → 実行バーの順に積み、
/// 確認シートを開いている間は背面を操作不可にして上へ重ねる。縦スクロールは本体 (<see cref="Body"/>) の中の一覧だけが持ち、
/// このコントロール自身は <see cref="ScrollViewer"/> で包まない。見た目は Styles/CleanupStyles.axaml の ControlTheme が定める。
/// </summary>
[TemplatePart("PART_SheetHost", typeof(InputElement))]
public sealed class CleanupPage : TemplatedControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<CleanupPage, string?>(nameof(Title));
    public static readonly StyledProperty<string?> DescriptionProperty = AvaloniaProperty.Register<CleanupPage, string?>(nameof(Description));
    public static readonly StyledProperty<int> DescriptionMaxLinesProperty = AvaloniaProperty.Register<CleanupPage, int>(nameof(DescriptionMaxLines), 2);
    public static readonly StyledProperty<object?> ToolbarProperty = AvaloniaProperty.Register<CleanupPage, object?>(nameof(Toolbar));
    public static readonly StyledProperty<bool> IsBusyProperty = AvaloniaProperty.Register<CleanupPage, bool>(nameof(IsBusy));
    public static readonly StyledProperty<bool> IsProgressIndeterminateProperty = AvaloniaProperty.Register<CleanupPage, bool>(nameof(IsProgressIndeterminate), true);
    public static readonly StyledProperty<double> ProgressValueProperty = AvaloniaProperty.Register<CleanupPage, double>(nameof(ProgressValue));
    public static readonly StyledProperty<double> ProgressMaximumProperty = AvaloniaProperty.Register<CleanupPage, double>(nameof(ProgressMaximum), 100d);
    public static readonly StyledProperty<string?> ProgressTextProperty = AvaloniaProperty.Register<CleanupPage, string?>(nameof(ProgressText));
    public static readonly StyledProperty<object?> NoticesProperty = AvaloniaProperty.Register<CleanupPage, object?>(nameof(Notices));
    public static readonly StyledProperty<object?> ResultProperty = AvaloniaProperty.Register<CleanupPage, object?>(nameof(Result));
    public static readonly StyledProperty<bool> IsResultVisibleProperty = AvaloniaProperty.Register<CleanupPage, bool>(nameof(IsResultVisible), true);
    public static readonly StyledProperty<object?> BodyProperty = AvaloniaProperty.Register<CleanupPage, object?>(nameof(Body));
    public static readonly StyledProperty<object?> FooterProperty = AvaloniaProperty.Register<CleanupPage, object?>(nameof(Footer));
    public static readonly StyledProperty<bool> IsFooterVisibleProperty = AvaloniaProperty.Register<CleanupPage, bool>(nameof(IsFooterVisible), true);
    public static readonly StyledProperty<object?> SheetProperty = AvaloniaProperty.Register<CleanupPage, object?>(nameof(Sheet));
    public static readonly StyledProperty<bool> IsSheetOpenProperty = AvaloniaProperty.Register<CleanupPage, bool>(nameof(IsSheetOpen));
    public static readonly StyledProperty<ICommand?> SheetCancelCommandProperty = AvaloniaProperty.Register<CleanupPage, ICommand?>(nameof(SheetCancelCommand));

    private bool _isResultShown;
    private bool _isFooterShown;
    private InputElement? _sheetHost;

    public static readonly DirectProperty<CleanupPage, bool> IsResultShownProperty =
        AvaloniaProperty.RegisterDirect<CleanupPage, bool>(nameof(IsResultShown), o => o._isResultShown);

    public static readonly DirectProperty<CleanupPage, bool> IsFooterShownProperty =
        AvaloniaProperty.RegisterDirect<CleanupPage, bool>(nameof(IsFooterShown), o => o._isFooterShown);

    private IInputElement? _focusBeforeSheet;
    private bool _isToolbarShown;
    private bool _isNoticesShown;

    public static readonly DirectProperty<CleanupPage, bool> IsToolbarShownProperty =
        AvaloniaProperty.RegisterDirect<CleanupPage, bool>(nameof(IsToolbarShown), o => o._isToolbarShown);

    public static readonly DirectProperty<CleanupPage, bool> IsNoticesShownProperty =
        AvaloniaProperty.RegisterDirect<CleanupPage, bool>(nameof(IsNoticesShown), o => o._isNoticesShown);

    public CleanupPage()
    {
        // 操作バー・通知は中の要素がすべて非表示になっても余白だけが残らないよう、レイアウトのたびに見直す。
        LayoutUpdated += (_, _) => UpdateContentShown();
    }

    static CleanupPage()
    {
        ToolbarProperty.Changed.AddClassHandler<CleanupPage>((page, _) => page.UpdateContentShown());
        NoticesProperty.Changed.AddClassHandler<CleanupPage>((page, _) => page.UpdateContentShown());
        // 結果・実行バーは「内容がある」かつ「表示してよい」ときだけ出す (空の枠や区切り線を残さない)。
        ResultProperty.Changed.AddClassHandler<CleanupPage>((page, _) => page.UpdateShown());
        IsResultVisibleProperty.Changed.AddClassHandler<CleanupPage>((page, _) => page.UpdateShown());
        FooterProperty.Changed.AddClassHandler<CleanupPage>((page, _) => page.UpdateShown());
        IsFooterVisibleProperty.Changed.AddClassHandler<CleanupPage>((page, _) => page.UpdateShown());
        IsSheetOpenProperty.Changed.AddClassHandler<CleanupPage>((page, e) => page.OnSheetOpenChanged(e.GetNewValue<bool>()));
    }

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string? Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    /// <summary>説明を何行まで出すか (超えた分は省略し、全文はツールチップに出す)。一覧の高さを優先するタブは 1 にする。</summary>
    public int DescriptionMaxLines { get => GetValue(DescriptionMaxLinesProperty); set => SetValue(DescriptionMaxLinesProperty, value); }

    public object? Toolbar { get => GetValue(ToolbarProperty); set => SetValue(ToolbarProperty, value); }

    /// <summary>true の間だけ進捗 (バー + <see cref="ProgressText"/>) を出す。</summary>
    public bool IsBusy { get => GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }

    public bool IsProgressIndeterminate { get => GetValue(IsProgressIndeterminateProperty); set => SetValue(IsProgressIndeterminateProperty, value); }

    public double ProgressValue { get => GetValue(ProgressValueProperty); set => SetValue(ProgressValueProperty, value); }

    public double ProgressMaximum { get => GetValue(ProgressMaximumProperty); set => SetValue(ProgressMaximumProperty, value); }

    public string? ProgressText { get => GetValue(ProgressTextProperty); set => SetValue(ProgressTextProperty, value); }

    public object? Notices { get => GetValue(NoticesProperty); set => SetValue(NoticesProperty, value); }

    public object? Result { get => GetValue(ResultProperty); set => SetValue(ResultProperty, value); }

    public bool IsResultVisible { get => GetValue(IsResultVisibleProperty); set => SetValue(IsResultVisibleProperty, value); }

    /// <summary>残りの高さをすべて使う本体。縦スクロールはこの中の一覧が持つ。</summary>
    public object? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

    public object? Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }

    public bool IsFooterVisible { get => GetValue(IsFooterVisibleProperty); set => SetValue(IsFooterVisibleProperty, value); }

    public object? Sheet { get => GetValue(SheetProperty); set => SetValue(SheetProperty, value); }

    public bool IsSheetOpen { get => GetValue(IsSheetOpenProperty); set => SetValue(IsSheetOpenProperty, value); }

    /// <summary>Esc で実行するコマンド (シートを閉じる処理)。</summary>
    public ICommand? SheetCancelCommand { get => GetValue(SheetCancelCommandProperty); set => SetValue(SheetCancelCommandProperty, value); }

    /// <summary>テンプレート用: <see cref="Result"/> に内容があり <see cref="IsResultVisible"/> が true のとき true。</summary>
    public bool IsResultShown => _isResultShown;

    /// <summary>テンプレート用: <see cref="Footer"/> に内容があり <see cref="IsFooterVisible"/> が true のとき true。</summary>
    public bool IsFooterShown => _isFooterShown;

    /// <summary>テンプレート用: <see cref="Toolbar"/> に表示される要素があるとき true。</summary>
    public bool IsToolbarShown => _isToolbarShown;

    /// <summary>テンプレート用: <see cref="Notices"/> に表示される要素があるとき true。</summary>
    public bool IsNoticesShown => _isNoticesShown;

    private static bool HasVisibleContent(object? content) => content switch
    {
        null => false,
        Panel panel => panel.IsVisible && panel.Children.Any(child => child.IsVisible),
        Visual visual => visual.IsVisible,
        _ => true,
    };

    private void UpdateContentShown()
    {
        SetAndRaise(IsToolbarShownProperty, ref _isToolbarShown, HasVisibleContent(Toolbar));
        SetAndRaise(IsNoticesShownProperty, ref _isNoticesShown, HasVisibleContent(Notices));
    }

    private void UpdateShown()
    {
        SetAndRaise(IsResultShownProperty, ref _isResultShown, Result is not null && IsResultVisible);
        SetAndRaise(IsFooterShownProperty, ref _isFooterShown, Footer is not null && IsFooterVisible);
    }

    private void OnSheetOpenChanged(bool isOpen)
    {
        if (isOpen)
        {
            // 2 段階の確認でシートの中身が入れ替わる場合があるため、シートの中の要素は「開く前のフォーカス」として記録しない。
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (!IsInsideSheet(focused))
            {
                _focusBeforeSheet = focused;
            }

            // シートの中身はこの変更の後のレイアウトで実体化するため、少し遅らせて最初のフォーカス可能な要素へ移す。
            Dispatcher.UIThread.Post(FocusSheet, DispatcherPriority.Loaded);
            return;
        }

        // 閉じた瞬間は背面がまだ無効 (IsEffectivelyEnabled が偽) のため、判定と復帰は有効に戻った後で行う。
        Dispatcher.UIThread.Post(RestoreFocus, DispatcherPriority.Loaded);
    }

    private bool IsInsideSheet(IInputElement? element) =>
        element is Visual visual && _sheetHost is { } host && (ReferenceEquals(visual, host) || host.IsVisualAncestorOf(visual));

    private void RestoreFocus()
    {
        var previous = _focusBeforeSheet;
        if (IsSheetOpen)
        {
            return;
        }

        _focusBeforeSheet = null;
        if (previous is InputElement { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } element)
        {
            element.Focus();
        }
    }

    private void FocusSheet()
    {
        if (!IsSheetOpen)
        {
            return;
        }

        if (_sheetHost is not { } host)
        {
            return;
        }

        var target = host.GetVisualDescendants()
            .OfType<InputElement>()
            .FirstOrDefault(c => c.Focusable && c.IsEffectivelyEnabled && c.IsEffectivelyVisible);
        // フォーカス可能な要素が無くても Esc を受けられるよう、枠自体へ移す。
        (target ?? host).Focus(NavigationMethod.Tab);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _sheetHost = e.NameScope.Find<InputElement>("PART_SheetHost");
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsSheetOpen && SheetCancelCommand is { } command && command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
