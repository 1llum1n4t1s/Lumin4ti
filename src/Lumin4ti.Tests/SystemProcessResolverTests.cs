using Lumin4ti.Core.Services;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class SystemProcessResolverTests
{
    [TestMethod]
    public void 既にフルパスならそのまま返す()
    {
        const string full = @"C:\Program Files\Windows Defender\MpCmdRun.exe";

        Assert.AreEqual(full, SystemProcessResolver.Resolve(full));
    }

    [TestMethod]
    public void System32常駐exeはSystem32のフルパスへ解決される()
    {
        // dism.exe は実機の System32 に存在するため絶対パスへ解決される
        var resolved = SystemProcessResolver.Resolve("dism.exe");

        Assert.IsTrue(Path.IsPathRooted(resolved), resolved);
        StringAssert.Contains(resolved.ToLowerInvariant(), "system32");
        StringAssert.EndsWith(resolved, "dism.exe", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void powershellはWindowsPowerShellのフルパスへ解決される()
    {
        var resolved = SystemProcessResolver.Resolve("powershell.exe");

        Assert.IsTrue(Path.IsPathRooted(resolved), resolved);
        StringAssert.Contains(resolved, @"WindowsPowerShell\v1.0");
    }

    [TestMethod]
    public void 拡張子なしのwingetもexeとして扱う()
    {
        var resolved = SystemProcessResolver.Resolve("winget");

        // WindowsApps の winget.exe が存在すればフルパス、無ければ winget.exe に正規化される
        StringAssert.EndsWith(resolved, "winget.exe", StringComparison.OrdinalIgnoreCase);
    }
}
