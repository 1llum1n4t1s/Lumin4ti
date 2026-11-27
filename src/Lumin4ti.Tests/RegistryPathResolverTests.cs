using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class RegistryPathResolverTests
{
    private static readonly Dictionary<string, string> Folders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["windir"] = @"C:\Windows",
        ["SystemRoot"] = @"C:\Windows",
        ["SystemDrive"] = "C:",
        ["ProgramFiles"] = @"C:\Program Files",
        ["ProgramW6432"] = @"C:\Program Files",
        ["ProgramFiles(x86)"] = @"C:\Program Files (x86)",
        ["CommonProgramFiles"] = @"C:\Program Files\Common Files",
        ["CommonProgramFiles(x86)"] = @"C:\Program Files (x86)\Common Files",
        ["ProgramData"] = @"C:\ProgramData",
        ["USERPROFILE"] = @"C:\Users\me",
        ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local",
        ["APPDATA"] = @"C:\Users\me\AppData\Roaming",
        ["Fonts"] = @"C:\Windows\Fonts",
    };

    private static RegistryPathResolver Create(string? profile = @"C:\Users\me")
    {
        var guard = new CustomFolderGuardContext(@"C:\Windows", [@"C:\Program Files"], @"C:\ProgramData", [@"C:\Users"], profile);
        return new RegistryPathResolver(new CleanupBaseTrust(guard, name => Folders.GetValueOrDefault(name)), name => Folders.GetValueOrDefault(name));
    }

    [TestMethod]
    public void 許可した変数を既知フォルダーで展開する()
    {
        CollectionAssert.AreEqual(new[] { @"C:\ProgramData\x\a.dll" }, Create().ExpandPath(@"%ProgramData%\x\a.dll", RegistryPathOrigin.Machine).ToArray());
        CollectionAssert.AreEqual(new[] { @"C:\Windows\a.dll" }, Create().ExpandPath(@"%SYSTEMROOT%\a.dll", RegistryPathOrigin.Machine).ToArray());
    }

    [TestMethod]
    public void 許可外の変数と閉じていない百分率は解釈しない()
    {
        var resolver = Create();
        Assert.AreEqual(0, resolver.ExpandPath(@"%TEMP%\a.exe", RegistryPathOrigin.User).Count);
        Assert.AreEqual(0, resolver.ExpandPath(@"%windir\a.exe", RegistryPathOrigin.Machine).Count);
        Assert.AreEqual(0, resolver.ExpandPath(@"%%\a.exe", RegistryPathOrigin.Machine).Count);
    }

    [TestMethod]
    public void 利用者側の変数はHKCU由来の値だけ展開する()
    {
        var resolver = Create();
        Assert.AreEqual(@"C:\Users\me\AppData\Local\a.exe", resolver.ExpandPath(@"%LOCALAPPDATA%\a.exe", RegistryPathOrigin.User).Single());
        Assert.AreEqual(0, resolver.ExpandPath(@"%LOCALAPPDATA%\a.exe", RegistryPathOrigin.Machine).Count);
        Assert.AreEqual(0, resolver.ExpandPath(@"%APPDATA%\a.exe", RegistryPathOrigin.MachineWow64).Count);
    }

    [TestMethod]
    public void プロファイルを確認できなければ利用者側の変数は展開しない()
    {
        Assert.AreEqual(0, Create(profile: null).ExpandPath(@"%USERPROFILE%\a.exe", RegistryPathOrigin.User).Count);
    }

    [TestMethod]
    public void WOW6432Nodeの値はProgramFilesの両方を候補にする()
    {
        var paths = Create().ExpandPath(@"%ProgramFiles%\Foo\a.exe", RegistryPathOrigin.MachineWow64);
        CollectionAssert.AreEquivalent(new[] { @"C:\Program Files\Foo\a.exe", @"C:\Program Files (x86)\Foo\a.exe" }, paths.ToArray());
        Assert.AreEqual(1, Create().ExpandPath(@"%ProgramFiles%\Foo\a.exe", RegistryPathOrigin.Machine).Count);
    }

    [TestMethod]
    public void WOW6432NodeのSystem32はSysWOW64も候補にする()
    {
        var paths = Create().ExpandPath(@"%windir%\System32\a.dll", RegistryPathOrigin.MachineWow64);
        CollectionAssert.AreEquivalent(new[] { @"C:\Windows\System32\a.dll", @"C:\Windows\SysWOW64\a.dll" }, paths.ToArray());
        Assert.AreEqual(@"C:\Windows\System32\a.dll", Create().ExpandPath(@"%windir%\System32\a.dll", RegistryPathOrigin.Machine).Single());
    }

    [TestMethod]
    public void SysnativeはWOW6432NodeだけSystem32へ読み替える()
    {
        Assert.AreEqual(@"C:\Windows\System32\a.dll", Create().ExpandPath(@"%windir%\Sysnative\a.dll", RegistryPathOrigin.MachineWow64).Single());
        Assert.AreEqual(0, Create().ExpandPath(@"%windir%\Sysnative\a.dll", RegistryPathOrigin.Machine).Count);
    }

    [TestMethod]
    public void コマンドラインは実行ファイルへ解釈しラッパーは解釈しない()
    {
        var resolver = Create();
        CollectionAssert.AreEqual(
            new[] { @"C:\Program Files\Foo\a.exe" },
            resolver.ResolveCommand(@"""%ProgramFiles%\Foo\a.exe"" /s", RegistryPathOrigin.Machine).ToArray());
        Assert.AreEqual(0, resolver.ResolveCommand(@"%windir%\System32\rundll32.exe x.dll,Run", RegistryPathOrigin.Machine).Count);
        Assert.AreEqual(0, resolver.ResolveCommand("a.exe", RegistryPathOrigin.Machine).Count);
    }

    [TestMethod]
    public void AppPathsは引用符を外しそのままと拡張子付きの両方を返す()
    {
        var resolver = Create();
        CollectionAssert.AreEqual(
            new[] { @"C:\Tools\a", @"C:\Tools\a.exe" },
            resolver.ResolveAppPath(@"""C:\Tools\a""", RegistryPathOrigin.Machine).ToArray());
        CollectionAssert.AreEqual(new[] { @"C:\Tools\a.exe" }, resolver.ResolveAppPath(@"C:\Tools\a.exe", RegistryPathOrigin.Machine).ToArray());
        Assert.AreEqual(0, resolver.ResolveAppPath("  ", RegistryPathOrigin.Machine).Count);
    }

    [TestMethod]
    public void フォントはファイル名だけならFontsフォルダー配下で縦棒を含む値は解釈しない()
    {
        var resolver = Create();
        Assert.AreEqual(@"C:\Windows\Fonts\a.ttf", resolver.ResolveFontPath("a.ttf", RegistryPathOrigin.Machine).Single());
        Assert.AreEqual(@"D:\f\a.ttf", resolver.ResolveFontPath(@"D:\f\a.ttf", RegistryPathOrigin.Machine).Single());
        Assert.AreEqual(0, resolver.ResolveFontPath("a.ttc|b.ttf", RegistryPathOrigin.Machine).Count);
    }
}

[TestClass]
public sealed class RegistryPathResolverOwnedLocationsTests
{
    private static readonly RegistryOwnedLocations Locations = new(
        @"C:\Windows", [@"C:\Program Files", @"C:\Program Files (x86)"], @"C:\Users\me\AppData\Local");

    [TestMethod]
    [DataRow(@"C:\Windows\System32\a.dll", true)]
    [DataRow(@"C:\Windows", true)]
    [DataRow(@"C:\Windows\Fonts\a.ttf", true)]
    [DataRow(@"C:\Program Files\Windows Defender\a.exe", true)]
    [DataRow(@"C:\Program Files\WindowsApps\p\a.exe", true)]
    [DataRow(@"C:\Program Files (x86)\Windows NT\a.exe", true)]
    [DataRow(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\a.exe", true)]
    [DataRow(@"C:\Program Files\Foo\a.exe", false)]
    [DataRow(@"C:\Windows.old\a.exe", false)]
    [DataRow(@"D:\Windows\a.exe", false)]
    [DataRow("rel\\a.exe", true)]
    public void 保護場所を判定する(string path, bool expected)
    {
        Assert.AreEqual(expected, Locations.IsProtected(path));
    }

    [TestMethod]
    public void フォントの許可があればFonts配下は保護しない()
    {
        Assert.IsFalse(Locations.IsProtected(@"C:\Windows\Fonts\a.ttf", allowWindowsFonts: true));
        Assert.IsTrue(Locations.IsProtected(@"C:\Windows\System32\a.dll", allowWindowsFonts: true));
    }

    [TestMethod]
    public void 候補なしは保護として扱う()
    {
        Assert.IsTrue(Locations.IsAnyProtected([]));
        Assert.IsTrue(Locations.IsAnyProtected([@"C:\Foo\a.exe", @"C:\Windows\a.exe"]));
        Assert.IsFalse(Locations.IsAnyProtected([@"C:\Foo\a.exe"]));
    }
}
