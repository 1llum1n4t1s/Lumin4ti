using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.Core.Services.Windows.Actions;
using Lumin4ti.UI.Services;

namespace Lumin4ti.UI.ViewModels;

/// <summary>
/// サイドバー 1 タブ分 (カテゴリ単位) の項目一覧。CommandCategoryView を共用する。
/// </summary>
public partial class CommandCategoryViewModel : ObservableObject
{
    internal static readonly TimeSpan StateQueryTimeout = TimeSpan.FromMinutes(2);

    /// <summary>ライブ出力の再描画間隔 (高頻度な進捗通知で UI スレッドを占有しないための下限)。</summary>
    private static readonly TimeSpan ProgressRenderInterval = TimeSpan.FromMilliseconds(120);

    private readonly CommandCategory _category;
    private readonly string _titleFallback;
    private readonly string _captionFallback;
    private readonly MaintenanceOperationCoordinator _operationCoordinator;

    /// <summary>カテゴリ見出し (ローカライズ済み)。</summary>
    public string Title => App.Text($"Category.{_category}", _titleFallback);

    /// <summary>カテゴリ説明 (ローカライズ済み)。</summary>
    public string Caption => App.Text($"Category.{_category}.Caption", _captionFallback);

    /// <summary>画面に並ぶトップレベル項目 (子項目は親カードの中に描画される)。</summary>
    public IReadOnlyList<CommandItemViewModel> Items { get; }

    /// <summary>子項目も含む全項目 (状態読み込み用)。</summary>
    private IReadOnlyList<CommandItemViewModel> AllItems { get; set; } = [];

    private Func<string> _statusText = static () => string.Empty;

    public string StatusText => _statusText();

    private void SetStatus(Func<string> render)
    {
        _statusText = render;
        OnPropertyChanged(nameof(StatusText));
    }

    public CommandCategoryViewModel(
        MaintenanceActionCatalog catalog,
        MaintenanceOperationCoordinator operationCoordinator,
        CommandCategory category,
        string title,
        string caption)
    {
        _category = category;
        _operationCoordinator = operationCoordinator;
        _titleFallback = title;
        _captionFallback = caption;
        var all = catalog.Items
            .Where(i => i.Category == category)
            .Select(i => new CommandItemViewModel(i, RunActionAsync, SetToggleAsync, SetChoiceAsync, ApplyBatchAsync))
            .ToList();

        // 前提設定を持つ項目 (ParentId) は親カードの中へ入れ子表示する。
        // 親が同じカテゴリに無い場合は独立項目として扱い、迷子にしない。
        var byId = all.ToDictionary(vm => vm.Item.Id);
        foreach (var children in all.Where(vm => vm.Item.ParentId is not null)
                     .GroupBy(vm => vm.Item.ParentId!))
        {
            if (byId.TryGetValue(children.Key, out var parent))
            {
                parent.AttachChildren(children.ToList());
            }
        }

        AllItems = all;
        Items = all
            .Where(vm => vm.Item.ParentId is not { } parentId || !byId.ContainsKey(parentId))
            .ToList();

        App.LocaleChanged += () =>
        {
            OnPropertyChanged(nameof(StatusText));
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
    public async Task LoadToggleStatesAsync(CancellationToken ct = default)
    {
        var toggles = AllItems.Where(i => i.Item is IMaintenanceToggle).ToList();
        var choices = AllItems.Where(i => i.Item is IMaintenanceChoice).ToList();

        // OS 外部変更と直近操作の override を再読込へ持ち越さない。
        // すべて破棄してから並行取得を開始し、同一再読込は Get-MMAgent 1 回へ集約する。
        foreach (var provider in toggles.Select(i => i.Item).OfType<MmAgentFeatureToggle>()
                     .Select(i => i.StateProvider).Distinct())
        {
            provider.ResetSnapshot();
        }

        // 状態取得は外部プロセス (Get-MMAgent / dism / bcdedit) を伴うため、
        // 遅い 1 項目が同じカテゴリの他項目を巻き添えにしないよう項目ごとに上限を持たせる。
        await Task.WhenAll(toggles.Select(item => QueryAndApplyStateAsync<bool?>(
            item, ((IMaintenanceToggle)item.Item).GetStateAsync, item.ApplyState, ct))
            .Concat(choices.Select(item => QueryAndApplyStateAsync<string>(
                item, ((IMaintenanceChoice)item.Item).GetSelectedValueAsync, item.ApplySelectedValue, ct))));
    }

    private static async Task QueryAndApplyStateAsync<T>(CommandItemViewModel item,
        Func<CancellationToken, Task<T?>> query, Action<T?> apply, CancellationToken ct)
    {
        T? state;
        try
        {
            state = await QueryStateAsync(query, item.Item, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }

        if (ct.IsCancellationRequested)
        {
            return;
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ct.IsCancellationRequested)
            {
                apply(state);
            }
        });
    }
    internal static Task<T?> QueryStateAsync<T>(
        Func<CancellationToken, Task<T?>> query,
        string itemId,
        CancellationToken ct = default,
        TimeSpan? timeout = null) =>
        QueryStateCoreAsync(query, itemId, itemId, () => null, ct, timeout);

    internal static Task<T?> QueryStateAsync<T>(
        Func<CancellationToken, Task<T?>> query,
        IMaintenanceItem item,
        CancellationToken ct = default,
        TimeSpan? timeout = null) =>
        QueryStateCoreAsync(query, item.Id, item.Label,
            () => item.StateFailureReason ?? (item as IMaintenanceToggle)?.StateDiagnostic?.Fallback, ct, timeout);

    private static async Task<T?> QueryStateCoreAsync<T>(
        Func<CancellationToken, Task<T?>> query,
        string itemId,
        string itemName,
        Func<string?> failureReason,
        CancellationToken ct,
        TimeSpan? timeout)
    {
        var effectiveTimeout = timeout ?? StateQueryTimeout;
        using var queryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        queryCts.CancelAfter(effectiveTimeout);
        try
        {
            var state = await query(queryCts.Token);
            if (state is null)
            {
                // 例外を投げず不明を返す項目も、失敗を画面だけに残さない。
                ct.ThrowIfCancellationRequested();
                MaintenanceItemLog.StateReadFailed(itemId, itemName, failureReason() ??
                    "状態を判定できませんでした。項目から詳細な理由が返されませんでした (非対応環境を含みます)。");
            }
            return state;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (queryCts.IsCancellationRequested)
        {
            MaintenanceItemLog.StateReadFailed(itemId, itemName,
                $"状態取得が {effectiveTimeout.TotalSeconds:0} 秒でタイムアウトしました");
            return default;
        }
        catch (Exception ex)
        {
            MaintenanceItemLog.StateReadFailed(itemId, itemName, $"{ex.GetType().Name}: {ex.Message}");
            return default;
        }
    }

    /// <summary>
    /// ドロップダウンで選ばれた値を適用する。トグルと同様に、成否にかかわらず
    /// 適用後の実状態を読み直して表示へ反映する (推測値を残さない)。
    /// </summary>
    private async Task SetChoiceAsync(CommandItemViewModel item, string value)
    {
        var choice = (IMaintenanceChoice)item.Item;
        var startedAt = Stopwatch.GetTimestamp();
        LoggerBootstrap.Log.Info($"{item.Item.Id}: {value} を適用開始");
        item.IsRunning = true;
        item.ResultText = string.Empty;
        var ct = item.BeginRun();
        if (!_operationCoordinator.TryBegin(out var operation, ct))
        {
            ShowBusyResult(item);
            // 実行中の別操作と外部コマンドによる状態照会を競合させない。
            // ドロップダウンで先に変わった表示だけを、直前の既知値へ戻す。
            item.RestorePreviousSelection();
            item.EndRun();
            item.IsRunning = false;
            return;
        }

        using var activeOperation = operation!;
        SetStatus(() => App.Text("Status.Applying", "{0} を適用中…", item.Label));
        try
        {
            MaintenanceActionResult result;
            try
            {
                result = await choice.SetSelectedValueAsync(value, activeOperation.Token);
            }
            catch (OperationCanceledException)
            {
                result = MaintenanceActionResult.Canceled();
            }
            catch (Exception ex)
            {
                MaintenanceItemLog.LogException(item.Item, "選択の適用", ex);
                result = MaintenanceActionResult.Fail(ex.Message);
            }

            await ReloadChoiceAsync(item, choice);
            LogOperationFinished(item, "選択の適用", result, startedAt);
            ShowResult(
                item,
                result,
                () => App.Text("Status.Applied", "{0} を変更しました", item.Label),
                () => App.Text("Status.ApplyFailed", "{0} の変更に失敗しました (ログを確認してください)", item.Label));
        }
        finally
        {
            item.EndRun();
            item.IsRunning = false;
        }
    }

    /// <summary>
    /// 親の切り替えで連動して変わった子の表示を読み直す。子の型 (トグル / 選択式) を問わず扱い、
    /// 将来トグルの子を足したときに片方だけ更新されない状態を作らない。
    /// </summary>
    private static async Task ReloadChildAsync(CommandItemViewModel child)
    {
        switch (child.Item)
        {
            case IMaintenanceChoice childChoice:
                await ReloadChoiceAsync(child, childChoice);
                break;
            case IMaintenanceToggle childToggle:
                var state = await QueryStateAsync(
                    childToggle.GetStateAsync,
                    child.Item);

                await Dispatcher.UIThread.InvokeAsync(() => child.ApplyState(state));
                break;
        }
    }

    private static async Task ReloadChoiceAsync(CommandItemViewModel item, IMaintenanceChoice choice)
    {
        var actual = await QueryStateAsync(
            choice.GetSelectedValueAsync,
            item.Item);

        await Dispatcher.UIThread.InvokeAsync(() => item.ApplySelectedValue(actual));
    }

    private Task RunActionAsync(CommandItemViewModel item) =>
        RunActionAsync(item, "実行", () => App.Text("Button.Run", "実行"),
            (progress, ct) => ((IMaintenanceAction)item.Item).ExecuteAsync(progress, ct));

    private Task ApplyBatchAsync(CommandItemViewModel item, bool enabled) =>
        RunActionAsync(item, enabled ? "一括ON" : "一括OFF",
            () => enabled ? App.Text("Button.BatchOn", "一括ON") : App.Text("Button.BatchOff", "一括OFF"),
            (_, ct) => ((IMaintenanceBatchSetting)item.Item).ApplyAsync(enabled, ct));

    private async Task RunActionAsync(CommandItemViewModel item, string operationName, Func<string> operationLabel,
        Func<IProgress<string>, CancellationToken, Task<MaintenanceActionResult>> execute)
    {
        var startedAt = Stopwatch.GetTimestamp();
        string DisplayName() => item.IsBatchSetting ? $"{item.Label}: {operationLabel()}" : item.Label;
        LoggerBootstrap.Log.Info($"{item.Item.Id}: {operationName} 開始");
        item.IsRunning = true;
        item.ResultText = string.Empty;
        var ct = item.BeginRun();
        if (!_operationCoordinator.TryBegin(out var operation, ct))
        {
            ShowBusyResult(item, item.IsBatchSetting ? operationLabel : null);
            item.EndRun();
            item.IsRunning = false;
            return;
        }

        using var activeOperation = operation!;
        item.ProgressText = string.Empty;
        SetStatus(() => item.IsLongRunning
            ? App.Text("Status.RunningLong", "{0} を実行中… (数分かかることがあります)", DisplayName())
            : App.Text("Status.Running", "{0} を実行中…", DisplayName()));
        try
        {
            MaintenanceActionResult result;
            try
            {
                // 実行中のライブ出力: 直近 8 行を保持して表示する (Progress<T> は UI スレッドへ marshal される)。
                // DISM / winget は \r ごとに進捗行を書き換えるため通知が高頻度になる。
                // 人間の知覚上ほぼ等価な間隔でまとめ、UI スレッドの再描画を抑える。
                var recentLines = new Queue<string>(capacity: 9);
                var lastRendered = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
                var progress = new Progress<string>(line =>
                {
                    recentLines.Enqueue(line);
                    while (recentLines.Count > 8)
                    {
                        recentLines.Dequeue();
                    }

                    if (Stopwatch.GetElapsedTime(lastRendered) < ProgressRenderInterval)
                    {
                        return;
                    }

                    lastRendered = Stopwatch.GetTimestamp();
                    item.ProgressText = string.Join(Environment.NewLine, recentLines);
                });

                result = await execute(progress, activeOperation.Token);
            }
            catch (OperationCanceledException)
            {
                result = MaintenanceActionResult.Canceled();
            }
            catch (Exception ex)
            {
                MaintenanceItemLog.LogException(item.Item, operationName, ex);
                result = MaintenanceActionResult.Fail(ex.Message);
            }
            finally
            {
                item.ProgressText = string.Empty;
            }

            var restart = await RestartExplorerIfNeededAsync(item, result);
            result = restart.Result;
            if (result.Status != MaintenanceActionStatus.Canceled && !activeOperation.Token.IsCancellationRequested)
            {
                item.RefreshCheckListEntries();
            }
            LogOperationFinished(item, operationName, result, startedAt);
            ShowResult(
                item,
                result,
                () => App.Text("Status.Completed", "{0} が完了しました", DisplayName()),
                () => App.Text("Status.Failed", "{0} が失敗しました (ログを確認してください)", DisplayName()),
                item.IsBatchSetting ? operationLabel : null, restart.RestartSucceeded, restart.OriginalDetail);
        }
        finally
        {
            item.EndRun();
            item.IsRunning = false;
        }
    }

    /// <summary>
    /// 操作の結末を 1 行にまとめて残す。キャンセルと部分成功は個々のアクションがログを書かないため、
    /// ここで記録しないと「いつ何をして、どうなったか」が後から追えない。
    /// </summary>
    private static void LogOperationFinished(
        CommandItemViewModel item,
        string operation,
        MaintenanceActionResult result,
        long startedAt)
    {
        MaintenanceItemLog.LogResult(item.Item, operation, result, Stopwatch.GetElapsedTime(startedAt));
    }

    private async Task SetToggleAsync(CommandItemViewModel item, bool on)
    {
        var startedAt = Stopwatch.GetTimestamp();
        LoggerBootstrap.Log.Info($"{item.Item.Id}: {(on ? "ON" : "OFF")} へ切り替え開始");
        item.IsRunning = true;
        item.ResultText = string.Empty;
        var ct = item.BeginRun();
        if (!_operationCoordinator.TryBegin(out var operation, ct))
        {
            ShowBusyResult(item);
            // スイッチ操作で先に変わった表示だけを直前の既知値へ戻す。
            // 実行中の別操作と外部コマンドによる状態照会を競合させない。
            item.RestorePreviousToggleState();

            item.EndRun();
            item.IsRunning = false;
            return;
        }

        using var activeOperation = operation!;
        SetStatus(() => on
            ? App.Text("Status.TogglingOn", "{0} を ON に設定中…", item.Label)
            : App.Text("Status.TogglingOff", "{0} を OFF に設定中…", item.Label));
        try
        {
            MaintenanceActionResult result;
            var toggle = (IMaintenanceToggle)item.Item;
            try
            {
                result = await toggle.SetStateAsync(on, activeOperation.Token);
            }
            catch (OperationCanceledException)
            {
                result = MaintenanceActionResult.Canceled();
            }
            catch (Exception ex)
            {
                MaintenanceItemLog.LogException(item.Item, "切り替え", ex);
                result = MaintenanceActionResult.Fail(ex.Message);
            }

            // 成否やキャンセルにかかわらず、推測値ではなく変更後の実状態を表示へ反映する。
            // 補償後も検証できる独立 token を使いつつ、終了処理を長時間塞がないよう上限を設ける。
            var actualState = await QueryStateAsync(
                toggle.GetStateAsync,
                item.Item);

            item.ApplyState(actualState);

            // 親を切り替えると OS 側で子設定も連動して変わるため、子の表示を読み直す。
            foreach (var child in item.Children)
            {
                await ReloadChildAsync(child);
            }

            var restart = await RestartExplorerIfNeededAsync(item, result);
            result = restart.Result;
            LogOperationFinished(item, on ? "ON 切り替え" : "OFF 切り替え", result, startedAt);
            ShowResult(
                item,
                result,
                () => on
                    ? App.Text("Status.ToggledOn", "{0} を ON にしました", item.Label)
                    : App.Text("Status.ToggledOff", "{0} を OFF にしました", item.Label),
                () => App.Text("Status.ToggleFailed", "{0} の切り替えに失敗しました (ログを確認してください)", item.Label),
                explorerRestartSucceeded: restart.RestartSucceeded, originalDetail: restart.OriginalDetail);
        }
        finally
        {
            item.EndRun();
            item.IsRunning = false;
        }
    }

    /// <summary>エクスプローラーに影響する変更は、成功時に再起動までをセットで行う。</summary>
    private static async Task<(MaintenanceActionResult Result, bool? RestartSucceeded, string OriginalDetail)> RestartExplorerIfNeededAsync(
        CommandItemViewModel item, MaintenanceActionResult result)
    {
        if (result.Status is MaintenanceActionStatus.Failed or MaintenanceActionStatus.Canceled ||
            !item.AffectsExplorer)
        {
            return (result, null, result.Detail);
        }

        try
        {
            await Task.Run(ExplorerRestarter.Restart);
            return (result with { Detail = AppendLine(result.Detail, "  - エクスプローラーを再起動しました") }, true, result.Detail);
        }
        catch (Exception ex)
        {
            MaintenanceItemLog.Failure(item.Item.Id, item.Item.Label, "エクスプローラーの再起動",
                $"{ex.GetType().Name}: {ex.Message}");
            return (MarkExplorerRestartFailed(result,
                "  - エクスプローラーの再起動に失敗しました (手動で再起動してください)"), false, result.Detail);
        }
    }

    internal static MaintenanceActionResult MarkExplorerRestartFailed(
        MaintenanceActionResult result,
        string restartFailureDetail)
    {
        if (result.Status is MaintenanceActionStatus.Failed or MaintenanceActionStatus.Canceled)
        {
            return result;
        }

        return MaintenanceActionResult.Partial(AppendLine(result.Detail, restartFailureDetail));
    }

    private static string ResultSummary(MaintenanceActionStatus status) => status switch
    {
        MaintenanceActionStatus.Success => App.Text("Result.Success", "✓ 完了しました"),
        MaintenanceActionStatus.Partial => App.Text("Result.Partial", "⚠ 一部完了しました"),
        MaintenanceActionStatus.Canceled => App.Text("Status.Cancelled", "キャンセルされました"),
        _ => App.Text("Result.Failure", "✗ 失敗しました"),
    };

    private void ShowResult(CommandItemViewModel item, MaintenanceActionResult result,
        Func<string> okStatus, Func<string> failStatus, Func<string>? prefix = null,
        bool? explorerRestartSucceeded = null, string? originalDetail = null)
    {
        item.SetResultText(() =>
        {
            var summary = ResultSummary(result.Status);
            var detail = RenderResultDetail(result, explorerRestartSucceeded, originalDetail);
            var text = string.IsNullOrWhiteSpace(detail)
                ? summary
                : $"{summary}\n{detail.Trim()}";
            return prefix is null ? text : $"{prefix()}\n{text}";
        });
        item.ApplyResultStatus(result.Status);
        SetStatus(() => result.Status switch
        {
            MaintenanceActionStatus.Success => okStatus(),
            MaintenanceActionStatus.Partial => $"{item.Label}: {ResultSummary(result.Status)}",
            MaintenanceActionStatus.Canceled => ResultSummary(result.Status),
            _ => failStatus(),
        });
    }

    internal static string RenderResultDetail(MaintenanceActionResult result, bool? explorerRestartSucceeded = null,
        string? originalDetail = null) => explorerRestartSucceeded switch
    {
        true => AppendLine(originalDetail ?? string.Empty,
            App.Text("Result.ExplorerRestarted", "  - エクスプローラーを再起動しました")),
        false => AppendLine(originalDetail ?? string.Empty,
            App.Text("Result.ExplorerRestartFailed", "  - エクスプローラーの再起動に失敗しました (手動で再起動してください)")),
        null => result.Detail,
    };

    private void ShowBusyResult(CommandItemViewModel item, Func<string>? prefix = null)
    {
        MaintenanceItemLog.LogRejected(item.Item,
            "別のメンテナンス操作が実行中、終了処理中、または共有ロックを取得できないため受け付けられませんでした。");
        static string Message() => App.Text("Status.Busy", "別のメンテナンス操作が実行中です。完了後にもう一度お試しください。");
        item.SetResultText(() => prefix is null ? Message() : $"{prefix()}\n{Message()}");
        item.ApplyResultStatus(MaintenanceActionStatus.Failed);
        SetStatus(Message);
    }

    private static string AppendLine(string detail, string line) =>
        string.IsNullOrWhiteSpace(detail) ? line : $"{detail.TrimEnd()}{Environment.NewLine}{line}";
}
