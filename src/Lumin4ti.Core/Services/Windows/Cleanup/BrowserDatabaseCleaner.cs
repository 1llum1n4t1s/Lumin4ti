using System.Globalization;
using System.Runtime.Versioning;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>
/// ブラウザーの SQLite を固定操作だけで編集する。SQL・表名・列名は全て本クラスの定数で、データからは選べない。
/// パスワード・カード・住所・検索エンジン・トークンの表には触れない。
/// 未知のスキーマ・ロック・破損・仮想表などは無変更で Partial を返す。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class BrowserDatabaseCleaner
{
    private const string BookmarkFreePlaces =
        "foreign_count = 0 AND id NOT IN (SELECT fk FROM moz_bookmarks WHERE fk IS NOT NULL)";

    /// <summary>固定操作を適用する。<paramref name="excludedDomains"/> は Cookie 操作だけで使う (本体とサブドメインを残す)。</summary>
    internal static DatabaseCleanResult Clean(CleanupSqliteOperation operation, string databasePath, IReadOnlyCollection<string>? excludedDomains,
        CancellationToken ct, long maxBytes = GuardedDatabase.DefaultMaxBytes) =>
        Run(operation, databasePath, excludedDomains, apply: true, ct, maxBytes);

    /// <summary>削除対象の行数だけを数える。DB を immutable で開き、何も変更しない。</summary>
    internal static DatabaseCleanResult Count(CleanupSqliteOperation operation, string databasePath, IReadOnlyCollection<string>? excludedDomains,
        CancellationToken ct, long maxBytes = GuardedDatabase.DefaultMaxBytes) =>
        Run(operation, databasePath, excludedDomains, apply: false, ct, maxBytes);

    private static DatabaseCleanResult Run(CleanupSqliteOperation operation, string path, IReadOnlyCollection<string>? excludedDomains, bool apply,
        CancellationToken ct, long maxBytes)
    {
        var domains = NormalizeDomains(excludedDomains);
        Func<SqliteDb, bool, long> work = operation switch
        {
            CleanupSqliteOperation.ChromiumCookies => (db, write) => ClearCookies(db, write, "cookies", "host_key", domains, ct),
            CleanupSqliteOperation.GeckoCookies => (db, write) => ClearCookies(db, write, "moz_cookies", "host", domains, ct),
            CleanupSqliteOperation.ChromiumWebData => (db, write) => ClearAll(db, write, "autofill", ["name", "value"]),
            CleanupSqliteOperation.GeckoFormHistory => (db, write) => ClearAll(db, write, "moz_formhistory", ["fieldname", "value"]),
            CleanupSqliteOperation.GeckoPlaces => ClearPlaces,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "ブラウザーの固定操作ではありません"),
        };
        return GuardedDatabase.Run(path, apply, maxBytes, work, ct);
    }

    /// <summary>除外ドメインを小文字・先頭ドット無しにそろえる。空・不正な名前は無視し、非 ASCII は punycode にする。</summary>
    internal static List<string> NormalizeDomains(IReadOnlyCollection<string>? domains)
    {
        var result = new List<string>();
        foreach (var raw in domains ?? [])
        {
            var domain = raw?.Trim().TrimStart('.').TrimEnd('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(domain)) continue;
            try { domain = new IdnMapping().GetAscii(domain); }
            catch (ArgumentException) { continue; }
            result.Add(domain);
        }
        return result;
    }

    /// <summary>host が除外ドメイン本体またはそのサブドメインなら true。先頭ドット付きの host_key も同じ扱い。</summary>
    internal static bool IsExcludedHost(string host, IReadOnlyCollection<string> normalizedDomains)
    {
        var name = host.Trim().TrimStart('.').ToLowerInvariant();
        return normalizedDomains.Any(domain => name == domain || name.EndsWith("." + domain, StringComparison.Ordinal));
    }

    private static long ClearCookies(SqliteDb db, bool apply, string table, string column, List<string> domains, CancellationToken ct)
    {
        GuardedDatabase.RequireTable(db, table, [column]);
        var hosts = db.Query($"SELECT {column}, count(*) FROM {table} WHERE {column} IS NOT NULL GROUP BY {column}", [],
            row => (Host: row.Text(0) ?? string.Empty, Count: row.Int64(1)));
        var doomed = hosts.Where(entry => !IsExcludedHost(entry.Host, domains)).ToList();
        if (apply)
        {
            foreach (var entry in doomed)
            {
                ct.ThrowIfCancellationRequested();
                db.Execute($"DELETE FROM {table} WHERE {column} = ?", entry.Host);
            }
        }
        return doomed.Sum(entry => entry.Count);
    }

    private static long ClearAll(SqliteDb db, bool apply, string table, string[] columns)
    {
        GuardedDatabase.RequireTable(db, table, columns);
        var rows = db.Scalar($"SELECT count(*) FROM {table}");
        if (apply) db.Execute($"DELETE FROM {table}");
        return rows;
    }

    private static long ClearPlaces(SqliteDb db, bool apply)
    {
        var hasOrigins = GuardedDatabase.TableExists(db, "moz_origins");
        GuardedDatabase.RequireTable(db, "moz_places", hasOrigins ? ["id", "foreign_count", "origin_id"] : ["id", "foreign_count"]);
        GuardedDatabase.RequireTable(db, "moz_historyvisits", ["place_id"]);
        GuardedDatabase.RequireTable(db, "moz_bookmarks", ["fk"]);
        if (hasOrigins) GuardedDatabase.RequireTable(db, "moz_origins", ["id"]);
        var rows = db.Scalar($"SELECT count(*) FROM moz_places WHERE {BookmarkFreePlaces}");
        if (!apply) return rows;
        // トリガーは無効なので、訪問・場所・孤立した origin をここで順に消す。外部キーは有効で、想定外の参照があれば失敗して巻き戻る。
        db.Execute($"DELETE FROM moz_historyvisits WHERE place_id IN (SELECT id FROM moz_places WHERE {BookmarkFreePlaces})");
        db.Execute($"DELETE FROM moz_places WHERE {BookmarkFreePlaces}");
        if (hasOrigins) db.Execute("DELETE FROM moz_origins WHERE id NOT IN (SELECT origin_id FROM moz_places WHERE origin_id IS NOT NULL)");
        return rows;
    }
}
