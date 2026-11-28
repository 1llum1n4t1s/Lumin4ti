using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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

    /// <summary>値名 (キー単位の項目は空)。既定値 (空の値名) は「(既定)」と表示する。</summary>
    public string ValueName => Item.ValueName switch
    {
        null => string.Empty,
        { Length: 0 } => CleanupText.Get("Cleanup.Registry.Ui.ValueDefault"),
        var name => name,
    };

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
        OnPropertyChanged(nameof(ValueName));
        OnPropertyChanged(nameof(ReasonText));
    }
}

/// <summary>
/// 範囲の追加・削除・差し替えを 1 回の通知で行うコレクション。数百行のカテゴリーの開閉でも、一覧の再構築を 1 回に抑える。
/// </summary>
internal sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>全体を差し替える (Reset を 1 回だけ通知する)。</summary>
    internal void Replace(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        RaiseChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    internal void InsertRange(int index, IReadOnlyCollection<T> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        CheckReentrancy();
        var added = items.ToList();
        ((List<T>)Items).InsertRange(index, added);
        RaiseChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added, index));
    }

    internal void RemoveRange(int index, int count)
    {
        if (count <= 0)
        {
            return;
        }

        CheckReentrancy();
        var list = (List<T>)Items;
        var removed = list.GetRange(index, count);
        list.RemoveRange(index, count);
        RaiseChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, index));
    }

    private void RaiseChanged(NotifyCollectionChangedEventArgs args)
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(args);
    }
}

/// <summary>カテゴリー 1 つ分の行の集まり。保持した件数と理由の要約は選択肢に出さず、文言だけ持つ。</summary>
internal sealed class RegistryCleanGroupViewModel : ObservableObject
{
    private const int MaxRetainedReasons = 3;

    private IReadOnlyList<RegistryRetainedEntry> _retained = [];
    private bool _isExpanded = true;

    internal RegistryCleanGroupViewModel(RegistryCleanCategoryDefinition definition)
    {
        Definition = definition;
    }

    internal RegistryCleanCategoryDefinition Definition { get; }

    /// <summary>開閉が変わったときに、表示用の一覧を直してもらうための通知先 (削除側の ViewModel が設定する)。</summary>
    internal Action<RegistryCleanGroupViewModel>? ExpandedChanged { get; set; }

    /// <summary>項目行を一覧に出しているか。既定は開く。走査し直すたびに開いた状態へ戻る。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                ExpandedChanged?.Invoke(this);
            }
        }
    }

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
            return CleanupText.GetCounted("Cleanup.Registry.Ui.Retained", _retained.Count, _retained.Count, string.Join(" / ", reasons));
        }
    }

    internal void Apply(IEnumerable<RegistryCleanRowViewModel> rows, IEnumerable<RegistryRetainedEntry> retained)
    {
        // 通知を出さずに開いた状態へ戻す (呼び出し側が一覧全体を組み直す)。
        _isExpanded = true;
        OnPropertyChanged(nameof(IsExpanded));
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
internal sealed class RegistryBackupRowViewModel(RegistryBackupSummary summary, RegistryRestoreViewModel? owner = null) : ObservableObject
{
    internal RegistryBackupSummary Summary { get; } = summary;

    /// <summary>表示用: 行のボタンのコマンド。画面側が親の画面を探さずに済むよう、行が持つ (行の引数は行自身を渡す)。</summary>
    public System.Windows.Input.ICommand? RequestRestoreCommand => owner?.RequestRestoreCommand;

    public System.Windows.Input.ICommand? RequestDeleteCommand => owner?.RequestDeleteCommand;

    /// <summary>表示用: 行のボタンを操作できるか (親の画面の <c>CanOperate</c> と同じ)。</summary>
    public bool CanOperate => owner?.CanOperate ?? false;

    internal void RefreshOperate() => OnPropertyChanged(nameof(CanOperate));

    public string Id => Summary.Id;

    public string CreatedText => Summary.CreatedUtc is { } created ? created.ToLocalTime().ToString("g") : Summary.Id;

    /// <summary>対象件数 (結果ファイルが無いときは「不明」)。</summary>
    public string ItemCountText => Summary.ItemCount is { } count
        ? CleanupText.GetCounted("Cleanup.Registry.Ui.Backup.Count", count, count)
        : CleanupText.Get("Cleanup.Registry.Ui.Backup.CountUnknown");

    public bool HasResultFile => Summary.HasResultFile;

    public string ResultFileText => CleanupText.Get(Summary.HasResultFile ? "Cleanup.Registry.Ui.Backup.HasResult" : "Cleanup.Registry.Ui.Backup.NoResult");

    internal void RefreshLocale()
    {
        OnPropertyChanged(nameof(ItemCountText));
        OnPropertyChanged(nameof(ResultFileText));
    }
}
