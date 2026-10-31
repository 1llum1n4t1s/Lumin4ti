using Lumin4ti.Core.Services;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class LegacyLogMigrationTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumin4ti-log-migration-tests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void 旧ログだけを移し同名と利用者ファイルを保持する()
    {
        var source = Path.Combine(_root, "old");
        var destination = Path.Combine(_root, "new");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, "Lumin4ti_2026-10-30.log"), "old");
        File.WriteAllText(Path.Combine(source, "keep.txt"), "user");
        File.WriteAllText(Path.Combine(destination, "Lumin4ti_2026-10-30.log"), "new");

        LegacyLogMigration.Migrate(source, destination);

        Assert.AreEqual("new", File.ReadAllText(Path.Combine(destination, "Lumin4ti_2026-10-30.log")));
        Assert.AreEqual("old", File.ReadAllText(Directory.GetFiles(destination, "legacy-*.log").Single()));
        Assert.IsTrue(File.Exists(Path.Combine(source, "keep.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(source, "Lumin4ti_2026-10-30.log")));
    }

    [TestMethod]
    public void 旧Kagayoi配下は空のアプリフォルダーだけを除去する()
    {
        var kagayoi = Path.Combine(_root, "Kagayoi");
        var app = Path.Combine(kagayoi, "Lumin4ti");
        var logs = Path.Combine(app, "logs");
        Directory.CreateDirectory(logs);
        var destination = Path.Combine(_root, "new");
        File.WriteAllText(Path.Combine(logs, "Lumin4ti_2026-10-30.log"), "old");

        LegacyLogMigration.Migrate(logs, destination);
        LegacyLogMigration.RemoveEmptyParent(logs);
        Assert.IsFalse(Directory.Exists(app));
        Assert.IsTrue(Directory.Exists(kagayoi));

        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "keep.txt"), "user");
        LegacyLogMigration.RemoveEmptyParent(logs);
        Assert.AreEqual("user", File.ReadAllText(Path.Combine(app, "keep.txt")));
    }

    [TestMethod]
    public void 使用中の旧ログは残し次回移行する()
    {
        var source = Path.Combine(_root, "old");
        var destination = Path.Combine(_root, "new");
        Directory.CreateDirectory(source);
        var file = Path.Combine(source, "Lumin4ti_2026-10-30.log");
        File.WriteAllText(file, "old");
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            LegacyLogMigration.Migrate(source, destination);
            Assert.IsTrue(File.Exists(file));
            Assert.AreEqual(0, Directory.GetFiles(destination).Length);
        }

        LegacyLogMigration.Migrate(source, destination);
        Assert.IsFalse(Directory.Exists(source));
        Assert.AreEqual("old", File.ReadAllText(Directory.GetFiles(destination).Single()));
    }

    [TestMethod]
    public void リンク先のログは移動しない()
    {
        var source = Path.Combine(_root, "old");
        var destination = Path.Combine(_root, "new");
        Directory.CreateDirectory(source);
        var external = Path.Combine(_root, "external.log");
        File.WriteAllText(external, "external");
        try
        {
            File.CreateSymbolicLink(Path.Combine(source, "Lumin4ti_2026-10-30.log"), external);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException ||
                                   ex is IOException && (ex.HResult & 0xffff) == 1314)
        {
            Assert.Inconclusive("シンボリックリンクの作成権限がありません。");
        }

        LegacyLogMigration.Migrate(source, destination);

        Assert.AreEqual("external", File.ReadAllText(external));
        Assert.AreEqual(0, Directory.GetFiles(destination).Length);
    }

    [TestMethod]
    public void 並行移行しても旧ログは一度だけ残る()
    {
        var source = Path.Combine(_root, "old");
        var destination = Path.Combine(_root, "new");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Lumin4ti_2026-10-30.log"), "old");

        Parallel.For(0, 8, _ => LegacyLogMigration.Migrate(source, destination));

        Assert.AreEqual("old", File.ReadAllText(Directory.GetFiles(destination).Single()));
        Assert.IsFalse(Directory.Exists(source));
    }
}
