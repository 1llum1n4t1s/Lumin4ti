using System.Threading.Tasks;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows;

namespace Lumin4ti.UI.ViewModels;

public class MainWindowViewModel
{
    public CommandCategoryViewModel Update { get; }

    public CommandCategoryViewModel Cleanup { get; }

    public CommandCategoryViewModel Performance { get; }

    public CommandCategoryViewModel System { get; }

    public CommandCategoryViewModel Organize { get; }

    public VersionViewModel Version { get; }

    public MainWindowViewModel(MaintenanceActionCatalog catalog, VersionViewModel version)
    {
        Update = new CommandCategoryViewModel(
            catalog,
            CommandCategory.Update,
            "更新・セキュリティ",
            "アプリと Windows Defender の更新、セキュリティ設定の健全化を行います。");
        Cleanup = new CommandCategoryViewModel(
            catalog,
            CommandCategory.Cleanup,
            "クリーンアップ・修復",
            "ディスク領域の回収と、壊れた登録情報の掃除・修復を行います。");
        Performance = new CommandCategoryViewModel(
            catalog,
            CommandCategory.Performance,
            "パフォーマンス",
            "メモリ・プロセス・描画まわりを最適化します。スイッチは基本 ON = 最適化を適用、OFF = Windows 既定に戻す、です" +
            "（MMAgent の各項目のみ ON = その機能を有効化 で、推奨値は各説明を参照してください）。");
        System = new CommandCategoryViewModel(
            catalog,
            CommandCategory.System,
            "システム設定",
            "電源・入力・時刻などの Windows 設定を調整します。スイッチは ON = 調整を適用、OFF = Windows 既定に戻す、です。");
        Organize = new CommandCategoryViewModel(
            catalog,
            CommandCategory.Organize,
            "整理・ソート",
            "ピン留めや環境変数などを整った並び順に揃えます。");

        Version = version;
        Version.Initialize();

        // トグルの現在状態はバックグラウンドで読み込む (DISM/Get-MMAgent 等で外部プロセスを起動しうる)。
        // Task.Run で包み、async の同期プレフィックス (Process.Start) が UI スレッド上で走って
        // 起動描画をブロックするのを防ぐ。
        _ = Task.Run(() => Task.WhenAll(
            Update.LoadToggleStatesAsync(),
            Cleanup.LoadToggleStatesAsync(),
            Performance.LoadToggleStatesAsync(),
            System.LoadToggleStatesAsync(),
            Organize.LoadToggleStatesAsync()));
    }
}
