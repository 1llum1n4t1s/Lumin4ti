using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>
/// <c>clearTable</c>: 指定された表の全行を削除する。データに書けるのは DB パス・表名・期待する列名だけで、WHERE と SQL は書けない。
/// 認証・決済・セッションを思わせる名前の表は拒否する。スキーマが合わない・ロック中・破損などは無変更で Partial を返す。
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class AppDatabaseCleaner
{
    private static readonly string[] ForbiddenWords = ["login", "password", "token", "credential", "account", "card", "key", "session"];

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    /// <summary>表名と列名が許される形かを確かめる。違反の理由は <paramref name="reason"/> に返す。</summary>
    internal static bool Validate(string? table, IReadOnlyCollection<string>? columns, out string reason)
    {
        reason = string.Empty;
        if (table is null || !IdentifierPattern().IsMatch(table) || table.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
        {
            reason = "表名が識別子の形式ではありません";
            return false;
        }
        var word = ForbiddenWords.FirstOrDefault(word => table.Contains(word, StringComparison.OrdinalIgnoreCase));
        if (word is not null)
        {
            reason = $"表名に認証・決済・セッションを示す語 ({word}) を含むため拒否しました";
            return false;
        }
        if (columns is null || columns.Count == 0 || columns.Any(column => column is null || !IdentifierPattern().IsMatch(column)))
        {
            reason = "期待する列名が必要で、識別子の形式でなければなりません";
            return false;
        }
        return true;
    }

    internal static DatabaseCleanResult Clean(string databasePath, string table, IReadOnlyCollection<string> columns, CancellationToken ct,
        long maxBytes = GuardedDatabase.DefaultMaxBytes) =>
        Run(databasePath, table, columns, apply: true, ct, maxBytes);

    /// <summary>削除対象の行数だけを数える。DB を immutable で開き、何も変更しない。</summary>
    internal static DatabaseCleanResult Count(string databasePath, string table, IReadOnlyCollection<string> columns, CancellationToken ct,
        long maxBytes = GuardedDatabase.DefaultMaxBytes) =>
        Run(databasePath, table, columns, apply: false, ct, maxBytes);

    private static DatabaseCleanResult Run(string path, string table, IReadOnlyCollection<string> columns, bool apply, CancellationToken ct, long maxBytes)
    {
        if (!Validate(table, columns, out var reason)) return DatabaseCleanResult.Partial(reason);
        return GuardedDatabase.Run(path, apply, maxBytes, (db, write) =>
        {
            GuardedDatabase.RequireTable(db, table, columns);
            var rows = db.Scalar($"SELECT count(*) FROM \"{table}\"");
            if (write) db.Execute($"DELETE FROM \"{table}\"");
            return rows;
        }, ct);
    }
}
