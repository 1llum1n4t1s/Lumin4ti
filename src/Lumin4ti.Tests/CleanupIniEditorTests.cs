using System.Text;
using Lumin4ti.Core.Services.Windows.Cleanup;

namespace Lumin4ti.Tests;

/// <summary>INI は一時領域の実ファイルで検証する (レジストリ・実アプリの設定は触らない)。</summary>
[TestClass]
public sealed class CleanupIniEditorTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "Lumin4tiIniTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Write(byte[] bytes, string name = "app.ini")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static CleanupIniKey Key(string section, string key) => new() { Section = section, Key = key };

    private static readonly CancellationToken None = CancellationToken.None;

    [TestMethod]
    public void RemovesMatchingKeysAndKeepsEverythingElseByteForByte()
    {
        const string text = "[Recent]\r\nFile1=a.txt\r\nFile2=b.txt\r\nFile10=c.txt\r\nFileX=keep\r\nCount=3\r\n[Other]\r\nFile1=keep\r\n; note\r\nLast=1";
        var path = Write(new UTF8Encoding(false).GetBytes(text));

        var result = new CleanupIniEditor().Remove(path, [Key("recent", "file*"), Key("Recent", "count")], None);

        Assert.AreEqual(CleanupIniStatus.Changed, result.Status);
        Assert.AreEqual(4, result.RemovedKeys);
        Assert.AreEqual("[Recent]\r\nFileX=keep\r\n[Other]\r\nFile1=keep\r\n; note\r\nLast=1", File.ReadAllText(path));
    }

    [TestMethod]
    public void ScanCountsWithoutChangingTheFile()
    {
        var bytes = Encoding.UTF8.GetBytes("[S]\nK1=1\nK2=2\n");
        var path = Write(bytes);
        var before = File.GetLastWriteTimeUtc(path);

        var result = new CleanupIniEditor().Scan(path, [Key("S", "K*")], None);

        Assert.AreEqual(2, result.RemovedKeys);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        Assert.AreEqual(before, File.GetLastWriteTimeUtc(path));
    }

    [TestMethod]
    public void NoMatchLeavesTheFileUntouchedAndMissingIsReported()
    {
        var path = Write("[S]\nA=1\n"u8.ToArray());
        var editor = new CleanupIniEditor();

        Assert.AreEqual(CleanupIniStatus.NoChange, editor.Remove(path, [Key("S", "B")], None).Status);
        Assert.AreEqual(CleanupIniStatus.Missing, editor.Remove(Path.Combine(_root, "none.ini"), [Key("S", "B")], None).Status);
        Assert.AreEqual("[S]\nA=1\n", File.ReadAllText(path));
    }

    [TestMethod]
    public void Utf16WithBomAndUtf8WithBomRoundTrip()
    {
        foreach (var encoding in new Encoding[] { new UnicodeEncoding(false, true), new UnicodeEncoding(true, true), new UTF8Encoding(true) })
        {
            var original = encoding.GetPreamble().Concat(encoding.GetBytes("[S]\r\nA=あ\r\nB=い\r\n")).ToArray();
            var path = Write(original, "enc.ini");

            var result = new CleanupIniEditor().Remove(path, [Key("S", "A")], None);

            Assert.AreEqual(CleanupIniStatus.Changed, result.Status, encoding.WebName);
            var expected = encoding.GetPreamble().Concat(encoding.GetBytes("[S]\r\nB=い\r\n")).ToArray();
            CollectionAssert.AreEqual(expected, File.ReadAllBytes(path), encoding.WebName);
        }
    }

    [TestMethod]
    public void NonReversibleOrOversizedFilesAreSkippedUnchanged()
    {
        var editor = new CleanupIniEditor();
        var cases = new Dictionary<string, byte[]>
        {
            ["odd-utf16"] = [0xFF, 0xFE, 0x5B, 0x00, 0x53],
            ["nul"] = "[S]\nA=1\0\n"u8.ToArray(),
            ["huge"] = Encoding.ASCII.GetBytes("[S]\nA=" + new string('x', CleanupIniEditor.MaxBytes)),
        };
        foreach (var (name, bytes) in cases)
        {
            var path = Write(bytes, name + ".ini");
            Assert.AreEqual(CleanupIniStatus.Skipped, editor.Remove(path, [Key("S", "A")], None).Status, name);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), name);
        }
    }

    [TestMethod]
    public void DuplicateTargetSectionsAreAmbiguousAndNotChanged()
    {
        var bytes = "[S]\nA=1\n[T]\nA=2\n[s]\nA=3\n"u8.ToArray();
        var path = Write(bytes);

        var result = new CleanupIniEditor().Remove(path, [Key("S", "A")], None);

        Assert.AreEqual(CleanupIniStatus.Skipped, result.Status);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void InvalidKeySpecsAreSkipped()
    {
        var path = Write("[S]\nA=1\n"u8.ToArray());
        var editor = new CleanupIniEditor();
        foreach (var keys in new CleanupIniKey[][] { [], [Key("S", "*")], [Key("S", "A*B")], [Key("", "A")], [Key("S*", "A")], [Key("S", "A=B")] })
            Assert.AreEqual(CleanupIniStatus.Skipped, editor.Remove(path, keys, None).Status);
        Assert.AreEqual(CleanupIniStatus.Skipped, editor.Remove("app.ini", [Key("S", "A")], None).Status);
    }

    [TestMethod]
    public void WriteFailureRestoresTheOriginalAndReportsFailed()
    {
        var bytes = Encoding.UTF8.GetBytes("[S]\nA=1\nB=2\nC=3\n");
        var path = Write(bytes);
        var opener = new FaultyOpener(failWrites: 1);

        var result = new CleanupIniEditor(opener).Remove(path, [Key("S", "B")], None);

        Assert.AreEqual(CleanupIniStatus.Failed, result.Status);
        Assert.IsTrue(result.FileRestored);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void FailedCompensationIsReportedAsNotRestored()
    {
        var path = Write("[S]\nA=1\nB=2\n"u8.ToArray());
        var opener = new FaultyOpener(failWrites: 2);

        var result = new CleanupIniEditor(opener).Remove(path, [Key("S", "B")], None);

        Assert.AreEqual(CleanupIniStatus.Failed, result.Status);
        Assert.IsFalse(result.FileRestored);
    }

    [TestMethod]
    public void FileHeldWithoutWriteSharingIsReportedAsFailed()
    {
        var path = Write("[S]\nA=1\n"u8.ToArray());
        using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        var result = new CleanupIniEditor().Remove(path, [Key("S", "A")], None);

        Assert.AreEqual(CleanupIniStatus.Failed, result.Status);
        holder.Position = 0;
        Assert.AreEqual("[S]\nA=1\n", new StreamReader(holder).ReadToEnd());
    }

    [TestMethod]
    public void JunctionPathIsNotFollowed()
    {
        var real = Path.Combine(_root, "real");
        Directory.CreateDirectory(real);
        var target = Path.Combine(real, "a.ini");
        File.WriteAllText(target, "[S]\nA=1\n");
        var link = Path.Combine(_root, "link");
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/c mklink /J \"{link}\" \"{real}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!)
        {
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode);
        }

        var result = new CleanupIniEditor().Remove(Path.Combine(link, "a.ini"), [Key("S", "A")], None);

        Assert.AreEqual(CleanupIniStatus.Failed, result.Status);
        Assert.AreEqual("[S]\nA=1\n", File.ReadAllText(target));
        Directory.Delete(link);
    }

    [TestMethod]
    public void CancellationBeforeWriteLeavesTheFileUnchanged()
    {
        var bytes = "[S]\nA=1\n"u8.ToArray();
        var path = Write(bytes);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => new CleanupIniEditor().Remove(path, [Key("S", "A")], cts.Token));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
    }

    /// <summary>実ファイルを開き、最初の <c>failWrites</c> 回の書込みを途中まで書いてから失敗させる。</summary>
    private sealed class FaultyOpener(int failWrites) : ICleanupIniOpener
    {
        private int _remaining = failWrites;

        public ICleanupIniStream Open(string path, bool writable) =>
            new FaultyStream(Lumin4ti.Core.Services.Windows.Cleanup.NativeCleanupIniOpener.Instance.Open(path, writable), this);

        private bool ShouldFail() => _remaining-- > 0;

        private sealed class FaultyStream(ICleanupIniStream inner, FaultyOpener owner) : ICleanupIniStream
        {
            public long Length => inner.Length;

            public int Read(Span<byte> buffer, long offset) => inner.Read(buffer, offset);

            public void Write(ReadOnlySpan<byte> data, long offset)
            {
                if (!owner.ShouldFail()) { inner.Write(data, offset); return; }
                inner.Write(data[..(data.Length / 2)], offset);
                throw new IOException("注入した書込み失敗");
            }

            public void SetLength(long length) => inner.SetLength(length);

            public void Flush() => inner.Flush();

            public void Dispose() => inner.Dispose();
        }
    }
}
