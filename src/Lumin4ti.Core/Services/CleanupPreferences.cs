using Lumin4ti.Core.Interfaces;

namespace Lumin4ti.Core.Services;

/// <summary>
/// <see cref="ICleanupPreferences"/> を settings.json 上に持つ実装。
/// 画面 (手動実行) とサインイン時の定期実行の両方がこの 1 か所を読むため、
/// 「チェックを外した対象は、どちらの経路でも消えない」が成立する。
/// </summary>
public sealed class CleanupPreferences(ISettingsService settings) : ICleanupPreferences
{
    /// <summary>
    /// 未設定のときにサインイン時のクリーンアップで実行する項目。
    /// 管理者権限やサービス停止を必要としない、ユーザー権限で完走できるものだけにする
    /// (タスクは非昇格で動くため、システム側の掃除は既定では選ばない)。
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultScheduledGroupIds =
    [
        "cleanup-user-temp",
        "cleanup-nul-files",
        "cleanup-package-cache",
        "cleanup-browser-cache",
    ];

    /// <summary>
    /// 設定サービスと共有するロック。自前のロックにすると、保存 (シリアライズ) が
    /// 別スレッドで走っている最中に除外リストを書き換えられてしまう。
    /// </summary>
    private readonly object _lock = settings.SyncRoot;

    public bool IsTargetEnabled(string itemId, string rawPath)
    {
        lock (_lock)
        {
            return !(settings.Current.CleanupExclusions.TryGetValue(itemId, out var excluded) &&
                     excluded.Contains(rawPath, StringComparer.OrdinalIgnoreCase));
        }
    }

    public void SetTargetEnabled(string itemId, string rawPath, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawPath);

        lock (_lock)
        {
            var exclusions = settings.Current.CleanupExclusions;
            if (enabled)
            {
                if (!exclusions.TryGetValue(itemId, out var excluded))
                {
                    return;
                }

                excluded.RemoveAll(p => string.Equals(p, rawPath, StringComparison.OrdinalIgnoreCase));
                if (excluded.Count == 0)
                {
                    // 空リストを残すと設定ファイルが項目 Id で埋まっていくため、その場で畳む。
                    exclusions.Remove(itemId);
                }

                return;
            }

            if (!exclusions.TryGetValue(itemId, out var list))
            {
                list = [];
                exclusions[itemId] = list;
            }

            if (!list.Contains(rawPath, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(rawPath);
            }
        }
    }

    public IReadOnlyList<string> ScheduledGroupIds
    {
        get
        {
            lock (_lock)
            {
                return settings.Current.ScheduledCleanupGroupIds is { } ids
                    ? [.. ids]
                    : DefaultScheduledGroupIds;
            }
        }
    }

    public void SetScheduledGroupEnabled(string groupId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        lock (_lock)
        {
            // 既定セットからの初回変更時に、その時点の実効値を書き出してから差分を適用する
            // (既定が将来変わっても、利用者が選んだ内容が勝手に増減しないようにする)。
            var ids = settings.Current.ScheduledCleanupGroupIds ??= [.. DefaultScheduledGroupIds];
            var index = ids.FindIndex(id => string.Equals(id, groupId, StringComparison.OrdinalIgnoreCase));
            if (enabled && index < 0)
            {
                ids.Add(groupId);
            }
            else if (!enabled && index >= 0)
            {
                ids.RemoveAt(index);
            }
        }
    }

    public Task SaveAsync(CancellationToken ct = default) => settings.SaveAsync(ct);
}
