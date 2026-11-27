using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>SQLite 操作の結果。<see cref="Succeeded"/> が false なら DB は何も変更していない (Partial)。</summary>
/// <param name="Succeeded">操作を完了した (走査では件数を取得できた)。</param>
/// <param name="Rows">削除した (走査では削除対象の) 行数。</param>
/// <param name="Reason">Partial の理由。成功時は null。</param>
internal sealed record DatabaseCleanResult(bool Succeeded, long Rows, string? Reason)
{
    internal static DatabaseCleanResult Done(long rows) => new(true, rows, null);

    internal static DatabaseCleanResult Partial(string reason) => new(false, 0, reason);
}

/// <summary>安全ガードまたはスキーマの照合で操作を拒否した。DB は変更しない。</summary>
internal sealed class DatabaseRefusedException(string reason) : Exception(reason);

/// <summary>
/// <c>System32\winsqlite3.dll</c> だけを呼ぶ薄い P/Invoke。NuGet・同梱 DLL は使わない。
/// 検索パスは System32 に限定する。
/// </summary>
internal static class WinSqlite
{
    internal const int Ok = 0;
    internal const int Row = 100;
    internal const int Done = 101;
    internal const int Interrupt = 9;
    internal const int OpenReadOnly = 0x1;
    internal const int OpenReadWrite = 0x2;
    internal const int OpenCreate = 0x4;
    internal const int OpenUri = 0x40;
    internal const int DbConfigEnableForeignKeys = 1002;
    internal const int DbConfigEnableTrigger = 1003;
    internal const int DbConfigEnableLoadExtension = 1005;
    internal const int DbConfigDefensive = 1010;
    internal const int DbConfigEnableView = 1015;
    internal const int DbConfigTrustedSchema = 1017;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out nint db, int flags, nint vfs);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close_v2", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Close(nint db);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_busy_timeout", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int BusyTimeout(nint db, int milliseconds);

    internal const int FcntlPersistWal = 10;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_file_control", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int FileControl(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string schema, int op, ref int argument);

    // sqlite3_db_config は可変長引数。x64 Windows では固定引数と同じ渡し方になる (配布は win-x64 のみ)。
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_db_config", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int DbConfig(nint db, int option, int value, nint result);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Prepare(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out nint statement, out nint tail);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Step(nint statement);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Finalize(nint statement);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_bind_text", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int BindText(nint statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int length, nint destructor);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_int64", CallingConvention = CallingConvention.Cdecl)]
    internal static extern long ColumnInt64(nint statement, int column);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint ColumnText(nint statement, int column);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_errmsg", CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint ErrorMessage(nint db);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_changes", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Changes(nint db);

    /// <summary>実行中の文が <c>operations</c> 命令ごとに呼ぶ。0 以外を返すと文は SQLITE_INTERRUPT で止まる。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ProgressCallback(nint context);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_progress_handler", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ProgressHandler(nint db, int operations, ProgressCallback? callback, nint context);
}

/// <summary>1 行分の列の読取り。文は <see cref="SqliteDb"/> が管理する。</summary>
internal readonly struct SqliteRow(nint statement)
{
    internal string? Text(int column)
    {
        var pointer = WinSqlite.ColumnText(statement, column);
        return pointer == 0 ? null : Marshal.PtrToStringUTF8(pointer);
    }

    internal long Int64(int column) => WinSqlite.ColumnInt64(statement, column);
}

/// <summary>SQLite 接続 1 本。SQL は呼び出し側の固定文だけで、値は必ずバインドして渡す。</summary>
internal sealed class SqliteDb : IDisposable
{
    private static readonly nint Transient = -1;
    private const int ProgressInterval = 1000;
    private nint _db;
    private CancellationToken _cancel;
    private WinSqlite.ProgressCallback? _progress; // ネイティブが呼ぶ間は GC されないよう保持する

    private SqliteDb(nint db) => _db = db;

    /// <summary>以降の文 (quick_check を含む) を、取消されたら途中で止めて <see cref="OperationCanceledException"/> にする。</summary>
    internal void SetCancellation(CancellationToken ct)
    {
        _cancel = ct;
        if (!ct.CanBeCanceled) return;
        _progress = _ => _cancel.IsCancellationRequested ? 1 : 0;
        WinSqlite.ProgressHandler(_db, ProgressInterval, _progress, 0);
    }

    /// <summary>コミットや巻き戻しを途中で止めないよう、取消の監視を外す。</summary>
    internal void ClearCancellation()
    {
        if (_progress is null) return;
        WinSqlite.ProgressHandler(_db, 0, null, 0);
        _progress = null;
    }

    internal static SqliteDb Open(string path, int flags)
    {
        var code = WinSqlite.Open(path, out var db, flags, 0);
        if (code != WinSqlite.Ok)
        {
            if (db != 0) WinSqlite.Close(db);
            throw new DatabaseRefusedException(CleanupMessage.Of("DbOpenFailed", code));
        }
        return new SqliteDb(db);
    }

    internal void SetBusyTimeout(int milliseconds) => WinSqlite.BusyTimeout(_db, milliseconds);

    /// <summary>
    /// 閉じる時に <c>-wal</c> を削除させない (SQLITE_FCNTL_PERSIST_WAL)。固定ハンドルは削除を許さないため、削除の再試行で閉じるのが遅くなるのを避ける。
    /// 失敗しても閉じる時間が延びるだけで安全性には影響しない。
    /// </summary>
    internal void PersistWal()
    {
        var enabled = 1;
        _ = WinSqlite.FileControl(_db, "main", WinSqlite.FcntlPersistWal, ref enabled);
    }

    internal void Configure(int option, int value, string name)
    {
        if (WinSqlite.DbConfig(_db, option, value, 0) != WinSqlite.Ok)
            throw new DatabaseRefusedException(CleanupMessage.Of("DbConfigFailed", name));
    }

    /// <summary>文を最後まで実行する。返る行は捨て、変更された行数を返す。</summary>
    internal long Execute(string sql, params string[] arguments)
    {
        Query(sql, arguments, _ => 0);
        return WinSqlite.Changes(_db);
    }

    internal long Scalar(string sql, params string[] arguments) => Query(sql, arguments, row => row.Int64(0)).FirstOrDefault();

    internal List<string> Strings(string sql, params string[] arguments) =>
        Query(sql, arguments, row => row.Text(0)).OfType<string>().ToList();

    internal List<T> Query<T>(string sql, string[] arguments, Func<SqliteRow, T> read)
    {
        var code = WinSqlite.Prepare(_db, sql, -1, out var statement, out _);
        if (code != WinSqlite.Ok) throw Error(code);
        try
        {
            for (var index = 0; index < arguments.Length; index++)
            {
                code = WinSqlite.BindText(statement, index + 1, arguments[index], -1, Transient);
                if (code != WinSqlite.Ok) throw Error(code);
            }
            var rows = new List<T>();
            while ((code = WinSqlite.Step(statement)) == WinSqlite.Row) rows.Add(read(new SqliteRow(statement)));
            if (code != WinSqlite.Done) throw Error(code);
            return rows;
        }
        finally { WinSqlite.Finalize(statement); }
    }

    private Exception Error(int code)
    {
        if (code == WinSqlite.Interrupt && _cancel.IsCancellationRequested) return new OperationCanceledException(_cancel);
        var message = Marshal.PtrToStringUTF8(WinSqlite.ErrorMessage(_db)) ?? string.Empty;
        return new DatabaseRefusedException(CleanupMessage.Of("DbSqliteError", code, message.Length > 120 ? message[..120] : message));
    }

    public void Dispose()
    {
        if (_db == 0) return;
        WinSqlite.Close(_db);
        _db = 0;
        _progress = null;
    }
}

/// <summary>
/// 非信頼の DB を安全に開いて 1 つの処理を行う共通の枠。
/// DB と祖先を固定し、再解析点・ハードリンク・未処理のジャーナル・巨大・破損・仮想表を無変更で拒否する。
/// 変更では、SQLite が名前で開く <c>-journal</c> (ロールバック) または <c>-wal</c> (WAL) を検査から終了まで同じハンドルで固定する
/// (<see cref="DatabaseSidecars"/>)。走査は immutable で開くため、サイドカーを開かず作らない。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class GuardedDatabase
{
    internal const long DefaultMaxBytes = 1L << 30;
    private const int BusyMilliseconds = 250;
    private static readonly string[] Sidecars = ["-journal", "-wal", "-shm"];

    /// <summary>テスト用の差し込み口。接続を開いて設定した直後 (quick_check の前) に呼ぶ。</summary>
    internal static Action<string>? AfterOpen { get; set; }

    /// <summary>テスト用の差し込み口。サイドカーを固定した直後 (SQLite が DB を開く前) に呼ぶ。</summary>
    internal static Action<string>? AfterAcquire { get; set; }

    /// <summary>
    /// <paramref name="work"/> は (接続, 変更するか) を受け、削除した (変更しない場合は削除対象の) 行数を返す。
    /// 変更する場合は排他の書込みトランザクション内で呼び、例外・取消は全て巻き戻す。
    /// </summary>
    internal static DatabaseCleanResult Run(string path, bool apply, long maxBytes, Func<SqliteDb, bool, long> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        ct.ThrowIfCancellationRequested();
        try
        {
            using var pin = NativeCleanupEntry.Open(path, directory: false);
            Verify(pin, maxBytes);
            if (!apply)
            {
                // immutable は -journal / -wal / -shm を開かない。変更経路と同じ基準で既存のものだけ検査する。
                foreach (var suffix in Sidecars) DatabaseSidecars.Inspect(pin.Path + suffix, requireEmpty: suffix != "-shm");
                return DatabaseCleanResult.Done(Execute(pin.Path, false, DatabaseJournalMode.Rollback, work, ct));
            }
            var mode = DatabaseSidecars.ReadMode(pin.Path);
            using var sidecars = DatabaseSidecars.Acquire(pin.Path);
            AfterAcquire?.Invoke(pin.Path);
            try { return DatabaseCleanResult.Done(Execute(pin.Path, true, mode, work, ct)); }
            finally { sidecars.Finish(); } // 接続は Execute 内で閉じ済み。自分が作った空のサイドカーだけ消す。
        }
        catch (OperationCanceledException) { throw; }
        catch (DatabaseRefusedException exception) { return DatabaseCleanResult.Partial(exception.Message); }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return DatabaseCleanResult.Partial(CleanupMessage.Of("DbPinFailed", exception.Message));
        }
    }

    private static void Verify(NativeCleanupEntry pin, long maxBytes)
    {
        if (pin.LinkCount > 1) throw new DatabaseRefusedException(CleanupMessage.Of("DbHardLinked"));
        if (pin.Length > maxBytes) throw new DatabaseRefusedException(CleanupMessage.Of("DbTooLarge"));
        if (!pin.MatchesGlobalDosPath(pin.Path)) throw new DatabaseRefusedException(CleanupMessage.Of("DbIdentityUnverified"));
    }

    private static long Execute(string path, bool apply, DatabaseJournalMode mode, Func<SqliteDb, bool, long> work, CancellationToken ct)
    {
        // 走査は immutable で開き、ロック・ジャーナル・-shm を作らず DB を変えない。変更は CREATE なしの READWRITE。
        using var db = apply
            ? SqliteDb.Open(path, WinSqlite.OpenReadWrite)
            : SqliteDb.Open(new Uri(path).AbsoluteUri + "?immutable=1", WinSqlite.OpenReadOnly | WinSqlite.OpenUri);
        db.SetBusyTimeout(BusyMilliseconds);
        db.Configure(WinSqlite.DbConfigDefensive, 1, "DEFENSIVE");
        db.Configure(WinSqlite.DbConfigEnableTrigger, 0, "trigger");
        db.Configure(WinSqlite.DbConfigEnableView, 0, "view");
        db.Configure(WinSqlite.DbConfigTrustedSchema, 0, "trusted_schema");
        db.Configure(WinSqlite.DbConfigEnableLoadExtension, 0, "load extension");
        db.SetCancellation(ct);
        db.Execute("PRAGMA temp_store=MEMORY");
        if (apply)
        {
            db.Configure(WinSqlite.DbConfigEnableForeignKeys, 1, "foreign keys");
            // 最初の DB アクセスより前に指定する。WAL でも -shm を使わず (ヒープ上の wal-index)、他の接続とは DB ファイルの排他ロックで競合する。
            db.Execute("PRAGMA locking_mode=EXCLUSIVE");
            db.Execute("PRAGMA secure_delete=ON");
            RequireJournalMode(db, mode);
            if (mode == DatabaseJournalMode.Wal) db.PersistWal();
        }
        AfterOpen?.Invoke(path);
        db.Execute(apply ? "BEGIN IMMEDIATE" : "BEGIN");
        try
        {
            if (!db.Strings("PRAGMA quick_check(1)").SequenceEqual(["ok"]))
                throw new DatabaseRefusedException(CleanupMessage.Of("DbIntegrityFailed"));
            if (db.Scalar("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND sql LIKE 'CREATE VIRTUAL%'") != 0)
                throw new DatabaseRefusedException(CleanupMessage.Of("DbVirtualTable"));
            var rows = work(db, apply);
            ct.ThrowIfCancellationRequested();
            db.ClearCancellation(); // コミットは途中で止めない
            db.Execute(apply ? "COMMIT" : "ROLLBACK");
            if (apply && mode == DatabaseJournalMode.Wal) Checkpoint(db);
            return rows;
        }
        catch
        {
            db.ClearCancellation();
            try { db.Execute("ROLLBACK"); } catch (DatabaseRefusedException) { /* 接続を閉じると巻き戻る */ }
            throw;
        }
    }

    /// <summary>
    /// 固定したサイドカーと実際の方式を一致させる。ロールバック方式は journal_mode=TRUNCATE (終了時の削除を SQLite にさせない)。
    /// 固定後に DB のヘッダーが書き換えられて方式が食い違ったら、書込み前に拒否する。
    /// </summary>
    private static void RequireJournalMode(SqliteDb db, DatabaseJournalMode mode)
    {
        var actual = db.Strings(mode == DatabaseJournalMode.Wal ? "PRAGMA journal_mode" : "PRAGMA journal_mode=TRUNCATE");
        if (!actual.SequenceEqual([mode == DatabaseJournalMode.Wal ? "wal" : "truncate"]))
            throw new DatabaseRefusedException(CleanupMessage.Of("DbIdentityUnverified"));
    }

    /// <summary>
    /// WAL の内容を DB へ戻して -wal を 0 バイトにする。固定ハンドルのせいで SQLite が閉じる時の -wal 削除は失敗するため、
    /// 空にしておく。失敗してもコミット済みの内容は -wal に残り、次の接続が回復するので結果は壊れない。
    /// </summary>
    private static void Checkpoint(SqliteDb db)
    {
        try { db.Query("PRAGMA wal_checkpoint(TRUNCATE)", [], _ => 0); }
        catch (DatabaseRefusedException) { /* コミット済み。-wal に残る */ }
    }

    /// <summary>名前が通常の表 (ビュー・仮想表以外) で、期待する列が全て存在することを確認する。</summary>
    internal static void RequireTable(SqliteDb db, string table, IEnumerable<string> columns)
    {
        var kinds = db.Strings("SELECT type FROM sqlite_master WHERE lower(name) = lower(?) AND type IN ('table', 'view')", table);
        if (kinds.Count != 1 || kinds[0] != "table") throw new DatabaseRefusedException(CleanupMessage.Of("DbUnknownTable", table));
        var present = db.Strings("SELECT name FROM pragma_table_info(?)", table).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = columns.FirstOrDefault(column => !present.Contains(column));
        if (missing is not null) throw new DatabaseRefusedException(CleanupMessage.Of("DbUnknownColumn", table, missing));
    }

    internal static bool TableExists(SqliteDb db, string table) =>
        db.Scalar("SELECT count(*) FROM sqlite_master WHERE lower(name) = lower(?) AND type = 'table'", table) > 0;
}
