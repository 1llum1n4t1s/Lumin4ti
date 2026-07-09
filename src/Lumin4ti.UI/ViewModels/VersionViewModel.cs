using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Services;
using Lumin4ti.UI.Services;
using VelopackUpdateDialog;

namespace Lumin4ti.UI.ViewModels;

/// <summary>
/// バージョン情報・自動更新タブ。更新の確認 / ダウンロード / 適用は VelopackUpdateDialog.Avalonia の
/// <see cref="UpdateDialogWindow"/> に委譲する (自前の進捗 UI は持たない)。
/// </summary>
public partial class VersionViewModel : ObservableObject
{
    private readonly UpdateService _updateService;
    private readonly ISettingsService _settingsService;
    private bool _updateDialogOpen;

    [ObservableProperty]
    private string versionText = "…";

    [ObservableProperty]
    private string updateStatusText = string.Empty;

    [ObservableProperty]
    private bool isChecking;

    [ObservableProperty]
    private bool checkForUpdatesOnStartup;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIgnoredUpdateTag))]
    private string ignoredUpdateTag = string.Empty;

    /// <summary>「このバージョンをスキップ」で無視中の更新があるか (再表示ボタンの表示制御)。</summary>
    public bool HasIgnoredUpdateTag => !string.IsNullOrEmpty(IgnoredUpdateTag);

    /// <summary>選択可能な言語一覧。</summary>
    public IReadOnlyList<LocaleOption> Locales { get; } = App.SupportedLocales;

    private LocaleOption? _selectedLocale;

    /// <summary>現在選択中の言語。変更すると即座に UI が切り替わり、設定に保存される。</summary>
    public LocaleOption? SelectedLocale
    {
        get => _selectedLocale;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedLocale))
            {
                return;
            }

            SetProperty(ref _selectedLocale, value);
            App.SetLocale(value.Key);
            _settingsService.Current.Locale = value.Key;
            _ = _settingsService.SaveAsync();
        }
    }

    public VersionViewModel(UpdateService updateService, ISettingsService settingsService)
    {
        _updateService = updateService;
        _settingsService = settingsService;
        VersionText = updateService.CurrentVersion;
        CheckForUpdatesOnStartup = settingsService.Current.CheckForUpdatesOnStartup;
        IgnoredUpdateTag = settingsService.Current.IgnoreUpdateTag ?? string.Empty;

        // 現在のロケールを選択状態に反映 (App.SetLocale は起動時に適用済み)
        _selectedLocale = Locales.FirstOrDefault(l => l.Key == App.CurrentLocaleKey) ?? Locales.FirstOrDefault();
    }

    public void Initialize()
    {
        if (_settingsService.Current.CheckForUpdatesOnStartup)
        {
            // MainWindow 確定後に走るよう Background 優先度で遅延投入する。
            Dispatcher.UIThread.Post(
                () => _ = ShowUpdateDialogAsync(manually: false),
                DispatcherPriority.Background);
        }
    }

    partial void OnCheckForUpdatesOnStartupChanged(bool value)
    {
        _settingsService.Current.CheckForUpdatesOnStartup = value;
        _ = _settingsService.SaveAsync();
    }

    [RelayCommand]
    private Task CheckForUpdateAsync() => ShowUpdateDialogAsync(manually: true);

    [RelayCommand]
    private async Task ClearIgnoredUpdateTagAsync()
    {
        if (string.IsNullOrEmpty(IgnoredUpdateTag))
        {
            return;
        }

        _settingsService.Current.IgnoreUpdateTag = null;
        IgnoredUpdateTag = string.Empty;
        await _settingsService.SaveAsync();
    }

    /// <summary>
    /// VelopackUpdateDialog を表示する。<paramref name="manually"/>=true は手動チェック
    /// (最新でも結果を表示・スキップタグを無視)、false は起動時自動チェック。
    /// </summary>
    private async Task ShowUpdateDialogAsync(bool manually)
    {
        if (_updateDialogOpen)
        {
            return;
        }

        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
        {
            return;
        }

        var manager = _updateService.TryCreateInstalledManager();
        if (manager is null)
        {
            if (manually)
            {
                UpdateStatusText = "開発ビルドのため更新確認はスキップされます (インストール版でのみ動作)";
            }

            return;
        }

        _updateDialogOpen = true;
        IsChecking = true;
        UpdateStatusText = string.Empty;
        try
        {
            var options = new UpdateDialogOptions
            {
                Strings = Lumin4tiUpdateStrings.Instance,
                IgnoredTagName = _settingsService.Current.IgnoreUpdateTag,
                AccentBrush = new SolidColorBrush(Color.Parse("#0A84FF")),
            };
            options.VersionIgnored += tag => Dispatcher.UIThread.Post(() =>
            {
                _settingsService.Current.IgnoreUpdateTag = tag;
                IgnoredUpdateTag = tag ?? string.Empty;
                _ = _settingsService.SaveAsync();
            });
            options.ErrorOccurred += ex =>
                LoggerBootstrap.Log.Error($"Velopack 更新失敗: {ex.GetType().Name}", ex);

            await UpdateDialogWindow.ShowAsync(owner, manager, options, manualCheck: manually);
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"更新の確認に失敗しました: {ex.Message}";
            LoggerBootstrap.Log.Error("更新ダイアログの表示に失敗しました", ex);
        }
        finally
        {
            (manager as IDisposable)?.Dispose();
            IsChecking = false;
            _updateDialogOpen = false;
        }
    }

    [RelayCommand]
    private void OpenLogsFolder() => OpenFolder(AppPaths.LogsDirectory);

    [RelayCommand]
    private void OpenSettingsFolder() => OpenFolder(AppPaths.AppDataDirectory);

    private static void OpenFolder(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LoggerBootstrap.Log.Error("フォルダを開けませんでした", ex);
        }
    }
}
