using System.Runtime.Versioning;
using Lumin4ti.Core.Services.Windows.Actions;
using EngineTarget = Lumin4ti.Core.Services.Windows.Actions.CleanupTarget;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>走査の指定。</summary>
/// <param name="Filter">除外の一致器。削除時と同じものを渡す。</param>
/// <param name="RepresentativeLimit">代表として返すパスの最大数。</param>
/// <param name="CollectIdentities">削除時の同一性照合用に、各ファイルの FileId・サイズ・更新時刻を集めるか (ファイルを読取属性だけで 1 回開く)。</param>
/// <param name="MaxIdentities">集める実体の上限。超過分は件数・サイズには数えるが削除対象には含めない。</param>
public sealed record CleanupScanOptions(
    CleanupExclusionMatcher? Filter = null,
    int RepresentativeLimit = 5,
    bool CollectIdentities = false,
    int MaxIdentities = 200_000)
{
    /// <summary>古さの基準時刻。既定は現在 (テスト用に差し替える)。</summary>
    public DateTime? Now { get; init; }

    /// <summary>
    /// 先に走査した別の規則が既に対象にしているものを見分ける。(パス, フォルダーか, 更新時刻) を受け、
    /// 該当する規則の識別子の連結を返す (無ければ null)。該当したものは通常どおり数え、<see cref="CleanupScanResult.Overlaps"/> にも記録する。
    /// </summary>
    internal Func<string, bool, long, string?>? ClaimKeyOf { get; init; }
}

/// <summary>先の規則と重なって数えたファイル・フォルダーの内訳 (1 つの先の規則の組ごと)。</summary>
internal sealed class OverlapCount
{
    public long Files { get; set; }

    public long Directories { get; set; }

    public long Bytes { get; set; }
}

/// <summary>1 対象の走査結果。何も変更していない。</summary>
public sealed class CleanupScanResult
{
    /// <summary>NTFS の時刻の粗さ (約 15.6 ms) で、走査直後に作られたファイルが走査前に見えないための余裕。</summary>
    internal static readonly TimeSpan CreationMargin = TimeSpan.FromMilliseconds(50);

    /// <summary>解決した絶対パス。拒否された場合は空。</summary>
    public string Path { get; internal set; } = string.Empty;

    /// <summary>対象フォルダーが存在しなかった。</summary>
    public bool Missing { get; internal set; }

    /// <summary>安全ガードで拒否した理由。拒否されていなければ null。</summary>
    public string? Rejection { get; internal set; }

    /// <summary>削除対象のファイル数。</summary>
    public long Files { get; internal set; }

    /// <summary>空になるため削除対象のフォルダー数。</summary>
    public long Directories { get; internal set; }

    /// <summary>削除対象ファイルの合計サイズ (EndOfFile)。</summary>
    public long Bytes { get; internal set; }

    /// <summary>除外のため残すファイル・フォルダー数。</summary>
    public long Retained { get; internal set; }

    /// <summary>リンク (再解析点) のため触れない数。</summary>
    public long SkippedLinks { get; internal set; }

    /// <summary>列挙・取得に失敗して調べられなかった数。</summary>
    public long Unreadable { get; internal set; }

    /// <summary>代表として挙げた削除対象のパス。</summary>
    public IReadOnlyList<string> Representatives { get; internal set; } = [];

    /// <summary><see cref="CleanupScanOptions.CollectIdentities"/> で集めた実体。</summary>
    public IReadOnlyList<CleanupFileIdentity> Identities { get; internal set; } = [];

    /// <summary>先に走査した規則と重なって数えた分。キーは重なった先の規則の識別子の連結。</summary>
    internal Dictionary<string, OverlapCount> Overlaps { get; } = new(StringComparer.Ordinal);

    internal void AddOverlap(string key, long files, long directories, long bytes)
    {
        if (!Overlaps.TryGetValue(key, out var count)) Overlaps[key] = count = new OverlapCount();
        count.Files += files;
        count.Directories += directories;
        count.Bytes += bytes;
    }

    /// <summary>集めた実体が上限に達し、残りは削除対象に含まれない。</summary>
    public bool IdentitiesTruncated { get; internal set; }

    /// <summary>走査を終えた時刻 (UTC)。</summary>
    public DateTime CompletedAtUtc { get; internal set; }

    /// <summary>削除時の <see cref="CleanupRunOptions.CreatedBefore"/> に渡す。これより後に作られたものは残す。</summary>
    public DateTime CreatedBefore => CompletedAtUtc - CreationMargin;
}

/// <summary>
/// 削除せずに、削除エンジンと同じ規則で対象の件数・サイズ・代表を数える。
/// リンクを辿らず、祖先を固定して列挙し、ファイルの内容・時刻・属性を変えない。
/// </summary>
[SupportedOSPlatform("windows")]
public static class FileCleanupScanner
{
    private const int MaxDepth = 64;

    /// <summary>対象 1 件を走査する。取消は <see cref="OperationCanceledException"/> で伝える。</summary>
    public static CleanupScanResult Scan(EngineTarget target, CleanupScanOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        options ??= new CleanupScanOptions();
        var result = new CleanupScanResult();
        try { ScanTarget(target, options, result, ct); }
        finally { result.CompletedAtUtc = DateTime.UtcNow; }
        return result;
    }

    private static void ScanTarget(EngineTarget target, CleanupScanOptions options, CleanupScanResult result, CancellationToken ct)
    {
        if (!FileCleanupEngine.IsValidFilter(target, out var invalid)) { result.Rejection = invalid; return; }
        if (!FileCleanupEngine.TryResolveTarget(target,target.Kind == CleanupTargetKind.Files, out var fullPath, out var rejection))
        {
            result.Rejection = rejection;
            return;
        }
        result.Path = fullPath;
        if (!Directory.Exists(fullPath)) { result.Missing = true; return; }
        if (options.Filter?.IsExcluded(fullPath, true) == true) { result.Retained++; return; }
        if (!FileCleanupEngine.TryAcquireTraversalGuard(fullPath, out var guard))
        {
            result.Rejection = "リンク非追従で開けませんでした";
            return;
        }
        using (guard)
        {
            var state = new ScanState(target, options, result, ct);
            ScanDirectory(guard, state, 0);
            result.Representatives = state.Representatives;
            result.Identities = state.Identities;
        }
    }

    private sealed class ScanState(EngineTarget target, CleanupScanOptions options, CleanupScanResult result, CancellationToken ct)
    {
        internal EngineTarget Target { get; } = target;
        internal CleanupScanOptions Options { get; } = options;
        internal CleanupScanResult Result { get; } = result;
        internal CancellationToken Ct { get; } = ct;
        internal long? AgeCutoff { get; } = FileCleanupEngine.AgeCutoffOf(target, options.Now);
        internal List<string> Representatives { get; } = [];
        internal List<CleanupFileIdentity> Identities { get; } = [];
    }

    /// <summary>ディレクトリを走査し、中身がすべて削除対象でこのフォルダーも消えるなら true を返す。</summary>
    private static bool ScanDirectory(NativeCleanupEntry directory, ScanState state, int depth)
    {
        var result = state.Result;
        if (depth > MaxDepth) { result.Unreadable++; return false; }
        IReadOnlyList<CleanupEntryInfo> entries;
        try { entries = directory.EnumerateDetailed().ToArray(); }
        catch (Exception ex) when (IsNativeFailure(ex)) { result.Unreadable++; return false; }

        var removable = true;
        foreach (var entry in entries)
        {
            state.Ct.ThrowIfCancellationRequested();
            var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
            if (isDirectory ? !state.Target.Descends : !FileCleanupEngine.IsTargetedFile(state.Target, entry.Name, entry.LastWriteTime, state.AgeCutoff))
            {
                // 辿らない指定のサブフォルダーは見ない。絞り込みから外れたファイルが残るとこのフォルダーは消えない。
                if (!isDirectory) removable = false;
                continue;
            }
            var path = System.IO.Path.Combine(directory.Path, entry.Name);
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { result.SkippedLinks++; removable = false; continue; }
            if (FileCleanupEngine.KeepsAsCredential(state.Target, entry.Name) ||
                FileCleanupEngine.RetentionOf(state.Options.Filter, path, isDirectory, entry.CreationTime, null) != FileCleanupEngine.CleanupRetention.None)
            {
                result.Retained++;
                removable = false;
                continue;
            }
            removable &= isDirectory ? ScanChildDirectory(directory, path, state, depth) : CountFile(directory, path, entry, state);        }
        return removable;
    }

    private static bool ScanChildDirectory(NativeCleanupEntry parent, string path, ScanState state, int depth)
    {
        try
        {
            using var child = parent.OpenDescendant(path, true);
            var removable = ScanDirectory(child, state, depth + 1);
            // 絞り込み指定でフォルダーを消さない場合は、空でも消えない。
            if (!removable || (state.Target.Filtered && !state.Target.RemoveEmptyDirs)) return false;
            state.Result.Directories++;
            if (state.Options.ClaimKeyOf?.Invoke(path, true, 0) is { } claim) state.Result.AddOverlap(claim, 0, 1, 0);
            return true;
        }
        catch (Exception ex) when (IsNativeFailure(ex)) { state.Result.Unreadable++; return false; }
    }

    private static bool CountFile(NativeCleanupEntry parent, string path, CleanupEntryInfo entry, ScanState state)
    {
        var result = state.Result;
        result.Files++;
        result.Bytes += entry.Size;
        if (state.Options.ClaimKeyOf?.Invoke(path, false, entry.LastWriteTime) is { } claim) result.AddOverlap(claim, 1, 0, entry.Size);
        if (state.Representatives.Count < state.Options.RepresentativeLimit) state.Representatives.Add(path);
        if (state.Options.CollectIdentities) CollectIdentity(parent, path, entry, state);
        return true;
    }

    private static void CollectIdentity(NativeCleanupEntry parent, string path, CleanupEntryInfo entry, ScanState state)
    {
        if (state.Identities.Count >= state.Options.MaxIdentities) { state.Result.IdentitiesTruncated = true; return; }
        try
        {
            // 読取属性だけで開く。内容を読まず、時刻も変えない。
            using var file = parent.OpenDescendant(path, false);
            var identity = file.Identity;
            state.Identities.Add(new CleanupFileIdentity(path, identity.VolumeSerialNumber, identity.FileIdLow, identity.FileIdHigh, entry.Size, entry.LastWriteTime));
        }
        catch (Exception ex) when (IsNativeFailure(ex)) { state.Result.Unreadable++; }
    }

    private static bool IsNativeFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception;
}
