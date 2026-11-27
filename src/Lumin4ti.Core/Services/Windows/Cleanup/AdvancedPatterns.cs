using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>高度なクリーンの規則の区分 (既定・追加・ユーザー定義の 3 つ)。</summary>
public enum AdvancedPatternGroup
{
    /// <summary>既定でオンの規則。</summary>
    Default,

    /// <summary>追加の規則。既定ではオフで、利用者が有効にしたときだけ走査する。</summary>
    Additional,

    /// <summary>利用者が追加した拡張子・ファイル名。</summary>
    UserDefined,
}

/// <summary>規則の種類。</summary>
public enum AdvancedRuleKind
{
    /// <summary>ファイル名・拡張子のパターン。</summary>
    NamePattern,

    /// <summary>0 バイトのファイル。</summary>
    ZeroByte,

    /// <summary>リンク先の欠損が確定した .lnk。</summary>
    InvalidShortcut,

    /// <summary>指定日数より古いファイル。</summary>
    OldFile,
}

/// <summary>結果の安全度 (安全・判定不能・ユーザー定義の 3 つ)。</summary>
public enum AdvancedVerdict
{
    /// <summary>再生成できる、または不要と判断できる。</summary>
    Safe,

    /// <summary>中身を確かめないと消してよいか分からない。既定では未選択。</summary>
    Undeterminable,

    /// <summary>利用者が指定したパターンに一致した。既定では未選択。</summary>
    UserDefined,
}

/// <summary>既定で選択しない理由、または判定不能の理由 (画面の文言キーに対応させる)。</summary>
public enum AdvancedHint
{
    /// <summary>なし。</summary>
    None,

    /// <summary><c>FOUND.nnn\FILEnnnn.CHK</c> 以外の .chk (ESE のチェックポイントなどの可能性)。</summary>
    CheckpointOutsideFound,

    /// <summary>24 時間以内に更新された。</summary>
    RecentlyModified,

    /// <summary>0 バイト。</summary>
    ZeroByte,

    /// <summary>古いというだけで一致した。</summary>
    OldFile,

    /// <summary>利用者が指定したパターン。</summary>
    UserPattern,
}

/// <summary>規則 1 件。</summary>
/// <param name="Pattern">正規化したパターン。特別な規則では空。</param>
/// <param name="Group">区分。</param>
/// <param name="Kind">種類。</param>
/// <param name="Order">同じファイルが複数に一致したときの優先順 (小さいほど優先)。</param>
public sealed record AdvancedPatternRule(string Pattern, AdvancedPatternGroup Group, AdvancedRuleKind Kind, int Order);

/// <summary>判定の結果。</summary>
public readonly record struct AdvancedClassification(AdvancedVerdict Verdict, bool DefaultSelected, AdvancedHint Hint);

/// <summary>
/// 高度なクリーンの規則表と判定。既定 (オン) と追加 (オフ) の組み込み表、ユーザー定義、
/// 特別な規則 (0 バイト・無効なショートカット・古いファイル) を扱う。
/// </summary>
public sealed class AdvancedPatterns
{
    /// <summary>24 時間以内に更新されたファイルは既定で選択しない。</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(24);

    /// <summary>既定でオンのパターン。</summary>
    public static IReadOnlyList<string> DefaultPatterns { get; } =
    [
        "*.tmp", "*.temp", "*.chk", "*.gid", "*.dmp", "*.mdmp", "*.hdmp", "*.fts", "*.ftg", "*._mp", "*.$$$", "*.nch",
    ];

    /// <summary>追加 (既定でオフ) のパターン。</summary>
    public static IReadOnlyList<string> AdditionalPatterns { get; } =
    [
        "*.log", "*.old", "*.bak", "*.wbk", "*.err", "*.prv", "*.syd", "*.sik", "*.~*", "~*.*", "*.ilk", "*.pch",
        "*.ncb", "*.sbr", "thumbs.db", "ehthumbs.db", "mscreate.dir",
    ];

    private readonly Dictionary<string, AdvancedPatternRule> _byExtension = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AdvancedPatternRule> _wildcards = [];

    private AdvancedPatterns(IReadOnlyList<AdvancedPatternRule> rules)
    {
        Rules = rules;
        foreach (var rule in rules)
        {
            var extension = ExtensionOf(rule.Pattern);
            if (extension is null) _wildcards.Add(rule);
            else _byExtension.TryAdd(extension, rule);
        }
    }

    /// <summary>有効な名前パターンの規則 (優先順)。</summary>
    public IReadOnlyList<AdvancedPatternRule> Rules { get; }

    /// <summary>
    /// 保存された選択 (<see cref="CleanupSettingsStore.GetPatterns"/>) から有効な規則を作る。
    /// 組み込みの表にあるキーは選択で上書きし、無いキーは正規化に通ったものだけをユーザー定義として扱う。
    /// </summary>
    public static AdvancedPatterns Resolve(IReadOnlyDictionary<string, bool>? saved)
    {
        var selection = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, enabled) in saved ?? new Dictionary<string, bool>())
        {
            if (CleanupInput.NormalizePattern(key) is { } normalized) selection[normalized] = enabled;
        }

        var rules = new List<AdvancedPatternRule>();
        var builtIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddBuiltIn(rules, builtIn, selection, DefaultPatterns, AdvancedPatternGroup.Default, defaultEnabled: true);
        AddBuiltIn(rules, builtIn, selection, AdditionalPatterns, AdvancedPatternGroup.Additional, defaultEnabled: false);
        foreach (var (pattern, enabled) in selection.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (enabled && !builtIn.Contains(pattern))
            {
                rules.Add(new AdvancedPatternRule(pattern, AdvancedPatternGroup.UserDefined, AdvancedRuleKind.NamePattern, rules.Count));
            }
        }

        return new AdvancedPatterns(rules);
    }

    private static void AddBuiltIn(
        List<AdvancedPatternRule> rules,
        HashSet<string> builtIn,
        Dictionary<string, bool> selection,
        IReadOnlyList<string> patterns,
        AdvancedPatternGroup group,
        bool defaultEnabled)
    {
        foreach (var pattern in patterns)
        {
            builtIn.Add(pattern);
            if (selection.TryGetValue(pattern, out var enabled) ? enabled : defaultEnabled)
            {
                rules.Add(new AdvancedPatternRule(pattern, group, AdvancedRuleKind.NamePattern, rules.Count));
            }
        }
    }

    /// <summary>ファイル名に一致する規則のうち、優先順の最も高いもの。無ければ null。</summary>
    public AdvancedPatternRule? Match(string fileName)
    {
        AdvancedPatternRule? best = null;
        var extension = Path.GetExtension(fileName);
        if (extension.Length > 1 && _byExtension.TryGetValue(extension, out var byExtension)) best = byExtension;
        foreach (var rule in _wildcards)
        {
            if (best is not null && rule.Order > best.Order) break;
            if (FileCleanupEngine.MatchesCleanupPattern(fileName, rule.Pattern))
            {
                best = rule;
                break;
            }
        }

        return best;
    }

    /// <summary><c>*.ext</c> の形 (ext にワイルドカードなし) なら拡張子 (<c>.ext</c>)。それ以外は null。</summary>
    private static string? ExtensionOf(string pattern) =>
        pattern.Length > 2 && pattern.StartsWith("*.", StringComparison.Ordinal) && pattern.AsSpan(2).IndexOfAny('*', '?') < 0
            ? pattern[1..]
            : null;

    /// <summary>
    /// 一致したファイルの安全度と既定の選択を決める。
    /// <c>.chk</c> は <c>FOUND.nnn\FILEnnnn.CHK</c> だけを安全とし、他は判定不能で未選択にする。
    /// 24 時間以内に更新された・0 バイト・古いというだけの・ユーザー定義のものも既定では選択しない。
    /// </summary>
    public static AdvancedClassification Classify(
        AdvancedPatternRule rule,
        string fileName,
        string? parentDirectoryName,
        long size,
        long lastWriteFileTime,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var recent = lastWriteFileTime > nowUtc.ToUniversalTime().Subtract(RecentWindow).ToFileTimeUtc();

        if (rule.Group == AdvancedPatternGroup.UserDefined) return new(AdvancedVerdict.UserDefined, false, AdvancedHint.UserPattern);
        if (rule.Kind == AdvancedRuleKind.ZeroByte) return new(AdvancedVerdict.Undeterminable, false, AdvancedHint.ZeroByte);
        if (rule.Kind == AdvancedRuleKind.OldFile) return new(AdvancedVerdict.Undeterminable, false, AdvancedHint.OldFile);
        if (rule.Kind == AdvancedRuleKind.NamePattern && IsCheckFile(fileName) && !IsChkdskOutput(fileName, parentDirectoryName))
        {
            return new(AdvancedVerdict.Undeterminable, false, AdvancedHint.CheckpointOutsideFound);
        }

        if (size == 0) return new(AdvancedVerdict.Safe, false, AdvancedHint.ZeroByte);
        return recent
            ? new(AdvancedVerdict.Safe, false, AdvancedHint.RecentlyModified)
            : new(AdvancedVerdict.Safe, true, AdvancedHint.None);
    }

    private static bool IsCheckFile(string fileName) =>
        string.Equals(Path.GetExtension(fileName), ".chk", StringComparison.OrdinalIgnoreCase);

    /// <summary>chkdsk の出力 (<c>FOUND.nnn</c> フォルダーの <c>FILEnnnn.CHK</c>) か。</summary>
    internal static bool IsChkdskOutput(string fileName, string? parentDirectoryName) =>
        parentDirectoryName is { Length: 9 } parent &&
        parent.StartsWith("FOUND.", StringComparison.OrdinalIgnoreCase) && parent.AsSpan(6).IndexOfAnyExceptInRange('0', '9') < 0 &&
        fileName.Length == 12 &&
        fileName.StartsWith("FILE", StringComparison.OrdinalIgnoreCase) && fileName.AsSpan(4, 4).IndexOfAnyExceptInRange('0', '9') < 0 &&
        fileName.EndsWith(".CHK", StringComparison.OrdinalIgnoreCase);

    /// <summary>特別な規則 (0 バイト・無効なショートカット・古いファイル) の規則オブジェクト。</summary>
    internal static AdvancedPatternRule Special(AdvancedRuleKind kind) => new(string.Empty, AdvancedPatternGroup.Additional, kind, int.MaxValue);
}
