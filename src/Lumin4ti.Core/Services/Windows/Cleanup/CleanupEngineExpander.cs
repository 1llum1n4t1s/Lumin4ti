using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>未展開パス (<c>%ENV%\...</c>) の直下にあるエントリー名。</summary>
internal sealed record BrowserDirectoryListing(IReadOnlyList<string> Directories, IReadOnlyList<string> Files);

/// <summary>プロファイルの列挙元。テストでは偽物に差し替える (実機のブラウザーを読まない)。</summary>
internal interface IBrowserProfileSource
{
    /// <summary>未展開パスの直下を列挙する。存在しない・読めない・リンクの場合は null。</summary>
    BrowserDirectoryListing? List(string rawDirectory);
}

/// <summary>実ディスクを、リンク非追従のハンドルで列挙する (旧クリーンアップカードと同じ手段)。</summary>
internal sealed class DiskBrowserProfileSource : IBrowserProfileSource
{
    public BrowserDirectoryListing? List(string rawDirectory)
    {
        if (!FileCleanupEngine.TryResolve(rawDirectory, out var full, out _)
            || !Directory.Exists(full)
            || !FileCleanupEngine.TryAcquireTraversalGuard(full, out var guard))
        {
            return null;
        }

        using (guard)
        {
            try
            {
                var directories = new List<string>();
                var files = new List<string>();
                foreach (var (name, attributes) in guard.Enumerate())
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    ((attributes & FileAttributes.Directory) != 0 ? directories : files).Add(name);
                }

                return new BrowserDirectoryListing(directories, files);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }
    }
}

/// <summary>
/// 製品定義 (<c>engine</c> が chromium / gecko の <c>browser.{製品}</c> 規則) から、種別ごとの規則を生成する。
/// <list type="bullet">
/// <item>製品定義の <c>detect[0]</c> がデータ基点 (Chromium は <c>User Data</c>、Gecko は <c>Profiles</c>)、
/// <c>detect[1]</c> があればキャッシュ基点。無ければキャッシュもデータ基点の下にある。</item>
/// <item>製品定義の <c>targets</c> のうち <c>*</c> を含まないもの (プロファイルに属さない既知のキャッシュ) は、
/// 生成するキャッシュ規則へそのまま引き継ぐ。<c>*</c> を含むものは、プロファイルを列挙した具体パスに置き換わる。</item>
/// <item><see cref="CleanupEngineKind.ChromiumProfile"/> (Opera) は <c>detect[0]</c> 自体がプロファイル (旧形式) で、<c>Default</c> など下位のプロファイルも併せて扱う。
/// 静的な形では基点自体と <c>*</c> の両方を生成する。キャッシュは <c>detect[0]</c> (Roaming) の全プロファイルだけを探す (Local 側は根拠が足りず対象外)。</item>
/// <item>生成するキャッシュ規則の id は製品定義と同じ (保存済みの選択と legacy キーを保つ)。残りは <c>.history</c> などの接尾辞を付ける。</item>
/// <item>パスワード・Local Storage・IndexedDB・ブックマーク本体は生成しない。</item>
/// </list>
/// 展開結果は必ず <see cref="CleanupRuleValidator"/> を通す (プロファイル名はディスク由来のため)。
/// </summary>
internal static class CleanupEngineExpander
{
    /// <summary>実ディスクを列挙しない静的な形で、プロファイル名の代わりに使う記号。</summary>
    public const string WildcardProfile = "*";

    private const string ChromiumMarker = "Preferences";

    /// <summary>旧ブラウザーカードのプロファイル別キャッシュと同じ (Local Storage・IndexedDB 等は含めない)。</summary>
    private static readonly string[] ChromiumCacheNames =
    [
        "Cache", "Code Cache", @"Service Worker\CacheStorage", @"Service Worker\ScriptCache", "GPUCache",
        "JumpListIconsRecentClosed", "JumpListIconsTopSites", "DawnGraphiteCache", "DawnWebGPUCache", "Shared Dictionary",
        "CRXTelemetry", "VideoDecodeStats", "blob_storage", "Media Cache", "Reporting and NEL", "CertificateTransparency", "DawnCache",
    ];

    private sealed record Spec(
        string Suffix,
        CleanupKind Kind,
        CleanupCategory Category,
        CleanupImpact Impact,
        Func<string, CleanupRuleTarget[]> Chromium,
        Func<string, CleanupRuleTarget[]> Gecko);

    private static readonly Spec[] TraceSpecs =
    [
        new("history", CleanupKind.History, CleanupCategory.BrowserTrace, CleanupImpact.Trace,
            p => [File(p, "History"), File(p, "Favicons"), File(p, "Top Sites"), File(p, "Visited Links"), File(p, "Shortcuts")],
            p => [Sql($@"{p}\places.sqlite", CleanupSqliteOperation.GeckoPlaces)]),
        new("form", CleanupKind.Form, CleanupCategory.BrowserTrace, CleanupImpact.Trace,
            p => [Sql($@"{p}\Web Data", CleanupSqliteOperation.ChromiumWebData)],
            p => [Sql($@"{p}\formhistory.sqlite", CleanupSqliteOperation.GeckoFormHistory)]),
        new("session", CleanupKind.Session, CleanupCategory.BrowserTrace, CleanupImpact.Trace,
            p => [Dir($@"{p}\Sessions")],
            p => [File(p, "sessionstore.jsonlz4"), Dir($@"{p}\sessionstore-backups")]),
        new("bookmark-backup", CleanupKind.Backup, CleanupCategory.BrowserTrace, CleanupImpact.Trace,
            p => [File(p, "Bookmarks.bak")],
            p => [Dir($@"{p}\bookmarkbackups")]),
        new("cookies", CleanupKind.Cookies, CleanupCategory.Cookies, CleanupImpact.Signout,
            p => [Sql($@"{p}\Network\Cookies", CleanupSqliteOperation.ChromiumCookies), Sql($@"{p}\Cookies", CleanupSqliteOperation.ChromiumCookies)],
            p => [Sql($@"{p}\cookies.sqlite", CleanupSqliteOperation.GeckoCookies)]),
    ];

    /// <summary>エンジン付きの製品定義か (<c>browser.{製品}</c> の形の id)。</summary>
    public static bool IsProduct(CleanupRule rule) =>
        rule.Engine is CleanupEngineKind.Chromium or CleanupEngineKind.ChromiumProfile or CleanupEngineKind.Gecko
        && rule.Category == CleanupCategory.BrowserCache
        && rule.Id.StartsWith("browser.", StringComparison.Ordinal)
        && rule.Id.Count(c => c == '.') == 1
        && rule.Detect.Count > 0
        && rule.Detect[0].Path is not null;

    /// <summary>
    /// カタログの規則から製品定義を取り除き、展開した規則を加えて検証する。
    /// <paramref name="source"/> が null なら実ディスクを見ず、プロファイル名を <see cref="WildcardProfile"/> にした静的な形で返す。
    /// </summary>
    public static CleanupValidationResult ExpandAll(IEnumerable<CleanupRule> rules, IBrowserProfileSource? source)
    {
        var all = rules.ToList();
        var generated = all.Where(IsProduct).SelectMany(p => Expand(p, source));
        return CleanupRuleValidator.ValidateAll([.. all.Where(r => !IsProduct(r)), .. generated]);
    }

    /// <summary>製品定義 1 件を展開する (検証はしない)。</summary>
    public static IReadOnlyList<CleanupRule> Expand(CleanupRule product, IBrowserProfileSource? source)
    {
        var data = product.Detect[0].Path!;
        var cache = product.Detect.Count > 1 ? product.Detect[1].Path! : data;
        var rootProfile = product.Engine == CleanupEngineKind.ChromiumProfile;
        var chromium = rootProfile || product.Engine == CleanupEngineKind.Chromium;
        var profiles = source is null
            ? (rootProfile ? [RootProfile, WildcardProfile] : [WildcardProfile])
            : ListProfiles(chromium, rootProfile, data, source);

        var result = new List<CleanupRule> { BuildCache(product, chromium, rootProfile, data, cache, profiles) };
        foreach (var spec in TraceSpecs)
        {
            var targets = profiles.SelectMany(name => (chromium ? spec.Chromium : spec.Gecko)(Join(data, name))).ToArray();
            result.Add(Derive(product, spec, targets));
        }

        return result;
    }

    /// <summary>基点フォルダー自体をプロファイルとして扱う印 (<see cref="CleanupEngineKind.ChromiumProfile"/> の旧形式)。</summary>
    private const string RootProfile = "";

    private static string Join(string root, string profile) => profile.Length == 0 ? root : $@"{root}\{profile}";

    private static CleanupRule BuildCache(
        CleanupRule product, bool chromium, bool rootProfile, string dataRoot, string cacheRoot, IReadOnlyList<string> profiles)
    {
        var fixedTargets = product.Targets.Where(t => t.Path is not null && !t.Path.Contains('*'));
        // Opera の Local 側 (Cache・Media Cache) は根拠が 1 ホストのため対象にしない。プロファイルの基点 (Roaming) だけを扱う。
        var perProfile = profiles.SelectMany(name => CacheTargets(chromium, Join(cacheRoot, name)));
        var targets = fixedTargets.Concat(perProfile)
            .DistinctBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return product with { Detect = [product.Detect[0]], Targets = targets };
    }

    private static IEnumerable<CleanupRuleTarget> CacheTargets(bool chromium, string profileDirectory) =>
        chromium ? ChromiumCacheNames.Select(n => Dir($@"{profileDirectory}\{n}")) : [Dir($@"{profileDirectory}\cache2")];

    private static CleanupRule Derive(CleanupRule product, Spec spec, CleanupRuleTarget[] targets) => product with
    {
        Id = $"{product.Id}.{spec.Suffix}",
        Kind = spec.Kind,
        Category = spec.Category,
        Impact = spec.Impact,
        Recommended = false,
        Detect = [product.Detect[0]],
        Targets = [.. targets],
        Legacy = [],
    };

    /// <summary>
    /// プロファイルのフォルダー名を列挙する。Chromium は <c>Preferences</c> のあるフォルダーだけ、Gecko は全フォルダー。
    /// <paramref name="rootProfile"/> なら、基点自体が <c>Preferences</c> を持つ場合に <see cref="RootProfile"/> (空の名前) を先頭に加える。
    /// </summary>
    private static IReadOnlyList<string> ListProfiles(bool chromium, bool rootProfile, string dataRoot, IBrowserProfileSource source)
    {
        var listing = source.List(dataRoot);
        if (listing is null)
        {
            return [];
        }

        var named = listing.Directories
            .Where(IsPlainSegment)
            .Where(name => !chromium || HasMarker(source, $@"{dataRoot}\{name}"))
            .Order(StringComparer.OrdinalIgnoreCase);
        var rootHasMarker = rootProfile && listing.Files.Contains(ChromiumMarker, StringComparer.OrdinalIgnoreCase);
        return [.. rootHasMarker ? named.Prepend(RootProfile) : named];
    }

    private static bool HasMarker(IBrowserProfileSource source, string profileDirectory) =>
        source.List(profileDirectory)?.Files.Contains(ChromiumMarker, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>パスの 1 要素として組み込んでよい名前か (環境変数の再展開・相対指定・末尾の空白やドットを避ける)。</summary>
    private static bool IsPlainSegment(string name) =>
        name.Length > 0 && name.IndexOf('%') < 0 && name is not ("." or "..") && !name.EndsWith(' ') && !name.EndsWith('.');

    private static CleanupRuleTarget Dir(string path) => new() { Type = CleanupTargetType.Contents, Path = path };

    private static CleanupRuleTarget File(string directory, string name) =>
        new() { Type = CleanupTargetType.Files, Path = directory, Pattern = name };

    private static CleanupRuleTarget Sql(string path, CleanupSqliteOperation operation) =>
        new() { Type = CleanupTargetType.Sqlite, Path = path, Operation = operation };
}
