using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Core.Services.Windows;

/// <summary>
/// タスクスケジューラーから起動されたときに、画面と同じクリーンアップ項目を実行して即終了する
/// 専用エントリポイント。<see cref="Actions.ScheduledTempCleanupToggle"/> が登録するタスクは、
/// この引数を付けて自分自身 (Lumin4ti.exe) を呼び出す。通常の起動フロー (自己昇格・多重起動ガード・UI) を
/// 経由しないため、サインインのたびに UAC を出さず無人で完走できる。
/// 実行する項目と、項目ごとに消す対象は settings.json の利用者設定 (<see cref="ICleanupPreferences"/>) が唯一の正本で、
/// 画面のボタンで走る処理とまったく同じコードを通る。
/// 対象は再生成可能なキャッシュ・ログ・一時領域だけに限定し、使用中のファイルはスキップする。
/// </summary>
[SupportedOSPlatform("windows")]
public static class ScheduledTempCleanup
{
    /// <summary>
    /// この引数を検知したら、呼び出し元 (Program.Main) は通常の起動フローに入らず
    /// <see cref="Run"/> だけ行って終了する。
    /// </summary>
    public const string CommandLineArgument = "--scheduled-cleanup-temp";

    /// <summary>選択されたクリーンアップ項目を順に実行する。例外は握りつぶして終了コードへ畳み込む。</summary>
    public static int Run()
    {
        try
        {
            return Run(new SettingsService(), new ProcessCommandExecutor());
        }
        catch (Exception ex)
        {
            LoggerBootstrap.Log.Error("scheduled-cleanup: 失敗しました", ex);
            return 1;
        }
    }

    /// <summary>
    /// 設定を正常に読めた場合だけ無人削除を行う。設定が無い、または読めない状態では
    /// 利用者が選んだ除外対象を再現できないため、既定値で推測して削除しない。
    /// </summary>
    internal static int Run(ISettingsService settingsService, ICommandExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(executor);

        if (settingsService.LoadStatus != SettingsLoadStatus.Loaded)
        {
            LoggerBootstrap.Log.Error(
                $"scheduled-cleanup: 設定を正常に読み込めないため削除を中止しました ({settingsService.LoadStatus})");
            return 1;
        }

        var preferences = new CleanupPreferences(settingsService);
        var actions = SelectActions(
            FileCleanupGroups.CreateCleanupActions(executor, preferences),
            preferences.ScheduledGroupIds);

        return RunAsync(actions).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 実行する項目を選ぶ。カタログの並び順を保ったまま絞り込み、設定に残っている
    /// 未知の Id (削除された項目・手書きのミス) は無視する。
    /// </summary>
    internal static IReadOnlyList<IMaintenanceAction> SelectActions(
        IEnumerable<IMaintenanceAction> actions,
        IReadOnlyList<string> selectedIds)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(selectedIds);

        var selected = new HashSet<string>(selectedIds, StringComparer.OrdinalIgnoreCase);
        return [.. actions.Where(a => selected.Contains(a.Id))];
    }

    /// <summary>
    /// 順に実行する。1 件の失敗で残りを止めると、後ろの項目が永久に実行されないため、
    /// 失敗しても続行し、最後に 1 件でも失敗があれば異常終了コードを返す。
    /// </summary>
    private static async Task<int> RunAsync(IReadOnlyList<IMaintenanceAction> actions)
    {
        if (actions.Count == 0)
        {
            LoggerBootstrap.Log.Info("scheduled-cleanup: 実行する項目が選ばれていません");
            return 0;
        }

        var failed = false;
        foreach (var action in actions)
        {
            try
            {
                var result = await action.ExecuteAsync(CancellationToken.None);
                LoggerBootstrap.Log.Info($"scheduled-cleanup: {action.Id} = {result.Status}");
                failed |= result.Status == MaintenanceActionStatus.Failed;
            }
            catch (Exception ex)
            {
                LoggerBootstrap.Log.Error($"scheduled-cleanup: {action.Id} で例外が発生しました", ex);
                failed = true;
            }
        }

        return failed ? 1 : 0;
    }
}
