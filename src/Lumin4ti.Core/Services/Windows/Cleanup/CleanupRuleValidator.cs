using System.Globalization;
using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>検証で見つかった違反。<see cref="Code"/> はテストと診断用の固定名。</summary>
internal sealed record CleanupViolation(string RuleId, string Code, string Message);

/// <summary>全規則の検証結果。違反した規則は <see cref="Valid"/> に含まれない。</summary>
internal sealed record CleanupValidationResult(
    IReadOnlyList<CleanupRule> Valid,
    IReadOnlyList<CleanupViolation> Violations);

/// <summary>
/// 規則定義の検証器。テストと起動時の両方で使い、違反した規則だけを無効にする。
/// 規則は削除対象を決めるデータなので、拒否表は「通してよい形」を狭く取り、
/// 迷うものは拒否する (許可を足すのは根拠付きの変更として行う)。
/// </summary>
internal static partial class CleanupRuleValidator
{
    private const int MaxOlderThanDays = 3650;

    private static readonly StringComparer Ignore = StringComparer.OrdinalIgnoreCase;

    /// <summary>基点に使える環境変数と、その配置が属するスコープ。</summary>
    private static readonly Dictionary<string, CleanupScope> AllowedEnvironment = new(Ignore)
    {
        ["LOCALAPPDATA"] = CleanupScope.User,
        ["APPDATA"] = CleanupScope.User,
        ["USERPROFILE"] = CleanupScope.User,
        ["TEMP"] = CleanupScope.User,
        ["TMP"] = CleanupScope.User,
        ["PROGRAMDATA"] = CleanupScope.Machine,
        ["ALLUSERSPROFILE"] = CleanupScope.Machine,
        ["PUBLIC"] = CleanupScope.Machine,
        ["SYSTEMROOT"] = CleanupScope.Machine,
        ["WINDIR"] = CleanupScope.Machine,
        ["PROGRAMFILES"] = CleanupScope.Machine,
        ["PROGRAMFILES(X86)"] = CleanupScope.Machine,
    };

    /// <summary>認証情報・鍵の拒否表 (ワイルドカード可)。パス・パターン・レジストリキーの各要素に当てる。</summary>
    private static readonly string[] CredentialNames =
    [
        "Login Data*", "logins.json", "key3.db", "key4.db", "cert9.db", "Local State",
        ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".netrc",
        "Credentials", "Vault", "Protect", "Crypto", "Storage2",
        "*.kdbx", "tdata", "id_rsa*", "id_ed25519*", "id_ecdsa*", "*.pfx", "*.p12", "*.pem", "*.key",
    ];

    /// <summary>固定操作 chromiumWebData 以外では拒否する名前 (カード・住所を持つ)。</summary>
    private static readonly string[] SecondaryDeniedNames = ["Web Data"];

    private static readonly string[] RegistryDeniedSegments = ["Classes", "Policies"];

    private static readonly string[] RegistryDeniedLeaves = ["Run", "RunOnce"];

    private static readonly string[] DeniedTableWords =
        ["login", "password", "token", "credential", "account", "card", "key", "session"];

    private static readonly HashSet<string> AllowedServices = new(Ignore)
    {
        "DoSvc", "wuauserv", "bits", "FontCache", "FontCache3.0.0.0", "SysMain", "WSearch", "msiserver", "DPS",
    };

    private static readonly HashSet<string> ProtectedProcesses = new(Ignore)
    {
        "explorer", "csrss", "winlogon", "svchost", "lsass", "services", "system", "smss", "wininit", "dwm",
        "Lumin4ti.UI", "Lumin4ti",
    };

    /// <summary>出典として数えない (ライセンス継承・本人決定により値の転記元にしない) URL の断片。</summary>
    private static readonly string[] BannedEvidenceFragments = ["winapp2", "bleachbit", "wisecleaner", "moscadotto"];

    private static readonly Dictionary<CleanupSqliteOperation, string> SqliteFileNames = new()
    {
        [CleanupSqliteOperation.ChromiumCookies] = "Cookies",
        [CleanupSqliteOperation.ChromiumWebData] = "Web Data",
        [CleanupSqliteOperation.GeckoCookies] = "cookies.sqlite",
        [CleanupSqliteOperation.GeckoFormHistory] = "formhistory.sqlite",
        [CleanupSqliteOperation.GeckoPlaces] = "places.sqlite",
    };

    private static readonly Lazy<HashSet<string>> ProtectedFolders = new(BuildProtectedFolders);

    [GeneratedRegex(@"^[a-z][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*)+$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-]*(_[a-z0-9]{13})?$")]
    private static partial Regex PackagePattern();

    /// <summary>全規則を検証する。id 重複は該当する全規則を、対象の完全重複は後の規則を無効にする。</summary>
    public static CleanupValidationResult ValidateAll(IEnumerable<CleanupRule> rules)
    {
        var list = rules.ToList();
        var violations = new List<CleanupViolation>();
        var duplicated = list.GroupBy(r => r.Id, Ignore).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(Ignore);

        var valid = new List<CleanupRule>();
        var signatures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rule in list)
        {
            var found = Validate(rule);
            if (duplicated.Contains(rule.Id))
            {
                found.Add(new(rule.Id, "duplicate-id", "id が他の規則と重複しています"));
            }

            if (found.Count == 0 && signatures.TryGetValue(Signature(rule), out var first))
            {
                found.Add(new(rule.Id, "duplicate-targets", $"対象が規則 {first} と完全に重複しています"));
            }

            if (found.Count == 0)
            {
                signatures[Signature(rule)] = rule.Id;
                valid.Add(rule);
            }

            violations.AddRange(found);
        }

        return new(valid, violations);
    }

    /// <summary>1規則だけを検証する (規則間の重複は <see cref="ValidateAll"/> が見る)。</summary>
    public static List<CleanupViolation> Validate(CleanupRule rule)
    {
        var v = new Collector(rule.Id ?? string.Empty);
        CheckIdentity(rule, v);
        CheckPolicy(rule, v);
        CheckDetect(rule.Detect, v);
        CheckTargets(rule, v);
        CheckLists(rule, v);
        CheckEvidence(rule, v);
        return v.Items;
    }

    private sealed class Collector(string ruleId)
    {
        public List<CleanupViolation> Items { get; } = [];

        public void Add(string code, string message) => Items.Add(new(ruleId, code, message));

        public void Require(bool ok, string code, string message)
        {
            if (!ok)
            {
                Add(code, message);
            }
        }
    }

    private static void CheckIdentity(CleanupRule rule, Collector v)
    {
        v.Require(!string.IsNullOrEmpty(rule.Id) && IdPattern().IsMatch(rule.Id), "id-invalid", "id は小文字英数とハイフンをドットで区切った形にしてください");
        v.Require(!string.IsNullOrWhiteSpace(rule.Name), "name-missing", "name が空です");
        v.Require(rule.Category != CleanupCategory.Custom, "category-custom", "custom は利用者のカスタムフォルダー専用です");
    }

    private static void CheckPolicy(CleanupRule rule, Collector v)
    {
        v.Require(!rule.Recommended || rule.Impact == CleanupImpact.Junk, "recommended-impact", "junk 以外は recommended にできません");
        v.Require(
            rule.Impact != CleanupImpact.Signout || rule.Category == CleanupCategory.Cookies,
            "signout-category",
            "signout は cookies 分類だけに許可されます");
        var cookieOps = (rule.Targets ?? []).Any(t => t.Operation is CleanupSqliteOperation.ChromiumCookies or CleanupSqliteOperation.GeckoCookies);
        v.Require(!cookieOps || rule.Impact == CleanupImpact.Signout, "cookie-impact", "Cookie の操作は impact を signout にしてください");
    }

    private static void CheckDetect(List<CleanupDetect> detect, Collector v)
    {
        if (detect is null || detect.Count == 0)
        {
            v.Add("detect-missing", "detect が空です");
            return;
        }

        foreach (var d in detect)
        {
            var kinds = (d.Path is null ? 0 : 1) + (d.Registry is null ? 0 : 1) + (d.Package is null ? 0 : 1);
            if (kinds != 1)
            {
                v.Add("detect-invalid", "detect は path・registry・package のどれか1つだけを指定します");
                continue;
            }

            var problem = DetectProblem(d);
            if (problem is not null)
            {
                v.Add("detect-invalid", problem);
            }
        }
    }

    private static string? DetectProblem(CleanupDetect d)
    {
        if ((d.ValueName is not null || d.ValueEquals is not null) && d.Registry is null)
        {
            return "valueName・valueEquals は registry とだけ使えます";
        }

        if (d.ValueEquals is not null && d.ValueName is null)
        {
            return "valueEquals には valueName が要ります";
        }

        if (d.Package is not null)
        {
            return PackagePattern().IsMatch(d.Package) ? null : "package が PFN の形ではありません";
        }

        if (d.Registry is not null)
        {
            var parts = d.Registry.Split('\\');
            var hive = parts[0].Equals("HKCU", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("HKLM", StringComparison.OrdinalIgnoreCase);
            return hive && parts.Length >= 2 && parts.All(p => p.Length > 0 && p != ".." && p != ".")
                ? null
                : "registry は HKCU または HKLM で始まる正規のキーにしてください";
        }

        return ResolvePath(d.Path!, allowProtected: true, out _, out _) is { } code ? $"detect の path が不正です ({code})" : null;
    }

    private static void CheckTargets(CleanupRule rule, Collector v)
    {
        if (rule.Targets is null || rule.Targets.Count == 0)
        {
            v.Add("target-missing", "targets が空です");
            return;
        }

        foreach (var t in rule.Targets)
        {
            CheckFieldShape(t, v);
            CheckTarget(t, rule, v);
        }
    }

    private static void CheckTarget(CleanupRuleTarget t, CleanupRule rule, Collector v)
    {
        switch (t.Type)
        {
            case CleanupTargetType.Contents or CleanupTargetType.Files:
                CheckFilePath(t, rule, v);
                break;
            case CleanupTargetType.RegistryValues or CleanupTargetType.RegistryTree:
                CheckRegistry(t, rule, v);
                break;
            case CleanupTargetType.Sqlite:
                CheckSqlite(t, rule, v);
                break;
            case CleanupTargetType.IniValues:
                CheckIni(t, rule, v);
                break;
        }
    }

    private static readonly Dictionary<CleanupTargetType, string[]> RequiredFields = new()
    {
        [CleanupTargetType.Contents] = ["Path"],
        [CleanupTargetType.Files] = ["Path", "Pattern"],
        [CleanupTargetType.RegistryValues] = ["Key", "Values"],
        [CleanupTargetType.RegistryTree] = ["Key"],
        [CleanupTargetType.Sqlite] = ["Path", "Operation"],
        [CleanupTargetType.IniValues] = ["Path", "IniKeys"],
        [CleanupTargetType.Handler] = ["Handler"],
    };

    private static readonly Dictionary<CleanupTargetType, string[]> OptionalFields = new()
    {
        // contents は常に下位まで消すため、recurse・removeEmptyDirs は指定できない (files だけが recurse を持つ)。
        [CleanupTargetType.Contents] = ["Pattern", "OlderThanDays"],
        [CleanupTargetType.Files] = ["Recurse", "OlderThanDays"],
        [CleanupTargetType.RegistryValues] = [],
        [CleanupTargetType.RegistryTree] = [],
        [CleanupTargetType.Sqlite] = [],
        [CleanupTargetType.IniValues] = [],
        [CleanupTargetType.Handler] = [],
    };

    /// <summary>種別ごとに、必須の欄が揃い、無関係な欄 (SQL・コマンド相当の混入を含む) が無いことを確かめる。</summary>
    private static void CheckFieldShape(CleanupRuleTarget t, Collector v)
    {
        var set = SetFields(t);
        var required = RequiredFields[t.Type].ToList();
        if (t.Operation == CleanupSqliteOperation.ClearTable)
        {
            required.AddRange(["Table", "Columns"]);
        }

        foreach (var name in required.Where(n => !set.Contains(n)))
        {
            v.Add("target-invalid", $"{t.Type} には {name} が必要です");
        }

        var allowed = required.Concat(OptionalFields[t.Type]).ToHashSet();
        foreach (var name in set.Where(n => !allowed.Contains(n)))
        {
            v.Add("target-invalid", $"{t.Type} では {name} を指定できません");
        }

        if (t.OlderThanDays is { } days && (days < 1 || days > MaxOlderThanDays))
        {
            v.Add("target-invalid", "olderThanDays は 1 以上 3650 以下にしてください");
        }
    }

    private static HashSet<string> SetFields(CleanupRuleTarget t)
    {
        var set = new HashSet<string>();
        AddIf(set, t.Path is not null, "Path");
        AddIf(set, t.Pattern is not null, "Pattern");
        AddIf(set, t.Recurse, "Recurse");
        AddIf(set, t.OlderThanDays is not null, "OlderThanDays");
        AddIf(set, t.RemoveEmptyDirs, "RemoveEmptyDirs");
        AddIf(set, t.Key is not null, "Key");
        AddIf(set, t.Values is not null, "Values");
        AddIf(set, t.Operation is not null, "Operation");
        AddIf(set, t.Table is not null, "Table");
        AddIf(set, t.Columns is not null, "Columns");
        AddIf(set, t.IniKeys is not null, "IniKeys");
        AddIf(set, t.Handler is not null, "Handler");
        return set;
    }

    private static void AddIf(HashSet<string> set, bool condition, string name)
    {
        if (condition)
        {
            set.Add(name);
        }
    }

    private static void CheckFilePath(CleanupRuleTarget t, CleanupRule rule, Collector v)
    {
        var isFiles = t.Type == CleanupTargetType.Files;
        if (t.Path is null || !CheckPath(t.Path, allowProtected: isFiles, rule, v, out var full))
        {
            return;
        }

        CheckPathPolicy(t.Path, secondaryDenied: true, v);
        if (t.Type == CleanupTargetType.Contents && ProtectedFolders.Value.Contains(full))
        {
            v.Add("path-protected", $"{t.Path} は丸ごと対象にできない基点フォルダーです");
        }

        if (t.Pattern is not null)
        {
            CheckPattern(t.Pattern, isFiles, v);
        }
    }

    private static void CheckPattern(string pattern, bool requireLiteral, Collector v)
    {
        var bad = pattern.Length == 0 || pattern.AsSpan().IndexOfAny('\\', '/', ':') >= 0 || pattern.Contains("..", StringComparison.Ordinal);
        v.Require(!bad, "pattern-invalid", "pattern に区切り文字・.. は使えません");
        v.Require(!requireLiteral || pattern.Any(c => c is not ('*' or '?' or '.')), "pattern-invalid", "files の pattern にはワイルドカード以外の文字が要ります");
        v.Require(!IsCredentialName(pattern, SecondaryDeniedNames), "credential-path", $"pattern {pattern} は拒否表に当たります");
        v.Require(!IsCredentialName(pattern, CredentialNames), "credential-path", $"pattern {pattern} は認証情報の拒否表に当たります");
    }

    private static void CheckPathPolicy(string raw, bool secondaryDenied, Collector v)
    {
        foreach (var segment in raw.Split('\\').Skip(1))
        {
            v.Require(!IsCredentialName(segment, CredentialNames), "credential-path", $"{segment} は認証情報の拒否表に当たります");
            v.Require(!(secondaryDenied && IsCredentialName(segment, SecondaryDeniedNames)), "credential-path", $"{segment} は拒否表に当たります");
            v.Require(!segment.Equals("Lumin4ti", StringComparison.OrdinalIgnoreCase), "path-self", "Lumin4ti 自身の領域は対象にできません");
        }
    }

    private static bool CheckPath(string raw, bool allowProtected, CleanupRule rule, Collector v, out string full)
    {
        full = string.Empty;
        if (ResolvePath(raw, allowProtected, out full, out var scope) is { } code)
        {
            v.Add(code, $"パス {raw} が不正です");
            return false;
        }

        v.Require(scope == rule.Scope, "scope-mismatch", $"{raw} は scope {scope} の場所です");
        return true;
    }

    /// <summary>パスの形と基点を検査して解決する。問題なければ null、あれば違反コードを返す。</summary>
    internal static string? ResolvePath(string raw, bool allowProtected, out string full, out CleanupScope scope)
    {
        full = string.Empty;
        scope = CleanupScope.User;
        if (string.IsNullOrWhiteSpace(raw) || raw[0] != '%')
        {
            return "path-relative";
        }

        var close = raw.IndexOf('%', 1);
        var baseOnly = close >= 2 && close == raw.Length - 1;
        if (close < 2 || (!baseOnly && raw[close + 1] != '\\') || (baseOnly && !allowProtected))
        {
            return "path-env";
        }

        if (!AllowedEnvironment.TryGetValue(raw[1..close], out scope))
        {
            return "path-env";
        }

        if (baseOnly)
        {
            return FileCleanupEngine.TryResolve(raw, true, out full, out _) ? null : "path-resolve";
        }

        var rest = raw[(close + 2)..];
        if (rest.Contains('%') || rest.AsSpan().IndexOfAny(':', '/') >= 0)
        {
            return "path-env";
        }

        if (rest.Length == 0 || rest.Split('\\').Any(s => s.Length == 0 || s is "." or ".."))
        {
            return "path-traversal";
        }

        var probe = $"{raw[..(close + 1)]}\\{rest.Replace('*', '_').Replace('?', '_')}";
        if (!FileCleanupEngine.TryResolve(probe, allowProtected, out full, out _))
        {
            return "path-resolve";
        }

        return null;
    }

    private static void CheckRegistry(CleanupRuleTarget t, CleanupRule rule, Collector v)
    {
        v.Require(rule.Scope == CleanupScope.User, "scope-mismatch", "HKCU のレジストリは scope user の規則だけです");
        if (t.Key is null)
        {
            return;
        }

        var parts = t.Key.Split('\\');
        if (!parts[0].Equals("HKCU", StringComparison.OrdinalIgnoreCase))
        {
            v.Add("registry-hive", "レジストリ対象は HKCU だけです (HKLM は handler だけ)");
            return;
        }

        var shapeOk = parts.Length >= 4 && parts[1].Equals("Software", StringComparison.OrdinalIgnoreCase)
            && parts.All(p => p.Length > 0 && p is not ("." or "..") && p.AsSpan().IndexOfAny('*', '?') < 0);
        v.Require(shapeOk, "registry-range", "キーは HKCU\\Software\\ の下の2階層以上にしてください");
        v.Require(!parts.Skip(2).Any(p => RegistryDeniedSegments.Contains(p, Ignore)), "registry-range", "Classes・Policies は対象にできません");
        v.Require(!RegistryDeniedLeaves.Contains(parts[^1], Ignore), "registry-range", "Run・RunOnce は対象にできません");
        foreach (var p in parts.Skip(1).Concat(t.Values ?? []))
        {
            v.Require(!IsCredentialName(p, CredentialNames), "credential-path", $"{p} は認証情報の拒否表に当たります");
        }

        CheckValueNames(t, v);
    }

    private static void CheckValueNames(CleanupRuleTarget t, Collector v)
    {
        foreach (var name in t.Values ?? [])
        {
            var star = name.IndexOf('*');
            var bad = name.Length == 0 || name == "*" || name.Contains('\\') || (star >= 0 && star != name.Length - 1);
            v.Require(!bad, "registry-range", $"値名 {name} が不正です (ワイルドカードは末尾の * だけ、* 単独は registryTree を使う)");
        }
    }

    private static void CheckSqlite(CleanupRuleTarget t, CleanupRule rule, Collector v)
    {
        if (t.Path is null || t.Operation is not { } op || !CheckPath(t.Path, allowProtected: false, rule, v, out _))
        {
            return;
        }

        CheckPathPolicy(t.Path, secondaryDenied: op != CleanupSqliteOperation.ChromiumWebData, v);
        var leaf = t.Path.Split('\\')[^1];
        if (SqliteFileNames.TryGetValue(op, out var expected))
        {
            v.Require(leaf.Equals(expected, StringComparison.OrdinalIgnoreCase), "sqlite-file", $"{op} の対象ファイルは {expected} です");
            return;
        }

        CheckTable(t, v);
    }

    private static void CheckTable(CleanupRuleTarget t, Collector v)
    {
        var table = t.Table ?? string.Empty;
        var valid = IdentifierPattern().IsMatch(table) && !table.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase);
        v.Require(valid, "table-invalid", $"表名 {table} が不正です");
        var word = DeniedTableWords.FirstOrDefault(w => table.Contains(w, StringComparison.OrdinalIgnoreCase));
        v.Require(word is null, "table-denied", $"表名 {table} は {word} を含むため拒否します");
        var columns = t.Columns ?? [];
        v.Require(columns.Count > 0 && columns.All(c => IdentifierPattern().IsMatch(c)), "table-invalid", "columns は1件以上の識別子にしてください");
    }

    private static void CheckIni(CleanupRuleTarget t, CleanupRule rule, Collector v)
    {
        if (t.Path is not null && CheckPath(t.Path, allowProtected: false, rule, v, out _))
        {
            CheckPathPolicy(t.Path, secondaryDenied: true, v);
        }

        var keys = t.IniKeys ?? [];
        v.Require(keys.Count > 0, "target-invalid", "iniKeys が空です");
        foreach (var k in keys)
        {
            var star = k.Key.IndexOf('*');
            var bad = k.Section.Length == 0 || k.Key.Length == 0 || k.Key == "*" || (star >= 0 && star != k.Key.Length - 1);
            v.Require(!bad, "target-invalid", $"iniKeys [{k.Section}] {k.Key} が不正です (ワイルドカードは末尾の * だけ)");
            v.Require(!IsCredentialName(k.Key, CredentialNames), "credential-path", $"{k.Key} は認証情報の拒否表に当たります");
        }
    }

    private static void CheckLists(CleanupRule rule, Collector v)
    {
        foreach (var service in rule.Services ?? [])
        {
            v.Require(AllowedServices.Contains(service), "service-denied", $"サービス {service} は許可表にありません");
        }

        foreach (var process in rule.Processes ?? [])
        {
            var bad = process.Length is 0 or > 64 || process.IndexOfAny(['\\', '/', ':', '*', '?']) >= 0 || process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            v.Require(!bad, "process-invalid", $"プロセス名 {process} は拡張子なしの名前だけです");
            v.Require(!ProtectedProcesses.Contains(process), "process-invalid", $"プロセス {process} は対象にできません");
        }

        v.Require((rule.Legacy ?? []).All(l => !string.IsNullOrWhiteSpace(l)), "legacy-invalid", "legacy に空の要素があります");
    }

    private static void CheckEvidence(CleanupRule rule, Collector v)
    {
        var evidence = rule.Evidence;
        if (evidence is null || evidence.Count == 0)
        {
            v.Add("evidence-missing", "evidence がありません");
            return;
        }

        if (evidence.Any(e => e.Tier == CleanupEvidenceTier.L))
        {
            CheckLegacyEvidence(rule, evidence, v);
            return;
        }

        var tierB = new HashSet<string>(Ignore);
        var hasA = false;
        foreach (var e in evidence)
        {
            if (!TryGetHost(e, v, out var host))
            {
                continue;
            }

            hasA |= e.Tier == CleanupEvidenceTier.A;
            if (e.Tier == CleanupEvidenceTier.B)
            {
                tierB.Add(host);
            }
        }

        v.Require(hasA || tierB.Count >= 2, "evidence-tier", "tier A が1件、または出典ホストの異なる tier B が2件必要です");
    }

    /// <summary>
    /// tier L (現行対象の継承) は、id が <c>legacy.</c> で始まり legacy が空でない規則の、url なしの単独の出典だけに許す。
    /// 拒否表・基点・認証情報の検査は他の規則と同じに掛かる (ここでは緩めない)。
    /// </summary>
    private static void CheckLegacyEvidence(CleanupRule rule, List<CleanupEvidence> evidence, Collector v)
    {
        var allowed = rule.Id is not null && rule.Id.StartsWith("legacy.", StringComparison.Ordinal)
            && (rule.Legacy ?? []).Any(l => !string.IsNullOrWhiteSpace(l));
        v.Require(allowed, "evidence-legacy", "tier L は id が legacy. で始まり legacy が空でない規則だけに使えます");
        var shapeOk = evidence.Count == 1 && evidence.All(e => e.Tier == CleanupEvidenceTier.L && string.IsNullOrEmpty(e.Url));
        v.Require(shapeOk, "evidence-legacy", "tier L は url なしの1件だけにしてください");
        v.Require(
            evidence.All(e => DateOnly.TryParseExact(e.Checked, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)),
            "evidence-invalid",
            "checked は yyyy-MM-dd にしてください");
    }

    private static bool TryGetHost(CleanupEvidence e, Collector v, out string host)
    {
        host = string.Empty;
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0)
        {
            v.Add("evidence-invalid", $"url {e.Url} は https の絶対 URL にしてください");
            return false;
        }

        if (!DateOnly.TryParseExact(e.Checked, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            v.Add("evidence-invalid", $"checked {e.Checked} は yyyy-MM-dd にしてください");
            return false;
        }

        if (BannedEvidenceFragments.Any(f => e.Url.Contains(f, StringComparison.OrdinalIgnoreCase)))
        {
            v.Add("evidence-source-banned", $"{e.Url} は出典に使えません");
            return false;
        }

        host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        return true;
    }

    /// <summary>
    /// 実在するファイル・フォルダーの名前 1 つが、認証情報の拒否表に当たるか。
    /// 走査と削除 (カスタムフォルダー・高度クリーン) が子孫を残すかどうかの判定に共有する。
    /// </summary>
    internal static bool IsCredentialFileName(string name) =>
        CredentialNames.Any(entry => FileSystemName.MatchesSimpleExpression(entry, name, ignoreCase: true));

    /// <summary>基点の環境変数が属するスコープ。許可表に無い変数は false。</summary>
    internal static bool TryGetBaseScope(string variable, out CleanupScope scope) => AllowedEnvironment.TryGetValue(variable, out scope);

    /// <summary>名前 (ワイルドカード可) が拒否表のどれかに当たるか。ワイルドカード側が拒否名を覆う場合も当たりとする。</summary>
    internal static bool IsCredentialName(string name, string[] denied)
    {
        var hasLiteral = name.Any(c => c is not ('*' or '?'));
        var hasWildcard = name.AsSpan().IndexOfAny('*', '?') >= 0;
        foreach (var entry in denied)
        {
            if (FileSystemName.MatchesSimpleExpression(entry, name, ignoreCase: true))
            {
                return true;
            }

            if (hasWildcard && hasLiteral && FileSystemName.MatchesSimpleExpression(name, entry.Replace("*", string.Empty), ignoreCase: true))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> BuildProtectedFolders()
    {
        var result = new HashSet<string>(Ignore);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(profile))
        {
            foreach (var sub in new[]
            {
                "AppData", @"AppData\Local", @"AppData\Roaming", @"AppData\LocalLow", @"AppData\Local\Microsoft", @"AppData\Roaming\Microsoft",
                "Documents", "Downloads", "Pictures", "Videos", "Music", "Desktop",
            })
            {
                result.Add(Path.Combine(profile, sub));
            }
        }

        AddUnder(result, common, "Microsoft");
        AddUnder(result, windows, "System32", "SysWOW64", "WinSxS", "Installer", "SoftwareDistribution");
        AddUnder(result, Environment.GetEnvironmentVariable("PUBLIC") ?? string.Empty);
        return result;
    }

    private static void AddUnder(HashSet<string> set, string root, params string[] subs)
    {
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        if (subs.Length == 0)
        {
            set.Add(Path.TrimEndingDirectorySeparator(root));
        }

        foreach (var sub in subs)
        {
            set.Add(Path.Combine(root, sub));
        }
    }

    /// <summary>規則の対象を、環境変数を展開・小文字化して並べ替えた比較用の文字列にする。</summary>
    private static string Signature(CleanupRule rule)
    {
        var sb = new StringBuilder();
        foreach (var line in rule.Targets.Select(TargetSignature).Order(StringComparer.Ordinal))
        {
            sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }

    private static string TargetSignature(CleanupRuleTarget t)
    {
        var path = t.Path is null ? string.Empty : Environment.ExpandEnvironmentVariables(t.Path);
        var values = string.Join(",", (t.Values ?? []).Order(Ignore));
        var ini = string.Join(",", (t.IniKeys ?? []).Select(k => $"{k.Section}/{k.Key}").Order(Ignore));
        var columns = string.Join(",", (t.Columns ?? []).Order(Ignore));
        return string.Join(
            '|',
            t.Type, path, t.Pattern, t.Recurse, t.OlderThanDays, t.RemoveEmptyDirs, t.Key, values, t.Operation, t.Table, columns, ini, t.Handler)
            .ToLowerInvariant();
    }
}
