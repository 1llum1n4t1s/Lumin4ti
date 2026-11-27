using Lumin4ti.Core.Services.Windows.Cleanup;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class AdvancedCleanerTests
{
    private string _root = string.Empty;
    private AdvancedScanLedger _ledger = null!;

    private sealed class SyncProgress(Action<string> action) : IProgress<string>
    {
        public void Report(string value) => action(value);
    }

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "Lumin4tiAdvancedClean", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _ledger = new AdvancedScanLedger();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var entry in new DirectoryInfo(_root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            entry.Attributes = FileAttributes.Normal;
        }

        Directory.Delete(_root, recursive: true);
    }

    private string CreateFile(string relativePath, string content = "12345", int ageDays = 3)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        if (ageDays > 0) File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddDays(-ageDays));
        return full;
    }

    private CustomFolderGuardContext Guard() => new(
        Path.Combine(_root, "Windows"), [], null, [Path.Combine(_root, "Users")], Path.Combine(_root, "Users", "me"));

    private AdvancedProtectedPaths Protection() => new(new AdvancedProtectionContext(Guard(), [Path.Combine(_root, "Self")]));

    private AdvancedScanResult Scan(IReadOnlyList<CustomFolderCheck>? folders = null, Func<string, bool>? missing = null) =>
        new AdvancedCleanScanner(_ledger).Scan(
            new AdvancedScanOptions([_root])
            {
                Protection = Protection(),
                CustomFolders = folders ?? [],
                IncludeInvalidShortcuts = missing is not null,
                IsShortcutTargetMissing = missing,
                MaxDegreeOfParallelism = 2,
            },
            null,
            CancellationToken.None);

    private AdvancedCleanOptions Options(bool wipe = false, Func<string, bool>? missing = null) =>
        new() { Protection = Protection(), GuardContext = Guard(), Wipe = wipe, IsShortcutTargetMissing = missing };

    private AdvancedCleanOutcome Clean(AdvancedScanResult scan, IEnumerable<AdvancedCleanItem> items, IEnumerable<AdvancedCustomFolderScan>? folders = null, AdvancedCleanOptions? options = null,
        IProgress<string>? progress = null, CancellationToken ct = default) =>
        new AdvancedCleaner(_ledger).Clean(scan, new AdvancedCleanSelection([.. items], [.. folders ?? []]), options ?? Options(), progress, ct);

    private static AdvancedCleanItem Item(AdvancedScanResult scan, string name) => scan.Items.Single(i => i.Name == name);

    [TestMethod]
    public void 選択した行だけを同一ハンドルで削除し更新や差し替えがあったものは残す()
    {
        var plain = CreateFile("plain.tmp");
        var updated = CreateFile("updated.tmp");
        var replaced = CreateFile("replaced.tmp", "12345");
        var unselected = CreateFile("unselected.tmp");
        var scan = Scan();
        var stamp = File.GetLastWriteTimeUtc(replaced);

        File.AppendAllText(updated, "more");
        File.Delete(replaced);
        File.WriteAllText(replaced, "12345");
        File.SetLastWriteTimeUtc(replaced, stamp);

        var outcome = Clean(scan, scan.Items.Where(i => i.Name != "unselected.tmp"));

        Assert.IsFalse(File.Exists(plain));
        Assert.IsTrue(File.Exists(updated), "更新されたファイルは消さない");
        Assert.IsTrue(File.Exists(replaced), "FileId が変わったファイルは同じ名前・サイズ・時刻でも消さない");
        Assert.IsTrue(File.Exists(unselected));
        Assert.AreEqual(1, outcome.DeletedFiles);
        Assert.AreEqual(5, outcome.FreedBytes);
        Assert.AreEqual(2, outcome.Retained);
        Assert.IsTrue(outcome.Succeeded);
    }

    [TestMethod]
    public void 失効した結果と二重の削除は何も消さない()
    {
        var file = CreateFile("a.tmp");
        var stale = Scan();
        _ledger.Invalidate();
        var expired = Clean(stale, stale.Items);
        Assert.IsTrue(expired.Expired);
        Assert.IsFalse(expired.Succeeded);
        Assert.IsTrue(File.Exists(file));

        var fresh = Scan();
        Assert.AreEqual(1, Clean(fresh, fresh.Items).DeletedFiles);
        CreateFile("a.tmp");
        var again = Clean(fresh, fresh.Items);
        Assert.IsTrue(again.Expired, "削除を始めた結果は再利用できない");
        Assert.IsTrue(File.Exists(Path.Combine(_root, "a.tmp")));
    }

    [TestMethod]
    public void 大文字小文字だけが違う走査結果があっても落ちず_区別できない指定は削除しない()
    {
        var file = CreateFile("a.tmp");
        var scan = Scan();
        var real = Item(scan, "a.tmp");
        scan.Items = [real, real with { Path = real.Path.ToUpperInvariant() }];
        var ambiguous = real with { Path = real.Path.ToLowerInvariant() };

        var outcome = Clean(scan, [ambiguous, real]);

        Assert.HasCount(1, outcome.Rejected, "どちらか決まらない指定は削除せず拒否する");
        Assert.AreEqual(1, outcome.DeletedFiles);
        Assert.IsFalse(File.Exists(file));
        Assert.IsTrue(outcome.IsPartial);
    }

    [TestMethod]
    public void 走査していないファイルと実行時に保護されたパスは削除を拒否する()
    {
        var outside = CreateFile("outside.log");
        var protectedFile = CreateFile(@"guarded\g.tmp");
        var normal = CreateFile("n.tmp");
        var scan = Scan();
        var fabricated = Item(scan, "n.tmp") with { Path = outside, Name = "outside.log" };
        var guardedNow = new AdvancedProtectedPaths(new AdvancedProtectionContext(Guard(), [Path.Combine(_root, "guarded")]));

        var outcome = Clean(scan, [fabricated, Item(scan, "g.tmp"), Item(scan, "n.tmp")], options: Options() with { Protection = guardedNow });

        Assert.IsTrue(File.Exists(outside));
        Assert.IsTrue(File.Exists(protectedFile));
        Assert.IsFalse(File.Exists(normal));
        Assert.HasCount(2, outcome.Rejected);
        Assert.IsTrue(outcome.IsPartial);
        Assert.AreEqual(1, outcome.DeletedFiles);
    }

    [TestMethod]
    public void カスタムフォルダーは中身だけを空にし走査後に作られたものと選べないフォルダーは残す()
    {
        CreateFile(@"custom\a.txt");
        CreateFile(@"custom\sub\b.txt");
        CreateFile(@"custom\inner.tmp");
        CreateFile(@"Windows\w\w.txt");
        Thread.Sleep(300);
        var scan = Scan([
            CustomFolderGuard.Check(Path.Combine(_root, "custom"), Guard()),
            CustomFolderGuard.Check(Path.Combine(_root, "Windows", "w"), Guard()),
        ]);
        Thread.Sleep(150);
        var late = CreateFile(@"custom\late.txt", ageDays: 0);

        var outcome = Clean(scan, [Item(scan, "inner.tmp")], scan.CustomFolders);

        Assert.IsTrue(Directory.Exists(Path.Combine(_root, "custom")), "フォルダー自身は残す");
        Assert.IsFalse(File.Exists(Path.Combine(_root, "custom", "a.txt")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "custom", "sub")));
        Assert.IsTrue(File.Exists(late), "走査後に作られたファイルは残す");
        Assert.IsTrue(File.Exists(Path.Combine(_root, "Windows", "w", "w.txt")));
        Assert.AreEqual(1, outcome.CoveredByFolders, "選択したフォルダー内の行は個別に消さない");
        Assert.AreEqual(3, outcome.DeletedFiles);
        Assert.AreEqual(1, outcome.DeletedDirectories);
        Assert.AreEqual(1, outcome.Retained);
        Assert.HasCount(1, outcome.Rejected, "選べないフォルダーは拒否に残る");
        Assert.IsTrue(outcome.IsPartial);
    }

    [TestMethod]
    public void カスタムフォルダーは削除の直前にも検査し拒否されたら空にしない()
    {
        var file = CreateFile(@"custom\a.txt");
        var scan = Scan([CustomFolderGuard.Check(Path.Combine(_root, "custom"), Guard())]);
        var tampered = new CustomFolderGuardContext(Path.Combine(_root, "custom"), [], null, [], null);

        var outcome = Clean(scan, [], scan.CustomFolders, Options() with { GuardContext = tampered });

        Assert.IsTrue(File.Exists(file));
        Assert.HasCount(1, outcome.Rejected);
        Assert.AreEqual(0, outcome.DeletedFiles);
    }

    [TestMethod]
    public void 安全な削除はハードリンクを共有するファイルを上書きせず削除だけ行う()
    {
        CreateFile("solo.tmp", "abcde");
        var linked = CreateFile("linked.tmp", "abcde");
        var peer = Path.Combine(_root, "peer.dat");
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/c mklink /H \"{peer}\" \"{linked}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!)
        {
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, process.StandardError.ReadToEnd());
        }

        var scan = Scan();
        var outcome = Clean(scan, scan.Items, options: Options(wipe: true));

        Assert.AreEqual(2, outcome.DeletedFiles);
        Assert.AreEqual(1, outcome.WipedFiles);
        Assert.AreEqual(1, outcome.WipeSkipped);
        Assert.AreEqual("abcde", File.ReadAllText(peer), "リンク先を共有する実体は上書きしない");
    }

    [TestMethod]
    public void 使用中は部分失敗として数え既に無かったものは失敗にしない()
    {
        var locked = CreateFile("locked.tmp");
        CreateFile("gone.tmp");
        CreateFile("ok.tmp");
        var scan = Scan();
        File.Delete(Path.Combine(_root, "gone.tmp"));

        AdvancedCleanOutcome outcome;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            outcome = Clean(scan, scan.Items);
        }

        Assert.AreEqual(1, outcome.DeletedFiles);
        Assert.AreEqual(1, outcome.Blocked);
        Assert.AreEqual(1, outcome.Gone);
        Assert.HasCount(1, outcome.BlockedExamples);
        Assert.IsTrue(outcome.IsPartial);
        Assert.IsFalse(outcome.Succeeded);
        Assert.IsTrue(File.Exists(locked));
    }

    [TestMethod]
    public void 取消は途中で止まり部分的な結果を返す()
    {
        for (var i = 0; i < 20; i++) CreateFile($"f{i:00}.tmp");
        var scan = Scan();
        using var cts = new CancellationTokenSource();
        var reports = 0;

        var outcome = Clean(scan, scan.Items, progress: new SyncProgress(_ =>
        {
            if (++reports == 3) cts.Cancel();
        }), ct: cts.Token);

        Assert.IsTrue(outcome.Cancelled);
        Assert.IsTrue(outcome.IsPartial);
        Assert.IsFalse(outcome.Succeeded);
        Assert.IsGreaterThan(0, outcome.DeletedFiles);
        Assert.IsLessThan(20, outcome.DeletedFiles);
        Assert.AreEqual(20 - outcome.DeletedFiles, Directory.GetFiles(_root).Length);
    }

    [TestMethod]
    public void 無効なショートカットは削除の直前に欠損を再確認し解消していたら残す()
    {
        var link = Path.Combine(_root, "gone.lnk");
        File.WriteAllBytes(link, AdvancedCleanScannerTests.ShortcutTo(@"C:\NoSuchDirectory\app.exe"));
        File.SetLastWriteTimeUtc(link, DateTime.UtcNow.AddDays(-10));
        var scan = Scan(missing: _ => true);
        Assert.AreEqual(AdvancedRuleKind.InvalidShortcut, Item(scan, "gone.lnk").Kind);

        var revived = Clean(scan, scan.Items, options: Options(missing: _ => false));
        Assert.IsTrue(File.Exists(link));
        Assert.AreEqual(1, revived.Retained);

        var again = Scan(missing: _ => true);
        var deleted = Clean(again, again.Items, options: Options(missing: _ => true));
        Assert.IsFalse(File.Exists(link));
        Assert.AreEqual(1, deleted.DeletedFiles);
    }
}
