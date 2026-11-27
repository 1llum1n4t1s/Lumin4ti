using System.IO.Enumeration;
using Lumin4ti.Core.Services.Windows.Actions;
using Lumin4ti.Core.Services.Windows.Cleanup;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class CleanupEngineExpanderTests
{
    private static readonly StringComparer Ignore = StringComparer.OrdinalIgnoreCase;

    private static readonly string[] FakeChromiumProfiles = ["Default", "Profile 1"];

    private sealed class FakeSource(Dictionary<string, BrowserDirectoryListing> map) : IBrowserProfileSource
    {
        private readonly Dictionary<string, BrowserDirectoryListing> _map = new(map, Ignore);

        public BrowserDirectoryListing? List(string rawDirectory) => _map.GetValueOrDefault(rawDirectory);
    }

    private static List<CleanupRule> Catalog() =>
        [.. CleanupRuleCatalog.LoadEmbeddedCore(typeof(CleanupRuleCatalog).Assembly).Rules];

    /// <summary>全製品に、プロファイル (Chromium は Default / Profile 1 と、Preferences の無い Crashpad、Gecko は 1 つ) があるディスクを模す。</summary>
    private static FakeSource FakeDisk(IEnumerable<CleanupRule> rules, params string[] extraDirectories)
    {
        var map = new Dictionary<string, BrowserDirectoryListing>(Ignore);
        foreach (var product in rules.Where(CleanupEngineExpander.IsProduct))
        {
            var root = product.Detect[0].Path!;
            if (product.Engine == CleanupEngineKind.Chromium)
            {
                string[] names = [.. FakeChromiumProfiles, "Crashpad", .. extraDirectories];
                map[root] = new(names, []);
                foreach (var name in names.Where(n => n != "Crashpad"))
                {
                    map[$@"{root}\{name}"] = new([], ["Preferences", "History"]);
                }

                map[$@"{root}\Crashpad"] = new([], []);
            }
            else if (product.Engine == CleanupEngineKind.ChromiumProfile)
            {
                map[root] = new(["Default", "Crashpad"], ["Preferences"]);
                map[$@"{root}\Default"] = new([], ["Preferences", "History"]);
                map[$@"{root}\Crashpad"] = new([], []);
            }
            else
            {
                map[root] = new(["abcd1234.default-release"], []);
            }
        }

        return new(map);
    }

    private static HashSet<string> ContentsPaths(IEnumerable<CleanupRule> rules) =>
        rules.SelectMany(r => r.Targets)
            .Where(t => t.Type == CleanupTargetType.Contents && t.Path is not null && t.Pattern is null && t.OlderThanDays is null)
            .Select(t => t.Path!)
            .ToHashSet(Ignore);

    [TestMethod]
    public void 製品定義は静的にも実列挙でも展開結果が検証器を通る()
    {
        var rules = Catalog();
        var products = rules.Where(CleanupEngineExpander.IsProduct).ToList();
        Assert.IsTrue(products.Count >= 8, "製品定義が読めていません");

        var staticForm = CleanupEngineExpander.ExpandAll(rules, null);
        var runtimeForm = CleanupEngineExpander.ExpandAll(rules, FakeDisk(rules));

        Assert.AreEqual(0, staticForm.Violations.Count, string.Join(";", staticForm.Violations.Select(v => $"{v.RuleId}:{v.Code}")));
        Assert.AreEqual(0, runtimeForm.Violations.Count, string.Join(";", runtimeForm.Violations.Select(v => $"{v.RuleId}:{v.Code}")));
        Assert.AreEqual(products.Count * 6, runtimeForm.Valid.Count(r => r.Id.StartsWith("browser.", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void 製品定義の対象は静的展開のキャッシュ規則に含まれ展開後も同じidで残る()
    {
        var rules = Catalog();
        foreach (var product in rules.Where(CleanupEngineExpander.IsProduct))
        {
            var cache = CleanupEngineExpander.Expand(product, null).Single(r => r.Id == product.Id);
            var expanded = cache.Targets.Select(t => t.Path!).ToHashSet(Ignore);
            var missing = product.Targets.Select(t => t.Path!).Where(p => !expanded.Contains(p)).ToList();
            Assert.AreEqual(0, missing.Count, $"{product.Id}: 製品定義の対象が静的展開と食い違います: {string.Join(";", missing)}");
            CollectionAssert.AreEqual(product.Legacy, cache.Legacy.ToList(), $"{product.Id}: legacy が変わっています");
        }
    }

    [TestMethod]
    public void 実列挙はPreferencesのあるプロファイルだけを展開し危険な名前を除く()
    {
        var rules = Catalog();
        // 根拠の無い固定対象は製品定義から継承規則へ分けたため、固定対象の引き継ぎは合成した対象で確かめる。
        var chrome = rules.Single(r => r.Id == "browser.chrome");
        var fixedTarget = new CleanupRuleTarget { Type = CleanupTargetType.Contents, Path = @"%LOCALAPPDATA%\Google\Chrome\Temp" };
        chrome = chrome with { Targets = [.. chrome.Targets, fixedTarget] };
        var root = chrome.Detect[0].Path!;
        var result = CleanupEngineExpander.Expand(chrome, FakeDisk(rules, "%TEMP%x", ".."));
        var all = result.SelectMany(r => r.Targets).Select(t => t.Path!).ToList();

        Assert.IsTrue(all.Contains($@"{root}\Default\Cache", Ignore));
        Assert.IsTrue(all.Contains($@"{root}\Profile 1\GPUCache", Ignore));
        Assert.IsTrue(all.Contains($@"{root}\Default\Network\Cookies", Ignore));
        Assert.IsFalse(all.Any(p => p.Contains("Crashpad\\Cache", StringComparison.OrdinalIgnoreCase)), "Preferences の無いフォルダーを展開しました");
        Assert.IsFalse(all.Any(p => p.Contains('*') || p.Contains("%TEMP%x", StringComparison.OrdinalIgnoreCase) || p.Contains("\\..\\", StringComparison.Ordinal)));
        var shared = result.Single(r => r.Id == "browser.chrome").Targets.Select(t => t.Path!).ToList();
        Assert.IsTrue(shared.Contains(@"%LOCALAPPDATA%\Google\Chrome\Temp", Ignore), "プロファイルに属さない既知キャッシュが引き継がれていません");
    }

    [TestMethod]
    public void 生成規則は認証情報とサイトデータを含まず影響と推奨が規約どおり()
    {
        var rules = Catalog();
        var generated = CleanupEngineExpander.ExpandAll(rules, FakeDisk(rules)).Valid
            .Where(r => r.Id.StartsWith("browser.", StringComparison.Ordinal) && r.Id.Count(c => c == '.') >= 1)
            .ToList();
        string[] forbidden = ["Login Data", "logins.json", "key4.db", "Local Storage", "IndexedDB", "Local State", "Storage2", "Bookmarks\\", "places.sqlite-"];

        foreach (var rule in generated)
        {
            var text = string.Join('\n', rule.Targets.Select(t => $"{t.Path}|{t.Pattern}"));
            Assert.IsFalse(forbidden.Any(f => text.Contains(f, StringComparison.OrdinalIgnoreCase)), $"{rule.Id} に対象外のデータが入っています");
            var expectedImpact = rule.Id.EndsWith(".cookies", StringComparison.Ordinal) ? CleanupImpact.Signout
                : rule.Kind == CleanupKind.Cache ? CleanupImpact.Junk : CleanupImpact.Trace;
            Assert.AreEqual(expectedImpact, rule.Impact, rule.Id);
            Assert.IsTrue(!rule.Recommended || (rule.Impact == CleanupImpact.Junk && rule.Kind == CleanupKind.Cache), $"{rule.Id}: 推奨は junk のキャッシュだけです");
            Assert.IsTrue(rule.Evidence.Count > 0);
        }

        var bookmarks = generated.Where(r => r.Id.EndsWith(".bookmark-backup", StringComparison.Ordinal)).SelectMany(r => r.Targets).ToList();
        Assert.IsTrue(bookmarks.All(t => t.Pattern is "Bookmarks.bak" || t.Path!.EndsWith("bookmarkbackups", StringComparison.OrdinalIgnoreCase)), "ブックマーク本体は対象にできません");
    }

    private const string OperaData = @"%APPDATA%\Opera Software\Opera Stable";
    private const string OperaCache = @"%LOCALAPPDATA%\Opera Software\Opera Stable";

    [TestMethod]
    public void 基点自体がプロファイルの製品は旧形式と新形式のDefaultの両方を展開しCacheはLocalとRoamingの両方で探す()
    {
        var rules = Catalog();
        var opera = rules.Single(r => r.Id == "browser.opera");
        var expanded = CleanupEngineExpander.ExpandAll(rules, FakeDisk(rules)).Valid.Where(r => r.Id.StartsWith("browser.opera.", StringComparison.Ordinal) || r.Id == "browser.opera").ToList();
        var all = expanded.SelectMany(r => r.Targets).Select(t => $@"{t.Path}|{t.Pattern}").ToList();

        foreach (var expected in new[]
        {
            $@"{OperaData}\Cache|", $@"{OperaData}\Media Cache|", $@"{OperaData}\Default\Cache|", $@"{OperaData}\Code Cache|", $@"{OperaData}\Default\GPUCache|",
            $"{OperaData}|History", $@"{OperaData}\Default|History", $@"{OperaData}\Network\Cookies|", $@"{OperaData}\Default\Network\Cookies|",
        })
        {
            Assert.IsTrue(all.Contains(expected, Ignore), $"{expected} が展開されていません");
        }

        Assert.IsFalse(all.Any(p => p.Contains("Crashpad", StringComparison.OrdinalIgnoreCase) || p.Contains('*')), "Preferences の無いフォルダーまたは * が残っています");
        Assert.IsFalse(all.Any(p => p.StartsWith(OperaCache, StringComparison.OrdinalIgnoreCase)), "Local 側は根拠が1ホストのため対象にできません");
        Assert.AreEqual(6, expanded.Count);

        var staticForm = CleanupEngineExpander.Expand(opera, null).SelectMany(r => r.Targets).Select(t => t.Path!).ToList();
        Assert.IsTrue(staticForm.Contains($@"{OperaData}\Cache", Ignore));
        Assert.IsFalse(staticForm.Any(p => p.StartsWith(OperaCache, StringComparison.OrdinalIgnoreCase)), "Local 側が対象に入っています");
        Assert.IsTrue(staticForm.Contains($@"{OperaData}\*\Cache", Ignore));
        Assert.IsTrue(staticForm.Contains(OperaData, Ignore), "旧形式の基点自体の履歴ファイルが静的形に無い");
    }

    [TestMethod]
    public void 基点にPreferencesが無い旧形式は基点自体を展開しない()
    {
        var rules = Catalog();
        var opera = rules.Single(r => r.Id == "browser.opera");
        var map = new Dictionary<string, BrowserDirectoryListing>(Ignore)
        {
            [OperaData] = new(["Default"], ["Local State"]),
            [$@"{OperaData}\Default"] = new([], ["Preferences"]),
        };
        var all = CleanupEngineExpander.Expand(opera, new FakeSource(map)).SelectMany(r => r.Targets).Select(t => t.Path!).ToList();

        Assert.IsTrue(all.Contains($@"{OperaData}\Default\Cache", Ignore));
        Assert.IsFalse(all.Contains($@"{OperaCache}\Default\Cache", Ignore));
        Assert.IsFalse(all.Contains($@"{OperaData}\Code Cache", Ignore), "基点自体が展開されています");
    }

    [TestMethod]
    public void 実ディスク列挙はリンク以外の直下を返し無い場所はnullを返す()
    {
        var token = $"lumin4ti-expander-{Guid.NewGuid():N}";
        var temp = Path.Combine(Path.GetTempPath(), token);
        Directory.CreateDirectory(Path.Combine(temp, "Default"));
        File.WriteAllText(Path.Combine(temp, "Local State"), "{}");
        try
        {
            var source = new DiskBrowserProfileSource();
            var listing = source.List($@"%TEMP%\{token}");

            Assert.IsNotNull(listing);
            CollectionAssert.AreEqual(new[] { "Default" }, listing.Directories.ToList());
            CollectionAssert.AreEqual(new[] { "Local State" }, listing.Files.ToList());
            Assert.IsNull(source.List($@"%TEMP%\{token}\absent"));
            Assert.IsNull(source.List(@"%TEMP%\.."));
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    // ═══ 現行 FileCleanupGroups の対象 (固定値) ═══
    // 5a で FileCleanupGroups の配列を消しても動くよう、期待側はこのテスト内の表に持つ。
    // 「固定表は現行の配列と一致する」テストだけが配列を参照し、5a で配列とともに削除する。

    private sealed record Exp(CleanupTargetType Type, string Path, string? Pattern = null);

    private static Exp Contents(string path) => new(CleanupTargetType.Contents, path);

    private static readonly (string Path, string Group)[] AppCache =
    [
        (@"%APPDATA%\Antigravity\Cache", "Antigravity"), (@"%APPDATA%\Antigravity\CachedData", "Antigravity"),
        (@"%APPDATA%\Antigravity\Crashpad", "Antigravity"), (@"%APPDATA%\Aqua Voice\Cache", "Aqua Voice"),
        (@"%APPDATA%\Claude\Cache", "Claude"), (@"%APPDATA%\Claude\Code Cache", "Claude"), (@"%APPDATA%\Claude\logs", "Claude"),
        (@"%APPDATA%\Cursor\Cache", "Cursor"), (@"%APPDATA%\Cursor\CachedData", "Cursor"), (@"%APPDATA%\Cursor\GPUCache", "Cursor"),
        (@"%APPDATA%\discord\Cache", "Discord"), (@"%APPDATA%\discord\Code Cache", "Discord"),
        (@"%APPDATA%\discord\GPUCache", "Discord"), (@"%APPDATA%\discord\VideoDecodeStats", "Discord"),
        (@"%LOCALAPPDATA%\AMD\DxCache", "AMD"), (@"%LOCALAPPDATA%\AMD\DxcCache", "AMD"), (@"%LOCALAPPDATA%\AMD\VkCache", "AMD"),
        (@"%LOCALAPPDATA%\Ati\GLCache", "AMD"), (@"%LOCALAPPDATA%\NVIDIA\DXCache", "NVIDIA"),
        (@"%ProgramData%\LGHUB\cache", "Logitech G HUB"),
        (@"%USERPROFILE%\AppData\LocalLow\NVIDIA\PerDriverVersion\DXCache", "NVIDIA"),
        (@"%USERPROFILE%\.claude\debug", "Claude"), (@"%USERPROFILE%\.claude\telemetry", "Claude"),
        (@"%USERPROFILE%\.claude-mem\logs", "Claude"),
    ];

    private static readonly (string Path, string Group)[] PackageCache =
    [
        (@"%LOCALAPPDATA%\pip\Cache", "pip"), (@"%LOCALAPPDATA%\Yarn\Cache", "Yarn"), (@"%LOCALAPPDATA%\NuGet\v3-cache", "NuGet"),
        (@"%LOCALAPPDATA%\npm-cache\_logs", "npm"), (@"%USERPROFILE%\.npm\_cacache", "npm"), (@"%USERPROFILE%\.npm\_logs", "npm"),
        (@"%USERPROFILE%\.nuget\packages", "NuGet"), (@"%USERPROFILE%\.gradle\caches", "Gradle"),
        (@"%USERPROFILE%\.bun\install\cache", "Bun"), (@"%LOCALAPPDATA%\npm-cache\_cacache", "npm"),
        (@"%LOCALAPPDATA%\pnpm\store", "pnpm"), (@"%LOCALAPPDATA%\uv\cache", "uv"), (@"%USERPROFILE%\.cargo\registry", "Cargo"),
    ];

    private static readonly string[] UserTemp =
    [
        @"%LOCALAPPDATA%\Temp", @"%LOCALAPPDATA%\CrashDumps", @"%LOCALAPPDATA%\D3DSCache",
        @"%LOCALAPPDATA%\Microsoft\Windows\INetCache", @"%LOCALAPPDATA%\Microsoft\Windows\Temporary Internet Files",
        @"%LOCALAPPDATA%\Microsoft\Windows\AppCache", @"%LOCALAPPDATA%\Microsoft\IME\15.0\IMEJP\Cache",
        @"%LOCALAPPDATA%\Microsoft\IME\15.0\IMEJP\Watson", @"%LOCALAPPDATA%\Microsoft\Office\16.0\Wef",
        @"%LOCALAPPDATA%\Microsoft\Office\SolutionPackages", @"%LOCALAPPDATA%\Microsoft\Outlook\HubAppFileCache",
        @"%USERPROFILE%\AppData\LocalLow\Microsoft\CryptnetUrlCache",
    ];

    private static readonly string[] SystemTemp =
    [
        @"%SystemRoot%\Temp", @"%SystemRoot%\SystemTemp", @"%ProgramData%\USOShared\Logs", @"%ProgramData%\Microsoft\EdgeUpdate\Log",
        @"%SystemRoot%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache",
        @"%ProgramData%\Microsoft\Windows Defender\Support",
    ];

    /// <summary>ETL を列挙する基点 (現行は基点とその全下位フォルダーの直下の *.etl)。</summary>
    private static readonly string[] EtlRoots =
    [
        @"%SystemRoot%\Logs", @"%SystemRoot%\System32\LogFiles", @"%SystemRoot%\Panther", @"%ProgramData%\Microsoft\Diagnosis\ETLLogs",
    ];

    private const string WindowsUpdateDownload = @"%SystemRoot%\SoftwareDistribution\Download";

    private static readonly string[] BrowserRootsFixed =
    [
        @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser", @"%LOCALAPPDATA%\Microsoft\Edge", @"%LOCALAPPDATA%\Google\Chrome",
        @"%LOCALAPPDATA%\Google\Chrome Beta", @"%LOCALAPPDATA%\Google\Chrome Dev", @"%LOCALAPPDATA%\Vivaldi",
        @"%LOCALAPPDATA%\Perplexity\Comet",
    ];

    private static readonly string[] BrowserShared =
    [
        "Temp", @"User Data\Safe Browsing", @"User Data\CertificateRevocation", @"User Data\optimization_guide_model_store",
        @"User Data\BrowserMetrics", @"User Data\component_crx_cache", @"User Data\Crashpad", @"User Data\extensions_crx_cache",
        @"User Data\GraphiteDawnCache", @"User Data\GrShaderCache", @"User Data\ShaderCache",
    ];

    private static readonly string[] BrowserProfile =
    [
        "Cache", "Code Cache", @"Service Worker\CacheStorage", @"Service Worker\ScriptCache", "GPUCache",
        "JumpListIconsRecentClosed", "JumpListIconsTopSites", "DawnGraphiteCache", "DawnWebGPUCache", "Shared Dictionary",
        "CRXTelemetry", "VideoDecodeStats", "blob_storage", "Media Cache", "Reporting and NEL", "CertificateTransparency", "DawnCache",
    ];

    private static List<string> BrowserExpected() =>
    [
        .. BrowserRootsFixed.SelectMany(root => BrowserShared.Select(rel => $@"{root}\{rel}")
            .Concat(FakeChromiumProfiles.SelectMany(profile => BrowserProfile.Select(rel => $@"{root}\User Data\{profile}\{rel}")))),
    ];

    private static List<Exp> AllExpected() =>
    [
        .. AppCache.Select(a => Contents(a.Path)),
        .. PackageCache.Select(a => Contents(a.Path)),
        .. UserTemp.Select(Contents),
        .. SystemTemp.Select(Contents),
        .. EtlRoots.Select(r => new Exp(CleanupTargetType.Files, r, "*.etl")),
        Contents(WindowsUpdateDownload),
        new Exp(CleanupTargetType.Files, @"%LOCALAPPDATA%", "IconCache.db"),
        .. BrowserExpected().Select(Contents),
    ];

    private static string Describe(IEnumerable<string> paths) => string.Join("\n  ", paths.Order(Ignore));

    /// <summary>規則側のパスは途中の * を含みうる (セグメント単位で照合する)。</summary>
    private static bool PathMatches(string rulePath, string path)
    {
        var a = rulePath.Split('\\');
        var b = path.Split('\\');
        return a.Length == b.Length && a.Zip(b).All(p => FileSystemName.MatchesSimpleExpression(p.First, p.Second, ignoreCase: true));
    }

    private static bool Covers(IEnumerable<CleanupRule> rules, Exp expected) =>
        rules.SelectMany(r => r.Targets).Any(t =>
            t.Type == expected.Type && t.Path is not null && t.OlderThanDays is null
            && string.Equals(t.Pattern, expected.Pattern, StringComparison.OrdinalIgnoreCase)
            && PathMatches(t.Path, expected.Path));

    /// <summary>期待する対象のうち、規則のどれにも含まれないもの。</summary>
    private static List<string> Missing(IEnumerable<CleanupRule> rules, IEnumerable<Exp> expected) =>
        [.. expected.Where(e => !Covers(rules, e)).Select(e => e.Pattern is null ? e.Path : $@"{e.Path}\{e.Pattern}").Distinct(Ignore)];

    [TestMethod]
    public void 現行のアプリキャッシュとパッケージキャッシュの対象が全定義に脱落なく含まれlegacyキーが現行の保存キーと一致する()
    {
        var rules = Catalog();
        var expanded = CleanupEngineExpander.ExpandAll(rules, FakeDisk(rules)).Valid;

        var appMissing = Missing(expanded, AppCache.Select(a => Contents(a.Path)));
        var packageMissing = Missing(expanded, PackageCache.Select(a => Contents(a.Path)));
        Assert.AreEqual(0, appMissing.Count, $"app-cache の脱落:\n  {Describe(appMissing)}");
        Assert.AreEqual(0, packageMissing.Count, $"package-cache の脱落:\n  {Describe(packageMissing)}");

        AssertLegacyKeys(expanded, "cleanup-app-cache", AppCache);
        AssertLegacyKeys(expanded, "cleanup-package-cache", PackageCache);
    }

    /// <summary>対象を含む規則 (旧項目を legacy に持つもの) が、現行セレクターが返すグループの保存キーを legacy に持つこと。</summary>
    private static void AssertLegacyKeys(IEnumerable<CleanupRule> rules, string itemId, (string Path, string Group)[] targets)
    {
        var list = rules.ToList();
        foreach (var (path, group) in targets)
        {
            var owners = list.Where(r => r.Targets.Any(t => t.Type == CleanupTargetType.Contents && t.Pattern is null && t.Path is not null && PathMatches(t.Path, path))
                                         && r.Legacy.Any(l => l.StartsWith(itemId + "|", StringComparison.Ordinal))).ToList();
            foreach (var owner in owners)
            {
                // パス単位の旧キーは、移行が規則の対象パス (生パス) と直接照合するため、グループキーだけを必須にする。
                CollectionAssert.Contains(owner.Legacy.ToList(), $"{itemId}|{group}", $"{owner.Id}: グループキーがありません");
            }
        }
    }

    [TestMethod]
    public void 現行のブラウザーキャッシュの対象が全定義に脱落なく含まれlegacyキーが現行の保存キーと一致する()
    {
        var rules = Catalog();
        var expanded = CleanupEngineExpander.ExpandAll(rules, FakeDisk(rules)).Valid;

        var missing = Missing(expanded, BrowserExpected().Select(Contents));
        Assert.AreEqual(0, missing.Count, $"browser-cache の脱落:\n  {Describe(missing)}");

        foreach (var root in BrowserRootsFixed)
        {
            // 根拠の無い固定対象は製品規則から分けた継承規則 (-shared / -rebuild) が同じキーを引き継ぐ。本体の規則は1件だけ。
            var owners = expanded.Where(r => r.Legacy.Contains($"cleanup-browser-cache|{root}")).ToList();
            var bodies = owners.Where(r => !r.Id.EndsWith("-shared", StringComparison.Ordinal) && !r.Id.EndsWith("-rebuild", StringComparison.Ordinal)).ToList();
            Assert.AreEqual(1, bodies.Count, $"{root}: legacy キーを持つ本体の規則が1件ではありません");
            Assert.AreEqual(CleanupKind.Cache, bodies[0].Kind);
            Assert.IsTrue(bodies[0].Recommended, "ブラウザーキャッシュは既定オン (junk) です");
            foreach (var split in owners.Except(bodies))
            {
                Assert.IsTrue(split.Id.StartsWith("legacy.", StringComparison.Ordinal), $"{split.Id}: 分けた規則は継承 (legacy.) です");
                Assert.AreEqual(split.Id.EndsWith("-rebuild", StringComparison.Ordinal), !split.Recommended, $"{split.Id}: -rebuild だけが既定オフです");
                Assert.AreEqual(split.Id.EndsWith("-rebuild", StringComparison.Ordinal) ? CleanupImpact.Rebuild : CleanupImpact.Junk, split.Impact, split.Id);
            }
        }
    }

    [TestMethod]
    public void 現行のWindows側3グループの対象は全定義の合算で照合し保留はWindowsUpdateだけ()
    {
        var rules = Catalog();
        var expanded = CleanupEngineExpander.ExpandAll(rules, FakeDisk(rules)).Valid;

        // Windows Update の Download はスリム化 (wuauserv・bits を止めて消す) が受け持つため、規則へは移さない。
        var expected = UserTemp.Select(Contents)
            .Concat(SystemTemp.Select(Contents))
            .Concat(EtlRoots.Select(r => new Exp(CleanupTargetType.Files, r, "*.etl")))
            .Append(Contents(WindowsUpdateDownload));
        var missing = Missing(expanded, expected);

        CollectionAssert.AreEqual(new[] { WindowsUpdateDownload }, missing, $"Windows Update 以外の脱落:\n  {Describe(missing)}");
    }

    [TestMethod]
    public void 既存対象の継承Lの規則は現行の対象表の外へ広げられない()
    {
        var rules = Catalog();
        var expected = AllExpected();
        var legacy = rules.Where(r => r.Evidence.Any(e => e.Tier == CleanupEvidenceTier.L)).ToList();
        Assert.IsTrue(legacy.Count >= 19, "継承規則が読めていません");

        foreach (var rule in legacy)
        {
            Assert.IsTrue(rule.Id.StartsWith("legacy.", StringComparison.Ordinal) && rule.Legacy.Count > 0, $"{rule.Id}: 継承の規則は legacy. で始まり legacy を持ちます");
            foreach (var target in rule.Targets)
            {
                var known = expected.Any(e => e.Type == target.Type && string.Equals(e.Pattern, target.Pattern, StringComparison.OrdinalIgnoreCase)
                                              && target.Path is not null && PathMatches(target.Path, e.Path));
                Assert.IsTrue(known, $"{rule.Id}: {target.Path} {target.Pattern} は現行の対象ではありません");
            }
        }
    }

    [TestMethod]
    public void 実行中ブラウザーの確認に使うプロセス名が全製品と派生規則に付き継承のCometだけ空()
    {
        var rules = Catalog();
        var expanded = CleanupEngineExpander.ExpandAll(rules, FakeDisk(rules)).Valid;
        var expected = new Dictionary<string, string[]>(Ignore)
        {
            ["browser.chrome"] = ["chrome"], ["browser.edge"] = ["msedge"], ["browser.brave"] = ["brave"], ["browser.vivaldi"] = ["vivaldi"],
            ["browser.firefox"] = ["firefox"], ["browser.yandex"] = ["browser"],
            ["browser.chromium"] = ["chrome"], ["legacy.browser-comet"] = [],
            ["browser.chrome-beta"] = ["chrome"], ["browser.chrome-dev"] = ["chrome"],
            ["browser.opera"] = ["opera"], ["browser.opera-gx"] = ["opera"],
        };

        foreach (var (product, processes) in expected)
        {
            var family = expanded.Where(r => r.Id == product || r.Id.StartsWith(product + ".", StringComparison.Ordinal)).ToList();
            Assert.IsTrue(family.Count > 0, $"{product}: 規則がありません");
            foreach (var rule in family)
            {
                CollectionAssert.AreEqual(processes, rule.Processes.ToArray(), $"{rule.Id}: processes が違います");
            }
        }
    }
}
