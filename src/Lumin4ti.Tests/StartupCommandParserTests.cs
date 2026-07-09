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
}
