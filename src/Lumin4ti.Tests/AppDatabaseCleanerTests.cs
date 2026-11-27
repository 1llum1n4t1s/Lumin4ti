using Lumin4ti.Core.Services.Windows.Cleanup;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class AppDatabaseCleanerTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup() => _root = DatabaseTestSupport.NewRoot();

    [TestCleanup]
    public void Cleanup() => DatabaseTestSupport.DeleteRoot(_root);

    private string CreateApp() => DatabaseTestSupport.CreateDatabase(_root, "app.db",
        "CREATE TABLE recent_files (path TEXT, opened INTEGER)",
        "INSERT INTO recent_files VALUES ('a',1),('b',2),('c',3)",
        "CREATE TABLE settings (name TEXT, value TEXT)",
        "INSERT INTO settings VALUES ('theme','dark')");

    [TestMethod]
    public void ClearTable_RemovesAllRowsOfTheNamedTableOnly()
    {
        var path = CreateApp();
        var result = AppDatabaseCleaner.Clean(path, "recent_files", ["path"], CancellationToken.None);
        Assert.IsTrue(result.Succeeded, result.Reason);
        Assert.AreEqual(3, result.Rows);
        Assert.AreEqual(0, DatabaseTestSupport.Count(path, "SELECT count(*) FROM recent_files"));
        Assert.AreEqual(1, DatabaseTestSupport.Count(path, "SELECT count(*) FROM settings"));
    }

    [TestMethod]
    public void Count_DoesNotChangeTheDatabase()
    {
        var path = CreateApp();
        var before = DatabaseTestSupport.Hash(path);
        var result = AppDatabaseCleaner.Count(path, "RECENT_FILES", ["PATH", "opened"], CancellationToken.None);
        Assert.IsTrue(result.Succeeded, result.Reason);
        Assert.AreEqual(3, result.Rows);
        Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
        CollectionAssert.AreEqual(new[] { path }, Directory.GetFiles(_root));
    }

    [TestMethod]
    [DataRow("user_login")]
    [DataRow("Passwords")]
    [DataRow("auth_token")]
    [DataRow("credentials")]
    [DataRow("accounts")]
    [DataRow("credit_cards")]
    [DataRow("keystore")]
    [DataRow("sessions")]
    [DataRow("recent_files; DROP TABLE settings")]
    [DataRow("recent files")]
    [DataRow("\"recent_files\"")]
    [DataRow("1table")]
    [DataRow("sqlite_master")]
    [DataRow("")]
    public void ForbiddenOrMalformedTableNames_AreRejectedWithoutOpeningTheDatabase(string table)
    {
        var path = CreateApp();
        var before = DatabaseTestSupport.Hash(path);
        var result = AppDatabaseCleaner.Clean(path, table, ["path"], CancellationToken.None);
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
    }

    [TestMethod]
    public void MissingOrMalformedColumns_AreRejected()
    {
        var path = CreateApp();
        Assert.IsFalse(AppDatabaseCleaner.Clean(path, "recent_files", [], CancellationToken.None).Succeeded);
        Assert.IsFalse(AppDatabaseCleaner.Clean(path, "recent_files", ["path; --"], CancellationToken.None).Succeeded);
        Assert.AreEqual(3, DatabaseTestSupport.Count(path, "SELECT count(*) FROM recent_files"));
    }

    [TestMethod]
    public void UnknownSchema_IsPartialWithoutChange()
    {
        var path = CreateApp();
        var before = DatabaseTestSupport.Hash(path);
        var missingColumn = AppDatabaseCleaner.Clean(path, "recent_files", ["path", "renamed"], CancellationToken.None);
        var missingTable = AppDatabaseCleaner.Clean(path, "history", ["path"], CancellationToken.None);
        Assert.IsFalse(missingColumn.Succeeded);
        Assert.IsFalse(missingTable.Succeeded);
        Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
    }

    [TestMethod]
    public void LockedDatabase_IsPartialWithoutChange()
    {
        var path = CreateApp();
        var before = DatabaseTestSupport.Hash(path);
        using (var holder = SqliteDb.Open(path, WinSqlite.OpenReadWrite))
        {
            holder.Execute("BEGIN EXCLUSIVE");
            Assert.IsFalse(AppDatabaseCleaner.Clean(path, "recent_files", ["path"], CancellationToken.None).Succeeded);
        }
        Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
    }

    [TestMethod]
    public void MaliciousDatabases_AreNotModifiedOutsideTheNamedTable()
    {
        var trigger = DatabaseTestSupport.CreateDatabase(_root, "trigger.db",
            "CREATE TABLE recent_files (path TEXT)",
            "INSERT INTO recent_files VALUES ('a')",
            "CREATE TABLE settings (name TEXT)",
            "INSERT INTO settings VALUES ('keep')",
            "CREATE TRIGGER t AFTER DELETE ON recent_files BEGIN DELETE FROM settings; END");
        Assert.IsTrue(AppDatabaseCleaner.Clean(trigger, "recent_files", ["path"], CancellationToken.None).Succeeded);
        Assert.AreEqual(1, DatabaseTestSupport.Count(trigger, "SELECT count(*) FROM settings"));

        var garbage = Path.Combine(_root, "garbage.db");
        File.WriteAllBytes(garbage, new byte[6000]);
        Assert.IsFalse(AppDatabaseCleaner.Clean(garbage, "recent_files", ["path"], CancellationToken.None).Succeeded);

        var big = CreateApp();
        var before = DatabaseTestSupport.Hash(big);
        Assert.IsFalse(AppDatabaseCleaner.Clean(big, "recent_files", ["path"], CancellationToken.None, maxBytes: 100).Succeeded);
        Assert.AreEqual(before, DatabaseTestSupport.Hash(big));
    }

    [TestMethod]
    public void VirtualTableTarget_IsPartialWithoutChange()
    {
        var path = DatabaseTestSupport.CreateDatabase(_root, "virtual.db", "CREATE TABLE settings (name TEXT)");
        string[] modules = ["fts5(path)", "fts4(path)", "fts3(path)"];
        var created = false;
        using (var db = SqliteDb.Open(path, WinSqlite.OpenReadWrite))
        {
            foreach (var module in modules)
            {
                try { db.Execute($"CREATE VIRTUAL TABLE recent_files USING {module}"); created = true; break; }
                catch (DatabaseRefusedException) { /* この SQLite に無いモジュール */ }
            }
        }
        Assert.IsTrue(created, "合成 DB に仮想表を作れるモジュールが winsqlite3 にありません");
        var before = DatabaseTestSupport.Hash(path);
        Assert.IsFalse(AppDatabaseCleaner.Clean(path, "recent_files", ["path"], CancellationToken.None).Succeeded);
        Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
    }
}
