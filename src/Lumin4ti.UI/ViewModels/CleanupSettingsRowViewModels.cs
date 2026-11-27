using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumin4ti.Core.Services.Windows.Cleanup;

namespace Lumin4ti.UI.ViewModels;

/// <summary>除外・カスタムフォルダーなど、リストから外せる 1 行。</summary>
public sealed class CleanupSettingsEntryViewModel : ObservableObject
{
    internal CleanupSettingsEntryViewModel(string value, string? rejection, Func<Task> remove)
    {
        Value = value;
        Rejection = rejection;
        RemoveCommand = new AsyncRelayCommand(remove);
        RemoveAutomationName = $"{CleanupText.Get("Cleanup.Exclusion.Remove")}: {value}";
    }

    /// <summary>表示する値 (パス・パターン・ドメイン)。</summary>
    public string Value { get; }

    /// <summary>使えない理由 (カスタムフォルダーの検査で拒否したとき)。使えるなら null。</summary>
    public string? Rejection { get; }

    public bool IsRejected => Rejection is not null;

    public IAsyncRelayCommand RemoveCommand { get; }

    public string RemoveAutomationName { get; }
}

/// <summary>除外 1 種 (ファイル・フォルダー・拡張子/ファイル名・Cookie ドメイン) の一覧と追加欄。</summary>
public sealed partial class CleanupSettingsExclusionGroupViewModel : ObservableObject
{
    private readonly CleanupSettingsViewModel _owner;

    internal CleanupSettingsExclusionGroupViewModel(CleanupExclusionKind kind, CleanupSettingsViewModel owner)
    {
        Kind = kind;
        _owner = owner;
        AddCommand = new AsyncRelayCommand(() => _owner.AddExclusionAsync(this));
    }

    public CleanupExclusionKind Kind { get; }

    /// <summary>見出し。言語切替で変わるため、構築時に固定せず都度引く (<see cref="RefreshText"/> で再通知する)。</summary>
    public string Title => CleanupText.Get($"Cleanup.Exclusion.Kind.{Kind}");

    /// <summary>入力欄の例 (透かし文字)。</summary>
    public string Hint => CleanupText.Get($"Cleanup.Exclusion.Hint.{Kind}");

    /// <summary>言語切替後に見出しと入力例を引き直す。</summary>
    internal void RefreshText()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Hint));
    }

    public ObservableCollection<CleanupSettingsEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    private string newValue = string.Empty;

    public IAsyncRelayCommand AddCommand { get; }

    internal void Refresh(IReadOnlyList<string> values)
    {
        Entries.Clear();
        foreach (var value in values)
        {
            Entries.Add(new CleanupSettingsEntryViewModel(value, null, () => _owner.RemoveExclusionAsync(Kind, value)));
        }
    }
}

/// <summary>高度なクリーンのパターン 1 件 (組み込みの既定・追加、またはユーザー定義) のオン/オフ。</summary>
public sealed partial class CleanupSettingsPatternViewModel : ObservableObject
{
    private readonly CleanupSettingsViewModel _owner;
    private bool _silent = true;

    internal CleanupSettingsPatternViewModel(
        string pattern, AdvancedPatternGroup group, bool isEnabled, CleanupSettingsViewModel owner)
    {
        Pattern = pattern;
        Group = group;
        GroupText = CleanupText.Get($"Cleanup.Advanced.Group.{group}");
        _owner = owner;
        IsEnabled = isEnabled;
        RemoveCommand = new AsyncRelayCommand(() => _owner.RemovePatternAsync(this), () => CanRemove);
        RemoveAutomationName = $"{CleanupText.Get("Cleanup.Exclusion.Remove")}: {pattern}";
        _silent = false;
    }

    public string Pattern { get; }

    public AdvancedPatternGroup Group { get; }

    public string GroupText { get; }

    /// <summary>ユーザー定義のものだけ、リストから外せる。</summary>
    public bool CanRemove => Group == AdvancedPatternGroup.UserDefined;

    public IAsyncRelayCommand RemoveCommand { get; }

    public string RemoveAutomationName { get; }

    [ObservableProperty]
    private bool isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_silent) _ = _owner.SetPatternAsync(this, value);
    }

    /// <summary>保存せず表示だけ戻す (変更を拒否したとき)。</summary>
    internal void RevertTo(bool value)
    {
        _silent = true;
        IsEnabled = value;
        _silent = false;
    }
}

/// <summary>通常のクリーンの項目 1 件について、既定の選択 (おすすめに従う / 常に選択 / 常に未選択) を上書きする行。</summary>
public sealed partial class CleanupSettingsRuleViewModel : ObservableObject
{
    private readonly CleanupSettingsViewModel _owner;
    private bool _silent = true;

    internal CleanupSettingsRuleViewModel(CleanupRuleInfo rule, bool? current, CleanupSettingsViewModel owner)
    {
        Rule = rule;
        _owner = owner;
        Name = CleanupText.RuleName(rule);
        SelectedIndex = IndexOf(current);
        _silent = false;
    }

    internal CleanupRuleInfo Rule { get; }

    public string RuleId => Rule.Id;

    public string Name { get; }

    /// <summary>選択肢の表示名 (おすすめに従う / 常に選択 / 常に未選択)。</summary>
    public IReadOnlyList<string> Choices => _owner.RuleChoices;

    /// <summary>0 = おすすめに従う、1 = 常に選択、2 = 常に未選択。</summary>
    [ObservableProperty]
    private int selectedIndex;

    internal bool? Override => SelectedIndex switch { 1 => true, 2 => false, _ => null };

    partial void OnSelectedIndexChanged(int value)
    {
        if (!_silent) _ = _owner.SetRuleOverrideAsync(this);
    }

    internal static int IndexOf(bool? value) => value switch { true => 1, false => 2, _ => 0 };

    internal void RevertTo(bool? value)
    {
        _silent = true;
        SelectedIndex = IndexOf(value);
        _silent = false;
    }
}
