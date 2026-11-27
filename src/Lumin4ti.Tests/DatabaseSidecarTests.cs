using Lumin4ti.Core.Services.Windows.Cleanup;

namespace Lumin4ti.Tests;

/// <summary>
/// 変更中の -journal / -wal の固定を確かめる。専用の一時領域の合成 DB だけを使い、検査後の差替えは
/// 作業の途中 (固定中) で置換・削除・改名を試みて拒否されることで模擬する (シンボリックリンクの権限は要らない)。
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DatabaseSidecarTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Setup() => _root = DatabaseTestSupport.NewRoot();

    [TestCleanup]
    public void Cleanup()
    {
        GuardedDatabase.AfterOpen = null;
        GuardedDatabase.AfterAcquire = null;
        DatabaseTestSupport.DeleteRoot(_root);
    }

    private string Create(bool wal, string name = "Cookies") => DatabaseTestSupport.CreateDatabase(_root, name,
    [
        .. wal ? new[] { "PRAGMA journal_mode=WAL" } : [],
        "CREATE TABLE cookies (host_key TEXT, name TEXT)",
        "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 2000) INSERT INTO cookies SELECT 'host' || i || '.test', hex(randomblob(16)) FROM n",
    ]);

    private static void AssertBlocked(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return; }
        Assert.Fail("固定中のサイドカーを操作できてしまいました");
    }

    /// <summary>固定中に置換・削除・改名・別名への移動が拒否されることを確かめる作業 (全行を削除する)。</summary>
    private static Func<SqliteDb, bool, long> ProbeAndDelete(string sidecar, string other, Action<string>? after = null) => (db, write) =>
    {
        var rows = db.Scalar("SELECT count(*) FROM cookies");
        if (!write) return rows;
        db.Execute("DELETE FROM cookies");
        Assert.IsTrue(File.Exists(sidecar), "固定したサイドカーが無い");
        AssertBlocked(() => File.Delete(sidecar));
        AssertBlocked(() => File.Move(sidecar, other));
        File.WriteAllText(other, "attacker");
        AssertBlocked(() => File.Move(other, sidecar, overwrite: true));
        AssertBlocked(() => File.Replace(other, sidecar, null));
        after?.Invoke(sidecar);
        return rows;
    };

    /// <summary>他のハンドルで開かれている間は、FileInfo のメタデータが古いことがあるため、ハンドルで長さを読む。</summary>
    private static long LiveLength(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return RandomAccess.GetLength(handle);
    }

    private static void AssertNoSidecars(string path)
    {
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
            Assert.IsFalse(File.Exists(path + suffix) && new FileInfo(path + suffix).Length > 0, suffix + " が中身を持ったまま残っている");
    }

    [TestMethod]
    public void Rollback_PinsTheJournalAndRemovesItAfterwards()
    {
        var path = Create(wal: false);
        var other = Path.Combine(_root, "other");
        var result = GuardedDatabase.Run(path, true, GuardedDatabase.DefaultMaxBytes,
            ProbeAndDelete(path + "-journal", other, journal => Assert.IsGreaterThan(0, LiveLength(journal), "TRUNCATE のジャーナルに書かれていない")),
            CancellationToken.None);
        Assert.IsTrue(result.Succeeded, result.Reason);
        Assert.AreEqual(2000, result.Rows);
        Assert.AreEqual(0, DatabaseTestSupport.Count(path, "SELECT count(*) FROM cookies"));
        CollectionAssert.AreEqual(new[] { "ok" }, DatabaseTestSupport.Strings(path, "PRAGMA integrity_check"));
        Assert.IsFalse(File.Exists(path + "-journal"), "自分が作ったジャーナルが残っている");
        Assert.IsFalse(File.Exists(path + "-wal"));
        Assert.IsFalse(File.Exists(path + "-shm"));
        Assert.AreEqual("attacker", File.ReadAllText(other));
    }

    [TestMethod]
    public void Rollback_KeepsAnExistingEmptyJournal()
    {
        var path = Create(wal: false);
        File.WriteAllBytes(path + "-journal", []);
        var result = GuardedDatabase.Run(path, true, GuardedDatabase.DefaultMaxBytes,
            ProbeAndDelete(path + "-journal", Path.Combine(_root, "other")), CancellationToken.None);
        Assert.IsTrue(result.Succeeded, result.Reason);
        Assert.IsTrue(File.Exists(path + "-journal"));
        Assert.AreEqual(0, new FileInfo(path + "-journal").Length);
        Assert.AreEqual(0, DatabaseTestSupport.Count(path, "SELECT count(*) FROM cookies"));
    }

    [TestMethod]
    public void Wal_PinsTheWalUsesNoSharedMemoryAndLeavesAConsistentDatabase()
    {
        var path = Create(wal: true);
        Assert.IsFalse(File.Exists(path + "-wal"), "合成 DB の作成後に -wal が残っている");
        var other = Path.Combine(_root, "other");
        var result = GuardedDatabase.Run(path, true, GuardedDatabase.DefaultMaxBytes,
            ProbeAndDelete(path + "-wal", other, wal =>
            {
                Assert.IsFalse(File.Exists(path + "-shm"), "EXCLUSIVE なのに -shm が作られた");
            }), CancellationToken.None);
        Assert.IsTrue(result.Succeeded, result.Reason);
        Assert.AreEqual(2000, result.Rows);
        // 開き直すと SQLite が -wal / -shm を作るので、先にサイドカーの有無を確かめる。
        Assert.IsFalse(File.Exists(path + "-wal"), "自分が作った -wal が残っている");
        Assert.IsFalse(File.Exists(path + "-shm"));
        Assert.AreEqual(2, File.ReadAllBytes(path)[18]);
        // 変更は DB 本体に入っている (-wal に頼らない): DB ファイルだけを写して確かめる。
        var alone = Path.Combine(_root, "alone");
        File.Copy(path, alone);
        Assert.AreEqual(0, DatabaseTestSupport.Count(alone, "SELECT count(*) FROM cookies"));
        CollectionAssert.AreEqual(new[] { "ok" }, DatabaseTestSupport.Strings(alone, "PRAGMA integrity_check"));
    }

    [TestMethod]
    public void Wal_KeepsAnExistingEmptyWal()
    {
        var path = Create(wal: true);
        File.WriteAllBytes(path + "-wal", []);
        var result = GuardedDatabase.Run(path, true, GuardedDatabase.DefaultMaxBytes,
            ProbeAndDelete(path + "-wal", Path.Combine(_root, "other")), CancellationToken.None);
        Assert.IsTrue(result.Succeeded, result.Reason);
        Assert.IsTrue(File.Exists(path + "-wal"));
        Assert.AreEqual(0, new FileInfo(path + "-wal").Length);
        Assert.AreEqual(0, DatabaseTestSupport.Count(path, "SELECT count(*) FROM cookies"));
    }

    [TestMethod]
    public void Wal_WithUnreflectedWalContent_IsPartialAndLosesNothing()
    {
        var source = Create(wal: true, "source");
        var folder = Directory.CreateDirectory(Path.Combine(_root, "copy")).FullName;
        var path = Path.Combine(folder, "Cookies");
        using (var holder = SqliteDb.Open(source, WinSqlite.OpenReadWrite))
        {
            holder.Execute("PRAGMA wal_autocheckpoint=0");
            holder.Execute("INSERT INTO cookies VALUES ('only-in-wal.test', 'x')");
            // 接続が開いている間の DB と -wal を丸ごと写す。-wal にしか無い行がある。
            File.Copy(source, path);
            File.Copy(source + "-wal", path + "-wal");
        }
        Assert.IsGreaterThan(0, new FileInfo(path + "-wal").Length);
        var databaseBefore = DatabaseTestSupport.Hash(path);
        var walBefore = DatabaseTestSupport.Hash(path + "-wal");
        var result = AppDatabaseCleaner.Clean(path, "cookies", ["host_key"], CancellationToken.None);
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("Cleanup.Msg.DbJournalPending", CleanupMessage.KeyOf(result.Reason));
        Assert.AreEqual(databaseBefore, DatabaseTestSupport.Hash(path));
        Assert.AreEqual(walBefore, DatabaseTestSupport.Hash(path + "-wal"));
        Assert.IsFalse(File.Exists(path + "-shm"));
    }

    [TestMethod]
    public void Wal_InUseByAnotherConnection_IsPartialWithoutChange()
    {
        var path = Create(wal: true);
        var before = DatabaseTestSupport.Hash(path);
        using (var holder = SqliteDb.Open(path, WinSqlite.OpenReadWrite))
        {
            Assert.AreEqual(2000, holder.Scalar("SELECT count(*) FROM cookies"));
            var result = AppDatabaseCleaner.Clean(path, "cookies", ["host_key"], CancellationToken.None);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(2000, holder.Scalar("SELECT count(*) FROM cookies"));
        }
        Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
        Assert.AreEqual(2000, DatabaseTestSupport.Count(path, "SELECT count(*) FROM cookies"));
    }

    [TestMethod]
    public void Rollback_InUseByAnotherConnection_IsPartialAndLeavesNoJournal()
    {
        var path = Create(wal: false);
        var before = DatabaseTestSupport.Hash(path);
        using (var holder = SqliteDb.Open(path, WinSqlite.OpenReadWrite))
        {
            holder.Execute("BEGIN EXCLUSIVE");
            Assert.IsFalse(AppDatabaseCleaner.Clean(path, "cookies", ["host_key"], CancellationToken.None).Succeeded);
        }
        Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
        Assert.IsFalse(File.Exists(path + "-journal"), "失敗した処理が自分で作ったジャーナルを残した");
    }

    [TestMethod]
    public void Rollback_WithAnEmptyWalBesideIt_StaysInRollbackModeAndKeepsTheEmptyWal()
    {
        // 空の -wal は SQLite が「無い」と扱う。固定したままロールバック方式で動くことを winsqlite3 の実物で確かめる。
        var path = Create(wal: false);
        File.WriteAllBytes(path + "-wal", []);
        var result = GuardedDatabase.Run(path, true, GuardedDatabase.DefaultMaxBytes,
            ProbeAndDelete(path + "-wal", Path.Combine(_root, "other")), CancellationToken.None);
        Assert.IsTrue(result.Succeeded, result.Reason);
        Assert.AreEqual(0, DatabaseTestSupport.Count(path, "SELECT count(*) FROM cookies"));
        Assert.AreEqual(1, File.ReadAllBytes(path)[18], "ロールバック方式のまま");
        Assert.IsTrue(File.Exists(path + "-wal"));
        Assert.AreEqual(0, new FileInfo(path + "-wal").Length);
        Assert.IsFalse(File.Exists(path + "-journal"));
    }

    [TestMethod]
    public void BothSidecarsArePinnedInBothModesBetweenAcquireAndOpen()
    {
        foreach (var wal in new[] { false, true })
        {
            var path = Create(wal, wal ? "w" : "r");
            var other = Path.Combine(_root, wal ? "other-w" : "other-r");
            var observed = false;
            GuardedDatabase.AfterAcquire = database =>
            {
                observed = true;
                foreach (var sidecar in new[] { database + "-journal", database + "-wal" })
                {
                    Assert.IsTrue(File.Exists(sidecar), sidecar + " を固定していない");
                    AssertBlocked(() => File.Delete(sidecar));
                    AssertBlocked(() => File.Move(sidecar, other));
                    File.WriteAllText(other, "attacker");
                    AssertBlocked(() => File.Move(other, sidecar, overwrite: true));
                    AssertBlocked(() => File.Replace(other, sidecar, null));
                    File.Delete(other);
                }
            };
            var result = GuardedDatabase.Run(path, true, GuardedDatabase.DefaultMaxBytes, (db, write) => write ? db.Execute("DELETE FROM cookies") : 0, CancellationToken.None);
            Assert.IsTrue(observed);
            Assert.IsTrue(result.Succeeded, result.Reason);
            // 開き直すと SQLite が -wal / -shm を作るので、先にサイドカーの有無を確かめる。
            foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
                Assert.IsFalse(File.Exists(path + suffix), (wal ? "wal" : "rollback") + suffix + " が残っている");
            Assert.AreEqual(0, DatabaseTestSupport.Count(path, "SELECT count(*) FROM cookies"));
        }
    }

    [TestMethod]
    public void HeaderRewrittenToRollbackAfterAcquireOnAWalDatabase_IsPartialWithoutLoss()
    {
        var path = Create(wal: true);
        GuardedDatabase.AfterAcquire = RewriteJournalVersion(1);
        var result = GuardedDatabase.Run(path, true, GuardedDatabase.DefaultMaxBytes, (db, write) => write ? db.Execute("DELETE FROM cookies") : 0, CancellationToken.None);
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("Cleanup.Msg.DbIdentityUnverified", CleanupMessage.KeyOf(result.Reason));
        Assert.AreEqual(2000, DatabaseTestSupport.Count(path, "SELECT count(*) FROM cookies"));
        AssertNoSidecars(path);
    }

    [TestMethod]
    public void HeaderRewrittenToWalAfterAcquireOnARollbackDatabase_NeverLosesRowsOrLeavesContent()
    {
        var path = Create(wal: false);
        GuardedDatabase.AfterAcquire = RewriteJournalVersion(2);
        var result = GuardedDatabase.Run(path, true, GuardedDatabase.DefaultMaxBytes, (db, write) => write ? db.Execute("DELETE FROM cookies") : 0, CancellationToken.None);
        if (!result.Succeeded) Assert.AreEqual("Cleanup.Msg.DbIdentityUnverified", CleanupMessage.KeyOf(result.Reason));
        AssertNoSidecars(path);
        CollectionAssert.AreEqual(new[] { "ok" }, DatabaseTestSupport.Strings(path, "PRAGMA integrity_check"));
        Assert.AreEqual(result.Succeeded ? 0 : 2000, DatabaseTestSupport.Count(path, "SELECT count(*) FROM cookies"));
    }

    /// <summary>固定の後に、攻撃者が DB のヘッダー (オフセット 18・19) を書き換える模擬。</summary>
    private static Action<string> RewriteJournalVersion(byte version) => database =>
    {
        using var handle = File.OpenHandle(database, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        RandomAccess.Write(handle, [version, version], 18);
    };

    [TestMethod]
    public void HardLinkedJournal_IsPartialAndTheLinkTargetIsUntouched()
    {
        foreach (var wal in new[] { false, true })
        {
            var path = Create(wal, wal ? "w" : "r");
            var victim = Path.Combine(_root, wal ? "victim-w" : "victim-r");
            File.WriteAllText(victim, "precious");
            DatabaseTestSupport.RunCmd($"mklink /H \"{path}{(wal ? "-wal" : "-journal")}\" \"{victim}\"");
            var before = DatabaseTestSupport.Hash(path);
            Assert.IsFalse(AppDatabaseCleaner.Clean(path, "cookies", ["host_key"], CancellationToken.None).Succeeded, wal ? "wal" : "rollback");
            Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
            Assert.AreEqual("precious", File.ReadAllText(victim));
        }
    }

    [TestMethod]
    public void SymbolicLinkSidecar_IsPartialAndTheTargetIsUntouched()
    {
        var path = Create(wal: false);
        var victim = Path.Combine(_root, "victim");
        File.WriteAllText(victim, "precious");
        try { File.CreateSymbolicLink(path + "-journal", victim); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            Assert.Inconclusive("シンボリックリンクを作成できない環境です (固定の検証は他のテストが行う): " + exception.Message);
            return;
        }
        var before = DatabaseTestSupport.Hash(path);
        Assert.IsFalse(AppDatabaseCleaner.Clean(path, "cookies", ["host_key"], CancellationToken.None).Succeeded);
        Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
        Assert.AreEqual("precious", File.ReadAllText(victim));
    }

    [TestMethod]
    public void JunctionWalBesideAWalDatabase_IsPartialWithoutChange()
    {
        var path = Create(wal: true);
        var target = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        DatabaseTestSupport.RunCmd($"mklink /J \"{path}-wal\" \"{target}\"");
        var before = DatabaseTestSupport.Hash(path);
        Assert.IsFalse(AppDatabaseCleaner.Clean(path, "cookies", ["host_key"], CancellationToken.None).Succeeded);
        Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
    }

    [TestMethod]
    public void Scan_OpensNoSidecarsInEitherMode()
    {
        foreach (var wal in new[] { false, true })
        {
            var path = Create(wal, wal ? "w" : "r");
            var result = AppDatabaseCleaner.Count(path, "cookies", ["host_key"], CancellationToken.None);
            Assert.IsTrue(result.Succeeded, result.Reason);
            Assert.AreEqual(2000, result.Rows);
            foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
                Assert.IsFalse(File.Exists(path + suffix), (wal ? "wal" : "rollback") + suffix);
        }
    }

    [TestMethod]
    public void Cancellation_DuringQuickCheck_StopsAndLeavesTheDatabase()
    {
        foreach (var wal in new[] { false, true })
        {
            var path = Create(wal, wal ? "w" : "r");
            var before = DatabaseTestSupport.Hash(path);
            using var cancel = new CancellationTokenSource();
            GuardedDatabase.AfterOpen = _ => cancel.Cancel();
            var reached = false;
            Assert.ThrowsExactly<OperationCanceledException>(() => GuardedDatabase.Run(path, true, GuardedDatabase.DefaultMaxBytes,
                (_, _) => { reached = true; return 0; }, cancel.Token));
            Assert.IsFalse(reached, "取消後に作業まで進んだ (quick_check が止まっていない)");
            Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
            AssertNoSidecars(path);
            Assert.IsFalse(File.Exists(path + "-journal") || File.Exists(path + "-wal"));
        }
    }

    [TestMethod]
    [Timeout(60000)]
    public void Cancellation_DuringALongStatement_RollsBack()
    {
        var path = Create(wal: false);
        var before = DatabaseTestSupport.Hash(path);
        using var cancel = new CancellationTokenSource();
        cancel.CancelAfter(300);
        Assert.ThrowsExactly<OperationCanceledException>(() => GuardedDatabase.Run(path, true, GuardedDatabase.DefaultMaxBytes, (db, _) =>
        {
            db.Execute("DELETE FROM cookies");
            return db.Scalar("WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT count(*) FROM c");
        }, cancel.Token));
        Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
        Assert.IsFalse(File.Exists(path + "-journal"));
        Assert.AreEqual(2000, DatabaseTestSupport.Count(path, "SELECT count(*) FROM cookies"));
    }

    [TestMethod]
    public void FailingWork_RollsBackAndRemovesTheJournalInBothModes()
    {
        foreach (var wal in new[] { false, true })
        {
            var path = Create(wal, wal ? "w" : "r");
            var before = DatabaseTestSupport.Hash(path);
            var result = GuardedDatabase.Run(path, true, GuardedDatabase.DefaultMaxBytes, (db, _) =>
            {
                db.Execute("DELETE FROM cookies");
                GuardedDatabase.RequireTable(db, "missing", ["a"]);
                return 0;
            }, CancellationToken.None);
            Assert.IsFalse(result.Succeeded);
            foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
                Assert.IsFalse(File.Exists(path + suffix), (wal ? "wal" : "rollback") + suffix);
            Assert.AreEqual(before, DatabaseTestSupport.Hash(path));
            Assert.AreEqual(2000, DatabaseTestSupport.Count(path, "SELECT count(*) FROM cookies"));
        }
    }
}
