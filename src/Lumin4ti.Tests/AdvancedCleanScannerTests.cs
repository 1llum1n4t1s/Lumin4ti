using System.Text;
using Lumin4ti.Core.Services.Windows.Cleanup;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class AdvancedCleanScannerTests
{
    private string _root = string.Empty;
    private string _other = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        var parent = Path.Combine(Path.GetTempPath(), "Lumin4tiAdvancedScan", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(parent, "root");
        _other = Path.Combine(parent, "other");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_other);
    }

    [TestCleanup]
    public void Cleanup()
    {
        var parent = Path.GetDirectoryName(_root)!;
        if (!Directory.Exists(parent)) return;
        foreach (var entry in new DirectoryInfo(parent).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            entry.Attributes = FileAttributes.Normal;
        }

        Directory.Delete(parent, recursive: true);
    }

    private string CreateFile(string relativePath, string content = "12345", int ageDays = 3, string? under = null)
    {
        var full = Path.Combine(under ?? _root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        if (ageDays > 0) File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddDays(-ageDays));
        return full;
    }

    /// <summary>テストの木を仮想のドライブ構成に見立てた枝刈り (Windows・Program Files・ProgramData・Users・Lumin4ti 自身)。</summary>
    private AdvancedProtectedPaths Protection() => new(new AdvancedProtectionContext(Guard(), [Path.Combine(_root, "Self")]));

    private CustomFolderGuardContext Guard() => new(
        Path.Combine(_root, "Windows"),
        [Path.Combine(_root, "Program Files")],
        Path.Combine(_root, "ProgramData"),
        [Path.Combine(_root, "Users")],
        Path.Combine(_root, "Users", "me"));

    private AdvancedScanOptions Options(IReadOnlyDictionary<string, bool>? saved = null) =>
        new([_root]) { Protection = Protection(), SavedPatterns = saved, MaxDegreeOfParallelism = 4 };

    private static AdvancedCleanScanner NewScanner(out AdvancedScanLedger ledger) => new(ledger = new AdvancedScanLedger());

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

    /// <summary>LinkInfo にローカルパスだけを持つ最小の .lnk。</summary>
    internal static byte[] ShortcutTo(string target)
    {
        var volume = new byte[20];
        BitConverter.GetBytes(volume.Length).CopyTo(volume, 0);
        BitConverter.GetBytes(3u).CopyTo(volume, 4);
        BitConverter.GetBytes(0x10).CopyTo(volume, 12);
        var path = Encoding.ASCII.GetBytes(target + "\0");
        var size = 0x1C + volume.Length + path.Length + 1;
        var info = new byte[size];
        BitConverter.GetBytes(size).CopyTo(info, 0);
        BitConverter.GetBytes(0x1C).CopyTo(info, 4);
        BitConverter.GetBytes(1u).CopyTo(info, 8);
        BitConverter.GetBytes(0x1C).CopyTo(info, 12);
        BitConverter.GetBytes(0x1C + volume.Length).CopyTo(info, 16);
        BitConverter.GetBytes(0x1C + volume.Length + path.Length).CopyTo(info, 24);
        volume.CopyTo(info, 0x1C);
        path.CopyTo(info, 0x1C + volume.Length);

        var header = new byte[0x4C];
        BitConverter.GetBytes(0x4C).CopyTo(header, 0);
        new byte[] { 0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 }.CopyTo(header, 4);
        BitConverter.GetBytes(0x2u).CopyTo(header, 0x14);
        return [.. header, .. info, 0, 0, 0, 0];
    }

    private string CreateShortcut(string relativePath, string target)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, ShortcutTo(target));
        File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddDays(-10));
        return full;
    }

    private string Relative(string path) => Path.GetRelativePath(_root, path);

    [TestMethod]
    public void 既定の規則は枝刈りと保護名とリンクを除いて列挙し走査は一覧と時刻を変えない()
    {
        CreateFile(@"a.tmp");
        CreateFile(@"sub\b.TMP");
        CreateFile(@"keep.txt");
        CreateFile(@"Users\me\e.tmp");
        CreateFile(@"recent.tmp", ageDays: 0);
        CreateFile(@"zero.tmp", content: string.Empty);
        CreateFile(@"FOUND.000\FILE0001.CHK");
        CreateFile(@"other\x.chk");
        CreateFile(@".git\c.tmp");
        CreateFile(@".ssh\k.tmp");
        CreateFile(@"Users\bob\d.tmp");
        CreateFile(@"Windows\f.tmp");
        CreateFile(@"Program Files\g.tmp");
        CreateFile(@"ProgramData\Microsoft\h.tmp");
        CreateFile(@"Self\i.tmp");
        CreateFile(@"sub\Login Data.tmp");
        CreateFile(@"z.tmp", under: _other);
        CreateJunction(Path.Combine(_root, "link"), _other);
        Thread.Sleep(300);
        var before = Snapshot(Path.GetDirectoryName(_root)!);

        var scanner = NewScanner(out _);
        var result = scanner.Scan(Options(), null, CancellationToken.None);

        var after = Snapshot(Path.GetDirectoryName(_root)!);
        Assert.IsEmpty(before.Except(after).Concat(after.Except(before)).ToArray(), "走査で一覧・時刻・属性が変わった");
        var names = result.Items.Select(i => Relative(i.Path)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        CollectionAssert.AreEqual(
            new[] { @"a.tmp", @"FOUND.000\FILE0001.CHK", @"other\x.chk", @"recent.tmp", @"sub\b.TMP", @"Users\me\e.tmp", @"zero.tmp" }.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            names);
        Assert.AreEqual(1, result.SkippedLinks);
        Assert.AreEqual(7, result.PrunedDirectories, ".git・.ssh・bob・Windows・Program Files・ProgramData\\Microsoft・Self");
        Assert.IsFalse(result.Truncated);

        AdvancedCleanItem Item(string relative) => result.Items.Single(i => Relative(i.Path).Equals(relative, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(Item("a.tmp").DefaultSelected);
        Assert.AreEqual(AdvancedPatternGroup.Default, Item("a.tmp").Group);
        Assert.AreEqual(AdvancedVerdict.Safe, Item(@"FOUND.000\FILE0001.CHK").Verdict);
        Assert.IsTrue(Item(@"FOUND.000\FILE0001.CHK").DefaultSelected);
        Assert.AreEqual(AdvancedVerdict.Undeterminable, Item(@"other\x.chk").Verdict);
        Assert.IsFalse(Item(@"other\x.chk").DefaultSelected);
        Assert.AreEqual(AdvancedHint.RecentlyModified, Item("recent.tmp").Hint);
        Assert.IsFalse(Item("recent.tmp").DefaultSelected);
        Assert.AreEqual(AdvancedHint.ZeroByte, Item("zero.tmp").Hint);
        Assert.IsFalse(Item("zero.tmp").DefaultSelected);
        Assert.AreNotEqual(0UL, Item("a.tmp").Identity.FileIdLow | Item("a.tmp").Identity.FileIdHigh);
    }

    [TestMethod]
    public void 追加とユーザー定義と特別な規則は区分と安全度つきで列挙する()
    {
        CreateFile("a.log");
        CreateFile("b.dat");
        CreateFile("c.tmp");
        CreateFile("empty.txt", content: string.Empty);
        CreateFile("old.txt", ageDays: 10);
        CreateFile("new.txt", ageDays: 0);
        CreateShortcut("gone.lnk", @"C:\NoSuchDirectory\app.exe");
        CreateShortcut("live.lnk", @"C:\LiveDirectory\app.exe");
        File.WriteAllBytes(Path.Combine(_root, "bad.lnk"), [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(Path.Combine(_root, "bad.lnk"), DateTime.UtcNow.AddDays(-1));
        var saved = new Dictionary<string, bool> { ["*.log"] = true, ["*.dat"] = true, ["*.tmp"] = false, ["*"] = true };
        var options = new AdvancedScanOptions([_root])
        {
            Protection = Protection(),
            SavedPatterns = saved,
            IncludeZeroByte = true,
            IncludeInvalidShortcuts = true,
            OlderThanDays = 5,
            IsShortcutTargetMissing = path => path.Contains("NoSuch", StringComparison.Ordinal),
        };

        var result = NewScanner(out _).Scan(options, null, CancellationToken.None);

        var byName = result.Items.ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);
        CollectionAssert.AreEquivalent(new[] { "a.log", "b.dat", "empty.txt", "old.txt", "gone.lnk", "live.lnk" }, byName.Keys.ToArray());
        Assert.AreEqual((AdvancedPatternGroup.Additional, AdvancedVerdict.Safe, true), (byName["a.log"].Group, byName["a.log"].Verdict, byName["a.log"].DefaultSelected));
        Assert.AreEqual((AdvancedPatternGroup.UserDefined, AdvancedVerdict.UserDefined, false), (byName["b.dat"].Group, byName["b.dat"].Verdict, byName["b.dat"].DefaultSelected));
        Assert.AreEqual((AdvancedRuleKind.ZeroByte, AdvancedVerdict.Undeterminable, false), (byName["empty.txt"].Kind, byName["empty.txt"].Verdict, byName["empty.txt"].DefaultSelected));
        Assert.AreEqual((AdvancedRuleKind.OldFile, AdvancedHint.OldFile, false), (byName["old.txt"].Kind, byName["old.txt"].Hint, byName["old.txt"].DefaultSelected));
        Assert.AreEqual((AdvancedRuleKind.InvalidShortcut, AdvancedVerdict.Safe, true), (byName["gone.lnk"].Kind, byName["gone.lnk"].Verdict, byName["gone.lnk"].DefaultSelected));
        Assert.AreEqual(@"C:\NoSuchDirectory\app.exe", byName["gone.lnk"].ShortcutTarget);
        Assert.AreEqual(AdvancedRuleKind.OldFile, byName["live.lnk"].Kind, "欠損が確定しない .lnk は古さだけで一致する");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(4)]
    public void 待ち行列の上限を超える幅のツリーでも全件を数える(int parallelism)
    {
        const int Width = 300; // 待ち行列に開いたまま持つハンドルの上限 (256) を超える幅。
        for (var i = 0; i < Width; i++) CreateFile($@"d{i:D3}\f.tmp");
        var options = new AdvancedScanOptions([_root]) { Protection = Protection(), MaxDegreeOfParallelism = parallelism, MaxItems = 10_000 };

        var result = NewScanner(out _).Scan(options, null, CancellationToken.None);

        Assert.AreEqual(Width, result.TotalMatched, "上限を超えた幅の一部を数え落とした");
        Assert.HasCount(Width, result.Items);
        Assert.AreEqual(Width + 1, result.DirectoriesScanned);
        Assert.AreEqual(0, result.Unreadable);
        Assert.IsFalse(result.Truncated);
    }

    [TestMethod]
    public void 上限を超えた分は数えるだけで一覧にも載せず超過を示す()
    {
        for (var i = 0; i < 5; i++) CreateFile($"f{i}.tmp", content: "12345678");
        var options = new AdvancedScanOptions([_root]) { Protection = Protection(), MaxItems = 3 };

        var result = NewScanner(out _).Scan(options, null, CancellationToken.None);

        Assert.HasCount(3, result.Items);
        Assert.AreEqual(5, result.TotalMatched);
        Assert.AreEqual(2, result.OmittedFiles);
        Assert.AreEqual(16, result.OmittedBytes);
        Assert.AreEqual(40, result.TotalBytes);
        Assert.IsTrue(result.Truncated);
    }

    [TestMethod]
    public void 除外のファイルとフォルダーは一覧に載せず残した数に数える()
    {
        CreateFile(@"skip\a.tmp");
        CreateFile(@"keep.tmp");
        CreateFile(@"other.tmp");
        var filter = new CleanupExclusionMatcher(null, [Path.Combine(_root, "skip")], ["keep.tmp"]);
        var options = new AdvancedScanOptions([_root]) { Protection = Protection(), Filter = filter };

        var result = NewScanner(out _).Scan(options, null, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "other.tmp" }, result.Items.Select(i => i.Name).ToArray());
        Assert.AreEqual(2, result.Retained);
    }

    [TestMethod]
    public void 取消はOperationCanceledExceptionになりハンドルを残さない()
    {
        for (var i = 0; i < 40; i++) CreateShortcut($@"d{i % 8}\s{i}.lnk", $@"C:\NoSuch{i}\app.exe");
        using var cts = new CancellationTokenSource();
        var options = new AdvancedScanOptions([_root])
        {
            Protection = Protection(),
            IncludeInvalidShortcuts = true,
            MaxDegreeOfParallelism = 2,
            IsShortcutTargetMissing = _ =>
            {
                cts.Cancel();
                return true;
            },
        };

        Assert.ThrowsExactly<OperationCanceledException>(() => NewScanner(out _).Scan(options, null, cts.Token));

        // 開いたままのフォルダーハンドルが残っていれば、削除できない。
        Directory.Delete(Path.Combine(_root, "d0"), recursive: true);
        Directory.Delete(_root, recursive: true);
        Directory.CreateDirectory(_root);
    }

    [TestMethod]
    public void 再走査と設定変更は前の結果を失効させる()
    {
        CreateFile("a.tmp");
        var scanner = NewScanner(out var ledger);

        var first = scanner.Scan(Options(), null, CancellationToken.None);
        Assert.IsTrue(first.IsCurrent);
        var second = scanner.Scan(Options(), null, CancellationToken.None);

        Assert.IsFalse(first.IsCurrent);
        Assert.IsTrue(second.IsCurrent);
        ledger.Invalidate();
        Assert.IsFalse(second.IsCurrent);
    }

    [TestMethod]
    public void 保護された場所や開けない起点は走査せず結果に残し入れ子の起点は重複しない()
    {
        CreateFile("a.tmp");
        CreateFile(@"sub\b.tmp");
        Directory.CreateDirectory(Path.Combine(_root, "Windows"));
        var scanner = NewScanner(out _);

        var rejected = scanner.Scan(
            new AdvancedScanOptions([Path.Combine(_root, "Windows"), Path.Combine(_root, "missing"), "relative"]) { Protection = Protection() },
            null,
            CancellationToken.None);
        Assert.HasCount(3, rejected.UnavailableRoots);
        Assert.IsEmpty(rejected.Items);

        var nested = scanner.Scan(
            new AdvancedScanOptions([_root, Path.Combine(_root, "sub"), _root.ToUpperInvariant()]) { Protection = Protection() },
            null,
            CancellationToken.None);
        Assert.IsEmpty(nested.UnavailableRoots);
        CollectionAssert.AreEquivalent(new[] { "a.tmp", "b.tmp" }, nested.Items.Select(i => i.Name).ToArray());
    }

    [TestMethod]
    public void カスタムフォルダーは件数を数え拒否と保護された場所は選べない()
    {
        CreateFile(@"custom\a.txt", "123");
        CreateFile(@"custom\sub\b.txt", "45");
        CreateFile(@"Windows\x\w.txt");
        CreateFile(@"Self\keep\s.txt");
        var guard = Guard();
        var options = new AdvancedScanOptions([_root])
        {
            Protection = Protection(),
            CustomFolders =
            [
                CustomFolderGuard.Check(Path.Combine(_root, "custom"), guard),
                CustomFolderGuard.Check(Path.Combine(_root, "Windows", "x"), guard),
                CustomFolderGuard.Check(Path.Combine(_root, "Self", "keep"), guard),
                CustomFolderGuard.Check(Path.Combine(_root, "absent"), guard),
            ],
        };

        var result = NewScanner(out _).Scan(options, null, CancellationToken.None);

        var custom = result.CustomFolders[0];
        Assert.IsTrue(custom.IsSelectable);
        Assert.IsFalse(custom.DefaultSelected, "許可されたカスタムフォルダーも既定では未選択");
        Assert.AreEqual((2L, 1L, 5L), (custom.Files, custom.Directories, custom.Bytes));
        Assert.AreEqual(CustomFolderRejection.Windows, result.CustomFolders[1].Rejection);
        Assert.IsFalse(result.CustomFolders[1].IsSelectable);
        Assert.IsNotNull(result.CustomFolders[2].OtherReason, "Lumin4ti 自身を含むフォルダーは選べない");
        Assert.IsFalse(result.CustomFolders[2].IsSelectable);
        Assert.IsTrue(result.CustomFolders[3].Missing);
        Assert.IsFalse(result.CustomFolders[3].DefaultSelected);
    }

    [TestMethod]
    public void 枝刈りと保護名の判定はドライブ直下の領域と他利用者と認証情報を含む()
    {
        var guard = new CustomFolderGuardContext(@"Z:\Windows", [@"Z:\Program Files"], @"Z:\ProgramData", [@"Z:\Users"], @"Z:\Users\me");
        var protection = new AdvancedProtectedPaths(new AdvancedProtectionContext(guard, [@"Z:\Tools\Lumin4ti"]));

        Assert.IsTrue(protection.IsPrunedDirectory(@"Z:\$Recycle.Bin", "$Recycle.Bin", @"Z:\"));
        Assert.IsTrue(protection.IsPrunedDirectory(@"Z:\System Volume Information", "System Volume Information", @"Z:\"));
        Assert.IsFalse(protection.IsPrunedDirectory(@"Z:\Data\Recovery", "Recovery", @"Z:\Data"), "ドライブ直下の名前だけが対象");
        Assert.IsTrue(protection.IsPrunedDirectory(@"Z:\Users\bob", "bob", @"Z:\Users"));
        Assert.IsFalse(protection.IsPrunedDirectory(@"Z:\Users\me", "me", @"Z:\Users"));
        Assert.IsTrue(protection.IsPrunedDirectory(@"Z:\Users\me\.aws", ".aws", @"Z:\Users\me"));
        Assert.IsTrue(protection.IsPrunedDirectory(@"Z:\work\.git", ".git", @"Z:\work"));
        Assert.IsTrue(protection.IsPrunedDirectory(@"Z:\ProgramData\Microsoft", "Microsoft", @"Z:\ProgramData"));
        Assert.IsFalse(protection.IsPrunedDirectory(@"Z:\ProgramData\Other", "Other", @"Z:\ProgramData"));
        Assert.IsTrue(protection.IsPrunedDirectory(@"Z:\Tools\Lumin4ti", "Lumin4ti", @"Z:\Tools"));
        Assert.IsTrue(protection.IsProtectedPath(@"Z:\Windows\System32\drivers"));
        Assert.IsTrue(protection.IsProtectedPath(@"Z:\Users\bob\Documents"));
        Assert.IsFalse(protection.IsProtectedPath(@"Z:\Users\me\Downloads"));
        Assert.IsTrue(protection.OverlapsProtected(@"Z:\ProgramData"), "保護された場所を含むフォルダーは巻き込む");
        Assert.IsTrue(protection.OverlapsProtected(@"Z:\Tools"));
        Assert.IsFalse(protection.OverlapsProtected(@"Z:\Users\me\Downloads"));
        foreach (var name in new[] { "key4.db", "Login Data", "Login Data-journal", "logins.json", "vault.kdbx", "vault.kdbx.bak", "id_rsa.bak", "a.pfx" })
        {
            Assert.IsTrue(AdvancedProtectedPaths.IsProtectedFile(name), name);
        }

        Assert.IsFalse(AdvancedProtectedPaths.IsProtectedFile("a.tmp"));
    }

    [TestMethod]
    public void パターンは保存された選択で上書きし優先順の高い規則を返し全ワイルドカードを受け付けない()
    {
        var defaults = AdvancedPatterns.Resolve(null);
        Assert.HasCount(12, defaults.Rules);
        Assert.AreEqual("*.tmp", defaults.Match("A.TMP")?.Pattern);
        Assert.AreEqual("*.$$$", defaults.Match("x.$$$")?.Pattern);
        Assert.IsNull(defaults.Match("a.log"));
        Assert.IsNull(defaults.Match("tmp"));

        var set = AdvancedPatterns.Resolve(new Dictionary<string, bool>
        {
            ["*.tmp"] = false, ["*.log"] = true, ["~*.*"] = true, ["thumbs.db"] = true, ["*.DAT"] = true, ["*"] = true, ["*.*"] = true, [@"a\b"] = true,
        });
        Assert.IsNull(set.Match("a.tmp"));
        Assert.AreEqual(AdvancedPatternGroup.Additional, set.Match("a.log")?.Group);
        Assert.AreEqual("~*.*", set.Match("~doc.docx")?.Pattern);
        Assert.AreEqual("thumbs.db", set.Match("Thumbs.DB")?.Pattern);
        Assert.AreEqual(AdvancedPatternGroup.UserDefined, set.Match("x.dat")?.Group);
        Assert.AreEqual(1, set.Rules.Count(r => r.Group == AdvancedPatternGroup.UserDefined), "全てを選ぶ形と不正な形は取り込まない");
        Assert.AreEqual("*.log", set.Match("~old.log")?.Pattern, "複数に一致したら表の先の規則を返す");
    }

    [TestMethod]
    public void 安全度はchkの置き場所と24時間以内の更新と0バイトで決まる()
    {
        var now = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        var rule = AdvancedPatterns.Resolve(null).Match("a.chk")!;
        var tmp = AdvancedPatterns.Resolve(null).Match("a.tmp")!;
        long Time(TimeSpan ago) => now.Subtract(ago).ToFileTimeUtc();

        Assert.AreEqual(new AdvancedClassification(AdvancedVerdict.Safe, true, AdvancedHint.None),
            AdvancedPatterns.Classify(rule, "FILE0001.CHK", "FOUND.001", 10, Time(TimeSpan.FromDays(3)), now));
        foreach (var (name, parent) in new[] { ("FILE0001.CHK", "FOUND.01"), ("FILE001.CHK", "FOUND.001"), ("x.chk", "FOUND.001"), ("FILE0001.CHK", "Other"), ("FILE0001.CHK", null) })
        {
            var verdict = AdvancedPatterns.Classify(rule, name, parent, 10, Time(TimeSpan.FromDays(3)), now);
            Assert.AreEqual((AdvancedVerdict.Undeterminable, false, AdvancedHint.CheckpointOutsideFound), (verdict.Verdict, verdict.DefaultSelected, verdict.Hint), $"{name} in {parent}");
        }

        Assert.IsFalse(AdvancedPatterns.Classify(tmp, "a.tmp", "x", 10, Time(TimeSpan.FromHours(23)), now).DefaultSelected);
        Assert.IsTrue(AdvancedPatterns.Classify(tmp, "a.tmp", "x", 10, Time(TimeSpan.FromHours(25)), now).DefaultSelected);
        Assert.AreEqual(AdvancedHint.ZeroByte, AdvancedPatterns.Classify(tmp, "a.tmp", "x", 0, Time(TimeSpan.FromDays(3)), now).Hint);
    }
}
