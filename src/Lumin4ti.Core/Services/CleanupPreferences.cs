using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services;

/// <summary>
/// <see cref="ICleanupPreferences"/> を settings.json 上に持つ実装。
/// 画面 (手動実行) と起動時の再試行が同じ除外設定を読むため、
/// 「チェックを外した対象は、どちらの経路でも消えない」が成立する。
/// </summary>
public sealed class CleanupPreferences(ISettingsService settings) : ICleanupPreferences
{
    /// <summary>
    /// 設定サービスと共有するロック。自前のロックにすると、保存 (シリアライズ) が
    /// 別スレッドで走っている最中に除外リストを書き換えられてしまう。
    /// </summary>
    private readonly object _lock = settings.SyncRoot;

    public bool IsTargetEnabled(string itemId, string rawPath)
    {
        lock (_lock)
        {
            var exclusions = settings.Current.CleanupExclusions;
            return exclusions is null ||
                   !exclusions.TryGetValue(itemId, out var excluded) ||
                   excluded is null ||
                   !excluded.Contains(rawPath, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void SetTargetEnabled(string itemId, string rawPath, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawPath);

        lock (_lock)
        {
            var exclusions = settings.Current.CleanupExclusions ??= [];
            if (enabled)
            {
                if (!exclusions.TryGetValue(itemId, out var excluded) || excluded is null)
                {
                    exclusions.Remove(itemId);
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

            if (!exclusions.TryGetValue(itemId, out var list) || list is null)
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

    public Task SaveAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (settings.LoadStatus == SettingsLoadStatus.Failed)
        {
            throw new InvalidOperationException("設定ファイルを読み込めないため、原本を保護して選択を保存していません。変更はこの起動中だけ有効です。");
        }
        return settings.SaveAsync(ct);
    }
}
