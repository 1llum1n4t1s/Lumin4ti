using Lumin4ti.Core.Services.Windows.Actions;
using Lumin4ti.Core.Services.Windows.Cleanup;
using EngineTarget = Lumin4ti.Core.Services.Windows.Actions.CleanupTarget;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class FileCleanupScannerTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "Lumin4tiScannerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
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

    private string CreateFile(string relativePath, string content = "x")
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    // フォルダーの更新・アクセス時刻は NTFS が一覧取得や handle の開閉に遅れて反映するため比較しない。ファイルは作成・更新・アクセスとも比較する。
    private static string[] Snapshot(string root) =>
        new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories)
            .OrderBy(entry => entry.FullName, StringComparer.Ordinal)
            .Select(entry => $"{entry.FullName}|{entry.Attributes}|{entry.CreationTimeUtc.Ticks}|{(entry is FileInfo ? entry.LastWriteTimeUtc.Ticks : 0)}|{(entry is FileInfo ? entry.LastAccessTimeUtc.Ticks : 0)}|{(entry as FileInfo)?.Length}")
            .ToArray();

    private static void CreateJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
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

    [TestMethod]
    public void 走査は一覧と時刻を変えずに件数とサイズと代表と実体を返す()
    {
        var top = CreateFile(@"t\a.tmp", "12345");
        CreateFile(@"t\sub\b.tmp", "123");
        CreateFile(@"t\sub\deep\c.txt", "12");
        // NTFS は親フォルダーの更新時刻を作成の少し後に反映するため、落ち着いてから基準を取る。
        Thread.Sleep(300);
        var before = Snapshot(_root);

        var result = FileCleanupScanner.Scan(EngineTarget.Contents(Path.Combine(_root, "t")), new CleanupScanOptions(CollectIdentities: true, RepresentativeLimit: 2), CancellationToken.None);

        var after = Snapshot(_root);
        Assert.IsEmpty(before.Except(after).Concat(after.Except(before)).ToArray(), "走査でファイル・フォルダーの一覧や時刻・属性が変わった: " + string.Join("\n", before.Except(after).Concat(after.Except(before))));
        Assert.AreEqual(3, result.Files);
        Assert.AreEqual(10, result.Bytes);
        Assert.AreEqual(2, result.Directories);
        Assert.AreEqual(0, result.Retained);
        Assert.HasCount(2, result.Representatives);
        Assert.HasCount(3, result.Identities);
        var identity = result.Identities.Single(item => item.Path == top);
        Assert.AreEqual(5, identity.Size);
        Assert.AreEqual(new FileInfo(top).LastWriteTimeUtc.ToFileTimeUtc(), identity.LastWriteFileTime);
    }

    [TestMethod]
    public void 走査はパターンと再帰と古さと空フォルダーを削除と同じ規則で数える()
    {
        CreateFile(@"p\a.tmp", "1");
        CreateFile(@"p\keep.txt", "1");
        CreateFile(@"p\sub\b.tmp", "11");
        CreateFile(@"p\mixed\c.tmp", "1");
        CreateFile(@"p\mixed\d.txt", "1");
        var old = CreateFile(@"p\old.tmp", "1111");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
        var path = Path.Combine(_root, "p");

        var flat = FileCleanupScanner.Scan(EngineTarget.Files(path, "*.tmp"), null, CancellationToken.None);
        var deep = FileCleanupScanner.Scan(EngineTarget.Files(path, "*.tmp", recurse: true, removeEmptyDirs: true), null, CancellationToken.None);
        var aged = FileCleanupScanner.Scan(EngineTarget.Files(path, "*.tmp", recurse: true, olderThanDays: 10), null, CancellationToken.None);

        Assert.AreEqual(2, flat.Files, "再帰しない指定は直下だけ");
        Assert.AreEqual(0, flat.Directories);
        Assert.AreEqual(4, deep.Files);
        Assert.AreEqual(1, deep.Directories, "tmp だけの sub は空になるが、txt が残る mixed は消えない");
        Assert.AreEqual(1, aged.Files);
        Assert.AreEqual(4, aged.Bytes);
    }

    [TestMethod]
    public void 取消された走査は例外で止まり何も変えない()
    {
        CreateFile(@"c\a.tmp");
        var before = Snapshot(_root);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            FileCleanupScanner.Scan(EngineTarget.Contents(Path.Combine(_root, "c")), null, cts.Token));
        var after = Snapshot(_root);
        Assert.IsEmpty(before.Except(after).ToArray(), string.Join("\n", before.Concat(after)));
    }

    [TestMethod]
    public void 存在しない対象と拒否される対象は結果に理由を残す()
    {
        var missing = FileCleanupScanner.Scan(EngineTarget.Contents(Path.Combine(_root, "none")), null, CancellationToken.None);
        var rejected = FileCleanupScanner.Scan(EngineTarget.Contents(@"%Lumin4tiUndefined%\x"), null, CancellationToken.None);
        var badDays = FileCleanupScanner.Scan(new EngineTarget(Path.Combine(_root, "none"), CleanupTargetKind.Contents, null, false, false, -1), null, CancellationToken.None);

        Assert.IsTrue(missing.Missing);
        Assert.IsNotNull(rejected.Rejection);
        Assert.IsNotNull(badDays.Rejection);
    }

    [TestMethod]
    public void 走査は除外を残す数として数え削除対象に含めない()
    {
        var keepFile = CreateFile(@"e\keep.dat", "1234");
        CreateFile(@"e\drop.dat", "12");
        CreateFile(@"e\note.log", "1");
        CreateFile(@"e\Protected\inner.dat", "1");
        CreateFile(@"e\other\x.dat", "1");
        var matcher = new CleanupExclusionMatcher([keepFile], [Path.Combine(_root, @"e\Protected")], [".log"]);

        var result = FileCleanupScanner.Scan(EngineTarget.Contents(Path.Combine(_root, "e")), new CleanupScanOptions(matcher), CancellationToken.None);

        Assert.AreEqual(2, result.Files, "drop.dat と other\\x.dat だけが対象");
        Assert.AreEqual(3, result.Retained, "keep.dat・note.log・Protected フォルダー");
        Assert.AreEqual(1, result.Directories, "other だけが空になる");
    }

    [TestMethod]
    public void 除外の一致器はパスとフォルダー配下と拡張子とファイル名を判定する()
    {
        var folder = Path.Combine(_root, "Folder");
        var matcher = new CleanupExclusionMatcher(
            [Path.Combine(_root, "a", "x.bin"), "relative.txt", " "],
            [folder + "\\", @"not\absolute"],
            [".LOG", "thumbs.db", "~*.*", @"bad\name", ""]);

        Assert.IsTrue(matcher.IsExcluded(Path.Combine(_root, "A", "X.BIN"), false), "パスは大文字小文字を区別しない");
        Assert.IsFalse(matcher.IsExcluded(Path.Combine(_root, "a", "x.bin2"), false));
        Assert.IsTrue(matcher.IsExcluded(folder, true));
        Assert.IsTrue(matcher.IsExcluded(Path.Combine(folder, "deep", "f.dat"), false));
        Assert.IsFalse(matcher.IsExcluded(folder + "Other", true), "前方一致だけの別フォルダーは除外しない");
        Assert.IsTrue(matcher.IsExcluded(Path.Combine(_root, "q", "app.Log"), false));
        Assert.IsTrue(matcher.IsExcluded(Path.Combine(_root, "q", "Thumbs.DB"), false));
        Assert.IsTrue(matcher.IsExcluded(Path.Combine(_root, "q", "~tmp.1"), false));
        Assert.IsFalse(matcher.IsExcluded(Path.Combine(_root, "q", "app.logx"), false));
        Assert.IsFalse(matcher.IsExcluded(Path.Combine(_root, "q", "dir.log"), true), "名前の除外はフォルダーに適用しない");
        Assert.IsFalse(matcher.IsExcluded("relative.txt", false), "相対パスは判定しない");
        Assert.IsTrue(CleanupExclusionMatcher.Empty.IsEmpty);
        Assert.IsFalse(matcher.IsEmpty);
    }

    [TestMethod]
    public void リンクは走査で辿らず外部の内容も数えない()
    {
        CreateFile(@"l\real.tmp", "12");
        var outside = CreateFile(@"outside\secret.tmp", "123456789");
        CreateJunction(Path.Combine(_root, "l", "junction"), Path.GetDirectoryName(outside)!);

        var result = FileCleanupScanner.Scan(EngineTarget.Contents(Path.Combine(_root, "l")), null, CancellationToken.None);

        Assert.AreEqual(1, result.Files);
        Assert.AreEqual(2, result.Bytes);
        Assert.AreEqual(1, result.SkippedLinks);
        Assert.AreEqual(0, result.Directories, "リンクを含むためフォルダーも消えない扱い");
        Directory.Delete(Path.Combine(_root, "l", "junction"));
        Assert.AreEqual("123456789", File.ReadAllText(outside));
    }
}
