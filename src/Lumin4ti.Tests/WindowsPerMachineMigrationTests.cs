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
    public void MsiマーカーがあるProgramFiles配下をPerMachine版と判定する()
    {
        var programFiles = Path.Combine(_testRoot, "Program Files");
        var installRoot = Path.Combine(programFiles, "ゆろち", "Lumin4ti");
        Directory.CreateDirectory(Path.Combine(installRoot, "current"));
        File.WriteAllText(Path.Combine(installRoot, ".msi-installed"), string.Empty);
        var processPath = Path.Combine(installRoot, "current", "Lumin4ti.UI.exe");

        Assert.IsTrue(WindowsPerMachineMigration.IsPerMachineProcessPath(processPath, programFiles));
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
