using Lumin4ti.Core.Services.Windows.Cleanup.Slim;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class InstallerOrphanScannerTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "Lumin4tiInstallerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeSource(Func<InstallerRegistrationSnapshot> read) : IInstallerRegistrationSource
    {
        public int Reads { get; private set; }

        public InstallerRegistrationSnapshot Read(CancellationToken ct)
        {
            Reads++;
            return read();
        }
    }

    private static InstallerRegistrationSnapshot Registered(params string[] paths) =>
        new(new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase), null);

    private string CreateFile(string name, int ageDays = 100, int creationAgeDays = 100, string content = "installer")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-creationAgeDays));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
        return path;
    }

    private InstallerOrphanScanner Scanner(params IInstallerRegistrationSource[] sources) => new(_root, sources);

    [TestMethod]
    public void 登録の列挙に失敗したら項目ごと利用不可でファイルも列挙しない()
    {
        CreateFile("a.msi");
        var scanner = Scanner(new FakeSource(() => Registered()), new FakeSource(() => InstallerRegistrationSnapshot.Failed("MsiEnumPatchesEx が 5")));

        var scan = scanner.Scan(CancellationToken.None);

        Assert.IsFalse(scan.Available);
        StringAssert.Contains(scan.UnavailableReason, "MsiEnumPatchesEx");
        Assert.AreEqual(0, scan.Orphans.Count);
    }

    [TestMethod]
    public void 列挙元が例外を投げても利用不可にする()
    {
        CreateFile("a.msi");
        var scan = Scanner(new FakeSource(() => throw new InvalidOperationException("boom"))).Scan(CancellationToken.None);

        Assert.IsFalse(scan.Available);
        Assert.AreEqual(0, scan.Orphans.Count);
    }

    [TestMethod]
    public void 列挙元がひとつも無ければ利用不可にする()
    {
        CreateFile("a.msi");
        Assert.IsFalse(Scanner().Scan(CancellationToken.None).Available);
    }

    [TestMethod]
    public void 登録の和集合に無く30日以上前のmsiとmspだけを孤児にする()
    {
        var byApi = CreateFile("api.msi");
        var byRegistry = CreateFile("reg.msp");
        CreateFile("orphan1.MSI");
        CreateFile("orphan2.msp");
        CreateFile("new.msi", ageDays: 5, creationAgeDays: 5);
        CreateFile("copied.msi", ageDays: 100, creationAgeDays: 1);
        CreateFile("other.exe");
        Directory.CreateDirectory(Path.Combine(_root, "folder.msi"));
        var scanner = Scanner(new FakeSource(() => Registered(byApi)), new FakeSource(() => Registered(byRegistry.ToLowerInvariant())));

        var scan = scanner.Scan(CancellationToken.None);

        Assert.IsTrue(scan.Available);
        CollectionAssert.AreEquivalent(
            new[] { "orphan1.MSI", "orphan2.msp" },
            scan.Orphans.Select(o => Path.GetFileName(o.Path)).ToArray());
        Assert.AreEqual(2, scan.RegisteredCount);
        Assert.AreEqual(2, scan.RecentCount);
        Assert.AreEqual(scan.Orphans.Sum(o => o.Size), scan.Bytes);
    }

    [TestMethod]
    public void 実行直前の再照合に失敗したら何も削除しない()
    {
        var orphan = CreateFile("orphan.msi");
        var fail = false;
        var scanner = Scanner(new FakeSource(() => fail ? InstallerRegistrationSnapshot.Failed("列挙失敗") : Registered()));
        var scan = scanner.Scan(CancellationToken.None);
        fail = true;

        var run = scanner.Run(scan, null, CancellationToken.None);

        Assert.IsFalse(run.Verified);
        Assert.IsNull(run.Outcome);
        Assert.IsTrue(File.Exists(orphan));
    }

    [TestMethod]
    public void 利用不可の走査結果は実行できない()
    {
        var scanner = Scanner(new FakeSource(() => InstallerRegistrationSnapshot.Failed("x")));
        var scan = scanner.Scan(CancellationToken.None);

        var run = scanner.Run(scan, null, CancellationToken.None);

        Assert.IsFalse(run.Verified);
    }

    [TestMethod]
    public void 走査後に登録されたファイルは残し孤児のままのものだけ消す()
    {
        var late = CreateFile("late.msi");
        var orphan = CreateFile("orphan.msi");
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scanner = Scanner(new FakeSource(() => new(registered, null)));
        var scan = scanner.Scan(CancellationToken.None);
        registered.Add(late);

        var run = scanner.Run(scan, null, CancellationToken.None);

        Assert.IsTrue(run.Verified);
        Assert.IsTrue(File.Exists(late));
        Assert.IsFalse(File.Exists(orphan));
        Assert.AreEqual(1, run.Outcome!.DeletedFiles);
        Assert.AreEqual(1, run.Outcome.Retained);
        Assert.AreEqual(0, run.Outcome.Blocked);
    }

    [TestMethod]
    public void 走査後に内容が変わったファイルは同一性が一致せず残す()
    {
        var changed = CreateFile("changed.msi", content: "before");
        var same = CreateFile("same.msi");
        var scanner = Scanner(new FakeSource(() => Registered()));
        var scan = scanner.Scan(CancellationToken.None);
        File.WriteAllText(changed, "after the scan, longer content");
        File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddDays(-100));

        var run = scanner.Run(scan, null, CancellationToken.None);

        Assert.IsTrue(File.Exists(changed));
        Assert.IsFalse(File.Exists(same));
        Assert.AreEqual(1, run.Outcome!.Retained);
    }

    [TestMethod]
    public void 走査後に別の実体へ差し替えられたファイルは残す()
    {
        var path = CreateFile("swap.msi");
        var scanner = Scanner(new FakeSource(() => Registered()));
        var scan = scanner.Scan(CancellationToken.None);
        File.Delete(path);
        CreateFile("swap.msi", content: "installer");

        var run = scanner.Run(scan, null, CancellationToken.None);

        Assert.IsTrue(File.Exists(path));
        Assert.AreEqual(1, run.Outcome!.Retained);
        Assert.AreEqual(0, run.Outcome.DeletedFiles);
    }

    [TestMethod]
    public void 登録パスの接頭辞と大文字小文字の違いを吸収する()
    {
        Assert.AreEqual(
            InstallerOrphanScanner.Normalize(@"C:\Windows\Installer\1a2b.msi"),
            InstallerOrphanScanner.Normalize(@"\\?\c:\windows\installer\1A2B.MSI"));
    }
}
