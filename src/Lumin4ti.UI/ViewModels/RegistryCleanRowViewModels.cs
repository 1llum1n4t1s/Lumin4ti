using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;

namespace Lumin4ti.UI.ViewModels;

/// <summary>レジストリタブの削除候補 1 行。選択と (アンインストールなどの) 個別承認を持つ。</summary>
internal sealed class RegistryCleanRowViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _isSelected;
    private bool _isApproved;
    private bool _editable;

    internal RegistryCleanRowViewModel(RegistryScanItem item, RegistryCleanCategoryDefinition definition, bool selected, Action changed)
    {
        _isSelected = selected;
        Item = item;
        Definition = definition;
        _changed = changed;
    }

    internal RegistryScanItem Item { get; }

    internal RegistryCleanCategoryDefinition Definition { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _changed();
            }
        }
    }

    /// <summary>確認画面での個別承認 (<see cref="RequiresApproval"/> の行だけが使う)。</summary>
    public bool IsApproved
    {
        get => _isApproved;
        set
        {
            if (SetProperty(ref _isApproved, value))
            {
                _changed();
            }
        }
    }

    public bool RequiresApproval => Definition.RequiresIndividualApproval;

    /// <summary>チェックボックスを操作できるか (走査済みで、実行中・確認中でないとき)。</summary>
    public bool IsCheckEnabled => _editable;

    /// <summary>ハイブの表示 (HKLM / HKCU。技術的な名前なので翻訳しない)。</summary>
    public string HiveText => Item.Hive == RegistryCleanHive.LocalMachine ? "HKLM" : "HKCU";

    /// <summary>32 ビット表示 (WOW6432Node) の行だけ印を出す。64 ビット側は空。</summary>
    public string ViewText => Item.View == RegistryCleanView.Registry32 ? CleanupText.Get("Cleanup.Registry.Ui.View32") : string.Empty;

    public bool Is32BitView => Item.View == RegistryCleanView.Registry32;

    /// <summary>キーパス。利用者の Classes (<c>&lt;SID&gt;_Classes</c>) は、HKCU から見た <c>Software\Classes\…</c> で表示する。</summary>
    public string KeyPath => Item.Hive == RegistryCleanHive.UserClasses ? @"Software\Classes\" + Item.KeyPath : Item.KeyPath;

    public string FullKeyPath => $"{HiveText}\\{KeyPath}";

    /// <summary>値名 (キー単位の項目は空、既定値は空文字)。</summary>
    public string ValueName => Item.ValueName ?? string.Empty;

    public bool HasValueName => Item.ValueName is not null;

    /// <summary>候補にした理由 (現在の言語)。</summary>
    public string ReasonText => CleanupText.Detail(Item.Reason);

    internal void RefreshEditable(bool editable)
    {
        _editable = editable;
        OnPropertyChanged(nameof(IsCheckEnabled));
    }

    internal void RefreshLocale()
    {
        OnPropertyChanged(nameof(ViewText));
        OnPropertyChanged(nameof(ReasonText));
    }
}

/// <summary>カテゴリー 1 つ分の行の集まり。保持した件数と理由の要約は選択肢に出さず、文言だけ持つ。</summary>
internal sealed class RegistryCleanGroupViewModel : ObservableObject
{
    private const int MaxRetainedReasons = 3;

    private IReadOnlyList<RegistryRetainedEntry> _retained = [];

    internal RegistryCleanGroupViewModel(RegistryCleanCategoryDefinition definition)
    {
        Definition = definition;
    }

    internal RegistryCleanCategoryDefinition Definition { get; }

    public ObservableCollection<RegistryCleanRowViewModel> Rows { get; } = [];

    public string Name => CleanupText.Get($"Cleanup.Registry.Category.{Definition.Category}.Name");

    public string Description => CleanupText.Get($"Cleanup.Registry.Category.{Definition.Category}.Description");

    /// <summary>「すべて選択」の対象外 (共有 DLL・アンインストール) か。</summary>
    public bool ExcludedFromSelectAll => !Definition.IncludedInSelectAll;

    public bool RequiresApproval => Definition.RequiresIndividualApproval;

    public bool HasRows => Rows.Count > 0;

    public int SelectedCount => Rows.Count(row => row.IsSelected);

    public int RetainedCount => _retained.Count;

    public bool HasRetained => _retained.Count > 0;

    /// <summary>保持した件数と、多い順の理由 (最大 3 種) の要約。</summary>
    public string RetainedText
    {
        get
        {
            if (_retained.Count == 0)
            {
                return string.Empty;
            }

            var reasons = _retained
                .GroupBy(entry => CleanupText.Detail(entry.Reason))
                .OrderByDescending(group => group.Count())
                .Take(MaxRetainedReasons)
                .Select(group => $"{group.Key} ({group.Count()})");
            return CleanupText.Get("Cleanup.Registry.Ui.Retained", _retained.Count, string.Join(" / ", reasons));
        }
    }

    internal void Apply(IEnumerable<RegistryCleanRowViewModel> rows, IEnumerable<RegistryRetainedEntry> retained)
    {
        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        _retained = [.. retained];
        RefreshCounts();
    }

    internal void Clear() => Apply([], []);

    internal void RefreshCounts()
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(RetainedCount));
        OnPropertyChanged(nameof(HasRetained));
        OnPropertyChanged(nameof(RetainedText));
    }

    internal void RefreshLocale()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(RetainedText));
        foreach (var row in Rows)
        {
            row.RefreshLocale();
        }
    }
}

/// <summary>復元の一覧の 1 行 (バックアップ 1 件)。</summary>
internal sealed class RegistryBackupRowViewModel(RegistryBackupSummary summary) : ObservableObject
{
    internal RegistryBackupSummary Summary { get; } = summary;

    public string Id => Summary.Id;

    public string CreatedText => Summary.CreatedUtc is { } created ? created.ToLocalTime().ToString("g") : Summary.Id;

    /// <summary>対象件数 (結果ファイルが無いときは「不明」)。</summary>
    public string ItemCountText => Summary.ItemCount is { } count
        ? CleanupText.Get("Cleanup.Registry.Ui.Backup.Count", count)
        : CleanupText.Get("Cleanup.Registry.Ui.Backup.CountUnknown");

    public bool HasResultFile => Summary.HasResultFile;

    public string ResultFileText => CleanupText.Get(Summary.HasResultFile ? "Cleanup.Registry.Ui.Backup.HasResult" : "Cleanup.Registry.Ui.Backup.NoResult");

    internal void RefreshLocale()
    {
        OnPropertyChanged(nameof(ItemCountText));
        OnPropertyChanged(nameof(ResultFileText));
    }
}
