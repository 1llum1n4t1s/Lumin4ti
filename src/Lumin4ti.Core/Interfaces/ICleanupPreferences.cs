namespace Lumin4ti.Core.Interfaces;

/// <summary>
/// ファイル削除まわりの利用者設定。手動実行 (画面のボタン) とサインイン時の定期実行が
/// 同じ設定を読むことで、「画面で外した対象は定期実行でも消えない」を保証する。
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

    /// <summary>サインイン時に実行するクリーンアップ項目の Id (未設定なら既定セット)。</summary>
    IReadOnlyList<string> ScheduledGroupIds { get; }

    /// <summary>サインイン時に実行する項目を足す・外す。</summary>
    void SetScheduledGroupEnabled(string groupId, bool enabled);

    /// <summary>変更を設定ファイルへ保存する。</summary>
    Task SaveAsync(CancellationToken ct = default);
}
