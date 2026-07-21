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
    public void 旧Msiのドライブ直下配置をProgramFilesへの修復対象にする()
    {
        var programFiles = Path.Combine(_testRoot, "Program Files");
        var misplacedRoot = Path.Combine(Path.GetPathRoot(programFiles)!, "Lumin4ti");

        var result = WindowsPerMachineMigration.GetKnownMisplacedPerMachineRoot(
            Path.Combine(misplacedRoot, "Lumin4ti.exe"),
            programFiles);

        Assert.AreEqual(Path.GetFullPath(misplacedRoot), result);
    }

    [TestMethod]
    public void 旧Msiの発行者名付き配置をProgramFilesへの修復対象にする()
    {
        var programFiles = Path.Combine(_testRoot, "Program Files");
        var misplacedRoot = Path.Combine(programFiles, "ゆろち", "Lumin4ti");

        var result = WindowsPerMachineMigration.GetKnownMisplacedPerMachineRoot(
            Path.Combine(misplacedRoot, "Lumin4ti.exe"),
            programFiles);

        Assert.AreEqual(Path.GetFullPath(misplacedRoot), result);
    }

    [TestMethod]
    public void 正しいProgramFiles配置と任意のカスタム配置は自動修復しない()
    {
        var programFiles = Path.Combine(_testRoot, "Program Files");

        Assert.IsNull(WindowsPerMachineMigration.GetKnownMisplacedPerMachineRoot(
            Path.Combine(programFiles, "Lumin4ti", "Lumin4ti.exe"),
            programFiles));
        Assert.IsNull(WindowsPerMachineMigration.GetKnownMisplacedPerMachineRoot(
            Path.Combine(_testRoot, "Custom", "Lumin4ti", "Lumin4ti.exe"),
            programFiles));
    }

    [TestMethod]
    public void 保留情報から削除できるのは既知の旧配置だけ()
    {
        var programFiles = Path.Combine(_testRoot, "Program Files");
        var expectedPerUserRoot = Path.Combine(_testRoot, "LocalAppData", "Lumin4ti");
        var driveRootInstall = Path.Combine(Path.GetPathRoot(programFiles)!, "Lumin4ti");
        var authorInstall = Path.Combine(programFiles, "ゆろち", "Lumin4ti");

        Assert.AreEqual(
            Path.GetFullPath(expectedPerUserRoot),
            WindowsPerMachineMigration.GetApprovedLegacyCleanupRoot(
                expectedPerUserRoot,
                expectedPerUserRoot,
                programFiles,
                allowMisplacedPerMachineRoot: false));
        Assert.AreEqual(
            Path.GetFullPath(driveRootInstall),
            WindowsPerMachineMigration.GetApprovedLegacyCleanupRoot(
                driveRootInstall,
                expectedPerUserRoot,
                programFiles,
                allowMisplacedPerMachineRoot: true));
        Assert.AreEqual(
            Path.GetFullPath(authorInstall),
            WindowsPerMachineMigration.GetApprovedLegacyCleanupRoot(
                authorInstall,
                expectedPerUserRoot,
                programFiles,
                allowMisplacedPerMachineRoot: true));
        Assert.IsNull(WindowsPerMachineMigration.GetApprovedLegacyCleanupRoot(
            Path.Combine(_testRoot, "Unrelated", "Lumin4ti"),
            expectedPerUserRoot,
            programFiles,
            allowMisplacedPerMachineRoot: true));
        Assert.IsNull(WindowsPerMachineMigration.GetApprovedLegacyCleanupRoot(
            driveRootInstall,
            expectedPerUserRoot,
            programFiles,
            allowMisplacedPerMachineRoot: false));
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
    public void 同一Msiの配置修復は全機能の再配置と再キャッシュを指定する()
    {
        const string msiPath = @"C:\Temp\Lumin4ti-win.msi";
        const string programFiles = @"C:\Program Files";

        var startInfo = WindowsPerMachineMigration.CreateMsiInstallStartInfo(
            msiPath,
            programFiles,
            reinstallExistingProduct: true);

        CollectionAssert.AreEqual(
            new[]
            {
                "/i",
                msiPath,
                @"VELOPACK_INSTALLDIR=C:\Program Files\Lumin4ti",
                "REINSTALL=ALL",
                "REINSTALLMODE=vamus",
                "/passive",
                "/norestart",
            },
            startInfo.ArgumentList.ToArray());
    }

    [TestMethod]
    public void UpdateExeまたはPackagesだけ残っていても再回収対象にする()
    {
        var legacyRoot = Path.Combine(_testRoot, "LocalAppData", "Lumin4ti");
        Directory.CreateDirectory(legacyRoot);
        File.WriteAllText(Path.Combine(legacyRoot, "Update.exe"), string.Empty);

        Assert.IsTrue(WindowsPerMachineMigration.HasLegacyInstallationArtifacts(legacyRoot));

        File.Delete(Path.Combine(legacyRoot, "Update.exe"));
        Directory.CreateDirectory(Path.Combine(legacyRoot, "packages"));

        Assert.IsTrue(WindowsPerMachineMigration.HasLegacyInstallationArtifacts(legacyRoot));
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

        string? refreshedDirectory = null;
        var cleaned = WindowsPerMachineMigration.TryCleanupLegacyArtifacts(
            legacyRoot,
            legacyRoot,
            programs,
            desktop,
            directory => refreshedDirectory = directory);

        Assert.IsTrue(cleaned);
        Assert.IsFalse(Directory.Exists(legacyRoot));
        Assert.IsFalse(File.Exists(Path.Combine(programs, "Lumin4ti.lnk")));
        Assert.IsFalse(Directory.Exists(legacyPrograms));
        Assert.IsFalse(File.Exists(Path.Combine(desktop, "Lumin4ti.lnk")));
        Assert.AreEqual(programs, refreshedDirectory);
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
