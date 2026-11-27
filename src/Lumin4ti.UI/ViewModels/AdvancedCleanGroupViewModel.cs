using System;
using System.Globalization;
using Lumin4ti.Core.Services.Windows.Cleanup;

namespace Lumin4ti.UI.ViewModels;

/// <summary>結果を規則ごと (名前のパターン、または 0 バイトなどの特別な規則) にまとめた 1 行。件数とサイズを示し、絞り込みに使う。</summary>
public sealed class AdvancedCleanGroupViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private readonly AdvancedCleanItem _sample;

    public AdvancedCleanGroupViewModel(string key, AdvancedCleanItem sample, long count, long bytes)
    {
        ArgumentNullException.ThrowIfNull(sample);
        _sample = sample;
        Key = key;
        Count = count;
        Bytes = bytes;
        Group = sample.Group;
    }

    public string Key { get; }

    public AdvancedPatternGroup Group { get; }

    public string Title => _sample.Kind == AdvancedRuleKind.NamePattern
        ? _sample.Rule
        : CleanupText.Get($"Cleanup.Advanced.RuleKind.{_sample.Kind}");

    /// <summary>既定・追加・ユーザー定義の区分。</summary>
    public string GroupText => CleanupText.Get($"Cleanup.Advanced.Group.{_sample.Group}");

    /// <summary>言語を切り替えたとき、種別名と区分の再読込を通知する。</summary>
    internal void RefreshLocale()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(GroupText));
    }

    public long Count { get; }

    public long Bytes { get; }

    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);

    public string SizeText => CleanupText.FormatBytes(Bytes);

    /// <summary>同じ規則の行が同じキーになる。特別な規則はパターンが空なので種類で区別する。</summary>
    internal static string KeyOf(AdvancedCleanItem item) =>
        item.Kind == AdvancedRuleKind.NamePattern ? "P:" + item.Rule.ToLowerInvariant() : "K:" + item.Kind;
}
