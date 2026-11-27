namespace Lumin4ti.Core.Interfaces;

/// <summary>
/// ファイル削除まわりの利用者設定。手動実行 (画面のボタン) と起動時の再試行が
/// 同じ設定を読むことで、「画面で外した対象は再試行でも消えない」を保証する。
/// </summary>
public interface ICleanupPreferences
{
    /// <summary>
    /// 削除対象が有効か。未保存の対象は既定で有効 (除外だけを保存する) にして、
    /// 新しく足した対象が黙って無効化されないようにする。
    /// </summary>
    bool IsTargetEnabled(string itemId, string rawPath);

    /// <summary>削除対象の有効・無効を切り替える。</summary>
    void SetTargetEnabled(string itemId, string rawPath, bool enabled);

    /// <summary>変更を設定ファイルへ保存する。</summary>
    Task SaveAsync(CancellationToken ct = default);
}
