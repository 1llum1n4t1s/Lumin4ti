using System.Diagnostics;
using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.Core.Services.Windows.Cleanup.Handlers;

namespace Lumin4ti.Tests;

/// <summary>走査・実行テストの共通基盤。環境変数・OS 境界を一時領域と偽の実装に差し替え、実機を変えない。</summary>
internal sealed class CleanupHarness : IDisposable
{
    public CleanupHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "Lumin4tiCleanupService", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "Local"));
        Directory.CreateDirectory(Path.Combine(Root, "Roaming"));
        Directory.CreateDirectory(Path.Combine(Root, "ProgramData"));
        Directory.CreateDirectory(Path.Combine(Root, "Settings"));
        Env["LOCALAPPDATA"] = Path.Combine(Root, "Local");
        Env["APPDATA"] = Path.Combine(Root, "Roaming");
        Env["PROGRAMDATA"] = Path.Combine(Root, "ProgramData");
        Service = new SettingsService(Path.Combine(Root, "Settings"), Path.Combine(Root, "Settings", "settings.json"));
        Store = new CleanupSettingsStore(Service);
        Guard = new CustomFolderGuardContext(@"C:\Windows", [@"C:\Program Files"], @"C:\ProgramData", [], null);
        RegistryHive.Add("Software").Add("Vendor").Add("App");
    }

    public string Root { get; }

    public Dictionary<string, string?> Env { get; } = new(StringComparer.OrdinalIgnoreCase);

    public SettingsService Service { get; }

    public CleanupSettingsStore Store { get; }

    public List<CleanupRule> Rules { get; } = [];

    public FakeProbe Probe { get; } = new();

    public InteractiveScopeResult Scope { get; set; } = new(true, InteractiveScopeReason.None);

    public Dictionary<CleanupHandlerKind, ICleanupHandler> Handlers { get; } = [];

    public FakeRegistryNode RegistryHive { get; } = new("HKCU");

    public List<string> RegistryLog { get; } = [];

    public FakeDatabases Databases { get; } = new();

    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

    public CustomFolderGuardContext Guard { get; set; }

    /// <summary>基点の信頼の根拠。プロファイルは一時領域、機械側の変数は注入した値と一致するものとして扱い、実機の環境に依存しない。</summary>
    public CleanupBaseTrust Trust => new(
        new CustomFolderGuardContext(@"C:\Windows", [@"C:\Program Files"], @"C:\ProgramData", [], Root),
        name => Env.GetValueOrDefault(name));

    public CleanupResolverHandle NewResolver() => new(new CleanupTargetResolver(name => Env.GetValueOrDefault(name), Probe, Trust));

    public CleanupScanService NewScan() => new(new CleanupParts(
        Store,
        () => Rules,
        new CleanupTargetResolver(name => Env.GetValueOrDefault(name), Probe, Trust),
        () => Scope,
        Handlers,
        () => new FakeRegistryKey(RegistryHive, RegistryLog),
        new CleanupIniEditor(),
        Databases,
        () => Running,
        () => Guard));

    public string Full(string relative) => Path.Combine(Root, relative);

    public string Write(string relative, string content = "x")
    {
        var full = Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>一覧と、ファイルの作成・更新・アクセス時刻・サイズ。フォルダーの時刻は NTFS の反映が遅れるため比較しない。</summary>
    public string[] Snapshot() =>
        new DirectoryInfo(Root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
            .Where(entry => !entry.FullName.StartsWith(Path.Combine(Root, "Settings"), StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
            .Select(entry => $"{entry.FullName}|{entry.Attributes}|{(entry is FileInfo ? entry.CreationTimeUtc.Ticks : 0)}|{(entry is FileInfo ? entry.LastWriteTimeUtc.Ticks : 0)}|{(entry as FileInfo)?.Length}")
            .ToArray();

    public static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, process.StandardError.ReadToEnd());
    }

    public void Dispose()
    {
        if (!Directory.Exists(Root)) return;
        var entries = new DirectoryInfo(Root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories).ToList();
        var links = entries.Where(e => (e.Attributes & FileAttributes.ReparsePoint) != 0).Select(e => e.FullName).ToList();
        foreach (var link in links)
        {
            // 結合点は先に外す (リンク先の中身を消さない)。
            Directory.Delete(link);
        }

        foreach (var entry in entries.Where(e => !links.Any(l => e.FullName.StartsWith(l, StringComparison.OrdinalIgnoreCase)) && Path.Exists(e.FullName)))
        {
            entry.Attributes = FileAttributes.Normal;
        }

        Directory.Delete(Root, recursive: true);
    }

    // ----- 規則の組み立て -----

    public static CleanupRuleTarget Contents(string path) => new() { Type = CleanupTargetType.Contents, Path = path };

    public static CleanupRule MakeRule(
        string id,
        CleanupRuleTarget[] targets,
        string detect = @"%LOCALAPPDATA%\Test",
        CleanupCategory category = CleanupCategory.Apps,
        CleanupImpact impact = CleanupImpact.Junk,
        bool recommended = true,
        CleanupScope scope = CleanupScope.User,
        string[]? processes = null,
        string[]? services = null) => new()
    {
        Id = id,
        Name = id,
        Kind = CleanupKind.Cache,
        Category = category,
        Scope = scope,
        Recommended = recommended,
        Impact = impact,
        Detect = [new CleanupDetect { Path = detect }],
        Targets = [.. targets],
        Processes = [.. processes ?? []],
        Services = [.. services ?? []],
        Evidence = [new CleanupEvidence { Tier = CleanupEvidenceTier.A, Url = "https://example.com/doc", Checked = "2026-11-25" }],
    };

    public static CleanupRule HandlerRule(string id, CleanupHandlerKind kind, CleanupScope scope = CleanupScope.Machine) => MakeRule(
        id,
        [new CleanupRuleTarget { Type = CleanupTargetType.Handler, Handler = kind }],
        detect: @"%PROGRAMDATA%",
        scope: scope);
}

/// <summary>検出の境界の偽物。パスは実在を見て、レジストリ・パッケージは指定した集合で答える。</summary>
internal sealed class FakeProbe : ICleanupProbe
{
    public bool Registry { get; set; }

    public HashSet<string> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool PathExists(string fullPath) => File.Exists(fullPath) || Directory.Exists(fullPath);

    public bool RegistryMatches(CleanupDetect detect) => Registry;

    public bool PackageInstalled(string packageFamilyName) => Packages.Contains(packageFamilyName);
}

internal sealed class FakeRegistryNode(string name)
{
    public string Name { get; } = name;

    public HashSet<string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, FakeRegistryNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeRegistryNode? Parent { get; private set; }

    public FakeRegistryNode Add(string child)
    {
        var node = new FakeRegistryNode(child) { Parent = this };
        Children[child] = node;
        return node;
    }

    public FakeRegistryNode Path(params string[] names) => names.Aggregate(this, (node, child) => node.Children[child]);
}

internal sealed class FakeRegistryKey(FakeRegistryNode node, List<string> log) : ICleanupRegistryKey
{
    public ICleanupRegistryKey? OpenChild(string name, CleanupRegistryAccess access) =>
        node.Children.TryGetValue(name, out var child) ? new FakeRegistryKey(child, log) : null;

    public IReadOnlyList<string> GetSubKeyNames() => node.Children.Keys.ToArray();

    public IReadOnlyList<string> GetValueNames() => node.Values.ToArray();

    public void DeleteValue(string name)
    {
        node.Values.Remove(name);
        log.Add($"delvalue:{node.Name}:{name}");
    }

    public void DeleteSelf()
    {
        node.Parent?.Children.Remove(node.Name);
        log.Add($"delkey:{node.Name}");
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeDatabases : ICleanupDatabaseOps
{
    public DatabaseCleanResult CountResult { get; set; } = DatabaseCleanResult.Done(5);

    public DatabaseCleanResult CleanResult { get; set; } = DatabaseCleanResult.Done(5);

    public List<(string Path, string[] Domains)> Cleaned { get; } = [];

    public List<(string Path, string[] Domains)> Counted { get; } = [];

    public DatabaseCleanResult Count(CleanupRuleTarget target, string path, IReadOnlyCollection<string> excludedDomains, CancellationToken ct)
    {
        Counted.Add((path, [.. excludedDomains]));
        return CountResult;
    }

    /// <summary><see cref="Clean"/> を実行したスレッド (呼び出しスレッドを塞がないことの検査用)。</summary>
    public List<int> CleanThreads { get; } = [];

    public DatabaseCleanResult Clean(CleanupRuleTarget target, string path, IReadOnlyCollection<string> excludedDomains, CancellationToken ct)
    {
        CleanThreads.Add(Environment.CurrentManagedThreadId);
        Cleaned.Add((path, [.. excludedDomains]));
        return CleanResult;
    }
}

internal sealed class FakeHandler(string id) : ICleanupHandler
{
    public string Id { get; } = id;

    public IReadOnlyList<string> Services { get; init; } = [];

    public CleanupHandlerScan ScanResult { get; set; } = new(CleanupHandlerScanState.Found, 3, 30);

    public CleanupHandlerResult RunResult { get; set; } = new(CleanupHandlerStatus.Succeeded, 3, 30);

    public Action? OnRun { get; set; }

    public int RunCalls { get; private set; }

    public Task<CleanupHandlerScan> ScanAsync(CancellationToken ct) => Task.FromResult(ScanResult);

    public Task<CleanupHandlerResult> RunAsync(IProgress<string>? progress, CancellationToken ct)
    {
        RunCalls++;
        OnRun?.Invoke();
        return Task.FromResult(RunResult);
    }
}

/// <summary>リゾルバーの検査用の薄い包み (テストから展開結果を見る)。</summary>
internal sealed class CleanupResolverHandle(CleanupTargetResolver resolver)
{
    public ResolvedRule Resolve(CleanupRule rule) => resolver.Resolve(rule);

    public bool IsDetected(CleanupRule rule) => resolver.IsDetected(rule);
}

[TestClass]
public sealed class CleanupScanServiceTests
{
    private CleanupHarness _h = null!;

    [TestInitialize]
    public void Setup() => _h = new CleanupHarness();

    [TestCleanup]
    public void Cleanup() => _h.Dispose();

    private static CleanupRuleTarget Cache(string path = @"%LOCALAPPDATA%\Test\Cache") => CleanupHarness.Contents(path);

    private async Task<CleanupScanSnapshot> ScanAsync() => await _h.NewScan().ScanAsync(null, null, CancellationToken.None);

    // ----- 走査は何も変更しない -----

    [TestMethod]
    public async Task 走査は何も変更せず件数とサイズと代表を返す()
    {
        _h.Write(@"Local\Test\Cache\a.tmp", "12345");
        _h.Write(@"Local\Test\Cache\sub\b.tmp", "123");
        _h.Rules.Add(CleanupHarness.MakeRule("app.test", [Cache()]));
        Thread.Sleep(300);
        var before = _h.Snapshot();

        var snapshot = await ScanAsync();

        var changed = before.Except(_h.Snapshot()).Concat(_h.Snapshot().Except(before)).ToArray();
        Assert.IsEmpty(changed, "走査でファイルの一覧・時刻・サイズが変わった: " + string.Join("\n", changed));
        Assert.HasCount(1, snapshot.Items);
        var item = snapshot.Items[0];
        Assert.AreEqual(CleanupItemState.Found, item.State);
        Assert.AreEqual(2, item.Files);
        Assert.AreEqual(1, item.Directories);
        Assert.AreEqual(8, item.Bytes);
        Assert.IsTrue(item.DefaultSelected);
        Assert.IsNotEmpty(item.Representatives);
        Assert.AreEqual(8, snapshot.TotalBytes);
    }

    // ----- 展開・再検証 -----

    [TestMethod]
    public void 展開した実パスの認証情報とリンクと終端のドットを拒否または除外する()
    {
        _h.Write(@"Local\Test\p1\Cache\a.tmp", "123");
        _h.Write(@"Local\Test\p2\Cache\b.tmp", "1234");
        _h.Write(@"Local\Test\Credentials\Cache\c.tmp", "1");
        _h.Write(@"Local\Test\.ssh\Cache\d.tmp", "1");
        _h.Write(@"Local\Test\Lumin4ti\Cache\e.tmp", "1");
        _h.Write(@"Outside\Cache\f.tmp", "1");
        CleanupHarness.CreateJunction(_h.Full(@"Local\Test\link"), _h.Full("Outside"));
        var rule = CleanupHarness.MakeRule("app.test", [Cache(@"%LOCALAPPDATA%\Test\*\Cache")]);

        var resolved = _h.NewResolver().Resolve(rule);

        var accepted = resolved.Targets.Where(t => t.Rejection is null).Select(t => t.Path).ToArray();
        CollectionAssert.AreEqual(new[] { _h.Full(@"Local\Test\p1\Cache"), _h.Full(@"Local\Test\p2\Cache") }, accepted);
        var rejected = resolved.Targets.Where(t => t.Rejection is not null).Select(t => t.Path!).ToArray();
        Assert.HasCount(3, rejected);
        Assert.IsFalse(resolved.Targets.Any(t => t.Path!.Contains(@"\link\", StringComparison.OrdinalIgnoreCase)), "リンクを展開で辿った");
        Assert.IsFalse(resolved.Truncated);
    }

    [TestMethod]
    public async Task ワイルドカードの対象は拒否した分を除いて数える()
    {
        _h.Write(@"Local\Test\p1\Cache\a.tmp", "123");
        _h.Write(@"Local\Test\p2\Cache\b.tmp", "1234");
        _h.Write(@"Local\Test\Credentials\Cache\c.tmp", "1");
        _h.Rules.Add(CleanupHarness.MakeRule("app.test", [Cache(@"%LOCALAPPDATA%\Test\*\Cache")]));

        var snapshot = await ScanAsync();

        var item = snapshot.Items[0];
        Assert.AreEqual(2, item.Files);
        Assert.AreEqual(7, item.Bytes);
        Assert.IsTrue(item.Detail.Any(d => d.Contains("credential-path", StringComparison.Ordinal)), string.Join("\n", item.Detail));
    }

    [TestMethod]
    public void 展開数は上限で打ち切って明示する()
    {
        for (var index = 0; index < CleanupTargetResolver.MaxExpansions + 4; index++)
        {
            Directory.CreateDirectory(_h.Full($@"Local\Test\d{index:D4}"));
        }

        var resolved = _h.NewResolver().Resolve(CleanupHarness.MakeRule("app.test", [Cache(@"%LOCALAPPDATA%\Test\*")]));

        Assert.IsTrue(resolved.Truncated);
        Assert.HasCount(CleanupTargetResolver.MaxExpansions, resolved.Targets);
    }

    [TestMethod]
    public void 環境変数を解決できない対象は拒否する()
    {
        _h.Env["LOCALAPPDATA"] = null;
        var resolved = _h.NewResolver().Resolve(CleanupHarness.MakeRule("app.test", [Cache()]));

        Assert.HasCount(1, resolved.Targets);
        Assert.IsNotNull(resolved.Targets[0].Rejection);
    }

    [TestMethod]
    public void レジストリの検出はワイルドカードと値の一致を読み取りだけで判定する()
    {
        var probe = new SystemCleanupProbe(Environment.GetEnvironmentVariable);
        var detect = new CleanupDetect { Registry = @"HKLM\SOFTWARE\Microsoft\Windows N?\Current*", ValueName = "ProductName" };

        Assert.IsTrue(probe.RegistryMatches(detect));
        Assert.IsFalse(probe.RegistryMatches(detect with { ValueEquals = "no such product" }));
        Assert.IsFalse(probe.RegistryMatches(detect with { Registry = @"HKLM\SOFTWARE\Microsoft\Windows NT\NoSuchKey*" }));
    }

    // ----- 検出・状態 -----

    [TestMethod]
    public async Task 検出できない規則は一覧に出さず検出条件のどれかで見つかれば走査する()
    {
        _h.Write(@"Local\Test\Cache\a.tmp");
        var missing = CleanupHarness.MakeRule("app.missing", [Cache()], detect: @"%LOCALAPPDATA%\NotInstalled");
        var viaRegistry = CleanupHarness.MakeRule("app.registry", [Cache()], detect: @"%LOCALAPPDATA%\NotInstalled") with
        {
            Detect = [new CleanupDetect { Path = @"%LOCALAPPDATA%\NotInstalled" }, new CleanupDetect { Registry = @"HKCU\Software\Vendor\App" }],
        };
        var viaPackage = CleanupHarness.MakeRule("app.package", [Cache()]) with { Detect = [new CleanupDetect { Package = "Vendor.App_abcdefghijklm" }] };
        _h.Rules.AddRange([missing, viaRegistry, viaPackage]);
        _h.Probe.Packages.Add("Vendor.App_abcdefghijklm");

        var first = await ScanAsync();
        _h.Probe.Registry = true;
        var second = await ScanAsync();

        Assert.AreEqual(CleanupItemState.NotDetected, first.Items[0].State);
        Assert.AreEqual(CleanupItemState.NotDetected, first.Items[1].State);
        Assert.AreEqual(CleanupItemState.Found, first.Items[2].State);
        Assert.AreEqual(CleanupItemState.Found, second.Items[1].State);
        Assert.AreEqual(CleanupItemState.NotDetected, second.Items[0].State);
    }

    [TestMethod]
    public async Task 基点の拒否だけの規則は利用できない状態にする()
    {
        // 基点フォルダー自体を丸ごとは対象にできない (ファイル名指定ではない contents)。
        Directory.CreateDirectory(_h.Full(@"Local\Test"));
        _h.Rules.Add(CleanupHarness.MakeRule("app.test", [Cache(@"%LOCALAPPDATA%")]));

        var snapshot = await ScanAsync();

        Assert.AreEqual(CleanupItemState.Unavailable, snapshot.Items[0].State);
        Assert.AreEqual(CleanupUnavailableReason.TargetRejected, snapshot.Items[0].Reason);
        Assert.IsFalse(snapshot.Items[0].DefaultSelected);
    }

    [TestMethod]
    public async Task 除外は件数から外しRetainedに数える()
    {
        var kept = _h.Write(@"Local\Test\Cache\keep.log", "12");
        _h.Write(@"Local\Test\Cache\drop.tmp", "123");
        _h.Rules.Add(CleanupHarness.MakeRule("app.test", [Cache()]));
        _h.Store.AddExclusion(CleanupExclusionKind.File, kept);

        var item = (await ScanAsync()).Items[0];

        Assert.AreEqual(1, item.Files);
        Assert.AreEqual(1, item.Retained);
    }

    [TestMethod]
    public async Task レジストリとINIとDBの対象を数えて読み取りだけで済ませる()
    {
        var app = _h.RegistryHive.Path("Software", "Vendor", "App");
        app.Values.UnionWith(["Mru1", "Mru2", "Other"]);
        _h.Write(@"Roaming\App\app.ini", "[Recent]\r\nFile1=a\r\nFile2=b\r\n[Other]\r\nKeep=1\r\n");
        _h.Write(@"Roaming\Mozilla\Firefox\Profiles\a.default\cookies.sqlite");
        _h.Probe.Registry = true;
        _h.Store.AddExclusion(CleanupExclusionKind.CookieDomain, "example.com");
        _h.Rules.AddRange(
        [
            CleanupHarness.MakeRule("app.registry", [new CleanupRuleTarget { Type = CleanupTargetType.RegistryValues, Key = @"HKCU\Software\Vendor\App", Values = ["Mru*"] }], detect: @"%APPDATA%"),
            CleanupHarness.MakeRule("app.ini", [new CleanupRuleTarget { Type = CleanupTargetType.IniValues, Path = @"%APPDATA%\App\app.ini", IniKeys = [new CleanupIniKey { Section = "Recent", Key = "File*" }] }], detect: @"%APPDATA%"),
            CleanupHarness.MakeRule(
                "app.cookies",
                [new CleanupRuleTarget { Type = CleanupTargetType.Sqlite, Path = @"%APPDATA%\Mozilla\Firefox\Profiles\*\cookies.sqlite", Operation = CleanupSqliteOperation.GeckoCookies }],
                detect: @"%APPDATA%",
                category: CleanupCategory.Cookies,
                impact: CleanupImpact.Signout,
                recommended: false),
        ]);

        var snapshot = await ScanAsync();

        Assert.AreEqual(2, snapshot.Items[0].Items);
        Assert.AreEqual(2, snapshot.Items[1].Items);
        Assert.AreEqual(5, snapshot.Items[2].Items);
        Assert.IsEmpty(_h.RegistryLog);
        CollectionAssert.AreEqual(new[] { "example.com" }, _h.Databases.Counted.Single().Domains);
        Assert.IsEmpty(_h.Databases.Cleaned);
        Assert.IsFalse(snapshot.Items[2].DefaultSelected);
    }

    [TestMethod]
    public async Task DBが開けない対象は走査結果に理由を残し実行計画へ入れない()
    {
        _h.Write(@"Roaming\Mozilla\Firefox\Profiles\a.default\cookies.sqlite");
        _h.Databases.CountResult = DatabaseCleanResult.Partial("ロック中");
        _h.Rules.Add(CleanupHarness.MakeRule(
            "app.cookies",
            [new CleanupRuleTarget { Type = CleanupTargetType.Sqlite, Path = @"%APPDATA%\Mozilla\Firefox\Profiles\*\cookies.sqlite", Operation = CleanupSqliteOperation.GeckoCookies }],
            detect: @"%APPDATA%",
            category: CleanupCategory.Cookies,
            impact: CleanupImpact.Signout,
            recommended: false));

        var item = (await ScanAsync()).Items[0];

        Assert.AreEqual(CleanupItemState.Unavailable, item.State);
        Assert.IsTrue(item.Detail.Any(d => d.Contains("ロック中", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ハンドラーの状態を写し利用できない理由を残す()
    {
        _h.Handlers[CleanupHandlerKind.DnsCache] = new FakeHandler("dnsCache") { ScanResult = new(CleanupHandlerScanState.Unmeasurable) };
        _h.Handlers[CleanupHandlerKind.NetworkUsage] = new FakeHandler("networkUsage") { ScanResult = new(CleanupHandlerScanState.Empty, 0, 0), Services = ["DPS"] };
        _h.Handlers[CleanupHandlerKind.InvalidScheduledTask] = new FakeHandler("invalidScheduledTask") { ScanResult = new(CleanupHandlerScanState.Unavailable, Detail: "列挙に失敗") };
        _h.Rules.AddRange(
        [
            CleanupHarness.HandlerRule("win.dns", CleanupHandlerKind.DnsCache),
            CleanupHarness.HandlerRule("win.network", CleanupHandlerKind.NetworkUsage),
            CleanupHarness.HandlerRule("win.task", CleanupHandlerKind.InvalidScheduledTask),
            CleanupHarness.HandlerRule("win.missing", CleanupHandlerKind.InvalidShortcut),
        ]);

        var items = (await ScanAsync()).Items;

        Assert.AreEqual(CleanupItemState.Unmeasurable, items[0].State);
        Assert.IsTrue(items[0].DefaultSelected);
        Assert.AreEqual(CleanupItemState.Empty, items[1].State);
        CollectionAssert.AreEqual(new[] { "DPS" }, items[1].Rule.Services.ToArray());
        Assert.AreEqual(CleanupItemState.Unavailable, items[2].State);
        Assert.AreEqual(CleanupUnavailableReason.HandlerUnavailable, items[2].Reason);
        Assert.AreEqual(CleanupItemState.Unavailable, items[3].State);
    }

    // ----- 利用者スコープ -----

    [TestMethod]
    public async Task 利用者スコープが使えないときは利用者の規則とIE系ハンドラーだけ無効にする()
    {
        _h.Scope = new InteractiveScopeResult(false, InteractiveScopeReason.DifferentUser);
        _h.Write(@"Local\Test\Cache\a.tmp");
        _h.Write(@"ProgramData\Test\Cache\b.tmp");
        _h.Handlers[CleanupHandlerKind.IeHistory] = new FakeHandler("ieHistory");
        _h.Rules.AddRange(
        [
            CleanupHarness.MakeRule("app.user", [Cache()]),
            CleanupHarness.MakeRule("app.machine", [Cache(@"%PROGRAMDATA%\Test\Cache")], detect: @"%PROGRAMDATA%\Test", scope: CleanupScope.Machine),
            CleanupHarness.HandlerRule("win.ie", CleanupHandlerKind.IeHistory),
        ]);

        var snapshot = await ScanAsync();

        Assert.AreEqual(CleanupScopeStatus.DifferentUser, snapshot.UserScope);
        Assert.AreEqual(CleanupUnavailableReason.UserScopeDifferentUser, snapshot.Items[0].Reason);
        Assert.AreEqual(CleanupItemState.Found, snapshot.Items[1].State);
        Assert.AreEqual(CleanupUnavailableReason.UserScopeDifferentUser, snapshot.Items[2].Reason);
    }

    // ----- 選択の既定・カスタムフォルダー -----

    [TestMethod]
    public async Task 既定の選択は推奨と上書きに従いUserFilesとカスタムは選ばない()
    {
        foreach (var name in new[] { "a", "b", "c", "d" }) _h.Write($@"Local\Test\{name}\x.tmp");
        _h.Rules.AddRange(
        [
            CleanupHarness.MakeRule("app.rec", [Cache(@"%LOCALAPPDATA%\Test\a")]),
            CleanupHarness.MakeRule("app.off", [Cache(@"%LOCALAPPDATA%\Test\b")]),
            CleanupHarness.MakeRule("app.user-files", [Cache(@"%LOCALAPPDATA%\Test\c")], impact: CleanupImpact.UserFiles, recommended: false),
            CleanupHarness.MakeRule("app.opt-in", [Cache(@"%LOCALAPPDATA%\Test\d")], recommended: false),
        ]);
        _h.Store.SetRuleOverride("app.off", false);
        _h.Store.SetRuleOverride("app.opt-in", true);
        _h.Store.SetRuleOverride("app.user-files", true);
        Directory.CreateDirectory(_h.Full("Custom"));
        _h.Write(@"Custom\file.bin", "123");
        Assert.IsTrue(_h.Store.AddCustomFolder(_h.Full("Custom"), _h.Guard).IsAllowed);

        var snapshot = await ScanAsync();

        var selected = snapshot.Items.Where(i => i.DefaultSelected).Select(i => i.Rule.Id).ToArray();
        CollectionAssert.AreEquivalent(new[] { "app.rec", "app.opt-in" }, selected);
        // おすすめは規則の推奨 (junk かつ recommended) だけ。利用者の上書きは既定の選択にだけ効く。
        CollectionAssert.AreEquivalent(new[] { "app.rec", "app.off" }, CleanupSelection.Recommended(snapshot).ToArray());
        var all = CleanupSelection.All(snapshot);
        CollectionAssert.AreEquivalent(new[] { "app.rec", "app.off", "app.opt-in" }, all.ToArray());
        var custom = snapshot.Items.Single(i => i.Rule.IsCustom);
        Assert.AreEqual(CleanupRuleImpact.UserFiles, custom.Rule.Impact);
        Assert.AreEqual(3, custom.Bytes);
        Assert.IsFalse(all.Contains(custom.Rule.Id));
    }

    [TestMethod]
    public async Task 拒否されたカスタムフォルダーは実行時の検査で利用できない状態にする()
    {
        Directory.CreateDirectory(_h.Full("Custom"));
        Assert.IsTrue(_h.Store.AddCustomFolder(_h.Full("Custom"), _h.Guard).IsAllowed);
        _h.Guard = _h.Guard with { WindowsDirectory = _h.Root };

        var item = (await ScanAsync()).Items.Single();

        Assert.AreEqual(CleanupItemState.Unavailable, item.State);
        Assert.AreEqual(CleanupUnavailableReason.CustomFolderRejected, item.Reason);
        Assert.IsFalse(item.DefaultSelected);
    }

    // ----- 世代・取消・進捗 -----

    [TestMethod]
    public async Task 設定変更と再走査と無効化で走査結果を失効させる()
    {
        _h.Write(@"Local\Test\Cache\a.tmp");
        _h.Rules.Add(CleanupHarness.MakeRule("app.test", [Cache()]));
        var service = _h.NewScan();

        var first = await service.ScanAsync(null, null, CancellationToken.None);
        Assert.IsTrue(service.IsCurrent(first));
        _h.Store.AddExclusion(CleanupExclusionKind.Name, "*.log");
        Assert.IsFalse(service.IsCurrent(first), "設定変更で失効しなかった");

        var second = await service.ScanAsync(null, null, CancellationToken.None);
        Assert.IsTrue(service.IsCurrent(second));
        Assert.IsFalse(service.IsCurrent(first));
        service.Invalidate();
        Assert.IsFalse(service.IsCurrent(second));

        var third = await service.ScanAsync(null, null, CancellationToken.None);
        _h.Store.SecureDelete = true;
        Assert.IsFalse(service.IsCurrent(third), "安全な削除の切替で失効しなかった");
    }

    [TestMethod]
    public async Task 取消はファイルを変えずに伝え進捗を報告する()
    {
        _h.Write(@"Local\Test\Cache\a.tmp");
        _h.Rules.AddRange([CleanupHarness.MakeRule("app.a", [Cache()]), CleanupHarness.MakeRule("app.b", [Cache(@"%LOCALAPPDATA%\Test\Cache")])]);
        using var cts = new CancellationTokenSource();
        var reports = new List<CleanupScanProgress>();
        var progress = new SyncProgress<CleanupScanProgress>(report =>
        {
            reports.Add(report);
            if (report.Completed == 1) cts.Cancel();
        });
        var before = _h.Snapshot();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await _h.NewScan().ScanAsync(null, progress, cts.Token));

        Assert.IsNotEmpty(reports);
        CollectionAssert.AreEqual(before, _h.Snapshot());
    }

    [TestMethod]
    public async Task 規則を指定した走査は指定した規則だけを調べる()
    {
        _h.Write(@"Local\Test\Cache\a.tmp");
        _h.Rules.AddRange([CleanupHarness.MakeRule("app.a", [Cache()]), CleanupHarness.MakeRule("app.b", [Cache()])]);

        var snapshot = await _h.NewScan().ScanAsync(["app.b"], null, CancellationToken.None);

        Assert.HasCount(1, snapshot.Items);
        Assert.AreEqual("app.b", snapshot.Items[0].Rule.Id);
    }

    // ----- 規則の列挙元・テキスト書き出し -----

    [TestMethod]
    public void 規則の列挙元は製品定義を展開した規則を含めて検証する()
    {
        var plain = CleanupHarness.MakeRule("app.one", [Cache(@"%LOCALAPPDATA%\One")]);
        var product = CleanupHarness.MakeRule(
            "browser.test",
            [Cache(@"%LOCALAPPDATA%\Vendor\Browser\User Data\Default\Cache")],
            detect: @"%LOCALAPPDATA%\Vendor\Browser\User Data",
            category: CleanupCategory.BrowserCache) with { Engine = CleanupEngineKind.Chromium };

        var rules = CleanupRuleSource.Compose([plain, product]);

        Assert.IsTrue(rules.Any(r => r.Id == "app.one"));
        Assert.IsTrue(rules.Any(r => r.Id == "browser.test"), "製品定義と同じ id のキャッシュ規則を保つ");
        Assert.IsTrue(rules.Any(r => r.Id == "browser.test.history"));
        Assert.IsTrue(rules.Any(r => r.Id == "browser.test.cookies"));
        Assert.IsTrue(rules.All(r => r.Engine is null || r.Id.StartsWith("browser.", StringComparison.Ordinal)));
        Assert.IsNotEmpty(CleanupRuleSource.All(), "埋め込み定義から規則を読めなかった");
    }

    [TestMethod]
    public void 埋め込み規則の全てを実環境で検出と展開しても例外を出さず読み取りだけで済む()
    {
        var resolver = new CleanupTargetResolver();
        var rules = CleanupRuleSource.All();

        Assert.IsNotEmpty(rules);
        foreach (var rule in rules.Where(resolver.IsDetected))
        {
            var resolved = resolver.Resolve(rule);
            Assert.IsTrue(resolved.Targets.All(t => t.Path is null || Path.IsPathFullyQualified(t.Path)), rule.Id);
        }
    }

    [TestMethod]
    public async Task テキスト書き出しは1行1規則で制御文字を空白にする()
    {
        _h.Write(@"Local\Test\Cache\a.tmp", "12");
        _h.Rules.Add(CleanupHarness.MakeRule("app.test", [Cache()]) with { Name = "Name\twith\ttab" });

        var text = CleanupResultsText.FromScan(await ScanAsync());

        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(3, lines);
        Assert.AreEqual(8, lines[1].Split('\t').Length);
        StringAssert.Contains(lines[1], "Name with tab");
        var path = _h.Full("export.txt");
        CleanupResultsText.Save(path, text);
        Assert.AreEqual(text, File.ReadAllText(path));
        Assert.Throws<ArgumentException>(() => CleanupResultsText.Save("relative.txt", text));
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
