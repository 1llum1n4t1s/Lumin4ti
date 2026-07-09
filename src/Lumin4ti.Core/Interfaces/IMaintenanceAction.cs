using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Interfaces;

/// <summary>
/// 画面に並ぶメンテナンス項目の共通メタデータ。
/// 実行ボタン型は <see cref="IMaintenanceAction"/>、ON/OFF 切替型は <see cref="IMaintenanceToggle"/> を実装する。
/// </summary>
public interface IMaintenanceItem
{
    string Id { get; }

    /// <summary>ラベルの日本語マスター (ローカライズ辞書に訳が無い場合のフォールバック)。</summary>
    string Label { get; }

    /// <summary>設定の意味・効果・注意点を利用者向けに説明する文章 (日本語マスター)。</summary>
    string Description { get; }

    /// <summary>ラベルのローカライズキー ("Action.{Id}.Label")。UI が App.Text で解決する。</summary>
    string LabelKey => $"Action.{Id}.Label";

    /// <summary>説明のローカライズキー ("Action.{Id}.Desc")。</summary>
    string DescriptionKey => $"Action.{Id}.Desc";

    CommandCategory Category { get; }

    /// <summary>変更の反映に OS 再起動が必要か。</summary>
    bool RequiresReboot { get; }

    /// <summary>
    /// エクスプローラー (シェル) に影響する変更か。true の項目は
    /// 実行成功後にエクスプローラーの再起動をセットで行う。
    /// </summary>
    bool AffectsExplorer => false;

    /// <summary>実行に数分かかる可能性があるか (UI の注記表示用)。</summary>
    bool IsLongRunning => false;
}

/// <summary>
/// 「実行」ボタンで 1 回実行する型のメンテナンス項目。
/// レジストリ・COM・WinRT 等で C# ネイティブに実装するのを基本とし、
/// OS 提供ツールの実行が唯一の手段のもの (DISM / regsvr32 / winget 等) のみ
/// ICommandExecutor 経由で外部プロセスを使う。
/// </summary>
public interface IMaintenanceAction : IMaintenanceItem
{
    Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default);

    /// <summary>
    /// 進捗行の通知付きで実行する。長時間コマンド (winget / DISM / defrag 等) が
    /// 実行中のライブ出力を UI に流すためにオーバーライドする。既定は通知なしで通常実行。
    /// </summary>
    Task<MaintenanceActionResult> ExecuteAsync(IProgress<string>? progress, CancellationToken ct = default) =>
        ExecuteAsync(ct);
}

/// <summary>
/// ON/OFF を切り替えられる型のメンテナンス項目。ON = 最適化 (tweak) が適用された状態、
/// OFF = Windows 既定の状態、で統一する。
/// </summary>
public interface IMaintenanceToggle : IMaintenanceItem
{
    /// <summary>現在の適用状態を読み取る。null = 状態を判定できない (非対応環境等)。</summary>
    Task<bool?> GetStateAsync(CancellationToken ct = default);

    /// <summary>ON (適用) / OFF (既定に戻す) を設定する。</summary>
    Task<MaintenanceActionResult> SetStateAsync(bool on, CancellationToken ct = default);
}
