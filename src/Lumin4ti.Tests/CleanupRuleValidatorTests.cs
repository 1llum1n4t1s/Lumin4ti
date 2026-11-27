using Lumin4ti.Core.Services.Windows.Cleanup;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class CleanupRuleValidatorTests
{
    private static readonly CleanupEvidence EvidenceA = new()
    {
        Tier = CleanupEvidenceTier.A,
        Url = "https://learn.microsoft.com/en-us/windows/win32/wer/collecting-user-mode-dumps",
        Checked = "2026-11-25",
    };

    private static CleanupEvidence B(string url) => new() { Tier = CleanupEvidenceTier.B, Url = url, Checked = "2026-11-25" };

    private static CleanupRuleTarget Contents(string path, string? pattern = null) =>
        new() { Type = CleanupTargetType.Contents, Path = path, Pattern = pattern };

    private static CleanupRule Rule(params CleanupRuleTarget[] targets) => new()
    {
        Id = "app.example",
        Name = "Example",
        Kind = CleanupKind.Cache,
        Category = CleanupCategory.Apps,
        Impact = CleanupImpact.Junk,
        Recommended = true,
        Detect = [new() { Path = @"%LOCALAPPDATA%\Example" }],
        Targets = targets.Length == 0 ? [Contents(@"%LOCALAPPDATA%\Example\Cache")] : [.. targets],
        Evidence = [EvidenceA],
    };

    private static void AssertRejected(CleanupRule rule, string code)
    {
        var codes = CleanupRuleValidator.Validate(rule).Select(v => v.Code).ToList();
        CollectionAssert.Contains(codes, code, $"期待した違反 {code} が出ませんでした (実際: {string.Join(",", codes)})");
        Assert.AreEqual(0, CleanupRuleValidator.ValidateAll([rule]).Valid.Count);
    }

    [TestMethod]
    public void 埋め込みサンプル定義が読め違反がない()
    {
        var result = CleanupRuleCatalog.LoadEmbeddedCore(typeof(CleanupRuleCatalog).Assembly);

        Assert.AreEqual(0, result.FileErrors.Count, string.Join(";", result.FileErrors.Select(e => e.Message)));
        Assert.AreEqual(0, result.Violations.Count, string.Join(";", result.Violations.Select(v => v.Message)));
        CollectionAssert.IsSubsetOf(new[] { "windows.crash-dumps", "windows.dns-cache" }, result.Rules.Select(r => r.Id).ToList());
    }

    [TestMethod]
    public void 正常な規則は通る()
    {
        Assert.AreEqual(0, CleanupRuleValidator.Validate(Rule()).Count);
    }

    [TestMethod]
    [DataRow(@"%LOCALAPPDATA%\Example\Login Data")]
    [DataRow(@"%LOCALAPPDATA%\Example\Local State")]
    [DataRow(@"%USERPROFILE%\.ssh")]
    [DataRow(@"%USERPROFILE%\.gnupg\x")]
    [DataRow(@"%USERPROFILE%\.aws")]
    [DataRow(@"%APPDATA%\Microsoft\Credentials")]
    [DataRow(@"%APPDATA%\Microsoft\Vault")]
    [DataRow(@"%APPDATA%\Microsoft\Protect")]
    [DataRow(@"%APPDATA%\Microsoft\Crypto")]
    [DataRow(@"%LOCALAPPDATA%\Example\Storage2")]
    [DataRow(@"%APPDATA%\Telegram\tdata")]
    [DataRow(@"%LOCALAPPDATA%\Example\Web Data")]
    [DataRow(@"%LOCALAPPDATA%\Example\Login*")]
    public void 認証情報の拒否表に当たるパスを落とす(string path)
    {
        AssertRejected(Rule(Contents(path)), "credential-path");
    }

    [TestMethod]
    [DataRow("logins.json")]
    [DataRow("key4.db")]
    [DataRow("*.kdbx")]
    [DataRow("Login Data*")]
    [DataRow("*.db")]
    public void 認証情報に当たるファイルパターンを落とす(string pattern)
    {
        var files = new CleanupRuleTarget { Type = CleanupTargetType.Files, Path = @"%LOCALAPPDATA%\Example", Pattern = pattern };

        AssertRejected(Rule(files), "credential-path");
    }

    [TestMethod]
    [DataRow(@"%LOCALAPPDATA%\Microsoft", "path-protected")]
    [DataRow(@"%USERPROFILE%\Downloads", "path-protected")]
    [DataRow(@"%SystemRoot%\System32", "path-resolve")]
    [DataRow(@"%SystemRoot%\WinSxS", "path-protected")]
    [DataRow(@"%USERPROFILE%", "path-env")]
    [DataRow(@"%LOCALAPPDATA%\", "path-traversal")]
    [DataRow(@"%APPDATA%\..\Example", "path-traversal")]
    [DataRow(@"C:\Temp\Example", "path-relative")]
    [DataRow(@"Example\Cache", "path-relative")]
    [DataRow(@"%UNDEFINED_VAR_FOR_TEST%\Example", "path-env")]
    [DataRow(@"%SystemDrive%\Example", "path-env")]
    [DataRow(@"%LOCALAPPDATA%\Example:stream", "path-env")]
    [DataRow(@"%LOCALAPPDATA%\Lumin4ti\logs", "path-self")]
    public void 基点と保護パスと形の違反を落とす(string path, string code)
    {
        AssertRejected(Rule(Contents(path)), code);
    }

    [TestMethod]
    public void 基点フォルダー直下のファイルパターンは許可しワイルドカードだけは拒否する()
    {
        var literal = new CleanupRuleTarget { Type = CleanupTargetType.Files, Path = @"%LOCALAPPDATA%", Pattern = "IconCache.db" };
        var wild = literal with { Pattern = "*.*" };

        Assert.AreEqual(0, CleanupRuleValidator.Validate(Rule(literal)).Count);
        AssertRejected(Rule(wild), "pattern-invalid");
    }

    [TestMethod]
    public void 配置スコープが規則と食い違えば落とす()
    {
        AssertRejected(Rule(Contents(@"%PROGRAMDATA%\Example\Cache")), "scope-mismatch");
    }

    [TestMethod]
    [DataRow(@"HKLM\Software\Vendor\App", "registry-hive")]
    [DataRow(@"HKCU\Software\Vendor", "registry-range")]
    [DataRow(@"HKCU\Environment\Vendor\App", "registry-range")]
    [DataRow(@"HKCU\Software\Classes\App", "registry-range")]
    [DataRow(@"HKCU\Software\Vendor\Run", "registry-range")]
    [DataRow(@"HKCU\Software\Vendor\*", "registry-range")]
    [DataRow(@"HKCU\Software\Vendor\Credentials", "credential-path")]
    public void レジストリの範囲違反を落とす(string key, string code)
    {
        var target = new CleanupRuleTarget { Type = CleanupTargetType.RegistryTree, Key = key };

        AssertRejected(Rule(target), code);
    }

    [TestMethod]
    public void レジストリの値名は末尾ワイルドカードだけを許す()
    {
        var ok = new CleanupRuleTarget { Type = CleanupTargetType.RegistryValues, Key = @"HKCU\Software\Vendor\App", Values = ["Recent*", "Last"] };
        var all = ok with { Values = ["*"] };
        var middle = ok with { Values = ["A*B"] };

        Assert.AreEqual(0, CleanupRuleValidator.Validate(Rule(ok)).Count);
        AssertRejected(Rule(all), "registry-range");
        AssertRejected(Rule(middle), "registry-range");
    }

    [TestMethod]
    public void 規則の影響と分類の組み合わせ違反を落とす()
    {
        AssertRejected(Rule() with { Impact = CleanupImpact.Trace }, "recommended-impact");
        AssertRejected(Rule() with { Impact = CleanupImpact.UserFiles }, "recommended-impact");
        AssertRejected(Rule() with { Impact = CleanupImpact.Signout, Recommended = false }, "signout-category");
        AssertRejected(Rule() with { Category = CleanupCategory.Custom }, "category-custom");

        var cookies = new CleanupRuleTarget
        {
            Type = CleanupTargetType.Sqlite,
            Path = @"%LOCALAPPDATA%\Example\Cookies",
            Operation = CleanupSqliteOperation.ChromiumCookies,
        };
        AssertRejected(Rule(cookies) with { Category = CleanupCategory.Cookies, Impact = CleanupImpact.Trace, Recommended = false }, "cookie-impact");

        var good = Rule(cookies) with { Category = CleanupCategory.Cookies, Impact = CleanupImpact.Signout, Recommended = false };
        Assert.AreEqual(0, CleanupRuleValidator.Validate(good).Count);
    }

    [TestMethod]
    [DataRow("user_password", "table-denied")]
    [DataRow("AuthTokens", "table-denied")]
    [DataRow("saved_cards", "table-denied")]
    [DataRow("account", "table-denied")]
    [DataRow("sessions", "table-denied")]
    [DataRow("api_keys", "table-denied")]
    [DataRow("a;drop", "table-invalid")]
    [DataRow("sqlite_master", "table-invalid")]
    [DataRow("1table", "table-invalid")]
    public void clearTableの表名違反を落とす(string table, string code)
    {
        var target = new CleanupRuleTarget
        {
            Type = CleanupTargetType.Sqlite,
            Path = @"%LOCALAPPDATA%\Example\app.db",
            Operation = CleanupSqliteOperation.ClearTable,
            Table = table,
            Columns = ["id"],
        };

        AssertRejected(Rule(target), code);
    }

    [TestMethod]
    public void clearTableは表名と列名があれば通りSQLに当たる欄は持てない()
    {
        var ok = new CleanupRuleTarget
        {
            Type = CleanupTargetType.Sqlite,
            Path = @"%LOCALAPPDATA%\Example\app.db",
            Operation = CleanupSqliteOperation.ClearTable,
            Table = "recent_items",
            Columns = ["id", "name"],
        };
        var noColumns = ok with { Columns = null };
        var tableOnFixed = ok with { Operation = CleanupSqliteOperation.GeckoPlaces, Path = @"%APPDATA%\Example\places.sqlite" };

        Assert.AreEqual(0, CleanupRuleValidator.Validate(Rule(ok)).Count);
        AssertRejected(Rule(noColumns), "target-invalid");
        AssertRejected(Rule(tableOnFixed with { Table = "x" }), "target-invalid");
    }

    [TestMethod]
    public void 固定操作の対象ファイル名が違えば落としWebDataは固定操作だけが触れる()
    {
        var wrongFile = new CleanupRuleTarget
        {
            Type = CleanupTargetType.Sqlite,
            Path = @"%LOCALAPPDATA%\Example\other.db",
            Operation = CleanupSqliteOperation.GeckoPlaces,
        };
        var webData = wrongFile with { Path = @"%LOCALAPPDATA%\Example\Web Data", Operation = CleanupSqliteOperation.ChromiumWebData };
        var webDataClear = webData with { Operation = CleanupSqliteOperation.ClearTable, Table = "autofill", Columns = ["name"] };

        AssertRejected(Rule(wrongFile), "sqlite-file");
        Assert.AreEqual(0, CleanupRuleValidator.Validate(Rule(webData)).Count);
        AssertRejected(Rule(webDataClear), "credential-path");
    }

    [TestMethod]
    public void 種別に合わない欄と必須欄の欠落を落とす()
    {
        var handlerWithPath = new CleanupRuleTarget { Type = CleanupTargetType.Handler, Handler = CleanupHandlerKind.DnsCache, Path = @"%LOCALAPPDATA%\Example" };
        var sqliteNoOperation = new CleanupRuleTarget { Type = CleanupTargetType.Sqlite, Path = @"%LOCALAPPDATA%\Example\Cookies" };
        var filesNoPattern = new CleanupRuleTarget { Type = CleanupTargetType.Files, Path = @"%LOCALAPPDATA%\Example" };
        var badDays = Contents(@"%LOCALAPPDATA%\Example\Cache") with { OlderThanDays = 0 };

        AssertRejected(Rule(handlerWithPath), "target-invalid");
        AssertRejected(Rule(sqliteNoOperation), "target-invalid");
        AssertRejected(Rule(filesNoPattern), "target-invalid");
        AssertRejected(Rule(badDays), "target-invalid");
    }

    [TestMethod]
    public void INIの対象は拒否表とワイルドカード位置を検査する()
    {
        var ok = new CleanupRuleTarget
        {
            Type = CleanupTargetType.IniValues,
            Path = @"%APPDATA%\Example\app.ini",
            IniKeys = [new() { Section = "Recent", Key = "File*" }],
        };
        var middle = ok with { IniKeys = [new() { Section = "Recent", Key = "F*e" }] };
        var empty = ok with { IniKeys = [] };

        Assert.AreEqual(0, CleanupRuleValidator.Validate(Rule(ok)).Count);
        AssertRejected(Rule(middle), "target-invalid");
        AssertRejected(Rule(empty), "target-invalid");
    }

    [TestMethod]
    public void サービスとプロセスは許可表と禁止表で検査する()
    {
        Assert.AreEqual(0, CleanupRuleValidator.Validate(Rule() with { Services = ["wuauserv", "BITS"] }).Count);
        AssertRejected(Rule() with { Services = ["TrustedInstaller"] }, "service-denied");
        AssertRejected(Rule() with { Processes = ["explorer"] }, "process-invalid");
        AssertRejected(Rule() with { Processes = ["example.exe"] }, "process-invalid");
    }

    [TestMethod]
    public void detectはどれか1つだけで正しい形を要求する()
    {
        AssertRejected(Rule() with { Detect = [] }, "detect-missing");
        AssertRejected(Rule() with { Detect = [new() { Path = @"%LOCALAPPDATA%\X", Registry = @"HKCU\Software\X" }] }, "detect-invalid");
        AssertRejected(Rule() with { Detect = [new() { Package = "not a pfn!" }] }, "detect-invalid");
        AssertRejected(Rule() with { Detect = [new() { Registry = @"HKCR\X" }] }, "detect-invalid");
        AssertRejected(Rule() with { Detect = [new() { Path = @"%LOCALAPPDATA%\X", ValueName = "v" }] }, "detect-invalid");
        Assert.AreEqual(0, CleanupRuleValidator.Validate(Rule() with { Detect = [new() { Package = "Vendor.App_8wekyb3d8bbwe" }] }).Count);
        Assert.AreEqual(0, CleanupRuleValidator.Validate(Rule() with { Detect = [new() { Registry = @"HKLM\Software\X", ValueName = "v", ValueEquals = "1" }] }).Count);
    }

    [TestMethod]
    public void 根拠はtierAが1件かホストの異なるtierBが2件のときだけ通る()
    {
        var b1 = B("https://example.org/a");
        var b2 = B("https://www.example.net/b");
        var sameHost = B("https://example.org/c");

        AssertRejected(Rule() with { Evidence = [] }, "evidence-missing");
        AssertRejected(Rule() with { Evidence = [b1] }, "evidence-tier");
        AssertRejected(Rule() with { Evidence = [b1, sameHost] }, "evidence-tier");
        Assert.AreEqual(0, CleanupRuleValidator.Validate(Rule() with { Evidence = [b1, b2] }).Count);
    }

    private static readonly CleanupEvidence EvidenceL = new() { Tier = CleanupEvidenceTier.L, Url = string.Empty, Checked = "2026-11-26" };

    private static CleanupRule LegacyRule(params CleanupRuleTarget[] targets) =>
        Rule(targets) with { Id = "legacy.example", Legacy = ["cleanup-app-cache|Example"], Evidence = [EvidenceL] };

    [TestMethod]
    public void tierLはlegacy始まりのidでlegacyを持つ規則の単独の出典だけに許す()
    {
        Assert.AreEqual(0, CleanupRuleValidator.Validate(LegacyRule()).Count);

        AssertRejected(LegacyRule() with { Id = "app.example" }, "evidence-legacy");
        AssertRejected(LegacyRule() with { Legacy = [] }, "evidence-legacy");
        AssertRejected(LegacyRule() with { Legacy = [" "] }, "evidence-legacy");
        AssertRejected(LegacyRule() with { Evidence = [EvidenceL with { Url = "https://learn.microsoft.com/x" }] }, "evidence-legacy");
        AssertRejected(LegacyRule() with { Evidence = [EvidenceL, EvidenceA] }, "evidence-legacy");
        AssertRejected(LegacyRule() with { Evidence = [EvidenceL, EvidenceL] }, "evidence-legacy");
        AssertRejected(LegacyRule() with { Evidence = [EvidenceL with { Checked = "2026/11/26" }] }, "evidence-invalid");
        AssertRejected(Rule() with { Evidence = [EvidenceL] }, "evidence-legacy");
    }

    [TestMethod]
    public void tierLでも拒否表と基点と推奨の規則は緩めない()
    {
        AssertRejected(LegacyRule(Contents(@"%LOCALAPPDATA%\Example\Login Data")), "credential-path");
        AssertRejected(LegacyRule(Contents(@"%USERPROFILE%\.ssh")), "credential-path");
        AssertRejected(LegacyRule(Contents(@"%LOCALAPPDATA%")), "path-env");
        AssertRejected(LegacyRule(Contents(@"%LOCALAPPDATA%\Example\..\Cache")), "path-traversal");
        AssertRejected(LegacyRule() with { Impact = CleanupImpact.Rebuild, Recommended = true }, "recommended-impact");
    }

    [TestMethod]
    [DataRow("http://learn.microsoft.com/x", "2026-11-25", "evidence-invalid")]
    [DataRow("not a url", "2026-11-25", "evidence-invalid")]
    [DataRow("https://learn.microsoft.com/x", "2026/11/25", "evidence-invalid")]
    [DataRow("https://raw.githubusercontent.com/MoscaDotTo/Winapp2/master/Winapp2.ini", "2026-11-25", "evidence-source-banned")]
    [DataRow("https://docs.bleachbit.org/x", "2026-11-25", "evidence-source-banned")]
    [DataRow("https://www.wisecleaner.com/help", "2026-11-25", "evidence-source-banned")]
    public void 根拠の形式と禁止出典を落とす(string url, string checkedOn, string code)
    {
        var evidence = EvidenceA with { Url = url, Checked = checkedOn };

        AssertRejected(Rule() with { Evidence = [EvidenceA, evidence] }, code);
    }

    [TestMethod]
    public void id重複は該当する全規則を落とし対象の完全重複は後の規則だけを落とす()
    {
        var a = Rule();
        var sameId = Rule(Contents(@"%LOCALAPPDATA%\Other\Cache"));
        var duplicateIds = CleanupRuleValidator.ValidateAll([a, sameId]);

        Assert.AreEqual(0, duplicateIds.Valid.Count);
        Assert.IsTrue(duplicateIds.Violations.All(v => v.Code == "duplicate-id"));

        var copy = a with { Id = "app.example-copy", Targets = [Contents(@"%localappdata%\EXAMPLE\cache")] };
        var duplicateTargets = CleanupRuleValidator.ValidateAll([a, copy]);

        Assert.AreEqual("app.example", duplicateTargets.Valid.Single().Id);
        Assert.AreEqual("duplicate-targets", duplicateTargets.Violations.Single().Code);
    }

    private static string FileJson(string ruleBody, int schema = 1) => $$"""{"schema":{{schema}},"rules":[{{ruleBody}}]}""";

    private const string GoodRuleJson = """
        {"id":"app.good","name":"Good","kind":"cache","category":"apps","scope":"user","recommended":true,"impact":"junk",
         "detect":[{"path":"%LOCALAPPDATA%\\Good"}],
         "targets":[{"type":"contents","path":"%LOCALAPPDATA%\\Good\\Cache"}],
         "evidence":[{"tier":"A","url":"https://learn.microsoft.com/","checked":"2026-11-25"}]}
        """;

    private const string BadRuleJson = """
        {"id":"app.bad","name":"Bad","kind":"cache","category":"apps","scope":"user","recommended":true,"impact":"junk",
         "detect":[{"path":"%LOCALAPPDATA%\\Bad"}],
         "targets":[{"type":"contents","path":"%LOCALAPPDATA%\\Bad\\Login Data"}],
         "evidence":[{"tier":"A","url":"https://learn.microsoft.com/","checked":"2026-11-25"}]}
        """;

    [TestMethod]
    public void カタログは違反した規則だけを無効にして残りを使う()
    {
        var result = CleanupRuleCatalog.LoadFrom([("t.json", FileJson($"{GoodRuleJson},{BadRuleJson}"))]);

        Assert.AreEqual("app.good", result.Rules.Single().Id);
        Assert.AreEqual("app.bad", result.Violations.Single().RuleId);
        Assert.AreEqual(0, result.FileErrors.Count);
    }

    [TestMethod]
    public void カタログは未知キー_未知の列挙値_未対応schemaのファイルを丸ごと読まない()
    {
        var unknownKey = FileJson(GoodRuleJson.Replace("\"name\":\"Good\"", "\"name\":\"Good\",\"sql\":\"DELETE FROM x\""));
        var unknownTargetKey = FileJson(GoodRuleJson.Replace("\"type\":\"contents\"", "\"type\":\"contents\",\"command\":\"del\""));
        var unknownEnum = FileJson(GoodRuleJson.Replace("\"impact\":\"junk\"", "\"impact\":\"dangerous\""));
        var intEnum = FileJson(GoodRuleJson.Replace("\"impact\":\"junk\"", "\"impact\":0"));
        var wrongSchema = FileJson(GoodRuleJson, schema: 2);
        var broken = "{\"schema\":1,\"rules\":[";

        var result = CleanupRuleCatalog.LoadFrom(
        [
            ("a.json", unknownKey),
            ("b.json", unknownTargetKey),
            ("c.json", unknownEnum),
            ("d.json", intEnum),
            ("e.json", wrongSchema),
            ("f.json", broken),
            ("ok.json", FileJson(GoodRuleJson)),
        ]);

        Assert.AreEqual(6, result.FileErrors.Count);
        Assert.AreEqual("app.good", result.Rules.Single().Id);
    }

    [TestMethod]
    public void カタログはファイルをまたいだid重複も落とす()
    {
        var result = CleanupRuleCatalog.LoadFrom([("a.json", FileJson(GoodRuleJson)), ("b.json", FileJson(GoodRuleJson))]);

        Assert.AreEqual(0, result.Rules.Count);
        Assert.AreEqual(2, result.Violations.Count(v => v.Code == "duplicate-id"));
    }
}
