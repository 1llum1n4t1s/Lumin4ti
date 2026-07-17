using Lumin4ti.Core.Services;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class SystemProcessResolverTests
{
    [TestMethod]
    public void 既にフルパスならそのまま返す()
    {
        var full = typeof(SystemProcessResolverTests).Assembly.Location;

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
        try
        {
            var resolved = SystemProcessResolver.Resolve("winget");

            Assert.IsTrue(Path.IsPathFullyQualified(resolved), resolved);
            StringAssert.Contains(resolved, @"Program Files\WindowsApps\Microsoft.DesktopAppInstaller_", StringComparison.OrdinalIgnoreCase);
            StringAssert.EndsWith(resolved, "winget.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch (FileNotFoundException ex)
        {
            // winget 非搭載環境でも bare 名やユーザー alias へ戻らず、明示的に失敗する。
            StringAssert.Contains(ex.Message, "信頼できる Microsoft Desktop App Installer");
        }
    }

    [TestMethod]
    public void 相対パスの外部コマンドは拒否される()
    {
        Assert.ThrowsExactly<FileNotFoundException>(() => SystemProcessResolver.Resolve(@"tools\dism.exe"));
    }

    [TestMethod]
    public void 不存在のbareコマンドはフォールバックせず拒否される()
    {
        var name = $"lumin4ti-missing-{Guid.NewGuid():N}.exe";

        var ex = Assert.ThrowsExactly<FileNotFoundException>(() => SystemProcessResolver.Resolve(name));

        StringAssert.Contains(ex.Message, name);
    }

    [TestMethod]
    public async Task Executorは解決失敗を例外ではなく失敗結果で返す()
    {
        var name = $"lumin4ti-missing-{Guid.NewGuid():N}.exe";

        var result = await new ProcessCommandExecutor().RunAsync(name, string.Empty);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(-1, result.ExitCode);
        StringAssert.Contains(result.StandardError, name);
    }

    [TestMethod]
    public void DesktopAppInstallerはMicrosoftのStore署名かつ正常状態だけを信頼する()
    {
        const string publisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

        Assert.IsTrue(SystemProcessResolver.IsTrustedDesktopAppInstallerIdentity(
            "Microsoft.DesktopAppInstaller", publisher, "8wekyb3d8bbwe",
            Windows.ApplicationModel.PackageSignatureKind.Store, statusOk: true));
        Assert.IsFalse(SystemProcessResolver.IsTrustedDesktopAppInstallerIdentity(
            "Microsoft.DesktopAppInstaller", publisher, "8wekyb3d8bbwe",
            Windows.ApplicationModel.PackageSignatureKind.Developer, statusOk: true));
        Assert.IsFalse(SystemProcessResolver.IsTrustedDesktopAppInstallerIdentity(
            "Microsoft.DesktopAppInstaller", publisher, "attacker",
            Windows.ApplicationModel.PackageSignatureKind.Store, statusOk: true));
        Assert.IsFalse(SystemProcessResolver.IsTrustedDesktopAppInstallerIdentity(
            "Microsoft.DesktopAppInstaller", publisher, "8wekyb3d8bbwe",
            Windows.ApplicationModel.PackageSignatureKind.Store, statusOk: false));
    }

    [TestMethod]
    public void WindowsAppsと同じ接頭辞の別ディレクトリは保護配置とみなさない()
    {
        Assert.IsTrue(SystemProcessResolver.IsPathInside(
            @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_x64\winget.exe",
            @"C:\Program Files\WindowsApps"));
        Assert.IsFalse(SystemProcessResolver.IsPathInside(
            @"C:\Program Files\WindowsApps-Evil\winget.exe",
            @"C:\Program Files\WindowsApps"));
    }
}
