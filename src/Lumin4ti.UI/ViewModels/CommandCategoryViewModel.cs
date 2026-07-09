using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows;

namespace Lumin4ti.UI.ViewModels;

/// <summary>
/// サイドバー 1 タブ分 (カテゴリ単位) の項目一覧。CommandCategoryView を共用する。
/// </summary>
public partial class CommandCategoryViewModel : ObservableObject
{
    private readonly CommandCategory _category;
    private readonly string _titleFallback;
    private readonly string _captionFallback;

    /// <summary>カテゴリ見出し (ローカライズ済み)。</summary>
    public string Title => App.Text($"Category.{_category}", _titleFallback);

    /// <summary>カテゴリ説明 (ローカライズ済み)。</summary>
    public string Caption => App.Text($"Category.{_category}.Caption", _captionFallback);

    public IReadOnlyList<CommandItemViewModel> Items { get; }

    [ObservableProperty]
    private string statusText = string.Empty;

    public CommandCategoryViewModel(
        MaintenanceActionCatalog catalog,
        CommandCategory category,
        string title,
        string caption)
    {
        _category = category;
        _titleFallback = title;
        _captionFallback = caption;
        Items = catalog.Items
            .Where(i => i.Category == category)
            .Select(i => new CommandItemViewModel(i, RunActionAsync, SetToggleAsync))
            .ToList();

        App.LocaleChanged += () =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Caption));
        };
    }

    /// <summary>
    /// トグル項目の現在状態をバックグラウンドで読み込む (起動時に 1 回)。
    /// GetStateAsync は外部プロセス (Get-MMAgent / bcdedit 等) を起動しうるため、
    /// 呼び出し側 (MainWindowViewModel) が Task.Run で UI スレッド外から呼ぶ前提。
    /// 状態反映 (ApplyState) は UI バインディングに触れるため UI スレッドへ marshal する。
    /// </summary>
    public async Task LoadToggleStatesAsync()
    {
        var toggles = Items.Where(i => i.Item is IMaintenanceToggle).ToList();
        await Task.WhenAll(toggles.Select(async item =>
        {
            bool? state;
            try
            {
                state = await ((IMaintenanceToggle)item.Item).GetStateAsync();
            }
            catch (Exception ex)
            {
                LoggerBootstrap.Log.Error($"{item.Item.Id} の状態取得に失敗しました", ex);
                state = null;
            }

            await Dispatcher.UIThread.InvokeAsync(() => item.ApplyState(state));
        }));
    }

    private async Task RunActionAsync(CommandItemViewModel item)
    {
        item.IsRunning = true;
        item.ResultText = string.Empty;
        var ct = item.BeginRun();
        StatusText = item.IsLongRunning
            ? App.Text("Status.RunningLong", "{0} を実行中… (数分かかることがあります)", item.Label)
            : App.Text("Status.Running", "{0} を実行中…", item.Label);
        try
        {
            MaintenanceActionResult result;
            try
            {
                // 実行中のライブ出力: 直近 8 行を保持して表示する (Progress<T> は UI スレッドへ marshal される)
                var recentLines = new Queue<string>(capacity: 9);
                var progress = new Progress<string>(line =>
                {
                    recentLines.Enqueue(line);
                    while (recentLines.Count > 8)
                    {
                        recentLines.Dequeue();
                    }

                    item.ProgressText = string.Join(Environment.NewLine, recentLines);
                });

                result = await ((IMaintenanceAction)item.Item).ExecuteAsync(progress, ct);
            }
            catch (OperationCanceledException)
            {
                result = MaintenanceActionResult.Fail(App.Text("Status.Cancelled", "キャンセルされました"));
            }
            catch (Exception ex)
            {
                LoggerBootstrap.Log.Error($"{item.Item.Id} の実行に失敗しました", ex);
                result = MaintenanceActionResult.Fail(ex.Message);
            }
            finally
            {
                item.ProgressText = string.Empty;
            }

            result = await RestartExplorerIfNeededAsync(item, result);
            ShowResult(
                item,
                result,
                App.Text("Status.Completed", "{0} が完了しました", item.Label),
                App.Text("Status.Failed", "{0} が失敗しました (ログを確認してください)", item.Label));
        }
        finally
        {
            item.EndRun();
            item.IsRunning = false;
        }
    }

    private async Task SetToggleAsync(CommandItemViewModel item, bool on)
    {
        item.IsRunning = true;
        item.ResultText = string.Empty;
        var ct = item.BeginRun();
        StatusText = on
            ? App.Text("Status.TogglingOn", "{0} を ON に設定中…", item.Label)
            : App.Text("Status.TogglingOff", "{0} を OFF に設定中…", item.Label);
        try
        {
            MaintenanceActionResult result;
            try
            {
                result = await ((IMaintenanceToggle)item.Item).SetStateAsync(on, ct);
            }
            catch (OperationCanceledException)
            {
                result = MaintenanceActionResult.Fail(App.Text("Status.Cancelled", "キャンセルされました"));
            }
            catch (Exception ex)
            {
                LoggerBootstrap.Log.Error($"{item.Item.Id} の切り替えに失敗しました", ex);
                result = MaintenanceActionResult.Fail(ex.Message);
            }

            if (!result.Success)
            {
                // 失敗したらスイッチ表示を実状態へ戻す
                item.RevertChecked(!on);
            }

            result = await RestartExplorerIfNeededAsync(item, result);
            ShowResult(
                item,
                result,
                on
                    ? App.Text("Status.ToggledOn", "{0} を ON にしました", item.Label)
                    : App.Text("Status.ToggledOff", "{0} を OFF にしました", item.Label),
                App.Text("Status.ToggleFailed", "{0} の切り替えに失敗しました (ログを確認してください)", item.Label));
        }
        finally
        {
            item.EndRun();
            item.IsRunning = false;
        }
    }

    /// <summary>エクスプローラーに影響する変更は、成功時に再起動までをセットで行う。</summary>
    private static async Task<MaintenanceActionResult> RestartExplorerIfNeededAsync(
        CommandItemViewModel item, MaintenanceActionResult result)
    {
        if (!result.Success || !item.AffectsExplorer)
        {
            return result;
        }

        try
        {
            await Task.Run(ExplorerRestarter.Restart);
            return result with { Detail = AppendLine(result.Detail, App.Text("Result.ExplorerRestarted", "  - エクスプローラーを再起動しました")) };
        }
        catch (Exception ex)
        {
            LoggerBootstrap.Log.Error("エクスプローラーの再起動に失敗しました", ex);
            return result with { Detail = AppendLine(result.Detail, App.Text("Result.ExplorerRestartFailed", "  - エクスプローラーの再起動に失敗しました (手動で再起動してください)")) };
        }
    }

    private void ShowResult(CommandItemViewModel item, MaintenanceActionResult result, string okStatus, string failStatus)
    {
        var summary = result.Success ? App.Text("Result.Success", "✓ 完了しました") : App.Text("Result.Failure", "✗ 失敗しました");
        item.ResultText = string.IsNullOrWhiteSpace(result.Detail)
            ? summary
            : $"{summary}\n{result.Detail.Trim()}";
        item.LastRunFailed = !result.Success;
        StatusText = result.Success ? okStatus : failStatus;
    }

    private static string AppendLine(string detail, string line) =>
        string.IsNullOrWhiteSpace(detail) ? line : $"{detail.TrimEnd()}{Environment.NewLine}{line}";
}
