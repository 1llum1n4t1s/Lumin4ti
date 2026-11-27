using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class StartupCommandParserTests
{
    [TestMethod]
    public void 引用符付きパスを解決できる()
    {
        var exe = StartupCommandParser.TryResolveExecutable(@"""C:\Program Files\Foo\foo.exe"" --minimized");

        Assert.AreEqual(@"C:\Program Files\Foo\foo.exe", exe);
    }

    [TestMethod]
    public void 引用符なしパスを解決できる()
    {
        var exe = StartupCommandParser.TryResolveExecutable(@"C:\Tools\bar.exe /silent");

        Assert.AreEqual(@"C:\Tools\bar.exe", exe);
    }

    [TestMethod]
    public void 環境変数を展開する()
    {
        var exe = StartupCommandParser.TryResolveExecutable(@"""%SystemRoot%\system32\notepad.exe""");

        Assert.IsNotNull(exe);
        Assert.IsFalse(exe.Contains('%'));
        StringAssert.EndsWith(exe, @"\system32\notepad.exe", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void ラッパー経由は対象外()
    {
        Assert.IsNull(StartupCommandParser.TryResolveExecutable(@"C:\Windows\System32\rundll32.exe foo.dll,Entry"));
        Assert.IsNull(StartupCommandParser.TryResolveExecutable(@"""C:\Windows\System32\cmd.exe"" /c start x"));
    }

    [TestMethod]
    public void 相対パスや解決不能な値は対象外()
    {
        Assert.IsNull(StartupCommandParser.TryResolveExecutable("foo.exe --flag"));
        Assert.IsNull(StartupCommandParser.TryResolveExecutable("何かのテキスト"));
        Assert.IsNull(StartupCommandParser.TryResolveExecutable(""));
    }

    [TestMethod]
    public void ドライブ相対パスは削除対象として解決しない()
    {
        Assert.IsNull(StartupCommandParser.TryResolveExecutable(@"C:Tools\app.exe --silent"));
        Assert.IsFalse(StartupCommandParser.CanTreatMissingAsBroken(
            @"C:Tools\app.exe",
            _ => (DriveType.Fixed, true)));
    }

    [TestMethod]
    public void 欠損と断定するのは準備済み固定ドライブだけ()
    {
        static (DriveType, bool) FixedReady(string _) => (DriveType.Fixed, true);
        static (DriveType, bool) FixedNotReady(string _) => (DriveType.Fixed, false);
        static (DriveType, bool) Network(string _) => (DriveType.Network, true);

        Assert.IsTrue(StartupCommandParser.CanTreatMissingAsBroken(@"C:\Tools\gone.exe", FixedReady));
        Assert.IsFalse(StartupCommandParser.CanTreatMissingAsBroken(@"C:\Tools\gone.exe", FixedNotReady));
        Assert.IsFalse(StartupCommandParser.CanTreatMissingAsBroken(@"X:\Tools\gone.exe", Network));
        Assert.IsFalse(StartupCommandParser.CanTreatMissingAsBroken(@"\\server\share\gone.exe", FixedReady));
    }

    [TestMethod]
    public void 不存在が明示された場合だけ欠損と確定する()
    {
        static (DriveType, bool) FixedReady(string _) => (DriveType.Fixed, true);
        static FileAttributes Existing(string _) => FileAttributes.Normal;
        static FileAttributes Missing(string path) =>
            path.EndsWith("gone.exe", StringComparison.OrdinalIgnoreCase)
                ? throw new FileNotFoundException()
                : FileAttributes.Directory;
        static FileAttributes Denied(string _) => throw new UnauthorizedAccessException();

        Assert.IsFalse(StartupCommandParser.IsConfirmedMissing(@"C:\Tools\app.exe", FixedReady, Existing));
        Assert.IsTrue(StartupCommandParser.IsConfirmedMissing(@"C:\Tools\gone.exe", FixedReady, Missing));
        Assert.IsFalse(StartupCommandParser.IsConfirmedMissing(@"C:\Tools\private.exe", FixedReady, Denied));
    }

    [TestMethod]
    public void 再解析点配下の不存在は一時不在として保持する()
    {
        const string executable = @"C:\Mounted\Tools\gone.exe";
        static (DriveType, bool) FixedReady(string _) => (DriveType.Fixed, true);
        static FileAttributes Attributes(string path)
        {
            if (path.Equals(executable, StringComparison.OrdinalIgnoreCase))
            {
                throw new DirectoryNotFoundException();
            }

            return path.TrimEnd('\\').Equals(@"C:\Mounted", StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : FileAttributes.Directory;
        }

        Assert.IsFalse(StartupCommandParser.IsConfirmedMissing(executable, FixedReady, Attributes));
    }
}
