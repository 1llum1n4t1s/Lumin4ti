using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Lumin4ti.UI.ViewModels;

namespace Lumin4ti.UI.Views;

public partial class CommonCleanView : UserControl
{
    public CommonCleanView()
    {
        InitializeComponent();
        ExportButton.Click += OnExportClick;
    }

    /// <summary>保存先は利用者がダイアログで選ぶ。書き出しの中身と保存は ViewModel が担う。</summary>
    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CommonCleanViewModel viewModel || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = CleanupText.Get("Cleanup.Export.Title"),
            SuggestedFileName = "lumin4ti-cleanup.txt",
            DefaultExtension = "txt",
            FileTypeChoices = new List<FilePickerFileType> { new("Text") { Patterns = ["*.txt"] } },
        });
        if (file?.TryGetLocalPath() is { } path)
        {
            await viewModel.ExportAsync(path);
        }
    }
}
