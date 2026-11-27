using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumin4ti.Core.Services.Windows.Cleanup;

namespace Lumin4ti.UI.ViewModels;

/// <summary>「通常」の結果ツリーの行の種類。分類 → 規則 → 項目 (代表パス) の 3 階層を、平らな一覧で表す。</summary>
public enum CommonCleanRowKind
{
    Category,
    Rule,
    Path,
}

/// <summary>
/// 「通常」の結果一覧の 1 行 (分類・規則・代表パスのどれか)。仮想化のため、ツリーは展開状態を持つ平らな一覧にして
/// ListBox へ渡す。選択の状態は <see cref="CommonCleanViewModel"/> が持ち、行は表示用の文字列だけを保持する。
/// </summary>
public sealed partial class CommonCleanRowViewModel : ObservableObject
{
    private readonly CommonCleanViewModel _owner;
    private bool _isExpanded;

    internal CommonCleanRowViewModel(CommonCleanViewModel owner, CommonCleanRowKind kind, string key, string name)
    {
        _owner = owner;
        Kind = kind;
        Key = key;
        Name = name;
    }

    public CommonCleanRowKind Kind { get; }

    /// <summary>分類は列挙名、規則は規則 ID、項目は絶対パス。</summary>
    public string Key { get; }

    public string Name { get; }

    internal CleanupRuleCategory? Category { get; init; }

    internal CleanupScanItem? Item { get; init; }

    public string StateText { get; init; } = string.Empty;

    public string FilesText { get; init; } = string.Empty;

    public string SizeText { get; init; } = string.Empty;

    public string ImpactText { get; init; } = string.Empty;

    /// <summary>警告・理由・補足 (複数行)。</summary>
    public string NoteText { get; init; } = string.Empty;

    public string AutomationName { get; init; } = string.Empty;

    public string SelectAutomationName { get; init; } = string.Empty;

    public string ExpandAutomationName { get; init; } = string.Empty;

    public bool CanSelect { get; init; }

    public bool IsExpandable { get; init; }

    public bool IsCategory => Kind == CommonCleanRowKind.Category;

    public bool IsRule => Kind == CommonCleanRowKind.Rule;

    public bool IsPath => Kind == CommonCleanRowKind.Path;

    public bool HasNote => NoteText.Length > 0;

    public double IndentWidth => (int)Kind * 22;

    /// <summary>場所を開く・除外へ追加できる項目か (絶対パスのときだけ)。</summary>
    public bool HasLocation => IsPath && Path.IsPathFullyQualified(Key);

    /// <summary>分類は 3 状態 (一部だけ選択のとき null)。規則は 2 状態。</summary>
    public bool? IsChecked
    {
        get => _owner.CheckStateOf(this);
        set => _owner.SetChecked(this, value == true);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(Chevron));
                _owner.OnExpandedChanged(this);
            }
        }
    }

    /// <summary>開閉を示す記号 (アイコン資産を増やさず、フォント非依存の記号を使う)。</summary>
    public string Chevron => IsExpanded ? "▾" : "▸";

    internal void SetExpandedSilently(bool value)
    {
        if (SetProperty(ref _isExpanded, value, nameof(IsExpanded)))
        {
            OnPropertyChanged(nameof(Chevron));
        }
    }

    internal void RefreshCheck() => OnPropertyChanged(nameof(IsChecked));

    [RelayCommand]
    private Task OpenLocationAsync() => _owner.OpenLocationAsync(Key);

    [RelayCommand]
    private Task IgnoreAsync() => _owner.IgnorePathAsync(Key);
}

/// <summary>確認画面の「利用者のファイル」の再確認 1 件。チェックを入れた規則だけが削除の対象になる。</summary>
public sealed partial class CommonCleanUserFileEntryViewModel(string ruleId, string name, string sizeText, string automationName)
    : ObservableObject
{
    public string RuleId { get; } = ruleId;

    public string Name { get; } = name;

    public string SizeText { get; } = sizeText;

    public string AutomationName { get; } = automationName;

    [ObservableProperty]
    private bool isConfirmed;
}

/// <summary>削除結果の 1 規則分の表示行。成功以外は理由と詳細を添える。</summary>
public sealed class CommonCleanResultLineViewModel(string name, string statusText, string detailText, bool isSuccess)
{
    public string Name { get; } = name;

    public string StatusText { get; } = statusText;

    public string DetailText { get; } = detailText;

    public bool IsSuccess { get; } = isSuccess;

    public bool HasDetail => DetailText.Length > 0;

    public string AutomationName { get; } = $"{name}、{statusText}";
}
