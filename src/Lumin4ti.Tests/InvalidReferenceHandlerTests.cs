using System.Text;
using Lumin4ti.Core.Services.Windows.Cleanup.Handlers;

namespace Lumin4ti.Tests;

/// <summary>
/// 無効なショートカット・タスクのハンドラー。ショートカットは一時フォルダーの合成 .lnk だけを使い、
/// タスクスケジューラーは偽の境界に差し替える。実機のショートカット・タスクには触れない。
/// </summary>
[TestClass]
public sealed class InvalidReferenceHandlerTests
{
    private const uint HasIdList = 0x1;
    private const uint HasLinkInfo = 0x2;
    private const uint IsUnicode = 0x80;
    private const uint HasDarwinId = 0x1000;

    private string _root = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "Lumin4tiInvalidRefTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // テスト後の掃除に失敗してもテスト結果には影響させない
        }
    }

    // ---- ショートカット ----

    [TestMethod]
    public async Task 欠損が確定したショートカットだけを消し_判定不能と通常ファイルは残す()
    {
        var desktop = Directory.CreateDirectory(Path.Combine(_root, "Desktop")).FullName;
        var broken = WriteLink(desktop, "broken.lnk", Link(HasLinkInfo | IsUnicode, LinkInfo(3, @"C:\gone\app.exe")));
        var alive = WriteLink(desktop, "alive.lnk", Link(HasLinkInfo | IsUnicode, LinkInfo(3, @"C:\alive\app.exe")));
        var advertised = WriteLink(desktop, "advertised.lnk", Link(HasLinkInfo | IsUnicode | HasDarwinId, LinkInfo(3, @"C:\gone\a.exe")));
        var removable = WriteLink(desktop, "removable.lnk", Link(HasLinkInfo | IsUnicode, LinkInfo(2, @"E:\gone\a.exe")));
        var pidl = WriteLink(desktop, "pidl.lnk", Link(HasIdList, null));
        var garbage = WriteLink(desktop, "garbage.lnk", [1, 2, 3]);
        var notLink = Path.Combine(desktop, "gone.txt");
        File.WriteAllText(notLink, "x");
        var handler = Shortcuts(new ShortcutRoot(desktop, false));

        var result = await handler.RunAsync(null, CancellationToken.None);

        Assert.AreEqual(CleanupHandlerStatus.Succeeded, result.Status);
        Assert.AreEqual(1, result.Items);
        Assert.IsFalse(File.Exists(broken));
        foreach (var kept in new[] { alive, advertised, removable, pidl, garbage, notLink }) Assert.IsTrue(File.Exists(kept), kept);
    }

    [TestMethod]
    public async Task ショートカットの走査は何も消さず_再帰する場所だけサブフォルダーを探す()
    {
        var recursive = Directory.CreateDirectory(Path.Combine(_root, "Start", "Programs", "Vendor")).FullName;
        var flat = Directory.CreateDirectory(Path.Combine(_root, "Desktop", "Sub")).FullName;
        var inStart = WriteLink(recursive, "a.lnk", Link(HasLinkInfo | IsUnicode, LinkInfo(3, @"C:\gone\a.exe")));
        var inSub = WriteLink(flat, "b.LNK", Link(HasLinkInfo | IsUnicode, LinkInfo(3, @"C:\gone\b.exe")));
        var handler = Shortcuts(new ShortcutRoot(Path.Combine(_root, "Start"), true), new ShortcutRoot(Path.Combine(_root, "Desktop"), false));
        var before = File.GetLastWriteTimeUtc(inStart);

        var scan = await handler.ScanAsync(CancellationToken.None);

        Assert.AreEqual(CleanupHandlerScanState.Found, scan.State);
        Assert.AreEqual(1, scan.Items);
        Assert.AreEqual(new FileInfo(inStart).Length, scan.Bytes);
        Assert.IsTrue(File.Exists(inStart));
        Assert.IsTrue(File.Exists(inSub));
        Assert.AreEqual(before, File.GetLastWriteTimeUtc(inStart));
    }

    [TestMethod]
    public async Task 実行の直前にリンク先が復活していたら残す()
    {
        var desktop = Directory.CreateDirectory(Path.Combine(_root, "Desktop")).FullName;
        var link = WriteLink(desktop, "back.lnk", Link(HasLinkInfo | IsUnicode, LinkInfo(3, @"C:\gone\app.exe")));
        var missing = true;
        var handler = new InvalidShortcutHandler([new ShortcutRoot(desktop, false)], _ => missing);
        await handler.ScanAsync(CancellationToken.None);
        missing = false;

        var result = await handler.RunAsync(null, CancellationToken.None);

        Assert.AreEqual(CleanupHandlerStatus.NothingToDo, result.Status);
        Assert.IsTrue(File.Exists(link));
    }

    [TestMethod]
    public async Task 走査後に差し替えられたショートカットは同一性が違うため消さない()
    {
        var desktop = Directory.CreateDirectory(Path.Combine(_root, "Desktop")).FullName;
        var link = WriteLink(desktop, "swap.lnk", Link(HasLinkInfo | IsUnicode, LinkInfo(3, @"C:\gone\app.exe")));
        var handler = Shortcuts(new ShortcutRoot(desktop, false));
        await handler.ScanAsync(CancellationToken.None);
        var replacement = Link(HasLinkInfo | IsUnicode, LinkInfo(3, @"C:\gone\a-much-longer-name\replaced.exe"));
        File.WriteAllBytes(link, replacement);

        var result = await handler.RunAsync(null, CancellationToken.None);

        Assert.AreEqual(CleanupHandlerStatus.NothingToDo, result.Status);
        CollectionAssert.AreEqual(replacement, File.ReadAllBytes(link));
    }

    [TestMethod]
    public async Task 走査せずに実行すると先に走査し_取消では何も消さない()
    {
        var desktop = Directory.CreateDirectory(Path.Combine(_root, "Desktop")).FullName;
        var link = WriteLink(desktop, "a.lnk", Link(HasLinkInfo | IsUnicode, LinkInfo(3, @"C:\gone\app.exe")));
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        var cancelResult = await Shortcuts(new ShortcutRoot(desktop, false)).RunAsync(null, canceled.Token);
        Assert.AreEqual(CleanupHandlerStatus.Canceled, cancelResult.Status);
        Assert.IsTrue(File.Exists(link));

        var result = await Shortcuts(new ShortcutRoot(desktop, false)).RunAsync(null, CancellationToken.None);
        Assert.AreEqual(CleanupHandlerStatus.Succeeded, result.Status);
        Assert.IsFalse(File.Exists(link));
    }

    [TestMethod]
    public async Task ショートカットの場所が無ければ空で_開けなければ利用不可にする()
    {
        var none = await Shortcuts(new ShortcutRoot(Path.Combine(_root, "missing"), true)).ScanAsync(CancellationToken.None);
        var rejected = await Shortcuts(new ShortcutRoot("relative\\path", true)).ScanAsync(CancellationToken.None);

        Assert.AreEqual(CleanupHandlerScanState.Empty, none.State);
        Assert.AreEqual(CleanupHandlerScanState.Unavailable, rejected.State);
    }

    // ---- タスク ----

    [TestMethod]
    [DataRow(@"C:\gone\a.exe", true)]
    [DataRow(@"""C:\Program Files\gone\a.exe""", true)]
    [DataRow(@"C:\alive\a.exe", false)]
    [DataRow(@"C:\Program Files\gone\a.exe", false)]
    [DataRow(@"%LOCALAPPDATA%\gone\a.exe", false)]
    [DataRow(@"gone\a.exe", false)]
    [DataRow(@"cmd.exe", false)]
    [DataRow(@"""C:\gone\a.exe"" /s", false)]
    [DataRow("", false)]
    public void 単一Execの欠損は絶対パスが確定した場合だけ確定する(string command, bool expected)
    {
        var verdict = InvalidScheduledTaskHandler.Evaluate(TaskXml(command), path => path.Contains("gone"));

        Assert.AreEqual(expected, verdict.Missing, verdict.Reason);
    }

    [TestMethod]
    public void 単一Exec以外と壊れた定義は判定しない()
    {
        const string ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var two = $"<Task xmlns=\"{ns}\"><Actions><Exec><Command>C:\\gone\\a.exe</Command></Exec><Exec><Command>C:\\gone\\b.exe</Command></Exec></Actions></Task>";
        var com = $"<Task xmlns=\"{ns}\"><Actions><ComHandler><ClassId>{{00000000-0000-0000-0000-000000000000}}</ClassId></ComHandler></Actions></Task>";
        var dtd = "<!DOCTYPE t [<!ENTITY x \"y\">]><Task xmlns=\"" + ns + "\"><Actions><Exec><Command>C:\\gone\\a.exe</Command></Exec></Actions></Task>";

        foreach (var xml in new[] { two, com, dtd, "<Task", string.Empty })
        {
            Assert.IsFalse(InvalidScheduledTaskHandler.Evaluate(xml, _ => true).Missing, xml);
        }
    }

    [TestMethod]
    public async Task 欠損が確定したタスクだけを消し_Microsoft配下とLumin4ti自身は触れない()
    {
        var gone = new FakeTask(@"\Vendor\Updater", TaskXml(@"C:\gone\a.exe"));
        var alive = new FakeTask(@"\Vendor\Alive", TaskXml(@"C:\alive\a.exe"));
        var microsoft = new FakeTask(@"\Microsoft\Windows\Gone", TaskXml(@"C:\gone\a.exe"));
        var own = new FakeTask(@"\Lumin4tiRebootCleanup\Task", TaskXml(@"C:\gone\a.exe"));
        var ownRoot = new FakeTask(@"\Lumin4tiX", TaskXml(@"C:\gone\a.exe"));
        var msLookalike = new FakeTask(@"\MicrosoftEdgeUpdate", TaskXml(@"C:\gone\a.exe"));
        var handler = Tasks(new FakeCatalog(gone, alive, microsoft, own, ownRoot, msLookalike));

        var scan = await handler.ScanAsync(CancellationToken.None);
        var result = await handler.RunAsync(null, CancellationToken.None);

        Assert.AreEqual(CleanupHandlerScanState.Found, scan.State);
        Assert.AreEqual(2, scan.Items);
        Assert.AreEqual(CleanupHandlerStatus.Succeeded, result.Status);
        Assert.AreEqual(2, result.Items);
        Assert.IsTrue(gone.Deleted && msLookalike.Deleted);
        Assert.IsFalse(alive.Deleted || microsoft.Deleted || own.Deleted || ownRoot.Deleted);
    }

    [TestMethod]
    public async Task 走査になかったタスクと削除直前に変わったタスクは消さない()
    {
        var late = new FakeTask(@"\Late", TaskXml(@"C:\alive\a.exe"));
        var changing = new FakeTask(@"\Changing", TaskXml(@"C:\gone\a.exe"));
        var catalog = new FakeCatalog(changing, late);
        var handler = Tasks(catalog);
        await handler.ScanAsync(CancellationToken.None);
        late.Xml = TaskXml(@"C:\gone\a.exe");
        changing.Xml = TaskXml(@"C:\alive\a.exe");

        var result = await handler.RunAsync(null, CancellationToken.None);

        Assert.AreEqual(CleanupHandlerStatus.NothingToDo, result.Status);
        Assert.IsFalse(late.Deleted);
        Assert.IsFalse(changing.Deleted);
    }

    [TestMethod]
    public async Task タスクの削除失敗は成功にせず_取消と照会失敗も区別する()
    {
        var bad = new FakeTask(@"\Bad", TaskXml(@"C:\gone\a.exe")) { DeleteError = new UnauthorizedAccessException("denied") };
        var good = new FakeTask(@"\Good", TaskXml(@"C:\gone\b.exe"));

        var partial = await Tasks(new FakeCatalog(bad, good)).RunAsync(null, CancellationToken.None);
        var failed = await Tasks(new FakeCatalog(bad)).RunAsync(null, CancellationToken.None);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        var cancelResult = await Tasks(new FakeCatalog(good)).RunAsync(null, canceled.Token);
        var unavailable = await new InvalidScheduledTaskHandler(() => throw new InvalidOperationException("com"), _ => true).ScanAsync(CancellationToken.None);

        Assert.AreEqual(CleanupHandlerStatus.Partial, partial.Status);
        Assert.AreEqual(1, partial.Items);
        Assert.AreEqual(1, partial.Details.Count);
        Assert.AreEqual(CleanupHandlerStatus.Failed, failed.Status);
        Assert.AreEqual(CleanupHandlerStatus.Canceled, cancelResult.Status);
        Assert.AreEqual(CleanupHandlerScanState.Unavailable, unavailable.State);
    }

    private static InvalidScheduledTaskHandler Tasks(FakeCatalog catalog) =>
        new(() => catalog, path => path.Contains("gone"));

    private static InvalidShortcutHandler Shortcuts(params ShortcutRoot[] roots) =>
        new(roots, path => path.Contains("gone"));

    private static string TaskXml(string command) =>
        "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Actions Context=\"Author\"><Exec><Command>" +
        command.Replace("&", "&amp;") + "</Command></Exec></Actions></Task>";

    private sealed class FakeTask(string path, string xml) : IScheduledTaskItem
    {
        public string Path { get; } = path;

        public string Xml { get; set; } = xml;

        public bool Deleted { get; private set; }

        public Exception? DeleteError { get; init; }

        public string ReadXml() => Xml;

        public void Delete()
        {
            if (DeleteError is not null) throw DeleteError;
            Deleted = true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeCatalog(params FakeTask[] tasks) : IScheduledTaskCatalog
    {
        public ScheduledTaskListing List(CancellationToken ct)
        {
            var listing = new ScheduledTaskListing();
            listing.Items.AddRange(tasks);
            return listing;
        }

        public void Dispose()
        {
        }
    }

    // ---- 合成 .lnk ----

    private static string WriteLink(string directory, string name, byte[] bytes)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Link(uint flags, byte[]? linkInfo)
    {
        var bytes = new List<byte>();
        var header = new byte[0x4C];
        BitConverter.GetBytes(0x4C).CopyTo(header, 0);
        new byte[] { 0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 }.CopyTo(header, 4);
        BitConverter.GetBytes(flags).CopyTo(header, 0x14);
        bytes.AddRange(header);
        if ((flags & HasIdList) != 0) bytes.AddRange(new byte[2]);
        if (linkInfo is not null) bytes.AddRange(linkInfo);
        bytes.AddRange(new byte[4]);
        return [.. bytes];
    }

    /// <summary>Unicode のパスを持つ LinkInfo (VolumeIDAndLocalBasePath)。</summary>
    private static byte[] LinkInfo(uint driveType, string basePath)
    {
        const int headerSize = 0x24;
        var volume = new byte[17];
        BitConverter.GetBytes(volume.Length).CopyTo(volume, 0);
        BitConverter.GetBytes(driveType).CopyTo(volume, 4);
        BitConverter.GetBytes(0x10).CopyTo(volume, 12);

        var ansiBase = Encoding.Latin1.GetBytes(basePath + "\0");
        var ansiSuffix = Encoding.Latin1.GetBytes("\0");
        var unicodeBase = Encoding.Unicode.GetBytes(basePath + "\0");
        var unicodeSuffix = Encoding.Unicode.GetBytes("\0");

        var volumeOffset = headerSize;
        var ansiBaseOffset = volumeOffset + volume.Length;
        var ansiSuffixOffset = ansiBaseOffset + ansiBase.Length;
        var unicodeBaseOffset = ansiSuffixOffset + ansiSuffix.Length;
        var unicodeSuffixOffset = unicodeBaseOffset + unicodeBase.Length;
        var size = unicodeSuffixOffset + unicodeSuffix.Length;

        var info = new byte[size];
        BitConverter.GetBytes(size).CopyTo(info, 0);
        BitConverter.GetBytes(headerSize).CopyTo(info, 4);
        BitConverter.GetBytes(1u).CopyTo(info, 8);
        BitConverter.GetBytes(volumeOffset).CopyTo(info, 12);
        BitConverter.GetBytes(ansiBaseOffset).CopyTo(info, 16);
        BitConverter.GetBytes(ansiSuffixOffset).CopyTo(info, 24);
        BitConverter.GetBytes(unicodeBaseOffset).CopyTo(info, 28);
        BitConverter.GetBytes(unicodeSuffixOffset).CopyTo(info, 32);
        volume.CopyTo(info, volumeOffset);
        ansiBase.CopyTo(info, ansiBaseOffset);
        ansiSuffix.CopyTo(info, ansiSuffixOffset);
        unicodeBase.CopyTo(info, unicodeBaseOffset);
        unicodeSuffix.CopyTo(info, unicodeSuffixOffset);
        return info;
    }
}
