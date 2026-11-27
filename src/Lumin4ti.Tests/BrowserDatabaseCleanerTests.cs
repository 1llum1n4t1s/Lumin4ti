using System.Security.Cryptography;
using Lumin4ti.Core.Services.Windows.Cleanup;

namespace Lumin4ti.Tests;

/// <summary>一時領域の合成 DB だけを使うテストの共通部品。実ブラウザーの DB には触れない。</summary>
internal static class DatabaseTestSupport
{
    internal static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lumin4tiDatabaseTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    internal static void DeleteRoot(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            entry.Attributes = FileAttributes.Normal;
        }
        Directory.Delete(root, recursive: true);
    }

    /// <summary>テスト専用の作成。本番の経路は CREATE なしで開く。</summary>
    internal static string CreateDatabase(string directory, string name, params string[] statements)
    {
        var path = Path.Combine(directory, name);
        using var db = SqliteDb.Open(path, WinSqlite.OpenReadWrite | WinSqlite.OpenCreate);
        foreach (var statement in statements) db.Execute(statement);
        return path;
    }

    internal static long Count(string path, string sql)
    {
        using var db = SqliteDb.Open(path, WinSqlite.OpenReadOnly);
        return db.Scalar(sql);
    }

    internal static List<string> Strings(string path, string sql)
    {
        using var db = SqliteDb.Open(path, WinSqlite.OpenReadOnly);
        return db.Strings(sql);
    }

    internal static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    internal static void RunCmd(string arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = "/c " + arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, process.StandardError.ReadToEnd());
    }
}

[TestClass]
public sealed class BrowserDatabaseCleanerTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup() => _root = DatabaseTestSupport.NewRoot();

    [TestCleanup]
    public void Cleanup() => DatabaseTestSupport.DeleteRoot(_root);

    private string ChromiumCookies(params string[] extra) => DatabaseTestSupport.CreateDatabase(_root, "Cookies",
    [
        "CREATE TABLE cookies (host_key TEXT, name TEXT, value TEXT)",
        "INSERT INTO cookies VALUES ('.example.com','a','1'),('www.example.com','b','2'),('example.com','c','3'),('sub.a.EXAMPLE.com','d','4')," +
        "('notexample.com','e','5'),('.other.org','f','6'),('.other.org','g','7')",
        "CREATE TABLE meta (key TEXT, value TEXT)",
        "INSERT INTO meta VALUES ('version','1')",
        .. extra,
    ]);

    [TestMethod]
    public void ChromiumCookies_KeepsExcludedDomainAndSubdomainsOnly()
    {
        var path = ChromiumCookies();
        var result = BrowserDatabaseCleaner.Clean(CleanupSqliteOperation.ChromiumCookies, path, ["Example.com"], CancellationToken.None);
        Assert.IsTrue(result.Succeeded, result.Reason);
        Assert.AreEqual(3, result.Rows);
        CollectionAssert.AreEquivalent(new[] { ".example.com", "www.example.com", "example.com", "sub.a.EXAMPLE.com" },
            DatabaseTestSupport.Strings(path, "SELECT host_key FROM cookies"));
        Assert.AreEqual(1, DatabaseTestSupport.Count(path, "SELECT count(*) FROM meta"));
    }

    [TestMethod]
    public void GeckoCookies_AppliesTheSameDomainRules()
    {
        var path = DatabaseTestSupport.CreateDatabase(_root, "cookies.sqlite",
            "CREATE TABLE moz_cookies (id INTEGER PRIMARY KEY, host TEXT, name TEXT)",
            "INSERT INTO moz_cookies (host, name) VALUES ('.example.com','a'),('badexample.com','b'),('x.org','c'),(NULL,'d')");
        var result = BrowserDatabaseCleaner.Clean(CleanupSqliteOperation.GeckoCookies, path, [".example.com"], CancellationToken.None);
        Assert.IsTrue(result.Succeeded, result.Reason);
        Assert.AreEqual(2, result.Rows);
        CollectionAssert.AreEqual(new[] { ".example.com" }, DatabaseTestSupport.Strings(path, "SELECT host FROM moz_cookies WHERE host IS NOT NULL"));
        Assert.AreEqual(2, DatabaseTestSupport.Count(path, "SELECT count(*) FROM moz_cookies"));
    }

    [TestMethod]
    public void ChromiumWebData_ClearsAutofillOnly()
    {
        var path = DatabaseTestSupport.CreateDatabase(_root, "Web Data",
            "CREATE TABLE autofill (name TEXT, value TEXT, count INTEGER)",
            "INSERT INTO autofill VALUES ('email','a@b.c',1),('q','x',2)",
            "CREATE TABLE credit_cards (guid TEXT, name_on_card TEXT)",
            "INSERT INTO credit_cards VALUES ('g','n')",
            "CREATE TABLE autofill_profiles (guid TEXT)",
            "INSERT INTO autofill_profiles VALUES ('p')",
            "CREATE TABLE token_service (service TEXT, encrypted_token BLOB)",
            "INSERT INTO token_service VALUES ('s', x'00')",
            "CREATE TABLE keywords (id INTEGER, url TEXT)",
            "INSERT INTO keywords VALUES (1,'u')");
        var result = BrowserDatabaseCleaner.Clean(CleanupSqliteOperation.ChromiumWebData, path, null, CancellationToken.None);
        Assert.IsTrue(result.Succeeded, result.Reason);
        Assert.AreEqual(2, result.Rows);
        Assert.AreEqual(0, DatabaseTestSupport.Count(path, "SELECT count(*) FROM autofill"));
        foreach (var table in new[] { "credit_cards", "autofill_profiles", "token_service", "keywords" })
            Assert.AreEqual(1, DatabaseTestSupport.Count(path, $"SELECT count(*) FROM {table}"), table);
    }

    [TestMethod]
    public void GeckoFormHistory_ClearsFormHistoryOnly()
    {
        var path = DatabaseTestSupport.CreateDatabase(_root, "formhistory.sqlite",
            "CREATE TABLE moz_formhistory (id INTEGER PRIMARY KEY, fieldname TEXT, value TEXT)",
            "INSERT INTO moz_formhistory (fieldname, value) VALUES ('a','b'),('c','d')",
            "CREATE TABLE moz_meta (key TEXT, value TEXT)",
            "INSERT INTO moz_meta VALUES ('k','v')");
        var result = BrowserDatabaseCleaner.Clean(CleanupSqliteOperation.GeckoFormHistory, path, null, CancellationToken.None);
        Assert.IsTrue(result.Succeeded, result.Reason);
        Assert.AreEqual(2, result.Rows);
        Assert.AreEqual(0, DatabaseTestSupport.Count(path, "SELECT count(*) FROM moz_formhistory"));
        Assert.AreEqual(1, DatabaseTestSupport.Count(path, "SELECT count(*) FROM moz_meta"));
    }

    private string GeckoPlaces() => DatabaseTestSupport.CreateDatabase(_root, "places.sqlite",
    [
        "CREATE TABLE moz_origins (id INTEGER PRIMARY KEY, host TEXT)",
        "CREATE TABLE moz_places (id INTEGER PRIMARY KEY, url TEXT, foreign_count INTEGER NOT NULL DEFAULT 0, origin_id INTEGER REFERENCES moz_origins(id))",
        "CREATE TABLE moz_historyvisits (id INTEGER PRIMARY KEY, place_id INTEGER REFERENCES moz_places(id), visit_date INTEGER)",
        "CREATE TABLE moz_bookmarks (id INTEGER PRIMARY KEY, fk INTEGER, title TEXT)",
        "CREATE TABLE moz_inputhistory (place_id INTEGER REFERENCES moz_places(id) ON DELETE CASCADE, input TEXT)",
        "INSERT INTO moz_origins VALUES (1,'history.example'),(2,'marked.example')",
        "INSERT INTO moz_places VALUES (1,'https://history.example/',0,1),(2,'https://marked.example/',1,2),(3,'https://history.example/b',0,1),(4,'https://stale.example/',0,2)",
        "INSERT INTO moz_historyvisits (place_id, visit_date) VALUES (1,1),(2,2),(3,3),(4,4)",
        "INSERT INTO moz_bookmarks VALUES (1,2,'kept'),(2,4,'kept too (foreign_count is stale)')",
        "INSERT INTO moz_inputhistory VALUES (1,'h'),(2,'m')",
    ]);

    [TestMethod]
    public void GeckoPlaces_RemovesOnlyHistoryWithoutBookmarks()
    {
        var path = GeckoPlaces();
        var result = BrowserDatabaseCleaner.Clean(CleanupSqliteOperation.GeckoPlaces, path, null, CancellationToken.None);
        Assert.IsTrue(result.Succeeded, result.Reason);
        Assert.AreEqual(2, result.Rows);
        CollectionAssert.AreEquivalent(new[] { "https://marked.example/", "https://stale.example/" }, DatabaseTestSupport.Strings(path, "SELECT url FROM moz_places"));
        Assert.AreEqual(2, DatabaseTestSupport.Count(path, "SELECT count(*) FROM moz_historyvisits"));
        Assert.AreEqual(2, DatabaseTestSupport.Count(path, "SELECT count(*) FROM moz_bookmarks"));
        Assert.AreEqual(1, DatabaseTestSupport.Count(path, "SELECT count(*) FROM moz_inputhistory"));
        CollectionAssert.AreEqual(new[] { "marked.example" }, DatabaseTestSupport.Strings(path, "SELECT host FROM moz_origins"));
    }

    [TestMethod]
    public void GeckoPlaces_RollsBackWhenAForeignKeyBlocksTheDelete()
    {
        var path = GeckoPlaces();
        using (var db = SqliteDb.Open(path, WinSqlite.OpenReadWrite))
        {
            db.Execute("CREATE TABLE moz_extra (place_id INTEGER REFERENCES moz_places(id))");
            db.Execute("INSERT INTO moz_extra VALUES (1)");
        }
        var before = DatabaseTestSupport.Hash(path);
        var result = BrowserDatabaseCleaner.Clean(CleanupSqliteOperation.GeckoPlaces, path, null, CancellationToken.None);
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
    }

    [TestMethod]
    public void Count_ReportsRowsWithoutChangingTheDatabaseOrCreatingFiles()
    {
        var path = ChromiumCookies();
        var before = DatabaseTestSupport.Hash(path);
        var written = File.GetLastWriteTimeUtc(path);
        var result = BrowserDatabaseCleaner.Count(CleanupSqliteOperation.ChromiumCookies, path, ["example.com"], CancellationToken.None);
        Assert.IsTrue(result.Succeeded, result.Reason);
        Assert.AreEqual(3, result.Rows);
        Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
        Assert.AreEqual(written, File.GetLastWriteTimeUtc(path));
        CollectionAssert.AreEqual(new[] { path }, Directory.GetFiles(_root));
    }

    [TestMethod]
    public void UnknownSchema_IsPartialWithoutChange()
    {
        var path = DatabaseTestSupport.CreateDatabase(_root, "Cookies", "CREATE TABLE cookies (domain TEXT)", "INSERT INTO cookies VALUES ('x')");
        var before = DatabaseTestSupport.Hash(path);
        var result = BrowserDatabaseCleaner.Clean(CleanupSqliteOperation.ChromiumCookies, path, null, CancellationToken.None);
        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Reason, "host_key");
        Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
    }

    [TestMethod]
    public void ViewInPlaceOfTable_IsPartial()
    {
        var path = DatabaseTestSupport.CreateDatabase(_root, "Web Data", "CREATE TABLE t (name TEXT, value TEXT)", "CREATE VIEW autofill AS SELECT name, value FROM t");
        var result = BrowserDatabaseCleaner.Clean(CleanupSqliteOperation.ChromiumWebData, path, null, CancellationToken.None);
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public void LockedDatabase_IsPartialWithoutChange()
    {
        var path = ChromiumCookies();
        var before = DatabaseTestSupport.Hash(path);
        using (var holder = SqliteDb.Open(path, WinSqlite.OpenReadWrite))
        {
            holder.Execute("BEGIN EXCLUSIVE");
            var result = BrowserDatabaseCleaner.Clean(CleanupSqliteOperation.ChromiumCookies, path, null, CancellationToken.None);
            Assert.IsFalse(result.Succeeded);
        }
        Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
    }

    [TestMethod]
    public void Triggers_DoNotRunDuringTheDelete()
    {
        var path = ChromiumCookies(
            "CREATE TABLE evil (host TEXT)",
            "CREATE TRIGGER t_del AFTER DELETE ON cookies BEGIN INSERT INTO evil VALUES (old.host); DELETE FROM meta; END");
        var result = BrowserDatabaseCleaner.Clean(CleanupSqliteOperation.ChromiumCookies, path, null, CancellationToken.None);
        Assert.IsTrue(result.Succeeded, result.Reason);
        Assert.AreEqual(0, DatabaseTestSupport.Count(path, "SELECT count(*) FROM cookies"));
        Assert.AreEqual(0, DatabaseTestSupport.Count(path, "SELECT count(*) FROM evil"));
        Assert.AreEqual(1, DatabaseTestSupport.Count(path, "SELECT count(*) FROM meta"));
    }

    [TestMethod]
    public void VirtualTable_IsPartialWithoutChange()
    {
        var path = ChromiumCookies();
        string[] modules = ["fts5(x)", "fts4(x)", "fts3(x)", "rtree(id, a, b)"];
        var created = false;
        using (var db = SqliteDb.Open(path, WinSqlite.OpenReadWrite))
        {
            foreach (var module in modules)
            {
                try { db.Execute($"CREATE VIRTUAL TABLE v USING {module}"); created = true; break; }
                catch (DatabaseRefusedException) { /* この SQLite に無いモジュール */ }
            }
        }
        Assert.IsTrue(created, "合成 DB に仮想表を作れるモジュールが winsqlite3 にありません");
        var before = DatabaseTestSupport.Hash(path);
        var result = BrowserDatabaseCleaner.Clean(CleanupSqliteOperation.ChromiumCookies, path, null, CancellationToken.None);
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("Cleanup.Msg.DbVirtualTable", CleanupMessage.KeyOf(result.Reason));
        Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
    }

    [TestMethod]
    public void GarbageFile_IsPartialWithoutChange()
    {
        var path = Path.Combine(_root, "Cookies");
        File.WriteAllBytes(path, [.. Enumerable.Range(0, 5000).Select(index => (byte)(index * 31))]);
        var before = DatabaseTestSupport.Hash(path);
        Assert.IsFalse(BrowserDatabaseCleaner.Clean(CleanupSqliteOperation.ChromiumCookies, path, null, CancellationToken.None).Succeeded);
        Assert.IsFalse(BrowserDatabaseCleaner.Count(CleanupSqliteOperation.ChromiumCookies, path, null, CancellationToken.None).Succeeded);
        Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
    }

    [TestMethod]
    public void DamagedPages_AreCaughtByTheIntegrityCheck()
    {
        var path = DatabaseTestSupport.CreateDatabase(_root, "Cookies",
            "CREATE TABLE cookies (host_key TEXT, name TEXT)",
            "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 3000) INSERT INTO cookies SELECT 'host' || i || '.test', hex(randomblob(16)) FROM n");
        var bytes = File.ReadAllBytes(path);
        Assert.IsGreaterThan(4096 * 4, bytes.Length);
        Array.Fill(bytes, (byte)0xFF, 4096 * 2, 4096);
        File.WriteAllBytes(path, bytes);
        var before = DatabaseTestSupport.Hash(path);
        var result = BrowserDatabaseCleaner.Clean(CleanupSqliteOperation.ChromiumCookies, path, null, CancellationToken.None);
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
    }

    [TestMethod]
    public void OversizedDatabase_IsPartialWithoutChange()
    {
        var path = ChromiumCookies();
        var before = DatabaseTestSupport.Hash(path);
        var result = BrowserDatabaseCleaner.Clean(CleanupSqliteOperation.ChromiumCookies, path, null, CancellationToken.None, maxBytes: 1024);
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("Cleanup.Msg.DbTooLarge", CleanupMessage.KeyOf(result.Reason));
        Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
    }

    [TestMethod]
    public void HotJournalOrWal_IsPartialWithoutChange()
    {
        foreach (var suffix in new[] { "-journal", "-wal" })
        {
            var path = ChromiumCookies();
            File.WriteAllBytes(path + suffix, [1, 2, 3]);
            var before = DatabaseTestSupport.Hash(path);
            Assert.IsFalse(BrowserDatabaseCleaner.Clean(CleanupSqliteOperation.ChromiumCookies, path, null, CancellationToken.None).Succeeded, suffix);
            Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
            File.Delete(path + suffix);
            File.Delete(path);
        }
    }

    [TestMethod]
    public void SidecarThatIsAJunction_IsPartialWithoutChange()
    {
        var path = ChromiumCookies();
        var target = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        DatabaseTestSupport.RunCmd($"mklink /J \"{path}-journal\" \"{target}\"");
        var before = DatabaseTestSupport.Hash(path);
        Assert.IsFalse(BrowserDatabaseCleaner.Clean(CleanupSqliteOperation.ChromiumCookies, path, null, CancellationToken.None).Succeeded);
        Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
    }

    [TestMethod]
    public void DatabaseReachedThroughAJunction_IsPartialWithoutChange()
    {
        var path = ChromiumCookies();
        var real = Directory.CreateDirectory(Path.Combine(_root, "real")).FullName;
        File.Move(path, Path.Combine(real, "Cookies"));
        path = Path.Combine(real, "Cookies");
        var link = Path.Combine(_root, "link");
        DatabaseTestSupport.RunCmd($"mklink /J \"{link}\" \"{real}\"");
        var before = DatabaseTestSupport.Hash(path);
        Assert.IsFalse(BrowserDatabaseCleaner.Clean(CleanupSqliteOperation.ChromiumCookies, Path.Combine(link, "Cookies"), null, CancellationToken.None).Succeeded);
        Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
    }

    [TestMethod]
    public void HardLinkedDatabase_IsPartialWithoutChange()
    {
        var path = ChromiumCookies();
        DatabaseTestSupport.RunCmd($"mklink /H \"{path}.copy\" \"{path}\"");
        var before = DatabaseTestSupport.Hash(path);
        Assert.IsFalse(BrowserDatabaseCleaner.Clean(CleanupSqliteOperation.ChromiumCookies, path, null, CancellationToken.None).Succeeded);
        Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
    }

    [TestMethod]
    public void MissingDatabase_IsPartialAndIsNotCreated()
    {
        var path = Path.Combine(_root, "Cookies");
        Assert.IsFalse(BrowserDatabaseCleaner.Clean(CleanupSqliteOperation.ChromiumCookies, path, null, CancellationToken.None).Succeeded);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void NotABrowserOperation_IsRejected()
    {
        var path = ChromiumCookies();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BrowserDatabaseCleaner.Clean(CleanupSqliteOperation.ClearTable, path, null, CancellationToken.None));
    }

    [TestMethod]
    public void CancelledBeforeStart_ThrowsAndLeavesTheDatabase()
    {
        var path = ChromiumCookies();
        var before = DatabaseTestSupport.Hash(path);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => BrowserDatabaseCleaner.Clean(CleanupSqliteOperation.ChromiumCookies, path, null, cancelled.Token));
        Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
    }
}
