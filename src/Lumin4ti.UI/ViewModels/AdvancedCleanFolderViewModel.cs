using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumin4ti.Core.Services.Windows.Cleanup;

namespace Lumin4ti.UI.ViewModels;

/// <summary>走査したカスタムフォルダー 1 件 (中身だけを空にする)。拒否・不存在のものは選べず、選べるものも既定では未選択。</summary>
public sealed partial class AdvancedCleanFolderViewModel : ObservableObject
{
    private readonly Action? _changed;

    [ObservableProperty]
    private bool isSelected;

    public AdvancedCleanFolderViewModel(AdvancedCustomFolderScan scan, Action? changed = null)
    {
        ArgumentNullException.ThrowIfNull(scan);
        Scan = scan;
        _changed = changed;
    }

    public AdvancedCustomFolderScan Scan { get; }

    /// <summary>言語を切り替えたとき、要約・理由の再読込を通知する (選択は変えない)。</summary>
    internal void RefreshLocale() => OnPropertyChanged(nameof(SummaryText));

    public bool IsSelectable => Scan.IsSelectable;

    public string Path => Scan.Path ?? string.Empty;

    /// <summary>件数とサイズ、または選べない理由。</summary>
    public string SummaryText
    {
        get
        {
            if (Scan.Rejection != CustomFolderRejection.None) return CleanupText.Get($"Cleanup.CustomFolder.Reject.{Scan.Rejection}");
            if (Scan.OtherReason is { } reason) return CleanupText.Detail(reason);
            if (Scan.Missing) return CleanupText.Get("Cleanup.Advanced.CustomFolder.Missing");
            return CleanupText.Get("Cleanup.Advanced.CustomFolder.Summary", Scan.Files.ToString("N0"), CleanupText.FormatBytes(Scan.Bytes));
        }
    }

    partial void OnIsSelectedChanged(bool value)
    {
        // 拒否・不存在のフォルダーは選べない (画面外から立てられても戻す)。
        if (value && !IsSelectable)
        {
            IsSelected = false;
            return;
        }

        _changed?.Invoke();
    }
}
