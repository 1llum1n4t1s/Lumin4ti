using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumin4ti.Core.Services.Windows.Cleanup;

namespace Lumin4ti.UI.ViewModels;

/// <summary>
/// 高度なクリーンの結果 1 行。20 万件でも持てるよう、表示用の文字列は読まれたとき (仮想化で実体化された行だけ) 作る。
/// 選択の集計は持ち主 (<see cref="AdvancedCleanViewModel"/>) が行うため、変更は <paramref name="changed"/> で知らせる。
/// </summary>
public sealed class AdvancedCleanRowViewModel : ObservableObject
{
    private readonly Action<AdvancedCleanRowViewModel>? _changed;
    private bool _isSelected;

    public AdvancedCleanRowViewModel(AdvancedCleanItem item, Action<AdvancedCleanRowViewModel>? changed = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        Item = item;
        _changed = changed;
        _isSelected = item.DefaultSelected;
        GroupKey = AdvancedCleanGroupViewModel.KeyOf(item);
    }

    public AdvancedCleanItem Item { get; }

    /// <summary>種別ごとの集計 (<see cref="AdvancedCleanGroupViewModel"/>) との突き合わせ用。</summary>
    public string GroupKey { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value)) _changed?.Invoke(this);
        }
    }

    public string Name => Item.Name;

    public string Folder => Item.Folder;

    public string SizeText => CleanupText.FormatBytes(Item.Size);

    public string ModifiedText => Item.LastWriteUtc == DateTime.MinValue
        ? string.Empty
        : Item.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public string RuleText => Item.Kind == AdvancedRuleKind.NamePattern ? Item.Rule : CleanupText.Get($"Cleanup.Advanced.RuleKind.{Item.Kind}");

    public string SafetyText => CleanupText.Get($"Cleanup.Advanced.Verdict.{Item.Verdict}");

    /// <summary>既定で選ばない・判定不能の理由。無ければ空。</summary>
    public string HintText => Item.Hint == AdvancedHint.None ? string.Empty : CleanupText.Get($"Cleanup.Advanced.Hint.{Item.Hint}");

    /// <summary>ツールチップ。パス全体と理由。</summary>
    public string ToolTipText => HintText.Length == 0 ? Item.Path : Item.Path + Environment.NewLine + HintText;

    /// <summary>判定不能の行を目立たせる。</summary>
    public bool IsWarning => Item.Verdict == AdvancedVerdict.Undeterminable;

    /// <summary>言語を切り替えたとき、読まれたときに作る文言の再読込を通知する (選択は変えない)。</summary>
    internal void RefreshLocale()
    {
        OnPropertyChanged(nameof(RuleText));
        OnPropertyChanged(nameof(SafetyText));
        OnPropertyChanged(nameof(HintText));
        OnPropertyChanged(nameof(ToolTipText));
    }

    /// <summary>集計を変えずに選択だけを切り替える (一括操作用)。変わったか。</summary>
    internal bool SetSelectedQuietly(bool value)
    {
        if (_isSelected == value) return false;
        _isSelected = value;
        OnPropertyChanged(nameof(IsSelected));
        return true;
    }
}
