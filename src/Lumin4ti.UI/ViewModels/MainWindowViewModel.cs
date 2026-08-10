using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.UI.Services;

namespace Lumin4ti.UI.ViewModels;

public class MainWindowViewModel
{
    public CommandCategoryViewModel Update { get; }

    public CommandCategoryViewModel Cleanup { get; }

    public CommandCategoryViewModel Repair { get; }

    public CommandCategoryViewModel Performance { get; }

    public CommandCategoryViewModel System { get; }

    public CommandCategoryViewModel Organize { get; }

    public VersionViewModel Version { get; }

    private readonly MaintenanceOperationCoordinator _operationCoordinator;

    public MainWindowViewModel(
        MaintenanceActionCatalog catalog,
        VersionViewModel version,
        MaintenanceOperationCoordinator operationCoordinator)
    {
        _operationCoordinator = operationCoordinator;
        Update = new CommandCategoryViewModel(
            catalog,
            operationCoordinator,
            CommandCategory.Update,
            "更新・セキュリティ",
            "アプリと Windows Defender の更新、セキュリティ設定の健全化を行います。");
        Cleanup = new CommandCategoryViewModel(
            catalog,
            operationCoordinator,
            CommandCategory.Cleanup,
            "クリーンアップ",
            "ディスク領域の回収と、不要なファイル・登録情報の削除を行います。");
        Repair = new CommandCategoryViewModel(
            catalog,
            operationCoordinator,
            CommandCategory.Repair,
            "修復",
            "壊れた登録状態や表示の不具合を、再登録・再構築して直します。");
        Performance = new CommandCategoryViewModel(
            catalog,
            operationCoordinator,
            CommandCategory.Performance,
            "パフォーマンス",
            "メモリ・プロセス・描画まわりを最適化します。スイッチは基本 ON = 最適化を適用、OFF = Windows 既定に戻す、です" +
            "（MMAgent の各項目のみ ON = その機能を有効化 で、推奨値は各説明を参照してください）。");
        System = new CommandCategoryViewModel(
            catalog,
            operationCoordinator,
            CommandCategory.System,
            "システム設定",
            "電源・入力・時刻などの Windows 設定を調整します。スイッチは ON = 調整を適用、OFF = Windows 既定に戻す、です。");
        Organize = new CommandCategoryViewModel(
            catalog,
            operationCoordinator,
            CommandCategory.Organize,
            "整理・ソート",
            "ピン留めや環境変数などを整った並び順に揃えます。");

        Version = version;
        Version.Initialize();

        // トグルの現在状態はバックグラウンドで読み込む (DISM/Get-MMAgent 等で外部プロセスを起動しうる)。
        // Task.Run で包み、async の同期プレフィックス (Process.Start) が UI スレッド上で走って
        // 起動描画をブロックするのを防ぐ。
        _ = Task.Run(ReloadAllStatesAsync);
    }

    /// <summary>状態を読み直した直近時刻 (再アクティブのたびに外部プロセスを起こさないための間隔管理)。</summary>
    private long _lastStateLoadAt = Stopwatch.GetTimestamp();

    /// <summary>再読込を許す最短間隔。</summary>
    private static readonly TimeSpan StateReloadInterval = TimeSpan.FromMinutes(1);

    private Task ReloadAllStatesAsync() => Task.WhenAll(
        Update.LoadToggleStatesAsync(),
        Cleanup.LoadToggleStatesAsync(),
        Repair.LoadToggleStatesAsync(),
        Performance.LoadToggleStatesAsync(),
        System.LoadToggleStatesAsync(),
        Organize.LoadToggleStatesAsync());

    /// <summary>
    /// ウィンドウが再びアクティブになったときに状態を読み直す。設定アプリやポリシー更新など
    /// アプリ外での変更を取り込み、古い表示のまま操作させない。
    /// 実行中の操作があるとき、および前回から間隔が空いていないときは何もしない。
    /// </summary>
    public void RefreshStatesOnActivated()
    {
        if (_operationCoordinator.ActiveCount != 0 ||
            Stopwatch.GetElapsedTime(_lastStateLoadAt) < StateReloadInterval)
        {
            return;
        }

        _lastStateLoadAt = Stopwatch.GetTimestamp();
        _ = Task.Run(ReloadAllStatesAsync);
    }
}
