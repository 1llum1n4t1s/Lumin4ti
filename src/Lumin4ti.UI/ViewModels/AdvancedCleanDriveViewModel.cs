using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumin4ti.Core.Services.Windows.Cleanup;

namespace Lumin4ti.UI.ViewModels;

/// <summary>走査するドライブ 1 件。準備済みの固定ドライブだけが既定で選ばれる。</summary>
public sealed partial class AdvancedCleanDriveViewModel : ObservableObject
{
    [ObservableProperty]
    private bool isSelected;

    public AdvancedCleanDriveViewModel(AdvancedDrive drive)
    {
        ArgumentNullException.ThrowIfNull(drive);
        Drive = drive;
        isSelected = drive.DefaultSelected;
    }

    public AdvancedDrive Drive { get; }

    /// <summary>言語を切り替えたとき、種別 (固定・リムーバブル) の表示の再読込を通知する。</summary>
    internal void RefreshLocale() => OnPropertyChanged(nameof(DisplayName));

    /// <summary>「C:  Windows（固定）」の形。</summary>
    public string DisplayName
    {
        get
        {
            var type = CleanupText.Get(Drive.Type == System.IO.DriveType.Removable
                ? "Cleanup.Advanced.Drive.Removable"
                : "Cleanup.Advanced.Drive.Fixed");
            var label = string.IsNullOrWhiteSpace(Drive.Label) ? string.Empty : "  " + Drive.Label;
            return $"{Drive.Root.TrimEnd('\\')}{label}（{type}）";
        }
    }
}
