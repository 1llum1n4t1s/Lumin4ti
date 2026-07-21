using System.Runtime.Versioning;
using Lumin4ti.Core.Services.Windows;

namespace Lumin4ti.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WindowsPerMachineMigrationTests
{
    private string _testRoot = null!;

    [TestInitialize]
    public void Initialize()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "Lumin4ti.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [TestMethod]
    public void PerUser版の実行パスから旧インストールルートを取得できる()
    {
        var localAppData = Path.Combine(_testRoot, "LocalAppData");
        var legacyRoot = Path.Combine(localAppData, "Lumin4ti");
        var processPath = Path.Combine(legacyRoot, "current", "Lumin4ti.UI.exe");

        var result = WindowsPerMachineMigration.GetLegacyRootIfCurrentProcessIsPerUser(
            processPath,
            localAppData);

        Assert.AreEqual(Path.GetFullPath(legacyRoot), result);
    }

    [TestMethod]
    public void 似た名前の隣接フォルダをPerUser版と誤認しない()
    {
        var localAppData = Path.Combine(_testRoot, "LocalAppData");
        var processPath = Path.Combine(localAppData, "Lumin4ti-Evil", "current", "Lumin4ti.UI.exe");

        var result = WindowsPerMachineMigration.GetLegacyRootIfCurrentProcessIsPerUser(
            processPath,
            localAppData);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void Hklm登録されたMsiのカスタム配置先もPerMachine版と判定できる()
    {
        var installRoot = Path.Combine(_testRoot, "CustomInstall", "Lumin4ti");
        Directory.CreateDirectory(Path.Combine(installRoot, "current"));
        File.WriteAllText(Path.Combine(installRoot, ".msi-installed"), string.Empty);
        var installedExecutable = Path.Combine(installRoot, "Lumin4ti.exe");
        File.WriteAllText(installedExecutable, string.Empty);
        var processPath = Path.Combine(installRoot, "current", "Lumin4ti.UI.exe");

        Assert.IsTrue(WindowsPerMachineMigration.IsMsiProcessPath(processPath, installedExecutable));
    }

    [TestMethod]
    public void Msi登録先と異なるフォルダのプロセスはPerMachine版と判定しない()
    {
        var installRoot = Path.Combine(_testRoot, "CustomInstall", "Lumin4ti");
        var otherRoot = Path.Combine(_testRoot, "CustomInstall", "Lumin4ti-Evil");
        Directory.CreateDirectory(installRoot);
        Directory.CreateDirectory(Path.Combine(otherRoot, "current"));
        File.WriteAllText(Path.Combine(installRoot, ".msi-installed"), string.Empty);

        Assert.IsFalse(WindowsPerMachineMigration.IsMsiProcessPath(
            Path.Combine(otherRoot, "current", "Lumin4ti.UI.exe"),
            Path.Combine(installRoot, "Lumin4ti.exe")));
    }

    [TestMethod]
    public void Msiマーカーがない登録先はPerMachine版と判定しない()
    {
        var installRoot = Path.Combine(_testRoot, "CustomInstall", "Lumin4ti");
        Directory.CreateDirectory(Path.Combine(installRoot, "current"));

        Assert.IsFalse(WindowsPerMachineMigration.IsMsiProcessPath(
            Path.Combine(installRoot, "current", "Lumin4ti.UI.exe"),
            Path.Combine(installRoot, "Lumin4ti.exe")));
    }

    [TestMethod]
    public void Msi移行はProgramFiles配下の配置先を明示する()
    {
        const string msiPath = @"C:\Temp\Lumin4ti-win.msi";
        const string programFiles = @"C:\Program Files";

        var startInfo = WindowsPerMachineMigration.CreateMsiInstallStartInfo(msiPath, programFiles);

        Assert.AreEqual("runas", startInfo.Verb);
        Assert.IsTrue(startInfo.UseShellExecute);
        CollectionAssert.AreEqual(
            new[]
            {
                "/i",
                msiPath,
                @"VELOPACK_INSTALLDIR=C:\Program Files\Lumin4ti",
                "/passive",
                "/norestart",
            },
            startInfo.ArgumentList.ToArray());
    }

    [TestMethod]
    public void Msi移行はドライブ直下をProgramFilesとして受け付けない()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            WindowsPerMachineMigration.CreateMsiInstallStartInfo(
                @"C:\Temp\Lumin4ti-win.msi",
                @"C:\"));
    }

    [TestMethod]
    public void Velopackフォールバックだけなら旧PerUser版と判定しない()
    {
        var legacyRoot = Path.Combine(_testRoot, "LocalAppData", "Lumin4ti");
        Directory.CreateDirectory(Path.Combine(legacyRoot, "packages"));
        File.WriteAllText(Path.Combine(legacyRoot, "Update.exe"), string.Empty);

        Assert.IsFalse(WindowsPerMachineMigration.HasLegacyInstallationArtifacts(legacyRoot));
    }

    [TestMethod]
    public void Currentまたはルート直下の旧実行ファイルを再回収対象にする()
    {
        var legacyRoot = Path.Combine(_testRoot, "LocalAppData", "Lumin4ti");
        Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(Path.Combine(legacyRoot, "Lumin4ti.UI.exe"), string.Empty);

        Assert.IsTrue(WindowsPerMachineMigration.HasLegacyInstallationArtifacts(legacyRoot));

        File.Delete(Path.Combine(legacyRoot, "Lumin4ti.UI.exe"));
        Directory.CreateDirectory(Path.Combine(legacyRoot, "current"));

        Assert.IsTrue(WindowsPerMachineMigration.HasLegacyInstallationArtifacts(legacyRoot));
    }

    [TestMethod]
    public void ロック中の旧実行ファイルがあっても他の残骸は回収する()
    {
        var legacyRoot = Path.Combine(_testRoot, "LocalAppData", "Lumin4ti");
        var currentDirectory = Path.Combine(legacyRoot, "current");
        var packagesDirectory = Path.Combine(legacyRoot, "packages");
        Directory.CreateDirectory(currentDirectory);
        Directory.CreateDirectory(packagesDirectory);
        var lockedExecutable = Path.Combine(currentDirectory, "Lumin4ti.UI.exe");
        File.WriteAllText(lockedExecutable, "locked");
        File.WriteAllText(Path.Combine(currentDirectory, "stale.dll"), "stale");
        File.WriteAllText(Path.Combine(legacyRoot, "Lumin4ti.UI.exe"), "stale");
        File.WriteAllText(Path.Combine(packagesDirectory, "old.nupkg"), "stale");

        using (File.Open(lockedExecutable, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            WindowsPerMachineMigration.TryDeleteTreeWithoutFollowingReparsePoints(legacyRoot);

            Assert.IsTrue(File.Exists(lockedExecutable));
            Assert.IsFalse(File.Exists(Path.Combine(currentDirectory, "stale.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(legacyRoot, "Lumin4ti.UI.exe")));
            Assert.IsFalse(Directory.Exists(packagesDirectory));
        }

        WindowsPerMachineMigration.TryDeleteTreeWithoutFollowingReparsePoints(legacyRoot);
        Assert.IsFalse(Directory.Exists(legacyRoot));
    }

    [TestMethod]
    public void 正確な旧ルートだけを削除して旧ショートカットも回収する()
    {
        var legacyRoot = Path.Combine(_testRoot, "LocalAppData", "Lumin4ti");
        var packageDirectory = Path.Combine(legacyRoot, "packages");
        var currentDirectory = Path.Combine(legacyRoot, "current");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(currentDirectory);
        File.WriteAllText(Path.Combine(packageDirectory, "old.nupkg"), "old");
        File.WriteAllText(Path.Combine(currentDirectory, "Lumin4ti.UI.exe"), "old");

        var programs = Path.Combine(_testRoot, "Programs");
        var legacyPrograms = Path.Combine(programs, "ゆろち");
        var desktop = Path.Combine(_testRoot, "Desktop");
        Directory.CreateDirectory(legacyPrograms);
        Directory.CreateDirectory(desktop);
        File.WriteAllText(Path.Combine(programs, "Lumin4ti.lnk"), "old");
        File.WriteAllText(Path.Combine(legacyPrograms, "Lumin4ti.lnk"), "old");
        File.WriteAllText(Path.Combine(desktop, "Lumin4ti.lnk"), "old");

        var cleaned = WindowsPerMachineMigration.TryCleanupLegacyArtifacts(
            legacyRoot,
            legacyRoot,
            programs,
            desktop);

        Assert.IsTrue(cleaned);
        Assert.IsFalse(Directory.Exists(legacyRoot));
        Assert.IsFalse(File.Exists(Path.Combine(programs, "Lumin4ti.lnk")));
        Assert.IsFalse(Directory.Exists(legacyPrograms));
        Assert.IsFalse(File.Exists(Path.Combine(desktop, "Lumin4ti.lnk")));
    }

    [TestMethod]
    public void 想定外のルートは削除しない()
    {
        var expectedRoot = Path.Combine(_testRoot, "LocalAppData", "Lumin4ti");
        var unrelatedRoot = Path.Combine(_testRoot, "Unrelated");
        Directory.CreateDirectory(unrelatedRoot);
        File.WriteAllText(Path.Combine(unrelatedRoot, "keep.txt"), "keep");

        var cleaned = WindowsPerMachineMigration.TryCleanupLegacyArtifacts(
            unrelatedRoot,
            expectedRoot,
            Path.Combine(_testRoot, "Programs"),
            Path.Combine(_testRoot, "Desktop"));

        Assert.IsFalse(cleaned);
        Assert.IsTrue(File.Exists(Path.Combine(unrelatedRoot, "keep.txt")));
    }
}
