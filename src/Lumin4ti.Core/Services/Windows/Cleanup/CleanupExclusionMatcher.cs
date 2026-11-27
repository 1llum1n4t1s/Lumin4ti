using System.IO.Enumeration;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>
/// 利用者が指定した除外 (ファイル・フォルダー・拡張子またはファイル名) の一致判定。
/// 走査と削除が同じ判定を使い、一覧に出たものと消すものがずれないようにする。
/// </summary>
public sealed class CleanupExclusionMatcher
{
    private readonly HashSet<string> _files;
    private readonly string[] _folders;
    private readonly string[] _namePatterns;

    /// <summary>何も除外しない一致器。</summary>
    public static CleanupExclusionMatcher Empty { get; } = new(null, null, null);

    /// <param name="files">除外する絶対パスのファイル。</param>
    /// <param name="folders">除外する絶対パスのフォルダー。配下も除外する。</param>
    /// <param name="namePatterns">
    /// 除外する拡張子またはファイル名。<c>.log</c>・<c>*.log</c>・<c>thumbs.db</c>・<c>~*.*</c> の形式。
    /// パス区切りを含むものは無視する。
    /// </param>
    public CleanupExclusionMatcher(IEnumerable<string>? files, IEnumerable<string>? folders, IEnumerable<string>? namePatterns)
    {
        _files = new HashSet<string>((files ?? []).Select(NormalizePath).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        _folders = (folders ?? []).Select(NormalizePath).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _namePatterns = (namePatterns ?? []).Select(NormalizeName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>除外が 1 件もないか。</summary>
    public bool IsEmpty => _files.Count == 0 && _folders.Length == 0 && _namePatterns.Length == 0;

    /// <summary>
    /// 絶対パスが除外に当たるか。フォルダーは指定フォルダー自身と配下を除外し、
    /// ファイルはパス・親フォルダー・拡張子またはファイル名のどれかで除外する。
    /// 拡張子・ファイル名の除外はフォルダーには適用しない。
    /// </summary>
    public bool IsExcluded(string fullPath, bool isDirectory)
    {
        var path = NormalizePath(fullPath);
        if (path is null) return false;
        if (!isDirectory && (_files.Contains(path) || MatchesName(Path.GetFileName(path)))) return true;
        return _folders.Any(folder => IsSameOrUnder(path, folder));
    }

    private bool MatchesName(string name) =>
        _namePatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true));

    private static bool IsSameOrUnder(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static string? NormalizeName(string? pattern)
    {
        var text = pattern?.Trim();
        if (string.IsNullOrEmpty(text) || text.IndexOfAny(['\\', '/', ':', '\0']) >= 0) return null;
        return text[0] == '.' && text.AsSpan().IndexOfAny('*', '?') < 0 ? "*" + text : text;
    }
}
