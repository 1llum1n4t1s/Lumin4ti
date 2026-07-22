using Lumin4ti.UI.Services;
using Velopack.Windows;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class WindowsLegacyStartMenuShortcutMigratorTests
{
    private string _testDirectory = null!;
    private string _programsDirectory = null!;
    private string _rootAppDirectory = null!;
    private WindowsLegacyStartMenuShortcutMigrator.ShortcutDetails _lumin4tiShortcut = null!;

    [TestInitialize]
    public void Initialize()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), "Lumin4ti.Tests", Guid.NewGuid().ToString("N"));
        _programsDirectory = Directory.CreateDirectory(Path.Combine(_testDirectory, "Programs")).FullName;
        var currentDirectory = Directory.CreateDirectory(Path.Combine(_testDirectory, "Lumin4ti", "current")).FullName;
        _rootAppDirectory = Directory.GetParent(currentDirectory)!.FullName;
        _lumin4tiShortcut = new(
            Path.Combine(currentDirectory, "Lumin4ti.UI.exe"),
            currentDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void TryMigrate_旧Lumin4tiリンクがある場合_直下へ移して空の旧フォルダを削除する()
    {
        var legacyDirectory = Directory.CreateDirectory(Path.Combine(_programsDirectory, "ゆろち"));
        var legacyShortcut = Path.Combine(legacyDirectory.FullName, "Lumin4ti.lnk");
        File.WriteAllText(legacyShortcut, "lumin4ti");

        var migrated = WindowsLegacyStartMenuShortcutMigrator.TryMigrate(
            _programsDirectory, _rootAppDirectory, ReadShortcut);

        Assert.IsTrue(migrated);
        Assert.IsFalse(File.Exists(legacyShortcut));
        Assert.IsFalse(Directory.Exists(legacyDirectory.FullName));
        Assert.AreEqual("lumin4ti", File.ReadAllText(Path.Combine(_programsDirectory, "Lumin4ti.lnk")));
    }

    [TestMethod]
    public void TryMigrate_実際のWindowsショートカットを直下へ移動できる()
    {
        var legacyDirectory = Directory.CreateDirectory(Path.Combine(_programsDirectory, "ゆろち"));
        var legacyShortcut = Path.Combine(legacyDirectory.FullName, "Lumin4ti.lnk");
        File.WriteAllText(_lumin4tiShortcut.TargetPath!, string.Empty);

        using (var shortcut = new ShellLink
        {
            Target = _lumin4tiShortcut.TargetPath,
            WorkingDirectory = _lumin4tiShortcut.WorkingDirectory,
        })
        {
            shortcut.Save(legacyShortcut);
        }

        var migrated = WindowsLegacyStartMenuShortcutMigrator.TryMigrate(
            _programsDirectory, _rootAppDirectory);

        Assert.IsTrue(migrated);
        Assert.IsFalse(File.Exists(legacyShortcut));
        Assert.IsTrue(File.Exists(Path.Combine(_programsDirectory, "Lumin4ti.lnk")));
    }

    [TestMethod]
    public void TryMigrate_直下に自アプリリンクがある場合_既存リンクを保持して旧リンクだけ削除する()
    {
        var legacyDirectory = Directory.CreateDirectory(Path.Combine(_programsDirectory, "ゆろち"));
        var legacyShortcut = Path.Combine(legacyDirectory.FullName, "Lumin4ti.lnk");
        var rootShortcut = Path.Combine(_programsDirectory, "Lumin4ti.lnk");
        File.WriteAllText(legacyShortcut, "lumin4ti");
        File.WriteAllText(rootShortcut, "lumin4ti");

        var migrated = WindowsLegacyStartMenuShortcutMigrator.TryMigrate(
            _programsDirectory, _rootAppDirectory, ReadShortcut);

        Assert.IsTrue(migrated);
        Assert.IsFalse(File.Exists(legacyShortcut));
        Assert.IsFalse(Directory.Exists(legacyDirectory.FullName));
        Assert.AreEqual("lumin4ti", File.ReadAllText(rootShortcut));
    }

    [TestMethod]
    public void TryMigrate_旧フォルダに別アプリのリンクがある場合_フォルダを残す()
    {
        var legacyDirectory = Directory.CreateDirectory(Path.Combine(_programsDirectory, "ゆろち"));
        File.WriteAllText(Path.Combine(legacyDirectory.FullName, "Lumin4ti.lnk"), "lumin4ti");
        var shisuiShortcut = Path.Combine(legacyDirectory.FullName, "Shisui.lnk");
        File.WriteAllText(shisuiShortcut, "other");

        var migrated = WindowsLegacyStartMenuShortcutMigrator.TryMigrate(
            _programsDirectory, _rootAppDirectory, ReadShortcut);

        Assert.IsTrue(migrated);
        Assert.IsTrue(Directory.Exists(legacyDirectory.FullName));
        Assert.AreEqual("other", File.ReadAllText(shisuiShortcut));
        Assert.IsTrue(File.Exists(Path.Combine(_programsDirectory, "Lumin4ti.lnk")));
    }

    [TestMethod]
    public void TryMigrate_直下が別アプリの同名リンクなら何も変更しない()
    {
        var legacyDirectory = Directory.CreateDirectory(Path.Combine(_programsDirectory, "ゆろち"));
        var legacyShortcut = Path.Combine(legacyDirectory.FullName, "Lumin4ti.lnk");
        var rootShortcut = Path.Combine(_programsDirectory, "Lumin4ti.lnk");
        File.WriteAllText(legacyShortcut, "lumin4ti");
        File.WriteAllText(rootShortcut, "other");

        var migrated = WindowsLegacyStartMenuShortcutMigrator.TryMigrate(
            _programsDirectory, _rootAppDirectory, ReadShortcut);

        Assert.IsFalse(migrated);
        Assert.AreEqual("lumin4ti", File.ReadAllText(legacyShortcut));
        Assert.AreEqual("other", File.ReadAllText(rootShortcut));
    }

    [TestMethod]
    public void TryMigrate_旧リンクがLumin4tiを指さない場合_何も変更しない()
    {
        var legacyDirectory = Directory.CreateDirectory(Path.Combine(_programsDirectory, "ゆろち"));
        var legacyShortcut = Path.Combine(legacyDirectory.FullName, "Lumin4ti.lnk");
        File.WriteAllText(legacyShortcut, "other");

        var migrated = WindowsLegacyStartMenuShortcutMigrator.TryMigrate(
            _programsDirectory, _rootAppDirectory, ReadShortcut);

        Assert.IsFalse(migrated);
        Assert.AreEqual("other", File.ReadAllText(legacyShortcut));
        Assert.IsFalse(File.Exists(Path.Combine(_programsDirectory, "Lumin4ti.lnk")));
    }

    [TestMethod]
    public void TryRepairShortcutMetadata_正規リンクならAumidと対象Exeアイコンを設定する()
    {
        var shortcutPath = Path.Combine(_programsDirectory, "Lumin4ti.lnk");
        File.WriteAllText(shortcutPath, "lumin4ti");
        string? repairedShortcut = null;
        string? repairedIcon = null;
        string? repairedAumid = null;

        var repaired = WindowsLegacyStartMenuShortcutMigrator.TryRepairShortcutMetadata(
            shortcutPath,
            _rootAppDirectory,
            "velopack.Lumin4ti",
            ReadShortcut,
            (path, icon, aumid) =>
            {
                repairedShortcut = path;
                repairedIcon = icon;
                repairedAumid = aumid;
            });

        Assert.IsTrue(repaired);
        Assert.AreEqual(shortcutPath, repairedShortcut);
        Assert.AreEqual(Path.GetFullPath(_lumin4tiShortcut.TargetPath!), repairedIcon);
        Assert.AreEqual("velopack.Lumin4ti", repairedAumid);
    }

    [TestMethod]
    public void TryRepairShortcutMetadata_実ショートカットへAumidと対象Exeアイコンを永続化する()
    {
        var shortcutPath = Path.Combine(_programsDirectory, "Lumin4ti.lnk");
        File.WriteAllText(_lumin4tiShortcut.TargetPath!, string.Empty);
        using (var shortcut = new ShellLink
               {
                   Target = _lumin4tiShortcut.TargetPath,
                   WorkingDirectory = _lumin4tiShortcut.WorkingDirectory,
               })
        {
            shortcut.Save(shortcutPath);
        }

        var repaired = WindowsLegacyStartMenuShortcutMigrator.TryRepairShortcutMetadata(
            shortcutPath,
            _rootAppDirectory,
            "velopack.Lumin4ti");

        Assert.IsTrue(repaired);
        using var repairedShortcut = new ShellLink(shortcutPath);
        Assert.AreEqual(Path.GetFullPath(_lumin4tiShortcut.TargetPath!), repairedShortcut.IconPath);
        Assert.AreEqual(0, repairedShortcut.IconIndex);
        Assert.AreEqual("velopack.Lumin4ti", WindowsShortcutPropertyStore.GetAppUserModelId(shortcutPath));
    }

    [TestMethod]
    public void TryRepairShortcutMetadata_ルート直下ランチャーも正規リンクとして補修する()
    {
        var shortcutPath = Path.Combine(_programsDirectory, "Lumin4ti.lnk");
        File.WriteAllText(shortcutPath, "launcher");
        var launcher = new WindowsLegacyStartMenuShortcutMigrator.ShortcutDetails(
            Path.Combine(_rootAppDirectory, "Lumin4ti.exe"),
            _rootAppDirectory);
        var called = false;

        var repaired = WindowsLegacyStartMenuShortcutMigrator.TryRepairShortcutMetadata(
            shortcutPath,
            _rootAppDirectory,
            "velopack.Lumin4ti",
            _ => launcher,
            (_, _, _) => called = true);

        Assert.IsTrue(repaired);
        Assert.IsTrue(called);
    }

    [TestMethod]
    public void TryRepairShortcutMetadata_別アプリの同名リンクは変更しない()
    {
        var shortcutPath = Path.Combine(_programsDirectory, "Lumin4ti.lnk");
        File.WriteAllText(shortcutPath, "other");
        var called = false;

        var repaired = WindowsLegacyStartMenuShortcutMigrator.TryRepairShortcutMetadata(
            shortcutPath,
            _rootAppDirectory,
            "velopack.Lumin4ti",
            ReadShortcut,
            (_, _, _) => called = true);

        Assert.IsFalse(repaired);
        Assert.IsFalse(called);
    }

    [TestMethod]
    public void TryRepairShortcutMetadata_既に整合しているリンクは書き直さない()
    {
        var shortcutPath = Path.Combine(_programsDirectory, "Lumin4ti.lnk");
        File.WriteAllText(shortcutPath, "lumin4ti");
        var targetPath = Path.GetFullPath(_lumin4tiShortcut.TargetPath!);
        var current = _lumin4tiShortcut with
        {
            IconPath = targetPath,
            IconIndex = 0,
            AppUserModelId = "velopack.Lumin4ti",
        };
        var called = false;

        var repaired = WindowsLegacyStartMenuShortcutMigrator.TryRepairShortcutMetadata(
            shortcutPath,
            _rootAppDirectory,
            "velopack.Lumin4ti",
            _ => current,
            (_, _, _) => called = true);

        Assert.IsFalse(repaired);
        Assert.IsFalse(called);
    }

    private WindowsLegacyStartMenuShortcutMigrator.ShortcutDetails? ReadShortcut(string shortcutPath) =>
        File.ReadAllText(shortcutPath) switch
        {
            "lumin4ti" => _lumin4tiShortcut,
            "other" => new WindowsLegacyStartMenuShortcutMigrator.ShortcutDetails(
                Path.Combine(_testDirectory, "Other", "Other.exe"),
                Path.Combine(_testDirectory, "Other")),
            _ => null,
        };
}
