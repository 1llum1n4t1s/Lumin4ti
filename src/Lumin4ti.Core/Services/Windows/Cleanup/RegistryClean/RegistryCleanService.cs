using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using System.Text;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Services.Windows.Cleanup.Slim;

namespace Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;

/// <summary>削除・復元の理由符号の名前 (<see cref="CleanupMessage"/>)。日本語のマスターは <see cref="CleanupMessages"/>、辞書への追加は UI 担当が行う。</summary>
internal static class RegistryCleanReasons
{
    public const string Stale = "RegistryCleanStale";
    public const string NothingSelected = "RegistryCleanNothingSelected";
    public const string RestorePointUnavailable = "RegistryCleanRestorePointUnavailable";
    public const string CanceledNotRun = "RegistryCleanCanceledNotRun";
    public const string NotInScan = "RegistryCleanNotInScan";
    public const string PathNotAllowed = "RegistryCleanPathNotAllowed";
    public const string Gone = "RegistryCleanGone";
    public const string Changed = "RegistryCleanChanged";
    public const string AccessDenied = "RegistryCleanAccessDenied";
    public const string TrustedInstaller = "RegistryCleanTrustedInstaller";
    public const string ItemFailed = "RegistryCleanItemFailed";
    public const string BackupItemFailed = "RegistryCleanBackupItemFailed";
    public const string BackupFailed = "RegistryCleanBackupFailed";
    public const string CoveredByOther = "RegistryCleanCoveredByOther";
    public const string CompanionFailed = "RegistryCleanCompanionFailed";
    public const string ResultNotWritten = "RegistryCleanResultNotWritten";

    public const string RestoreNotFound = "RegistryRestoreNotFound";
    public const string RestoreLoadFailed = "RegistryRestoreLoadFailed";
    public const string RestoreRejected = "RegistryRestoreRejected";
    public const string RestoreSidMismatch = "RegistryRestoreSidMismatch";
    public const string RestoreConflict = "RegistryRestoreConflict";
    public const string RestoreFailed = "RegistryRestoreFailed";
    public const string RestoreVerifyFailed = "RegistryRestoreVerifyFailed";
}

/// <summary>関連付けの変更をシェルへ知らせる (テストでは偽物に差し替える)。</summary>
internal interface IRegistryShellNotifier
{
    /// <summary>SHChangeNotify(SHCNE_ASSOCCHANGED) を呼ぶ。</summary>
    void AssociationsChanged();
}

[SupportedOSPlatform("windows")]
internal sealed class NativeRegistryShellNotifier : IRegistryShellNotifier
{
    private const int ShcneAssocChanged = 0x08000000;

    public void AssociationsChanged() => SHChangeNotify(ShcneAssocChanged, 0, 0, 0);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}

/// <summary>復元ポイントの境界 (本番は <see cref="RestorePointService"/>)。</summary>
internal interface IRegistryCleanRestorePoints
{
    Task<RestorePointResult> CreateAsync(string description, CancellationToken ct);

    Task<RestorePointProtectionResult> EnableProtectionAsync(CancellationToken ct);
}

internal sealed class RestorePointServiceAdapter(RestorePointService service) : IRegistryCleanRestorePoints
{
    public Task<RestorePointResult> CreateAsync(string description, CancellationToken ct) => service.CreateAsync(description, ct);

    public Task<RestorePointProtectionResult> EnableProtectionAsync(CancellationToken ct) => service.EnableProtectionAsync(ct);
}

/// <summary>
/// 走査結果の世代。走査結果を <see cref="Register"/> し、削除は <see cref="TryConsume"/> (照合と使用済みの印を不可分に行う) で
/// 一度だけ通す。復元・別の走査・取消は <see cref="Invalidate"/> で失効させる。削除サービスと復元サービスが共有する。
/// </summary>
internal sealed class RegistryScanGeneration
{
    private readonly object _gate = new();
    private string? _current;

    public void Register(string generationId)
    {
        lock (_gate)
        {
            _current = generationId;
        }
    }

    /// <summary>世代が現在のものなら失効させて true。古い・使用済み (並行実行を含む) なら false。</summary>
    public bool TryConsume(string generationId)
    {
        lock (_gate)
        {
            if (!string.Equals(_current, generationId, StringComparison.Ordinal))
            {
                return false;
            }

            _current = null;
            return true;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _current = null;
        }
    }
}

/// <summary>削除の要求。<paramref name="Selected"/> は <paramref name="Scan"/> の候補の部分集合。</summary>
/// <param name="CreateRestorePoint">削除の前に復元ポイントを作る (既定オンは画面側が持つ)。</param>
/// <param name="OnRestorePointUnavailable">復元ポイントを使えないときの判断 (null なら中止する)。</param>
internal sealed record RegistryCleanRequest(
    RegistryScanResult Scan,
    IReadOnlyCollection<RegistryScanItem> Selected,
    bool CreateRestorePoint,
    Func<RestorePointResult, CancellationToken, Task<RestorePointDecision>>? OnRestorePointUnavailable = null);

internal sealed record RegistryCleanItemResult(RegistryScanItem Item, RegistryItemState State, string? Reason);

/// <summary>削除の結果。<see cref="Stale"/> のときは何も実行していない。走査結果は常に使用済み (再走査が要る)。</summary>
/// <param name="BackupId">保存したバックアップの ID (保存していなければ null)。</param>
/// <param name="Message">全体の理由 (符号)。</param>
/// <param name="ResultFileWritten">結果ファイルを書けた (バックアップが無ければ false)。</param>
internal sealed record RegistryCleanRunResult(
    RegistryOverallState Overall,
    IReadOnlyList<RegistryCleanItemResult> Items,
    string? BackupId,
    RestorePointResult? RestorePoint,
    string? Message,
    bool Stale,
    bool ResultFileWritten);

/// <summary>
/// レジストリクリーナーの削除。実行順 (docs/agents/registry-cleaner-guards.md): 世代照合 → 復元ポイント →
/// ハンドルを開いたまま再照合 → バックアップ (書けなければ 1 件も消さない) → 同じハンドルで直前に再照合して削除 → 結果ファイル。
/// lease は呼び出し側が取る。
/// </summary>
internal sealed class RegistryCleanService(
    IRegistryCleanerRootFactory factory,
    IRegistryBackupStore store,
    IRegistryCleanRestorePoints restorePoints,
    IRegistryShellNotifier notifier,
    RegistryScanGeneration generation,
    Func<string?> currentUserSid)
{
    private const string RestorePointDescription = "Lumin4ti レジストリクリーナーの前";

    [SupportedOSPlatform("windows")]
    public static RegistryCleanService CreateDefault(ICommandExecutor executor, RegistryScanGeneration generation) => new(
        new NativeRegistryCleanerRootFactory(),
        RegistryBackupStore.CreateDefault(),
        new RestorePointServiceAdapter(new RestorePointService(executor)),
        new NativeRegistryShellNotifier(),
        generation,
        CurrentSid.Get);

    /// <summary>走査結果を現在の世代として登録する (走査のたびに呼ぶ)。</summary>
    public void Register(RegistryScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        generation.Register(scan.GenerationId);
    }

    /// <summary>走査結果を失効させる。</summary>
    public void Invalidate() => generation.Invalidate();

    /// <summary>
    /// 選択された候補を削除する。古い世代・並行実行は <see cref="RegistryCleanRunResult.Stale"/> で拒否する。
    /// 復元ポイントを使えず続行も選ばれなければ何も削除せず Failed (取消は Canceled)。
    /// </summary>
    public async Task<RegistryCleanRunResult> ExecuteAsync(RegistryCleanRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!generation.TryConsume(request.Scan.GenerationId))
        {
            return new(RegistryOverallState.Failed, [], null, null, CleanupMessage.Of(RegistryCleanReasons.Stale), true, false);
        }

        if (request.Selected.Count == 0)
        {
            return new(RegistryOverallState.Success, [], null, null, CleanupMessage.Of(RegistryCleanReasons.NothingSelected), false, false);
        }

        RestorePointResult? point = null;
        try
        {
            if (request.CreateRestorePoint)
            {
                var ensured = await EnsureRestorePointAsync(request, ct).ConfigureAwait(false);
                point = ensured.Result;
                if (!ensured.Proceed)
                {
                    var canceled = point.Outcome == RestorePointOutcome.Cancelled;
                    return new(
                        canceled ? RegistryOverallState.Canceled : RegistryOverallState.Failed, [], null, point,
                        canceled ? null : CleanupMessage.Of(RegistryCleanReasons.RestorePointUnavailable, point.Message), false, false);
                }
            }

            ct.ThrowIfCancellationRequested();
            using var run = new RegistryCleanRun(factory, store, notifier, request, currentUserSid, ct);
            return run.Execute() with { RestorePoint = point };
        }
        catch (OperationCanceledException)
        {
            return new(RegistryOverallState.Canceled, [], null, point, null, false, false);
        }
    }

    private async Task<(RestorePointResult Result, bool Proceed)> EnsureRestorePointAsync(RegistryCleanRequest request, CancellationToken ct)
    {
        var point = await restorePoints.CreateAsync(RestorePointDescription, ct).ConfigureAwait(false);
        var retried = false;
        while (true)
        {
            if (point.IsUsable)
            {
                return (point, true);
            }

            if (point.Outcome == RestorePointOutcome.Cancelled || request.OnRestorePointUnavailable is null)
            {
                return (point, false);
            }

            var decision = await request.OnRestorePointUnavailable(point, ct).ConfigureAwait(false);
            if (decision == RestorePointDecision.ContinueWithout)
            {
                return (point, true);
            }

            if (decision != RestorePointDecision.EnableProtectionAndRetry || retried || point.Outcome != RestorePointOutcome.ProtectionDisabled)
            {
                return (point, false);
            }

            retried = true;
            var enabled = await restorePoints.EnableProtectionAsync(ct).ConfigureAwait(false);
            point = enabled.Enabled
                ? await restorePoints.CreateAsync(RestorePointDescription, ct).ConfigureAwait(false)
                : new RestorePointResult(RestorePointOutcome.Failed, enabled.Message, null);
        }
    }
}

/// <summary>昇格利用者の SID。</summary>
[SupportedOSPlatform("windows")]
internal static class CurrentSid
{
    public static string? Get()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value;
    }
}

/// <summary>キーの処理で起きうる想定内の失敗の分類 (削除・復元で共有)。</summary>
internal static class RegistryCleanFailures
{
    public static bool IsExpected(Exception ex) =>
        ex is IOException or Win32Exception or UnauthorizedAccessException or SecurityException or RegistryHiveUnavailableException;

    /// <summary>アクセス拒否・リンク・利用者ハイブなし → Skipped、それ以外 → Failed。理由は符号。</summary>
    public static (RegistryItemState State, string Reason) Classify(Exception ex, string failedReason) => ex switch
    {
        RegistryCleanLinkException => (RegistryItemState.Skipped, ex.Message),
        RegistryHiveUnavailableException => (RegistryItemState.Skipped, CleanupMessage.Of("UserRegistryOpenFailed")),
        UnauthorizedAccessException or SecurityException or Win32Exception { NativeErrorCode: 5 } =>
            (RegistryItemState.Skipped, CleanupMessage.Of(RegistryCleanReasons.AccessDenied)),
        _ => (RegistryItemState.Failed, CleanupMessage.Of(failedReason, ex.Message)),
    };
}

/// <summary>利用者のハイブを開けない (対象の利用者が不明)。</summary>
internal sealed class RegistryHiveUnavailableException() : IOException("user hive unavailable");

/// <summary>開いたキーのハンドルを経路ごとに使い回す。ルートと開いたキーはこの入れ物が所有する (呼び出し側は閉じない)。</summary>
internal sealed class RegistryHandleCache(IRegistryCleanerRootFactory factory, string? userSid) : IDisposable
{
    private readonly Dictionary<(RegistryCleanHive, RegistryCleanView), IRegistryCleanerKey> _roots = [];
    private readonly Dictionary<string, IRegistryCleanerKey?> _keys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>パスのキーを開く。無ければ null。リンクは <see cref="RegistryCleanLinkException"/>、拒否は例外。</summary>
    public IRegistryCleanerKey? Open(RegistryCleanHive hive, RegistryCleanView view, string keyPath, RegistryCleanAccess access)
    {
        var id = $"{hive}|{view}|{access}|{keyPath}";
        if (_keys.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var key = Navigate(Root(hive, view), view, keyPath, access, create: false);
        _keys[id] = key;
        return key;
    }

    /// <summary>パスのキーを開く。無い階層は作る (復元用。すべて <see cref="RegistryCleanAccess.DeleteTree"/> で開く)。</summary>
    public IRegistryCleanerKey OpenOrCreate(RegistryCleanHive hive, RegistryCleanView view, string keyPath)
    {
        var id = $"create|{hive}|{view}|{keyPath}";
        if (_keys.TryGetValue(id, out var cached) && cached is not null)
        {
            return cached;
        }

        var key = Navigate(Root(hive, view), view, keyPath, RegistryCleanAccess.DeleteTree, create: true)!;
        _keys[id] = key;
        return key;
    }

    private static IRegistryCleanerKey? Navigate(
        IRegistryCleanerKey root, RegistryCleanView view, string keyPath, RegistryCleanAccess access, bool create)
    {
        var segments = keyPath.Split('\\');
        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            var level = create || i == segments.Length - 1 ? access : RegistryCleanAccess.Read;
            var next = Step(current, segments[i], level, view, root, create);
            if (next is null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static IRegistryCleanerKey? Step(
        IRegistryCleanerKey current, string name, RegistryCleanAccess access, RegistryCleanView view, IRegistryCleanerKey root, bool create)
    {
        try
        {
            return current.OpenChild(name, access, view) ?? (create ? current.CreateChild(name, view) : null);
        }
        finally
        {
            // 途中の階層は開き直さないため、その場で閉じる (ルートは入れ物が持つ)。
            if (!ReferenceEquals(current, root))
            {
                current.Dispose();
            }
        }
    }

    private IRegistryCleanerKey Root(RegistryCleanHive hive, RegistryCleanView view)
    {
        if (_roots.TryGetValue((hive, view), out var root))
        {
            return root;
        }

        root = hive switch
        {
            RegistryCleanHive.LocalMachine => factory.OpenLocalMachine(view),
            RegistryCleanHive.UserClasses => factory.OpenUserClasses(userSid ?? throw new RegistryHiveUnavailableException(), view),
            _ => factory.OpenUserHive(userSid ?? throw new RegistryHiveUnavailableException(), view),
        };
        _roots[(hive, view)] = root;
        return root;
    }

    public void Dispose()
    {
        foreach (var key in _keys.Values)
        {
            key?.Dispose();
        }

        foreach (var root in _roots.Values)
        {
            root.Dispose();
        }

        _keys.Clear();
        _roots.Clear();
    }
}

/// <summary>REG_SZ の読み書き (MRUList 用)。</summary>
internal static class RegistryTextValue
{
    public const uint RegSz = 1;

    public static string Decode(byte[] data) => Encoding.Unicode.GetString(data).TrimEnd('\0');

    public static byte[] Encode(string text) => Encoding.Unicode.GetBytes(text + "\0");
}

/// <summary>1 回の削除の実行状態 (サービスから 1 回だけ使う)。</summary>
internal sealed class RegistryCleanRun : IDisposable
{
    private enum Check
    {
        Same,
        Gone,
        Changed,
    }

    private sealed class Entry(RegistryScanItem item)
    {
        public RegistryScanItem Item { get; } = item;

        public IRegistryCleanerKey? Handle { get; set; }

        public RegistryItemState? State { get; set; }

        public string? Reason { get; set; }

        /// <summary>同じ物理キー・選択済みの親キーの処理に含まれる場合の本体。</summary>
        public Entry? Primary { get; set; }

        public Group? Group { get; set; }

        public IReadOnlyList<string> Slots { get; set; } = [];

        public bool Pending => State is null && Primary is null;
    }

    private sealed class Group(RegistryScanCompanion companion)
    {
        public RegistryCompanionKind Kind { get; } = companion.Kind;

        public RegistryScanItem Related { get; } = companion.Related;

        public IRegistryCleanerKey? Handle { get; set; }

        public List<Entry> Owners { get; } = [];
    }

    private readonly IRegistryBackupStore _store;
    private readonly IRegistryShellNotifier _notifier;
    private readonly RegistryCleanRequest _request;
    private readonly Func<string?> _currentUserSid;
    private readonly CancellationToken _ct;
    private readonly RegistryHandleCache _cache;
    private readonly List<Entry> _entries = [];
    private readonly List<Group> _groups = [];
    private bool _warning;

    public RegistryCleanRun(
        IRegistryCleanerRootFactory factory, IRegistryBackupStore store, IRegistryShellNotifier notifier,
        RegistryCleanRequest request, Func<string?> currentUserSid, CancellationToken ct)
    {
        _store = store;
        _notifier = notifier;
        _request = request;
        _currentUserSid = currentUserSid;
        _ct = ct;
        _cache = new RegistryHandleCache(factory, request.Scan.UserScope.Available ? request.Scan.UserScope.Sid : null);
    }

    public void Dispose() => _cache.Dispose();

    public RegistryCleanRunResult Execute()
    {
        BuildPlan();
        foreach (var entry in _entries.Where(entry => entry.Pending))
        {
            _ct.ThrowIfCancellationRequested();
            Prepare(entry);
        }

        VerifyGroups();
        string? backupId = null;
        if (_entries.Any(entry => entry.Pending))
        {
            _ct.ThrowIfCancellationRequested();
            if (!TryBackup(out backupId, out var backupFailure))
            {
                return Finish(null, false, backupFailure);
            }

            DeleteAll();
            WriteGroups();
        }

        return Finish(backupId, _ct.IsCancellationRequested, null);
    }

    // ---- 計画 ----

    private void BuildPlan()
    {
        var inScan = _request.Scan.Items.ToHashSet();
        var companions = _request.Scan.Companions.GroupBy(c => c.Owner).ToDictionary(g => g.Key, g => g.First());
        var byIdentity = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _request.Selected.Distinct())
        {
            var entry = new Entry(item);
            _entries.Add(entry);
            if (!inScan.Contains(item))
            {
                Set(entry, RegistryItemState.Skipped, CleanupMessage.Of(RegistryCleanReasons.NotInScan));
            }
            else if (!IsAllowed(item))
            {
                Set(entry, RegistryItemState.Skipped, CleanupMessage.Of(RegistryCleanReasons.PathNotAllowed));
            }
            else if (byIdentity.TryGetValue(Identity(item), out var first))
            {
                entry.Primary = first;
            }
            else
            {
                byIdentity[Identity(item)] = entry;
            }
        }

        var keys = _entries.Where(e => e.Pending && e.Item.ValueName is null).ToList();
        foreach (var entry in _entries.Where(e => e.Pending))
        {
            entry.Primary = keys
                .Where(key => key != entry && Covers(key.Item.KernelName, entry.Item))
                .OrderBy(key => key.Item.KernelName.Length)
                .FirstOrDefault();
        }

        foreach (var entry in _entries.Where(e => e.Pending))
        {
            if (companions.TryGetValue(entry.Item, out var companion))
            {
                AttachCompanion(entry, companion);
            }
        }
    }

    private bool IsAllowed(RegistryScanItem item) =>
        RegistryBackupValidator.IsAllowedPath(item.Category, item.Hive, item.KeyPath, item.ValueName is null);

    private static string Identity(RegistryScanItem item) =>
        item.ValueName is null ? "K\0" + item.KernelName : "V\0" + item.KernelName + "\0" + item.ValueName;

    /// <summary>選択済みのキー <paramref name="keyKernel"/> の配下 (値なら、そのキー自身も) か。</summary>
    private static bool Covers(string keyKernel, RegistryScanItem item) =>
        item.KernelName.StartsWith(keyKernel + "\\", StringComparison.OrdinalIgnoreCase) ||
        (item.ValueName is not null && item.KernelName.Equals(keyKernel, StringComparison.OrdinalIgnoreCase));

    private void AttachCompanion(Entry entry, RegistryScanCompanion companion)
    {
        var related = companion.Related;
        var group = _groups.FirstOrDefault(g =>
            g.Kind == companion.Kind && g.Related.KernelName.Equals(related.KernelName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(g.Related.ValueName, related.ValueName, StringComparison.OrdinalIgnoreCase));
        if (group is null)
        {
            group = new Group(companion);
            _groups.Add(group);
        }

        group.Owners.Add(entry);
        entry.Group = group;
        entry.Slots = companion.RemovedSlots;
    }

    // ---- ハンドルを開いて再照合 ----

    private void Prepare(Entry entry)
    {
        var item = entry.Item;
        try
        {
            var access = item.ValueName is null ? RegistryCleanAccess.DeleteTree : RegistryCleanAccess.ModifyValues;
            entry.Handle = _cache.Open(item.Hive, item.View, item.KeyPath, access);
            if (entry.Handle is null)
            {
                Set(entry, RegistryItemState.Retained, CleanupMessage.Of(RegistryCleanReasons.Gone));
            }
            else if (string.Equals(entry.Handle.OwnerSid(), RegistryCategoryDetectors.TrustedInstallerSid, StringComparison.OrdinalIgnoreCase))
            {
                Set(entry, RegistryItemState.Skipped, CleanupMessage.Of(RegistryCleanReasons.TrustedInstaller));
            }
            else
            {
                ApplyCheck(entry, Compare(entry.Handle, item));
            }
        }
        catch (Exception ex) when (RegistryCleanFailures.IsExpected(ex))
        {
            Fail(entry, ex);
        }
    }

    private void VerifyGroups()
    {
        foreach (var group in _groups.Where(g => g.Owners.Any(o => o.Pending)))
        {
            try
            {
                var related = group.Related;
                group.Handle = _cache.Open(related.Hive, related.View, related.KeyPath, RegistryCleanAccess.ModifyValues);
                var check = group.Handle is null ? Check.Gone : Compare(group.Handle, related);
                if (check != Check.Same)
                {
                    foreach (var owner in group.Owners.Where(o => o.Pending))
                    {
                        ApplyCheck(owner, check);
                    }
                }
            }
            catch (Exception ex) when (RegistryCleanFailures.IsExpected(ex))
            {
                foreach (var owner in group.Owners.Where(o => o.Pending))
                {
                    Fail(owner, ex);
                }
            }
        }
    }

    /// <summary>値は <see cref="RegistryScanHashing.OfValue"/>、キーは <see cref="RegistryScanHashing.OfSubtree"/> と LastWriteTime で走査時と照合する。</summary>
    private static Check Compare(IRegistryCleanerKey handle, RegistryScanItem item)
    {
        if (item.ValueName is { } name)
        {
            return handle.GetValue(name) is not { } raw
                ? Check.Gone
                : RegistryScanHashing.OfValue(raw) == item.ContentHash ? Check.Same : Check.Changed;
        }

        return RegistryScanHashing.OfSubtree(handle, item.View) == item.ContentHash &&
               handle.QueryInfo().LastWriteTimeUtc == item.LastWriteTimeUtc
            ? Check.Same
            : Check.Changed;
    }

    private void ApplyCheck(Entry entry, Check check)
    {
        if (check != Check.Same)
        {
            Set(entry, RegistryItemState.Retained, CleanupMessage.Of(check == Check.Gone ? RegistryCleanReasons.Gone : RegistryCleanReasons.Changed));
        }
    }

    // ---- バックアップ ----

    private bool TryBackup(out string? backupId, out string? failure)
    {
        backupId = null;
        failure = null;
        var items = new List<RegistryBackupItem>();

        // 付随値 (StartupApproved・MRUList) を先に取り込む。取り込めなければ、その付随値を変更する候補は消さない。
        foreach (var group in _groups.Where(g => g.Owners.Any(o => o.Pending)))
        {
            CaptureGroup(group, items);
        }

        foreach (var entry in _entries.Where(e => e.Pending))
        {
            CaptureEntry(entry, items);
        }

        if (!_entries.Any(e => e.Pending))
        {
            return true;
        }

        try
        {
            var sid = _request.Scan.UserScope.Sid ?? _currentUserSid() ?? throw new RegistryBackupException("current user SID unavailable");
            backupId = _store.Save(new RegistryBackupDocument(RegistryBackupDocument.CurrentVersion, DateTime.UtcNow, sid, [.. items]));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            failure = CleanupMessage.Of(RegistryCleanReasons.BackupFailed, ex.Message);
            foreach (var entry in _entries.Where(e => e.Pending))
            {
                Set(entry, RegistryItemState.Failed, failure);
            }

            return false;
        }
    }

    private void CaptureEntry(Entry entry, List<RegistryBackupItem> items)
    {
        var item = entry.Item;
        try
        {
            if (item.ValueName is null)
            {
                var node = RegistryBackupCapture.CaptureKey(entry.Handle!, item.View);
                items.Add(new RegistryBackupItem(item.Category, item.Hive, item.View, item.KeyPath, null, null, null, node));
            }
            else if (!CaptureValueItem(item, entry.Handle!, items))
            {
                Set(entry, RegistryItemState.Retained, CleanupMessage.Of(RegistryCleanReasons.Gone));
            }
        }
        catch (Exception ex) when (RegistryCleanFailures.IsExpected(ex))
        {
            Set(entry, RegistryItemState.Skipped, CleanupMessage.Of(RegistryCleanReasons.BackupItemFailed, ex.Message));
        }
    }

    /// <summary>付随値を取り込む。読み取りに失敗したら、本体の取り込み失敗と同じく、その付随値を持つ候補を Skipped にする。</summary>
    private static void CaptureGroup(Group group, List<RegistryBackupItem> items)
    {
        try
        {
            CaptureValueItem(group.Related, group.Handle!, items);
        }
        catch (Exception ex) when (RegistryCleanFailures.IsExpected(ex))
        {
            var reason = CleanupMessage.Of(RegistryCleanReasons.BackupItemFailed, ex.Message);
            foreach (var owner in group.Owners.Where(o => o.Pending))
            {
                Set(owner, RegistryItemState.Skipped, reason);
            }
        }
    }

    private static bool CaptureValueItem(RegistryScanItem item, IRegistryCleanerKey handle, List<RegistryBackupItem> items)
    {
        if (RegistryBackupCapture.CaptureValue(handle, item.ValueName!) is not { } value)
        {
            return false;
        }

        var duplicate = items.Any(i =>
            i.Hive == item.Hive && i.View == item.View && i.ValueName == item.ValueName &&
            i.KeyPath.Equals(item.KeyPath, StringComparison.OrdinalIgnoreCase));
        if (!duplicate)
        {
            items.Add(new RegistryBackupItem(item.Category, item.Hive, item.View, item.KeyPath, item.ValueName, value.Type, value.DataBase64, null));
        }

        return true;
    }

    // ---- 削除 ----

    private void DeleteAll()
    {
        foreach (var entry in _entries.Where(e => e.Pending))
        {
            if (_ct.IsCancellationRequested)
            {
                Set(entry, RegistryItemState.Skipped, CleanupMessage.Of(RegistryCleanReasons.CanceledNotRun));
                continue;
            }

            DeleteOne(entry);
        }
    }

    /// <summary>同じハンドルで直前に再照合 (付随値を含む) してから削除する。</summary>
    private void DeleteOne(Entry entry)
    {
        try
        {
            var item = entry.Item;
            var check = Compare(entry.Handle!, item);
            if (check == Check.Same && entry.Group is { } group && group.Handle is not null)
            {
                check = Compare(group.Handle, group.Related);
            }

            if (check != Check.Same)
            {
                ApplyCheck(entry, check);
                return;
            }

            if (item.ValueName is null)
            {
                entry.Handle!.DeleteSelf();
            }
            else
            {
                entry.Handle!.DeleteValue(item.ValueName);
            }

            Set(entry, RegistryItemState.Deleted, null);
            DeleteApproved(entry);
        }
        catch (Exception ex) when (RegistryCleanFailures.IsExpected(ex))
        {
            Fail(entry, ex);
        }
    }

    private void DeleteApproved(Entry entry)
    {
        if (entry.Group is not { Kind: RegistryCompanionKind.StartupApprovedValue } group)
        {
            return;
        }

        try
        {
            group.Handle!.DeleteValue(group.Related.ValueName!);
        }
        catch (Exception ex) when (RegistryCleanFailures.IsExpected(ex))
        {
            entry.Reason = CleanupMessage.Of(RegistryCleanReasons.CompanionFailed, ex.Message);
            _warning = true;
        }
    }

    /// <summary>削除できたスロットだけを MRUList から外して書き戻す (空になれば値ごと削除)。</summary>
    private void WriteGroups()
    {
        foreach (var group in _groups.Where(g => g.Kind == RegistryCompanionKind.OpenWithMruList))
        {
            var slots = group.Owners.Where(o => o.State == RegistryItemState.Deleted).SelectMany(o => o.Slots).ToHashSet(StringComparer.Ordinal);
            if (slots.Count > 0)
            {
                WriteMruList(group, slots);
            }
        }
    }

    private void WriteMruList(Group group, HashSet<string> slots)
    {
        var name = group.Related.ValueName!;
        try
        {
            if (group.Handle!.GetValue(name) is not { Type: RegistryTextValue.RegSz } raw || RegistryScanHashing.OfValue(raw) != group.Related.ContentHash)
            {
                throw new IOException(CleanupMessage.Of(RegistryCleanReasons.Changed));
            }

            var remaining = new string([.. RegistryTextValue.Decode(raw.Data).Where(c => !slots.Contains(c.ToString()))]);
            if (remaining.Length == 0)
            {
                group.Handle.DeleteValue(name);
            }
            else
            {
                group.Handle.SetValue(name, new RegistryRawValue(raw.Type, RegistryTextValue.Encode(remaining)));
            }
        }
        catch (Exception ex) when (RegistryCleanFailures.IsExpected(ex))
        {
            _warning = true;
            foreach (var owner in group.Owners.Where(o => o.State == RegistryItemState.Deleted))
            {
                owner.Reason = CleanupMessage.Of(RegistryCleanReasons.CompanionFailed, ex.Message);
            }
        }
    }

    // ---- 結果 ----

    private RegistryCleanRunResult Finish(string? backupId, bool canceled, string? failureMessage)
    {
        if (_entries.Any(e => e.State == RegistryItemState.Deleted && e.Item.Category == RegistryCleanCategory.FileAssociations))
        {
            _notifier.AssociationsChanged();
        }

        foreach (var entry in _entries.Where(e => e.Primary is not null))
        {
            var primary = entry.Primary!;
            while (primary.Primary is not null)
            {
                primary = primary.Primary;
            }

            var state = primary.State ?? RegistryItemState.Skipped;
            entry.State = state;
            entry.Reason = state == RegistryItemState.Deleted ? CleanupMessage.Of(RegistryCleanReasons.CoveredByOther) : primary.Reason;
        }

        var items = _entries.Select(e => new RegistryCleanItemResult(e.Item, e.State ?? RegistryItemState.Skipped, e.Reason)).ToList();
        var overall = canceled ? RegistryOverallState.Canceled : Summarize(items, failureMessage is not null);
        var written = backupId is not null && TryWriteResult(backupId, overall, items, ref failureMessage);
        return new(overall, items, backupId, null, failureMessage, false, written);
    }

    private RegistryOverallState Summarize(List<RegistryCleanItemResult> items, bool backupFailed)
    {
        if (backupFailed)
        {
            return RegistryOverallState.Failed;
        }

        var bad = items.Where(i => i.State is RegistryItemState.Skipped or RegistryItemState.Failed).ToList();
        if (bad.Count == 0 && !_warning)
        {
            return RegistryOverallState.Success;
        }

        return items.All(i => i.State == RegistryItemState.Failed) ? RegistryOverallState.Failed : RegistryOverallState.Partial;
    }

    private bool TryWriteResult(string backupId, RegistryOverallState overall, List<RegistryCleanItemResult> items, ref string? message)
    {
        var rows = items.Select(i => new RegistryBackupResultItem(
            i.Item.Category, i.Item.Hive, i.Item.View, i.Item.KeyPath, i.Item.ValueName, i.State, i.Reason)).ToArray();
        try
        {
            _store.WriteResult(backupId, new RegistryBackupResultDocument(RegistryBackupDocument.CurrentVersion, backupId, DateTime.UtcNow, overall, rows));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            message = CleanupMessage.Of(RegistryCleanReasons.ResultNotWritten, ex.Message);
            return false;
        }
    }

    private static void Set(Entry entry, RegistryItemState state, string? reason)
    {
        entry.State = state;
        entry.Reason = reason;
    }

    private static void Fail(Entry entry, Exception ex)
    {
        var (state, reason) = RegistryCleanFailures.Classify(ex, RegistryCleanReasons.ItemFailed);
        Set(entry, state, reason);
    }
}
