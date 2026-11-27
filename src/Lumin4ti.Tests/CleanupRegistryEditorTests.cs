using Lumin4ti.Core.Services.Windows.Cleanup;

namespace Lumin4ti.Tests;

/// <summary>実レジストリは触らず、偽のキー木 (ハンドルを模した <see cref="ICleanupRegistryKey"/>) に対して検証する。</summary>
[TestClass]
public sealed class CleanupRegistryEditorTests
{
    private const string Key = @"HKCU\Software\Vendor\App";

    private sealed class FakeNode(string name)
    {
        public string Name { get; } = name;

        public bool IsLink { get; init; }

        public bool Deleted { get; set; }

        public HashSet<string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, FakeNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> DenyDelete { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FakeNode? Parent { get; private set; }

        public FakeNode Add(string child, bool link = false)
        {
            var node = new FakeNode(child) { IsLink = link, Parent = this };
            Children[child] = node;
            return node;
        }
    }

    private sealed class FakeKey(FakeNode node, List<string> log) : ICleanupRegistryKey
    {
        public FakeNode Node => node;

        public ICleanupRegistryKey? OpenChild(string name, CleanupRegistryAccess access)
        {
            if (name.Contains('\\')) throw new ArgumentException("1 階層だけ", nameof(name));
            if (!node.Children.TryGetValue(name, out var child)) return null;
            if (child.IsLink) throw new CleanupRegistryLinkException(name);
            log.Add($"open:{name}:{access}");
            return new FakeKey(child, log);
        }

        public IReadOnlyList<string> GetSubKeyNames() => node.Children.Keys.ToArray();

        public IReadOnlyList<string> GetValueNames() => node.Values.ToArray();

        public void DeleteValue(string name)
        {
            if (node.DenyDelete.Contains(name)) throw new UnauthorizedAccessException("denied");
            node.Values.Remove(name);
            log.Add($"delvalue:{node.Name}:{name}");
        }

        public void DeleteSelf()
        {
            if (node.Children.Count > 0) throw new IOException("サブキーが残っています");
            node.Deleted = true;
            node.Parent?.Children.Remove(node.Name);
            log.Add($"delkey:{node.Name}");
        }

        public void Dispose()
        {
        }
    }

    private static (FakeNode Hive, FakeNode App, CleanupRegistryEditor Editor, List<string> Log) Create()
    {
        var hive = new FakeNode("HKCU");
        var app = hive.Add("Software").Add("Vendor").Add("App");
        var log = new List<string>();
        return (hive, app, new CleanupRegistryEditor(new FakeKey(hive, log)), log);
    }

    [TestMethod]
    public void ParseKeyAcceptsOnlyHkcuSoftwareTwoLevelsDeep()
    {
        CollectionAssert.AreEqual(new[] { "Software", "A", "B" }, CleanupRegistryEditor.ParseKey(@"hkcu\Software\A\B"));
        foreach (var bad in new[] { @"HKCU\Software\A", @"HKLM\Software\A\B", @"HKCU\Other\A\B", @"HKCU\Software\A\", @"HKCU\Software\A\*", "", @"HKCU\Software\ A\B" })
            Assert.IsNull(CleanupRegistryEditor.ParseKey(bad), bad);
    }

    [TestMethod]
    public void ScanCountsTreeWithoutChangingIt()
    {
        var (_, app, editor, log) = Create();
        app.Values.Add("a");
        app.Add("Sub").Values.UnionWith(["x", "y"]);
        app.Children["Sub"].Add("Deep").Values.Add("z");

        var result = editor.Scan(Key, null, CancellationToken.None);

        Assert.AreEqual(CleanupRegistryStatus.Done, result.Status);
        Assert.AreEqual(4, result.Values);
        Assert.AreEqual(2, result.Keys);
        Assert.IsFalse(log.Any(entry => entry.StartsWith("del", StringComparison.Ordinal)));
        Assert.IsTrue(log.All(entry => entry.EndsWith(":Read", StringComparison.Ordinal)), "走査は読取権限だけで開く");
        Assert.AreEqual(2, app.Children["Sub"].Values.Count);
    }

    [TestMethod]
    public void ScanCountsOnlyMatchingValuesForRegistryValues()
    {
        var (_, app, editor, _) = Create();
        app.Values.UnionWith(["MRU1", "MRU2", "Keep", "mru"]);

        var result = editor.Scan(Key, ["MRU*"], CancellationToken.None);

        Assert.AreEqual(3, result.Values);
        Assert.AreEqual(4, app.Values.Count);
    }

    [TestMethod]
    public void DeleteValuesRemovesMatchesAndKeepsTheKeyAndSubKeys()
    {
        var (_, app, editor, log) = Create();
        app.Values.UnionWith(["MRU1", "MRU2", "Keep", "Exact"]);
        app.Add("Child").Values.Add("MRU9");

        var result = editor.DeleteValues(Key, ["MRU*", "exact"], CancellationToken.None);

        Assert.AreEqual(CleanupRegistryStatus.Done, result.Status);
        Assert.AreEqual(3, result.Values);
        CollectionAssert.AreEquivalent(new[] { "Keep" }, app.Values.ToArray());
        Assert.IsTrue(app.Children["Child"].Values.Contains("MRU9"), "直下の値だけが対象");
        Assert.IsFalse(log.Any(entry => entry.StartsWith("delkey", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void DeleteValuesRejectsInvalidPatternsAndKeys()
    {
        var (_, app, editor, _) = Create();
        app.Values.Add("MRU1");
        foreach (var patterns in new[] { new[] { "*" }, new[] { "A*B" }, new[] { "" }, Array.Empty<string>() })
            Assert.AreEqual(CleanupRegistryStatus.Rejected, editor.DeleteValues(Key, patterns, CancellationToken.None).Status);
        Assert.AreEqual(CleanupRegistryStatus.Rejected, editor.DeleteValues(@"HKCU\Software\Vendor", ["MRU1"], CancellationToken.None).Status);
        Assert.AreEqual(1, app.Values.Count);
    }

    [TestMethod]
    public void DeleteTreeRemovesAllValuesAndSubKeysButKeepsTheKey()
    {
        var (hive, app, editor, _) = Create();
        app.Values.UnionWith(["a", "b"]);
        var sub = app.Add("Sub");
        sub.Values.Add("x");
        sub.Add("Deep").Values.Add("z");

        var result = editor.DeleteTree(Key, CancellationToken.None);

        Assert.AreEqual(CleanupRegistryStatus.Done, result.Status);
        Assert.AreEqual(4, result.Values);
        Assert.AreEqual(2, result.Keys);
        Assert.AreEqual(0, app.Values.Count);
        Assert.AreEqual(0, app.Children.Count);
        Assert.IsFalse(app.Deleted, "キー自体は残す");
        Assert.IsTrue(hive.Children["Software"].Children["Vendor"].Children.ContainsKey("App"));
    }

    [TestMethod]
    public void DeleteTreeKeepsLinkSubKeysAndTheirTargets()
    {
        var (_, app, editor, _) = Create();
        app.Values.Add("a");
        var link = app.Add("Link", link: true);
        link.Values.Add("target-value");
        var normal = app.Add("Normal");
        normal.Values.Add("n");

        var result = editor.DeleteTree(Key, CancellationToken.None);

        Assert.AreEqual(CleanupRegistryStatus.Partial, result.Status);
        Assert.AreEqual(1, result.Skipped);
        Assert.IsTrue(app.Children.ContainsKey("Link"));
        Assert.IsFalse(link.Deleted);
        Assert.IsTrue(link.Values.Contains("target-value"));
        Assert.IsTrue(normal.Deleted);
        StringAssert.Contains(result.Examples[0], "Link");
    }

    [TestMethod]
    public void LinkOnTheWayRejectsWithoutChangingAnything()
    {
        var hive = new FakeNode("HKCU");
        var vendor = hive.Add("Software").Add("Vendor");
        var link = vendor.Add("App", link: true);
        link.Values.Add("v");
        var editor = new CleanupRegistryEditor(new FakeKey(hive, []));

        var result = editor.DeleteTree(Key, CancellationToken.None);

        Assert.AreEqual(CleanupRegistryStatus.Rejected, result.Status);
        Assert.AreEqual(1, link.Values.Count);
    }

    [TestMethod]
    public void MissingKeyIsReportedAsMissing()
    {
        var (_, _, editor, _) = Create();
        Assert.AreEqual(CleanupRegistryStatus.Missing, editor.DeleteTree(@"HKCU\Software\Vendor\Other", CancellationToken.None).Status);
        Assert.AreEqual(CleanupRegistryStatus.Missing, editor.Scan(@"HKCU\Software\Nothing\Other", null, CancellationToken.None).Status);
    }

    [TestMethod]
    public void DeniedValueDeletionIsPartialAndKeepsTheParentKeyChain()
    {
        var (_, app, editor, _) = Create();
        var sub = app.Add("Sub");
        sub.Values.UnionWith(["ok", "locked"]);
        sub.DenyDelete.Add("locked");

        var result = editor.DeleteTree(Key, CancellationToken.None);

        Assert.AreEqual(CleanupRegistryStatus.Partial, result.Status);
        Assert.IsTrue(app.Children.ContainsKey("Sub"), "残りがあるサブキーは消さない");
        CollectionAssert.AreEquivalent(new[] { "locked" }, sub.Values.ToArray());
        Assert.AreEqual(1, result.Values);
    }

    [TestMethod]
    public void ValuesAreDeletedOnTheHandleOpenedBeforeAReplacement()
    {
        var hive = new FakeNode("HKCU");
        var app = hive.Add("Software").Add("Vendor").Add("App");
        app.Values.Add("MRU1");
        var replacement = new FakeNode("App") { IsLink = true };
        replacement.Values.Add("MRU1");
        var log = new List<string>();
        var root = new SwappingKey(new FakeKey(hive, log), () => hive.Children["Software"].Children["Vendor"].Children["App"] = replacement);
        var editor = new CleanupRegistryEditor(root);

        var result = editor.DeleteValues(Key, ["MRU1"], CancellationToken.None);

        Assert.AreEqual(CleanupRegistryStatus.Done, result.Status);
        Assert.AreEqual(0, app.Values.Count, "開いたハンドルのキーだけを変更する");
        Assert.AreEqual(1, replacement.Values.Count, "後から差し替わったものには触らない");
    }

    [TestMethod]
    public void CancellationStopsBeforeAnyDeletion()
    {
        var (_, app, editor, _) = Create();
        app.Values.Add("a");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => editor.DeleteTree(Key, cts.Token));
        Assert.AreEqual(1, app.Values.Count);
    }

    /// <summary>最初に開かれた直後に差替えを実行する (パスの再解決で別物を開くと作用先がずれる状況を再現する)。</summary>
    private sealed class SwappingKey(FakeKey inner, Action swap) : ICleanupRegistryKey
    {
        private bool _swapped;

        public ICleanupRegistryKey? OpenChild(string name, CleanupRegistryAccess access)
        {
            var child = inner.OpenChild(name, access);
            if (child is null) return null;
            return new SwappingKey((FakeKey)child, swap) { _swapped = _swapped };
        }

        public IReadOnlyList<string> GetSubKeyNames() => inner.GetSubKeyNames();

        public IReadOnlyList<string> GetValueNames() => inner.GetValueNames();

        public void DeleteValue(string name)
        {
            if (!_swapped) { _swapped = true; swap(); }
            inner.DeleteValue(name);
        }

        public void DeleteSelf() => inner.DeleteSelf();

        public void Dispose() => inner.Dispose();
    }
}
