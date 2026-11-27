using System.Security.Principal;
using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class RegistryBackupTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string RunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    [TestMethod]
    public void 保存した文書を読み込むと値とサブツリーが往復する()
    {
        var store = new RegistryBackupStore(new MemoryFiles());
        var node = new RegistryBackupKeyNode(
            [new RegistryBackupValue("", 1, Convert.ToBase64String([1, 2, 3])), new RegistryBackupValue("Path", 2, "")],
            [new RegistryBackupSubKey("Child", new RegistryBackupKeyNode([], []))]);
        var document = Document(
            ValueItem(RunPath, "Tool", 1, [0x41, 0, 0, 0]),
            KeyItem(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\gone.exe", node));

        var id = store.Save(document);
        var loaded = store.Load(id);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(Sid, loaded.UserSid);
        Assert.AreEqual(document.CreatedUtc, loaded.CreatedUtc);
        Assert.HasCount(2, loaded.Items);
        Assert.AreEqual("Tool", loaded.Items[0].ValueName);
        Assert.AreEqual(Convert.ToBase64String([0x41, 0, 0, 0]), loaded.Items[0].DataBase64);
        Assert.AreEqual(RegistryCleanCategory.AppPaths, loaded.Items[1].Category);
        Assert.AreEqual("", loaded.Items[1].Subtree!.Values[0].Name);
        Assert.AreEqual("Child", loaded.Items[1].Subtree!.SubKeys[0].Name);
        CollectionAssert.AreEqual(new[] { id }, store.List().ToArray());
        Assert.IsNull(store.Load(NewId()));
    }

    [TestMethod]
    public void 保存形式はキャメルケースと文字列の列挙で版を持つ()
    {
        var files = new MemoryFiles();
        var id = new RegistryBackupStore(files).Save(Document(ValueItem(RunPath, "Tool", 1, [1])));

        var json = files.Files[Path.Combine("registry", id + ".json")];
        StringAssert.Contains(json, "\"formatVersion\":1");
        StringAssert.Contains(json, "\"category\":\"Startup\"");
        StringAssert.Contains(json, "\"hive\":\"LocalMachine\"");
        StringAssert.Contains(json, "\"dataBase64\":\"AQ==\"");
    }

    [TestMethod]
    [DataRow(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false, true)]
    [DataRow(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", false, true)]
    [DataRow(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32", false, true)]
    [DataRow(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", false, false)]
    [DataRow(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run\..\RunOnce", false, false)]
    [DataRow(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run\", false, false)]
    [DataRow(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run\\X", false, false)]
    [DataRow(@"SOFTWARE\Evil\Run", false, false)]
    [DataRow(@"SYSTEM\CurrentControlSet\Services", false, false)]
    [DataRow(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true, false)]
    [DataRow(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run\Entry", true, true)]
    public void スタートアップのパスは許可表の根の配下だけ通す(string path, bool wholeKey, bool expected) =>
        Assert.AreEqual(expected, RegistryBackupValidator.IsAllowedPath(RegistryCleanCategory.Startup, RegistryCleanHive.LocalMachine, path, wholeKey));

    [TestMethod]
    public void 関連付けはUserChoiceと拡張子以外のOpenWithProgidsを拒否する()
    {
        const RegistryCleanCategory fa = RegistryCleanCategory.FileAssociations;
        var user = RegistryCleanHive.CurrentUser;
        const string exts = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";

        Assert.IsTrue(RegistryBackupValidator.IsAllowedPath(fa, user, exts + @"\.txt\OpenWithList", false));
        Assert.IsFalse(RegistryBackupValidator.IsAllowedPath(fa, user, exts + @"\.txt\UserChoice", false));
        Assert.IsFalse(RegistryBackupValidator.IsAllowedPath(fa, user, exts + @"\.txt\USERCHOICE\x", true));
        Assert.IsTrue(RegistryBackupValidator.IsAllowedPath(
            fa, RegistryCleanHive.LocalMachine, @"SOFTWARE\WOW6432Node\Classes\Applications\a.exe", true));
    }

    [TestMethod]
    public void 利用者のClassesはClassesハイブの関連付けの根だけ通しHKCUのリンク経由のパスは拒否する()
    {
        const RegistryCleanCategory fa = RegistryCleanCategory.FileAssociations;
        var classes = RegistryCleanHive.UserClasses;
        var user = RegistryCleanHive.CurrentUser;

        Assert.IsTrue(RegistryBackupValidator.IsAllowedPath(fa, classes, @".txt\OpenWithProgids", false));
        Assert.IsTrue(RegistryBackupValidator.IsAllowedPath(fa, classes, @"Applications\a.exe", true));
        Assert.IsFalse(RegistryBackupValidator.IsAllowedPath(fa, classes, "Applications", true));
        Assert.IsFalse(RegistryBackupValidator.IsAllowedPath(fa, classes, @"txtfile\OpenWithProgids", false));
        Assert.IsFalse(RegistryBackupValidator.IsAllowedPath(fa, classes, @".txt\shell", false));
        Assert.IsFalse(RegistryBackupValidator.IsAllowedPath(fa, classes, @"CLSID\{00000000-0000-0000-0000-000000000000}", true));
        Assert.IsFalse(RegistryBackupValidator.IsAllowedPath(fa, classes, @"Software\Classes\Applications\a.exe", true));
        Assert.IsFalse(RegistryBackupValidator.IsAllowedPath(RegistryCleanCategory.AppPaths, classes, @"Applications\a.exe", true));
        Assert.IsFalse(RegistryBackupValidator.IsAllowedPath(fa, user, @"Software\Classes\.txt\OpenWithProgids", false));
        Assert.IsFalse(RegistryBackupValidator.IsAllowedPath(fa, user, @"Software\Classes\Applications\a.exe", true));
    }

    [TestMethod]
    public void 共有DLLはHKLMだけ通しフォントとアンインストールはHKCUも通す()
    {
        const string cv = @"Microsoft\Windows\CurrentVersion";
        Assert.IsTrue(RegistryBackupValidator.IsAllowedPath(
            RegistryCleanCategory.SharedDlls, RegistryCleanHive.LocalMachine, $@"SOFTWARE\{cv}\SharedDLLs", false));
        Assert.IsFalse(RegistryBackupValidator.IsAllowedPath(
            RegistryCleanCategory.SharedDlls, RegistryCleanHive.CurrentUser, $@"Software\{cv}\SharedDLLs", false));
        Assert.IsTrue(RegistryBackupValidator.IsAllowedPath(
            RegistryCleanCategory.Fonts, RegistryCleanHive.CurrentUser, @"Software\Microsoft\Windows NT\CurrentVersion\Fonts", false));
        Assert.IsTrue(RegistryBackupValidator.IsAllowedPath(
            RegistryCleanCategory.Uninstall, RegistryCleanHive.CurrentUser, $@"Software\{cv}\Uninstall\App", true));
        Assert.IsFalse(RegistryBackupValidator.IsAllowedPath(
            RegistryCleanCategory.Uninstall, RegistryCleanHive.LocalMachine, $@"SOFTWARE\{cv}\Run\App", true));
    }

    [TestMethod]
    public void 改ざんされたパスの文書は読み込みで拒否する()
    {
        var files = new MemoryFiles();
        var store = new RegistryBackupStore(files);
        var id = store.Save(Document(ValueItem(RunPath, "Tool", 1, [1])));
        var key = Path.Combine("registry", id + ".json");
        files.Files[key] = files.Files[key].Replace("Windows\\\\CurrentVersion\\\\Run", "Windows\\\\CurrentVersion\\\\Winlogon");

        var ex = Assert.ThrowsExactly<RegistryBackupRejectedException>(() => store.Load(id));
        StringAssert.Contains(ex.Message, "キーパス");
        Assert.ThrowsExactly<RegistryBackupRejectedException>(() => store.Save(Document(ValueItem(@"SOFTWARE\Evil", "x", 1, [1]))));
    }

    [TestMethod]
    public void 版違い_SID不正_未知プロパティ_壊れたJSONは読み込みで拒否する()
    {
        var files = new MemoryFiles();
        var store = new RegistryBackupStore(files);
        var id = store.Save(Document(ValueItem(RunPath, "Tool", 1, [1])));
        var key = Path.Combine("registry", id + ".json");
        var original = files.Files[key];

        files.Files[key] = original.Replace("\"formatVersion\":1", "\"formatVersion\":2");
        StringAssert.Contains(Assert.ThrowsExactly<RegistryBackupRejectedException>(() => store.Load(id)).Message, "版");

        files.Files[key] = original.Replace(Sid, "not-a-sid");
        StringAssert.Contains(Assert.ThrowsExactly<RegistryBackupRejectedException>(() => store.Load(id)).Message, "SID");

        files.Files[key] = original.Replace("\"formatVersion\":1", "\"formatVersion\":1,\"extra\":true");
        Assert.ThrowsExactly<RegistryBackupRejectedException>(() => store.Load(id));

        files.Files[key] = original[..^5];
        Assert.ThrowsExactly<RegistryBackupRejectedException>(() => store.Load(id));
    }

    [TestMethod]
    public void 項目の形が不正な文書は拒否する()
    {
        var empty = new RegistryBackupKeyNode([], []);
        var key = KeyItem(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\App", empty, RegistryCleanCategory.Uninstall);
        var value = ValueItem(RunPath, "Tool", 1, [1]);

        Assert.IsFalse(Valid(Document(key with { Subtree = null })));
        Assert.IsFalse(Valid(Document(key with { ValueType = 1 })));
        Assert.IsFalse(Valid(Document(value with { Subtree = empty })));
        Assert.IsFalse(Valid(Document(value with { DataBase64 = null })));
        Assert.IsFalse(Valid(Document(value with { DataBase64 = "***" })));
        Assert.IsFalse(Valid(Document(value with { Category = (RegistryCleanCategory)99 })));
        Assert.IsFalse(Valid(Document(value) with { CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local) }));
        Assert.IsFalse(Valid(Document(value) with { Items = null! }));
        Assert.IsFalse(Valid(null));
        Assert.IsTrue(Valid(Document(key, value)));
    }

    [TestMethod]
    public void 子キーと値の名前の重複と不正な名前を拒否する()
    {
        RegistryBackupDocument With(RegistryBackupKeyNode node) =>
            Document(KeyItem(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\App", node, RegistryCleanCategory.Uninstall));
        var leaf = new RegistryBackupKeyNode([], []);

        Assert.IsTrue(Valid(With(new([new("A", 1, "")], [new("c", leaf)]))));
        Assert.IsFalse(Valid(With(new([new("A", 1, ""), new("a", 1, "")], []))));
        Assert.IsFalse(Valid(With(new([], [new("c", leaf), new("C", leaf)]))));
        Assert.IsFalse(Valid(With(new([], [new("a\\b", leaf)]))));
        Assert.IsFalse(Valid(With(new([], [new("", leaf)]))));
        Assert.IsFalse(Valid(With(new([null!], []))));
        Assert.IsFalse(Valid(With(new([], [new("c", null!)]))));
    }

    [TestMethod]
    public void キー単位の深さは64まで許し65で拒否する()
    {
        Assert.IsTrue(Valid(Document(DeepItem(64))));
        Assert.IsFalse(Valid(Document(DeepItem(65))));

        // 深い文書は JSON の深さ上限の内側で往復できる。
        var store = new RegistryBackupStore(new MemoryFiles());
        var id = store.Save(Document(DeepItem(64)));
        Assert.IsNotNull(store.Load(id));
    }

    [TestMethod]
    public void 件数とサイズの上限を超える文書を拒否する()
    {
        var value = ValueItem(RunPath, "Tool", 3, new byte[RegistryBackupLimits.MaxValueBytes]);
        Assert.IsTrue(Valid(Document(value)));
        Assert.IsFalse(Valid(Document(value with { DataBase64 = Convert.ToBase64String(new byte[RegistryBackupLimits.MaxValueBytes + 1]) })));

        var many = Enumerable.Repeat(ValueItem(RunPath, "x", 1, [1]), RegistryBackupLimits.MaxItems + 1).ToArray();
        Assert.IsFalse(Valid(Document(many)));

        // 値の合計サイズ (1 MiB を 33 個 > 32 MiB)。
        var big = Enumerable.Range(0, 33)
            .Select(i => ValueItem(RunPath, "v" + i, 3, new byte[RegistryBackupLimits.MaxValueBytes]))
            .ToArray();
        Assert.IsFalse(Valid(Document(big)));
    }

    [TestMethod]
    public void 一覧は時刻順で結果ファイルを除き1000件を超えたら失敗する()
    {
        var files = new MemoryFiles();
        var store = new RegistryBackupStore(files);
        var first = store.Save(Document(ValueItem(RunPath, "a", 1, [1])));
        store.WriteResult(first, Result(first));
        files.Names.Add("stray.txt");
        files.Names.Add($".{NewId()}.json.tmp");

        CollectionAssert.AreEqual(new[] { first }, store.List().ToArray());

        files.Names.Clear();
        for (var i = 0; i <= RegistryBackupLimits.MaxBackups; i++)
        {
            files.Names.Add($"20260101T000000{i % 1000:000}Z-{Guid.NewGuid():N}.json");
        }

        Assert.ThrowsExactly<RegistryBackupException>(() => store.List());
    }

    [TestMethod]
    public void 読み戻しが一致しなければファイルを消して失敗する()
    {
        var files = new MemoryFiles { CorruptReads = true };
        var store = new RegistryBackupStore(files);

        Assert.ThrowsExactly<RegistryBackupException>(() => store.Save(Document(ValueItem(RunPath, "a", 1, [1]))));
        Assert.IsEmpty(files.Files);
    }

    [TestMethod]
    public void 読み戻しで例外が出ても削除して失敗に包む()
    {
        var files = new MemoryFiles { ThrowOnRead = true };
        var ex = Assert.ThrowsExactly<RegistryBackupException>(
            () => new RegistryBackupStore(files).Save(Document(ValueItem(RunPath, "a", 1, [1]))));

        Assert.IsNotNull(ex.InnerException);
        Assert.IsEmpty(files.Files);
    }

    [TestMethod]
    public void 書き込みに失敗したら保存は例外で何も残さない()
    {
        var files = new MemoryFiles { ThrowOnWrite = true };

        Assert.ThrowsExactly<RegistryBackupException>(
            () => new RegistryBackupStore(files).Save(Document(ValueItem(RunPath, "a", 1, [1]))));
        Assert.IsEmpty(files.Files);
    }

    [TestMethod]
    public void 結果ファイルは新規にだけ書け読み戻せる_削除で両方消える()
    {
        var files = new MemoryFiles();
        var store = new RegistryBackupStore(files);
        var id = store.Save(Document(ValueItem(RunPath, "a", 1, [1])));

        Assert.IsNull(store.ReadResult(id));
        store.WriteResult(id, Result(id));
        Assert.ThrowsExactly<RegistryBackupException>(() => store.WriteResult(id, Result(id)));
        var result = store.ReadResult(id);
        Assert.AreEqual(RegistryOverallState.Partial, result!.Overall);
        Assert.AreEqual(RegistryItemState.Skipped, result.Items[0].State);
        Assert.ThrowsExactly<RegistryBackupRejectedException>(() => store.WriteResult(id, Result(id) with { FormatVersion = 9 }));

        store.Delete(id);
        Assert.IsEmpty(files.Files);
        Assert.IsNull(store.Load(id));
    }

    [TestMethod]
    [DataRow("..\\x")]
    [DataRow("")]
    [DataRow("20260101T000000000Z-zzzz")]
    [DataRow("20260101T000000000Z-00000000000000000000000000000000\\..\\evil")]
    public void 形式に合わないIDは拒否する(string id)
    {
        var store = new RegistryBackupStore(new MemoryFiles());

        Assert.IsFalse(RegistryBackupStore.IsValidId(id));
        Assert.ThrowsExactly<ArgumentException>(() => store.Load(id));
        Assert.ThrowsExactly<ArgumentException>(() => store.Delete(id));
    }

    [TestMethod]
    public void 取り込みは既定値と子キーを含むサブツリー全体を写す()
    {
        var root = new FakeKey();
        root.Values[""] = new RegistryRawValue(1, [1]);
        root.Values["A"] = new RegistryRawValue(4, [1, 0, 0, 0]);
        root.Children["Sub"] = new FakeKey();
        root.Children["Sub"].Values["B"] = new RegistryRawValue(3, [9]);

        var node = RegistryBackupCapture.CaptureKey(root, RegistryCleanView.Registry64);

        Assert.HasCount(2, node.Values);
        Assert.AreEqual("", node.Values[0].Name);
        Assert.AreEqual("Sub", node.SubKeys[0].Name);
        Assert.AreEqual("B", node.SubKeys[0].Node.Values[0].Name);
        Assert.AreEqual(Convert.ToBase64String([9]), node.SubKeys[0].Node.Values[0].DataBase64);
    }

    [TestMethod]
    public void 取り込みは深すぎる木と消えた子で失敗する()
    {
        var deep = new FakeKey();
        var cursor = deep;
        for (var i = 0; i < RegistryBackupLimits.MaxKeyDepth; i++)
        {
            cursor.Children["k"] = new FakeKey();
            cursor = cursor.Children["k"];
        }

        Assert.ThrowsExactly<RegistryBackupException>(() => RegistryBackupCapture.CaptureKey(deep, RegistryCleanView.Registry64));

        var vanishing = new FakeKey { VanishingChild = "gone" };
        Assert.ThrowsExactly<RegistryBackupException>(() => RegistryBackupCapture.CaptureKey(vanishing, RegistryCleanView.Registry64));
        Assert.IsNull(RegistryBackupCapture.CaptureValue(new FakeKey(), "missing"));
    }

    [TestMethod]
    public void 実保護ストレージで保存と読み込みが往復する()
    {
        if (!OperatingSystem.IsWindows() || !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Assert.Inconclusive("保護 ACL のディレクトリ作成に管理者権限が要ります。");
        }

        var parent = Path.Combine(Path.GetTempPath(), "Lumin4ti.Tests", "RegistryBackup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            var storage = new ProtectedBackupStorage(Path.Combine(parent, "Lumin4ti", "backups"), parent);
            var store = new RegistryBackupStore(storage);
            var id = store.Save(Document(ValueItem(RunPath, "Tool", 1, [1, 2])));

            Assert.AreEqual(id, store.List().Single());
            Assert.AreEqual("Tool", store.Load(id)!.Items[0].ValueName);
            store.WriteResult(id, Result(id));
            Assert.IsNotNull(store.ReadResult(id));
            store.Delete(id);
            Assert.IsEmpty(store.List());
        }
        finally
        {
            try
            {
                Directory.Delete(parent, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // テスト専用の一時領域。残っても次回の別名に影響しない。
            }
        }
    }

    private static bool Valid(RegistryBackupDocument? document) => RegistryBackupValidator.TryValidate(document, out _);

    private static string NewId() => $"20260101T000000000Z-{Guid.NewGuid():N}";

    private static RegistryBackupDocument Document(params RegistryBackupItem[] items) =>
        new(RegistryBackupDocument.CurrentVersion, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), Sid, items);

    private static RegistryBackupItem ValueItem(string path, string name, uint type, byte[] data) =>
        new(RegistryCleanCategory.Startup, RegistryCleanHive.LocalMachine, RegistryCleanView.Registry64,
            path, name, type, Convert.ToBase64String(data), null);

    private static RegistryBackupItem KeyItem(
        string path, RegistryBackupKeyNode node, RegistryCleanCategory category = RegistryCleanCategory.AppPaths) =>
        new(category, RegistryCleanHive.LocalMachine, RegistryCleanView.Registry64, path, null, null, null, node);

    private static RegistryBackupItem DeepItem(int depth)
    {
        var node = new RegistryBackupKeyNode([], []);
        for (var i = 1; i < depth; i++)
        {
            node = new RegistryBackupKeyNode([], [new RegistryBackupSubKey("k", node)]);
        }

        return KeyItem(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\deep.exe", node);
    }

    private static RegistryBackupResultDocument Result(string id) =>
        new(RegistryBackupDocument.CurrentVersion, id, new DateTime(2026, 1, 2, 3, 4, 6, DateTimeKind.Utc),
            RegistryOverallState.Partial,
            [new RegistryBackupResultItem(RegistryCleanCategory.Startup, RegistryCleanHive.LocalMachine,
                RegistryCleanView.Registry64, RunPath, "a", RegistryItemState.Skipped, "denied")]);

    private sealed class MemoryFiles : IRegistryBackupFiles
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>一覧に出す名前 (空なら Files の名前)。</summary>
        public List<string> Names { get; } = [];

        public bool CorruptReads { get; init; }

        public bool ThrowOnRead { get; init; }

        public bool ThrowOnWrite { get; init; }

        public bool Exists(string relativePath) => Files.ContainsKey(relativePath);

        public long Length(string relativePath) => Files[relativePath].Length;

        public string ReadAllText(string relativePath)
        {
            if (ThrowOnRead)
            {
                throw new IOException("read failed");
            }

            var text = Files[relativePath];
            return CorruptReads ? text + " " : text;
        }

        public void WriteNew(string relativePath, byte[] content)
        {
            if (ThrowOnWrite)
            {
                throw new IOException("write failed");
            }

            if (!Files.TryAdd(relativePath, System.Text.Encoding.UTF8.GetString(content)))
            {
                throw new IOException("exists");
            }

            Names.Add(Path.GetFileName(relativePath));
        }

        public void Delete(string relativePath)
        {
            if (Files.Remove(relativePath))
            {
                Names.Remove(Path.GetFileName(relativePath));
            }
        }

        public IReadOnlyList<string> EnumerateNames(string relativeDirectory, int limit) => [.. Names];
    }

    private sealed class FakeKey : IRegistryCleanerKey
    {
        public Dictionary<string, RegistryRawValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, FakeKey> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? VanishingChild { get; init; }

        public IRegistryCleanerKey? OpenChild(string name, RegistryCleanAccess access, RegistryCleanView view) =>
            Children.GetValueOrDefault(name);

        public IRegistryCleanerKey CreateChild(string name, RegistryCleanView view) => throw new NotSupportedException();

        public RegistryRawValue? GetValue(string name) => Values.GetValueOrDefault(name);

        public void SetValue(string name, RegistryRawValue value) => throw new NotSupportedException();

        public IReadOnlyList<string> GetValueNames() => [.. Values.Keys];

        public IReadOnlyList<string> GetSubKeyNames() => VanishingChild is null ? [.. Children.Keys] : [VanishingChild];

        public void DeleteValue(string name) => throw new NotSupportedException();

        public void DeleteSelf() => throw new NotSupportedException();

        public RegistryKeyInfo QueryInfo() => new(DateTime.UtcNow, Children.Count, Values.Count);

        public string KernelName() => @"\REGISTRY\MACHINE\FAKE";

        public string OwnerSid() => "S-1-5-18";

        public void Dispose()
        {
        }
    }
}
