using System.Runtime.Versioning;
using System.Security;

namespace Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;

internal sealed record RegistryRestoreItemResult(RegistryBackupItem Item, RegistryItemState State, string? Reason);

/// <summary>
/// 復元の結果。<see cref="Items"/> が空で <see cref="Overall"/> が Failed のときは、検証で拒否した (何も書いていない)。
/// <see cref="ScanInvalidated"/> が真なら、走査結果は失効させた (画面は再走査を促す)。
/// </summary>
internal sealed record RegistryRestoreResult(
    RegistryOverallState Overall,
    IReadOnlyList<RegistryRestoreItemResult> Items,
    string? Message,
    bool ScanInvalidated);

/// <summary>
/// バックアップ 1 件の復元。版・SID・許可パス・上限を <see cref="RegistryBackupValidator"/> で検証し、HKCU の SID が現在の昇格利用者と
/// 違えば拒否する。対象が無ければ書き戻して Restored、同値なら AlreadyPresent、別の値なら上書きせず Conflict (全体 Partial)。
/// lease は呼び出し側が取る。
/// </summary>
internal sealed class RegistryRestoreService(
    IRegistryCleanerRootFactory factory,
    IRegistryBackupStore store,
    IRegistryShellNotifier notifier,
    RegistryScanGeneration generation,
    Func<string?> currentUserSid)
{
    private const string MruListName = "MRUList";

    [SupportedOSPlatform("windows")]
    public static RegistryRestoreService CreateDefault(RegistryScanGeneration generation) => new(
        new NativeRegistryCleanerRootFactory(),
        RegistryBackupStore.CreateDefault(),
        new NativeRegistryShellNotifier(),
        generation,
        CurrentSid.Get);

    /// <summary>バックアップ ID の一覧 (時刻順)。1000 件を超えたら例外 (fail closed)。</summary>
    public IReadOnlyList<string> ListBackups() => store.List();

    /// <summary>バックアップとその結果ファイルを消す。</summary>
    public void DeleteBackup(string id) => store.Delete(id);

    /// <summary>削除時の結果ファイルを読む。無ければ null。</summary>
    public RegistryBackupResultDocument? ReadResult(string id) => store.ReadResult(id);

    /// <summary>バックアップを復元する。</summary>
    public RegistryRestoreResult Restore(string id, CancellationToken ct = default)
    {
        var document = Load(id, out var rejection);
        if (document is null)
        {
            return Rejected(rejection!);
        }

        // 利用者の Classes (<SID>_Classes) も、_Classes を外した利用者の SID (文書の UserSid) で照合する。
        if (document.Items.Any(item => RegistryUserClassesHive.IsUserHive(item.Hive)) &&
            !string.Equals(currentUserSid(), document.UserSid, StringComparison.OrdinalIgnoreCase))
        {
            return Rejected(CleanupMessage.Of(RegistryCleanReasons.RestoreSidMismatch));
        }

        // 書き込みを始める前に走査結果を失効させる (戻した内容は、走査結果と食い違う)。
        generation.Invalidate();
        using var cache = new RegistryHandleCache(factory, document.UserSid);
        var results = new List<RegistryRestoreItemResult>();
        var canceled = false;

        // OpenWithList の MRUList は、同じキーのスロット値を戻した後に合成する。
        foreach (var item in document.Items.OrderBy(item => IsMruList(item) ? 1 : 0))
        {
            if (ct.IsCancellationRequested)
            {
                canceled = true;
                break;
            }

            results.Add(IsMruList(item) ? RestoreMruList(cache, item, results) : RestoreItem(cache, item));
        }

        if (results.Any(r => r.State == RegistryItemState.Restored && r.Item.Category == RegistryCleanCategory.FileAssociations))
        {
            notifier.AssociationsChanged();
        }

        return new(canceled ? RegistryOverallState.Canceled : Summarize(results), results, null, true);
    }

    private RegistryBackupDocument? Load(string id, out string? rejection)
    {
        rejection = null;
        try
        {
            var document = store.Load(id);
            rejection = document is null ? CleanupMessage.Of(RegistryCleanReasons.RestoreNotFound) : null;
            return document;
        }
        catch (RegistryBackupRejectedException ex)
        {
            rejection = CleanupMessage.Of(RegistryCleanReasons.RestoreRejected, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            rejection = CleanupMessage.Of(RegistryCleanReasons.RestoreLoadFailed, ex.Message);
        }

        return null;
    }

    private static RegistryRestoreResult Rejected(string message) => new(RegistryOverallState.Failed, [], message, false);

    private static RegistryOverallState Summarize(List<RegistryRestoreItemResult> results)
    {
        if (results.All(r => r.State is RegistryItemState.Restored or RegistryItemState.AlreadyPresent))
        {
            return RegistryOverallState.Success;
        }

        return results.All(r => r.State == RegistryItemState.Failed) ? RegistryOverallState.Failed : RegistryOverallState.Partial;
    }

    private static RegistryRestoreItemResult RestoreItem(RegistryHandleCache cache, RegistryBackupItem item)
    {
        try
        {
            var key = cache.OpenOrCreate(item.Hive, item.View, item.KeyPath);
            var tally = new Tally();
            if (item.ValueName is { } name)
            {
                tally.Add(ApplyValue(key, name, item.ValueType!.Value, Convert.FromBase64String(item.DataBase64!)));
            }
            else
            {
                RestoreNode(key, item.Subtree!, item.View, tally);
            }

            return tally.ToResult(item);
        }
        catch (Exception ex) when (RegistryCleanFailures.IsExpected(ex))
        {
            var (state, reason) = RegistryCleanFailures.Classify(ex, RegistryCleanReasons.RestoreFailed);
            return new(item, state, reason);
        }
    }

    /// <summary>OpenWithList の MRUList (削除時に、消したスロットの文字を外して書き換えた値) か。</summary>
    private static bool IsMruList(RegistryBackupItem item) =>
        item.Category == RegistryCleanCategory.FileAssociations &&
        item.ValueType == RegistryTextValue.RegSz &&
        string.Equals(item.ValueName, MruListName, StringComparison.OrdinalIgnoreCase) &&
        item.KeyPath.EndsWith(@"\OpenWithList", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// MRUList の合成。同じキーで戻した (または既に同値だった) スロットの文字のうち、現在の MRUList に無いものを
    /// バックアップの並び順で末尾へ足す。既存の文字は並べ替えも削除もしない。スロットが衝突した文字は足さない
    /// (そのスロットの項目が Conflict になる)。MRUList が文字列でなければ上書きせず Conflict。
    /// </summary>
    private static RegistryRestoreItemResult RestoreMruList(
        RegistryHandleCache cache, RegistryBackupItem item, List<RegistryRestoreItemResult> done)
    {
        try
        {
            var backup = RegistryTextValue.Decode(Convert.FromBase64String(item.DataBase64!));
            var slots = done
                .Where(r => r.State is RegistryItemState.Restored or RegistryItemState.AlreadyPresent && IsSlotOf(r.Item, item))
                .Select(r => r.Item.ValueName![0])
                .ToHashSet();
            var key = cache.OpenOrCreate(item.Hive, item.View, item.KeyPath);
            var current = key.GetValue(item.ValueName!);
            if (current is not null && current.Type != RegistryTextValue.RegSz)
            {
                return new(item, RegistryItemState.Conflict, CleanupMessage.Of(RegistryCleanReasons.RestoreConflict));
            }

            var text = current is null ? string.Empty : RegistryTextValue.Decode(current.Data);
            var ordered = backup.Where(slots.Contains).Concat(slots.Order().Where(c => !backup.Contains(c)));
            var added = new string([.. ordered.Distinct().Where(c => !text.Contains(c))]);
            if (added.Length == 0)
            {
                return new(item, RegistryItemState.AlreadyPresent, null);
            }

            var wanted = new RegistryRawValue(RegistryTextValue.RegSz, RegistryTextValue.Encode(text + added));
            key.SetValue(item.ValueName!, wanted);
            return key.GetValue(item.ValueName!) is { } written && Same(written, wanted)
                ? new(item, RegistryItemState.Restored, null)
                : new(item, RegistryItemState.Failed, CleanupMessage.Of(RegistryCleanReasons.RestoreVerifyFailed));
        }
        catch (Exception ex) when (RegistryCleanFailures.IsExpected(ex))
        {
            var (state, reason) = RegistryCleanFailures.Classify(ex, RegistryCleanReasons.RestoreFailed);
            return new(item, state, reason);
        }
    }

    /// <summary><paramref name="slot"/> が <paramref name="mruList"/> と同じキーのスロット値 (1 文字の名前) か。</summary>
    private static bool IsSlotOf(RegistryBackupItem slot, RegistryBackupItem mruList) =>
        slot.ValueName is { Length: 1 } && slot.Hive == mruList.Hive && slot.View == mruList.View &&
        slot.KeyPath.Equals(mruList.KeyPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>キーの値と子を再帰的に戻す。既にある値は上書きせず、同値か衝突かを数える。</summary>
    private static void RestoreNode(IRegistryCleanerKey key, RegistryBackupKeyNode node, RegistryCleanView view, Tally tally)
    {
        foreach (var value in node.Values)
        {
            tally.Add(ApplyValue(key, value.Name, value.Type, Convert.FromBase64String(value.DataBase64)));
        }

        foreach (var subKey in node.SubKeys)
        {
            using var child = key.OpenChild(subKey.Name, RegistryCleanAccess.DeleteTree, view) ?? key.CreateChild(subKey.Name, view);
            RestoreNode(child, subKey.Node, view, tally);
        }
    }

    private static RegistryItemState ApplyValue(IRegistryCleanerKey key, string name, uint type, byte[] data)
    {
        var wanted = new RegistryRawValue(type, data);
        if (key.GetValue(name) is { } existing)
        {
            return Same(existing, wanted) ? RegistryItemState.AlreadyPresent : RegistryItemState.Conflict;
        }

        key.SetValue(name, wanted);
        return key.GetValue(name) is { } written && Same(written, wanted) ? RegistryItemState.Restored : RegistryItemState.Failed;
    }

    private static bool Same(RegistryRawValue a, RegistryRawValue b) => a.Type == b.Type && a.Data.AsSpan().SequenceEqual(b.Data);

    /// <summary>項目 1 件分の値ごとの結果の集計。</summary>
    private sealed class Tally
    {
        private int _restored;
        private int _already;
        private int _conflict;
        private int _failed;

        public void Add(RegistryItemState state)
        {
            switch (state)
            {
                case RegistryItemState.Restored:
                    _restored++;
                    break;
                case RegistryItemState.AlreadyPresent:
                    _already++;
                    break;
                case RegistryItemState.Conflict:
                    _conflict++;
                    break;
                default:
                    _failed++;
                    break;
            }
        }

        public RegistryRestoreItemResult ToResult(RegistryBackupItem item)
        {
            if (_failed > 0)
            {
                return new(item, RegistryItemState.Failed, CleanupMessage.Of(RegistryCleanReasons.RestoreVerifyFailed));
            }

            if (_conflict > 0)
            {
                return new(item, RegistryItemState.Conflict, CleanupMessage.Of(RegistryCleanReasons.RestoreConflict));
            }

            return new(item, _restored > 0 ? RegistryItemState.Restored : RegistryItemState.AlreadyPresent, null);
        }
    }
}
