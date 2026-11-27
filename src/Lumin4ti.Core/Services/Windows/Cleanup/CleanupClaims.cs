using System.Runtime.Versioning;
using Lumin4ti.Core.Services.Windows.Actions;
using EngineTarget = Lumin4ti.Core.Services.Windows.Actions.CleanupTarget;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>
/// 先に処理した規則のファイル系の対象 1 件が、どのファイル・フォルダーを扱うか。
/// 包含関係にある対象 (例: %TEMP% 全体と %TEMP%\NeroSupport) で同じファイルを二重に数えない・二重に失敗扱いにしないための判定に使う。
/// ファイルの同一性は、同じ絶対パス (リンクを辿らない走査なので同じ実体) で決める。実体ごとの記録は持たず、規則の数だけの領域で済む。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ClaimRegion
{
    private readonly EngineTarget _target;
    private readonly long? _ageCutoff;

    private ClaimRegion(string ruleId, EngineTarget target, string root, DateTime? now)
    {
        RuleId = ruleId;
        Root = root;
        _target = target;
        _ageCutoff = FileCleanupEngine.AgeCutoffOf(target, now);
    }

    public string RuleId { get; }

    /// <summary>対象フォルダーの絶対パス (末尾の区切りなし)。</summary>
    public string Root { get; }

    /// <param name="now">古さの基準時刻。実行では、エンジンの実行 (<see cref="CleanupRunOptions.Now"/>) と同じ値を渡し、同じファイルに同じ判定を下す。</param>
    public static ClaimRegion? Create(string ruleId, EngineTarget target, DateTime? now = null)
    {
        var ok = FileCleanupEngine.TryResolveTarget(target,target.Kind == CleanupTargetKind.Files, out var full, out _);
        return ok ? new ClaimRegion(ruleId, target, Path.TrimEndingDirectorySeparator(full), now) : null;
    }

    /// <summary>この領域のフォルダーと、指定のフォルダーが祖先・子孫・同一の関係にあるか。どちらかが他方の中なら、重なるファイルがあり得る。</summary>
    public bool Relates(string root) => IsWithin(root, Root) || IsWithin(Root, root);

    /// <summary>この領域が、そのパスを扱うか。</summary>
    /// <param name="forRun">実行時の判定か。実行ではフォルダーは、中身ごと消す指定 (絞り込みなし) のときだけ扱うと見なす。</param>
    public bool Claims(string path, bool isDirectory, long lastWriteTime, bool forRun)
    {
        if (!path.StartsWith(Root, StringComparison.OrdinalIgnoreCase) || path.Length <= Root.Length || path[Root.Length] != '\\') return false;
        if (!_target.Descends && (isDirectory || !string.Equals(Path.GetDirectoryName(path), Root, StringComparison.OrdinalIgnoreCase))) return false;
        if (!isDirectory) return FileCleanupEngine.IsTargetedFile(_target, Path.GetFileName(path), lastWriteTime, _ageCutoff);
        return forRun ? !_target.Filtered : !_target.Filtered || _target.RemoveEmptyDirs;
    }

    private static bool IsWithin(string path, string root) =>
        path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && (path.Length == root.Length || path[root.Length] == '\\');
}

/// <summary>先に処理した規則の領域の集まり。</summary>
[SupportedOSPlatform("windows")]
internal sealed class ClaimSet
{
    /// <summary>キーの区切り。規則の識別子には現れない。</summary>
    internal const char KeySeparator = '\u0001';

    private readonly List<ClaimRegion> _regions = [];

    /// <summary>古さの基準時刻。実行では、全規則のエンジンの実行 (<see cref="CleanupRunOptions.Now"/>) と領域が同じ値を共有する。走査では null (現在)。</summary>
    internal DateTime? Now { get; init; }

    /// <summary>
    /// 規則 1 件の計画に含まれるファイル系の対象を領域として加える。実行では対象 1 件を処理した後に、その対象だけを加える
    /// (処理していない対象を「扱った」ことにしない)。
    /// </summary>
    internal void Add(string ruleId, IEnumerable<TargetPlan> targets)
    {
        foreach (var target in targets)
        {
            if (target.Engine is { } engine && ClaimRegion.Create(ruleId, engine, Now) is { } region) _regions.Add(region);
        }
    }

    /// <summary>走査用。該当する先の規則の識別子 (複数なら昇順に連結) を返す。関係する領域が無ければ null。</summary>
    internal Func<string, bool, long, string?>? ScanResolver(EngineTarget target)
    {
        var related = RelatedTo(target);
        if (related is null) return null;
        return (path, isDirectory, lastWrite) =>
        {
            string? first = null;
            List<string>? more = null;
            foreach (var region in related)
            {
                if (!region.Claims(path, isDirectory, lastWrite, forRun: false)) continue;
                if (first is null) first = region.RuleId;
                else if (!string.Equals(first, region.RuleId, StringComparison.Ordinal) && more?.Contains(region.RuleId) != true) (more ??= []).Add(region.RuleId);
            }

            if (more is null) return first;
            more.Add(first!);
            more.Sort(StringComparer.Ordinal);
            return string.Join(KeySeparator, more);
        };
    }

    /// <summary>実行用。先に実行した規則が既に扱ったものなら true。</summary>
    internal Func<string, bool, long, bool>? RunResolver(EngineTarget target)
    {
        var related = RelatedTo(target);
        if (related is null) return null;
        return (path, isDirectory, lastWrite) => related.Any(region => region.Claims(path, isDirectory, lastWrite, forRun: true));
    }

    private List<ClaimRegion>? RelatedTo(EngineTarget target)
    {
        if (_regions.Count == 0) return null;
        if (!FileCleanupEngine.TryResolveTarget(target,target.Kind == CleanupTargetKind.Files, out var full, out _)) return null;
        var root = Path.TrimEndingDirectorySeparator(full);
        var related = _regions.Where(region => region.Relates(root)).ToList();
        return related.Count == 0 ? null : related;
    }
}
