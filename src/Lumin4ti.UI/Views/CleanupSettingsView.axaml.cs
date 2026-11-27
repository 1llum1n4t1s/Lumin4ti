using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Lumin4ti.UI.ViewModels;

namespace Lumin4ti.UI.Views;

public partial class CleanupSettingsView : UserControl
{
    private static readonly FilePickerFileType JsonFile = new("JSON") { Patterns = ["*.json"] };

    public CleanupSettingsView() => InitializeComponent();

    private CleanupSettingsViewModel? ViewModel => DataContext as CleanupSettingsViewModel;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // タブを開くたびに、実行後の実績などを読み直す (走査・削除は自動で始めない)。
        ViewModel?.Reload();
    }

    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Lookup("Text.Cleanup.Exclusion.Import.Title"),
            AllowMultiple = false,
            FileTypeFilter = [JsonFile],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) await vm.ImportExclusionsAsync(path);
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Lookup("Text.Cleanup.Exclusion.Export.Title"),
            SuggestedFileName = "lumin4ti-cleanup-exclusions.json",
            DefaultExtension = "json",
            FileTypeChoices = [JsonFile],
        });
        if (file?.TryGetLocalPath() is { } path) await vm.ExportExclusionsAsync(path);
    }

    private async void OnPickFolderClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        IReadOnlyList<IStorageFolder> folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Lookup("Text.Cleanup.Settings.CustomFolders.Pick"),
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) await vm.AddCustomFolderAsync(path);
    }

    private string Lookup(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is string text ? text : key;
}
