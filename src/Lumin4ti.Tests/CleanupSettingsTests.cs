using System.Text;
using System.Text.Json;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows.Cleanup;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class CleanupSettingsTests
{
    private static readonly CustomFolderGuardContext Context = new(
        @"C:\Windows",
        [@"C:\Program Files", @"C:\Program Files (x86)"],
        @"C:\ProgramData",
        [@"C:\Users"],
        @"C:\Users\me");

    private string _directory = null!;
    private string _settingsPath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "Lumin4ti.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _settingsPath = Path.Combine(_directory, "settings.json");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private SettingsService NewService() => new(_directory, _settingsPath);

    private CleanupSettingsStore NewStore(out SettingsService service)
    {
        service = NewService();
        return new CleanupSettingsStore(service);
    }

    private static async Task SaveAsync(SettingsService service)
    {
        await service.SaveAsync();
        await service.FlushAsync();
    }

    // ----- カスタムフォルダーの検査 -----

    [TestMethod]
    [DataRow("", CustomFolderRejection.Invalid)]
    [DataRow(@"relative\dir", CustomFolderRejection.Invalid)]
    [DataRow(@"\\server\share\x", CustomFolderRejection.Invalid)]
    [DataRow(@"\\?\C:\Temp\x", CustomFolderRejection.Invalid)]
    [DataRow(@"C:\Temp\a*", CustomFolderRejection.Invalid)]
    [DataRow(@"C:\ZZQQXX~1\x", CustomFolderRejection.Invalid)]
    [DataRow(@"C:\Temp\x:stream", CustomFolderRejection.Invalid)]
    [DataRow(@"C:\", CustomFolderRejection.DriveRoot)]
    [DataRow(@"D:\", CustomFolderRejection.DriveRoot)]
    [DataRow(@"C:\Windows\Temp", CustomFolderRejection.Windows)]
    [DataRow(@"c:\WINDOWS", CustomFolderRejection.Windows)]
    [DataRow(@"C:\Program Files\App\Cache", CustomFolderRejection.ProgramFiles)]
    [DataRow(@"C:\Program Files (x86)", CustomFolderRejection.ProgramFiles)]
    [DataRow(@"C:\ProgramData\Vendor", CustomFolderRejection.ProgramData)]
    [DataRow(@"C:\Users", CustomFolderRejection.OtherUserProfile)]
    [DataRow(@"C:\Users\other\AppData\Local\Temp", CustomFolderRejection.OtherUserProfile)]
    [DataRow(@"C:\Users\me\..\other\x", CustomFolderRejection.OtherUserProfile)]
    [DataRow(@"C:\Users\me", CustomFolderRejection.ProfileRoot)]
    [DataRow(@"C:\Users\me\AppData", CustomFolderRejection.ProfileRoot)]
    [DataRow(@"C:\Users\me\AppData\Local\", CustomFolderRejection.ProfileRoot)]
    [DataRow(@"C:\Users\me\AppData\Roaming", CustomFolderRejection.ProfileRoot)]
    [DataRow(@"C:\Users\me\.ssh", CustomFolderRejection.Credential)]
    [DataRow(@"c:\users\ME\.SSH\keys", CustomFolderRejection.Credential)]
    [DataRow(@"C:\Users\me\AppData\Roaming\Microsoft\Credentials", CustomFolderRejection.Credential)]
    [DataRow(@"D:\Backup\Vault\old", CustomFolderRejection.Credential)]
    [DataRow(@"C:\Users\me\AppData\Local\Temp", CustomFolderRejection.None)]
    [DataRow(@"C:\Users\me\Downloads\cache\", CustomFolderRejection.None)]
    [DataRow(@"D:\Work\cache", CustomFolderRejection.None)]
    public void カスタムフォルダーの検査は危険な基点を拒否する(string path, CustomFolderRejection expected)
    {
        var check = CustomFolderGuard.Check(path, Context);

        Assert.AreEqual(expected, check.Rejection);
        Assert.AreEqual(expected == CustomFolderRejection.None, check.IsAllowed);
    }

    [TestMethod]
    public void カスタムフォルダーの検査は現在の利用者が不明ならプロファイル配下をすべて拒否する()
    {
        var context = Context with { CurrentProfile = null };

        Assert.AreEqual(
            CustomFolderRejection.OtherUserProfile,
            CustomFolderGuard.Check(@"C:\Users\me\AppData\Local\Temp", context).Rejection);
        Assert.IsTrue(CustomFolderGuard.Check(@"D:\Work\cache", context).IsAllowed);
    }

    [TestMethod]
    public void 許可フォルダーだけを正規化して重複なしで返す()
    {
        var allowed = CustomFolderGuard.FilterAllowed(
            [@"D:\Work\cache\", @"d:\work\CACHE", @"C:\Windows\Temp", null, @"C:\Users\me\.ssh"],
            Context);

        Assert.HasCount(1, allowed);
        Assert.AreEqual(@"D:\Work\cache", allowed[0]);
    }

    // ----- 除外・パターンの正規化 -----

    [TestMethod]
    public void 除外は正規化して重複を数えず不正な形式を保存しない()
    {
        var store = NewStore(out var service);

        Assert.AreEqual(CleanupAddResult.Added, store.AddExclusion(CleanupExclusionKind.Name, ".log"));
        Assert.AreEqual(CleanupAddResult.Duplicate, store.AddExclusion(CleanupExclusionKind.Name, "*.LOG"));
        Assert.AreEqual(CleanupAddResult.Added, store.AddExclusion(CleanupExclusionKind.Folder, @"C:\a\b\"));
        Assert.AreEqual(CleanupAddResult.Duplicate, store.AddExclusion(CleanupExclusionKind.Folder, @"c:\A\B"));
        Assert.AreEqual(CleanupAddResult.Added, store.AddExclusion(CleanupExclusionKind.File, @"C:\a\b.txt"));
        Assert.AreEqual(CleanupAddResult.Added, store.AddExclusion(CleanupExclusionKind.CookieDomain, " *.Example.COM. "));

        Assert.AreEqual(CleanupAddResult.Invalid, store.AddExclusion(CleanupExclusionKind.Name, @"dir\file.log"));
        Assert.AreEqual(CleanupAddResult.Invalid, store.AddExclusion(CleanupExclusionKind.Folder, @"relative"));
        Assert.AreEqual(CleanupAddResult.Invalid, store.AddExclusion(CleanupExclusionKind.Folder, @"C:\a\*"));
        Assert.AreEqual(CleanupAddResult.Invalid, store.AddExclusion(CleanupExclusionKind.File, @"C:\a\"));
        Assert.AreEqual(CleanupAddResult.Invalid, store.AddExclusion(CleanupExclusionKind.CookieDomain, "exa mple.com"));
        Assert.AreEqual(CleanupAddResult.Invalid, store.AddExclusion(CleanupExclusionKind.CookieDomain, "-a.com"));
        Assert.AreEqual(CleanupAddResult.Invalid, store.AddExclusion(CleanupExclusionKind.CookieDomain, ""));

        CollectionAssert.AreEqual(new[] { "*.log" }, store.GetExclusions(CleanupExclusionKind.Name).ToArray());
        CollectionAssert.AreEqual(new[] { @"C:\a\b" }, store.GetExclusions(CleanupExclusionKind.Folder).ToArray());
        CollectionAssert.AreEqual(new[] { "example.com" }, store.GetExclusions(CleanupExclusionKind.CookieDomain).ToArray());
        Assert.IsTrue(store.RemoveExclusion(CleanupExclusionKind.Name, ".LOG"));
        Assert.IsFalse(store.RemoveExclusion(CleanupExclusionKind.Name, ".log"));
        Assert.HasCount(0, service.Current.Cleanup.ExcludedNames);
    }

    [TestMethod]
    public void 除外の一致器は保存した除外と同じ判定をする()
    {
        var store = NewStore(out _);
        store.AddExclusion(CleanupExclusionKind.Folder, @"C:\keep");
        store.AddExclusion(CleanupExclusionKind.Name, ".log");

        var matcher = store.CreateExclusionMatcher();

        Assert.IsTrue(matcher.IsExcluded(@"C:\keep\a.bin", isDirectory: false));
        Assert.IsTrue(matcher.IsExcluded(@"C:\x\a.log", isDirectory: false));
        Assert.IsFalse(matcher.IsExcluded(@"C:\x\a.bin", isDirectory: false));
    }

    [TestMethod]
    public void パターンは全てを選ぶ形を拒否する()
    {
        var store = NewStore(out _);

        Assert.AreEqual(CleanupAddResult.Invalid, store.SetPattern("*", true));
        Assert.AreEqual(CleanupAddResult.Invalid, store.SetPattern("*.*", true));
        Assert.AreEqual(CleanupAddResult.Invalid, store.SetPattern(@"a\b", true));
        Assert.AreEqual(CleanupAddResult.Added, store.SetPattern(".tmp", true));
        Assert.AreEqual(CleanupAddResult.Duplicate, store.SetPattern("*.TMP", false));

        Assert.IsFalse(store.GetPatterns()["*.tmp"]);
        Assert.IsTrue(store.RemovePattern(".tmp"));
        Assert.HasCount(0, store.GetPatterns());
    }

    [TestMethod]
    public void 規則の上書きは設定して戻せる()
    {
        var store = NewStore(out _);

        Assert.IsNull(store.GetRuleOverride("app.x"));
        store.SetRuleOverride("app.x", false);
        Assert.AreEqual(false, store.GetRuleOverride("app.x"));
        store.SetRuleOverride("app.x", null);
        Assert.IsNull(store.GetRuleOverride("app.x"));
        Assert.Throws<ArgumentException>(() => store.SetRuleOverride(" ", true));
    }

    [TestMethod]
    public void 既定は安全な削除オフと復元ポイントオンで実績は上限で止まる()
    {
        var store = NewStore(out _);

        Assert.IsFalse(store.SecureDelete);
        Assert.IsTrue(store.CreateRestorePoint);

        var at = new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);
        store.RecordRun(10, 100, 2, partial: true, at);
        store.RecordRun(-5, long.MaxValue, 0, partial: false, at);

        var totals = store.GetTotals();
        Assert.AreEqual(2, totals.Runs);
        Assert.AreEqual(10, totals.FilesDeleted);
        Assert.AreEqual(long.MaxValue, totals.BytesFreed);
        Assert.AreEqual(0, store.GetLastRun()!.FilesDeleted);
        Assert.IsFalse(store.GetLastRun()!.Partial);
    }

    // ----- カスタムフォルダーの保存と実行時の再検査 -----

    [TestMethod]
    public void カスタムフォルダーは拒否した基点を保存せず実行時にも再検査する()
    {
        var store = NewStore(out var service);

        Assert.IsFalse(store.AddCustomFolder(@"C:\Windows\Temp", Context).IsAllowed);
        Assert.IsFalse(store.AddCustomFolder(@"C:\Users\me\.ssh", Context).IsAllowed);
        Assert.IsTrue(store.AddCustomFolder(@"D:\Work\cache\", Context).IsAllowed);
        Assert.HasCount(1, service.Current.Cleanup.CustomFolders);

        // settings.json は非昇格でも書けるため、保存済みの値も実行時に再検査する。
        service.Current.Cleanup.CustomFolders.Add(@"C:\Program Files\App");
        var checks = store.GetCustomFolders(Context);

        Assert.HasCount(2, checks);
        Assert.IsTrue(checks[0].IsAllowed);
        Assert.AreEqual(CustomFolderRejection.ProgramFiles, checks[1].Rejection);
        Assert.IsTrue(store.RemoveCustomFolder(@"d:\work\CACHE"));
    }

    // ----- インポート・エクスポート -----

    [TestMethod]
    public void 除外はエクスポートしてインポートで復元できる()
    {
        var source = NewStore(out _);
        source.AddExclusion(CleanupExclusionKind.File, @"C:\a\b.txt");
        source.AddExclusion(CleanupExclusionKind.Folder, @"C:\keep");
        source.AddExclusion(CleanupExclusionKind.Name, "thumbs.db");
        source.AddExclusion(CleanupExclusionKind.CookieDomain, "example.com");
        var path = Path.Combine(_directory, "export.json");

        source.ExportExclusions(path);

        Assert.IsFalse(File.Exists(path + ".tmp"));
        var target = new CleanupSettingsStore(new SettingsService(_directory, Path.Combine(_directory, "other.json")));
        var result = target.ImportExclusions(path);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(4, result.Added);
        Assert.AreEqual(0, result.Rejected);
        CollectionAssert.AreEqual(new[] { @"C:\keep" }, target.GetExclusions(CleanupExclusionKind.Folder).ToArray());
        CollectionAssert.AreEqual(new[] { "thumbs.db" }, target.GetExclusions(CleanupExclusionKind.Name).ToArray());

        var again = target.ImportExclusions(path);
        Assert.AreEqual(0, again.Added);
        Assert.AreEqual(4, again.Duplicates);
    }

    [TestMethod]
    public void インポートは不正な要素だけを数えて除き有効な要素を取り込む()
    {
        var store = NewStore(out _);
        const string json = """
            {"schema":1,"files":["C:\\a.txt","relative.txt",null],"folders":["C:\\*"],"names":[".log","a/b"],"cookieDomains":["ok.example","bad domain"]}
            """;

        var result = store.ImportExclusionsJson(json);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(3, result.Added);
        Assert.AreEqual(5, result.Rejected);
    }

    [TestMethod]
    [DataRow("", CleanupImportFailure.InvalidFormat)]
    [DataRow("{ invalid", CleanupImportFailure.InvalidFormat)]
    [DataRow("null", CleanupImportFailure.InvalidFormat)]
    [DataRow("[]", CleanupImportFailure.InvalidFormat)]
    [DataRow("{\"schema\":1,\"files\":\"C:\\\\a\"}", CleanupImportFailure.InvalidFormat)]
    [DataRow("{\"schema\":1,\"unknown\":[]}", CleanupImportFailure.InvalidFormat)]
    [DataRow("{\"schema\":1,\"names\":[\".log\",]}", CleanupImportFailure.InvalidFormat)]
    [DataRow("{\"schema\":2,\"names\":[\".log\"]}", CleanupImportFailure.UnsupportedSchema)]
    [DataRow("{\"names\":[\".log\"]}", CleanupImportFailure.UnsupportedSchema)]
    public void 不正なインポート文書は設定を変えずに拒否する(string json, CleanupImportFailure expected)
    {
        var store = NewStore(out var service);
        store.AddExclusion(CleanupExclusionKind.Name, "keep.me");

        var result = store.ImportExclusionsJson(json);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(expected, result.Failure);
        CollectionAssert.AreEqual(new[] { "keep.me" }, service.Current.Cleanup.ExcludedNames.ToArray());
    }

    [TestMethod]
    public void 大きすぎるまたは件数が多すぎるインポートは設定を変えずに拒否する()
    {
        var store = NewStore(out var service);
        var tooMany = new StringBuilder("{\"schema\":1,\"names\":[");
        for (var i = 0; i <= CleanupSettingsStore.MaxEntriesPerList; i++)
        {
            tooMany.Append(i == 0 ? "" : ",").Append("\"a").Append(i).Append(".log\"");
        }

        tooMany.Append("]}");
        var oversize = "{\"schema\":1,\"names\":[\"" + new string('a', CleanupSettingsStore.MaxImportBytes) + "\"]}";

        Assert.AreEqual(CleanupImportFailure.TooManyEntries, store.ImportExclusionsJson(tooMany.ToString()).Failure);
        Assert.AreEqual(CleanupImportFailure.TooLarge, store.ImportExclusionsJson(oversize).Failure);

        var bigFile = Path.Combine(_directory, "big.json");
        File.WriteAllText(bigFile, oversize);
        Assert.AreEqual(CleanupImportFailure.TooLarge, store.ImportExclusions(bigFile).Failure);
        Assert.AreEqual(CleanupImportFailure.Unreadable, store.ImportExclusions(Path.Combine(_directory, "none.json")).Failure);
        Assert.AreEqual(CleanupImportFailure.Unreadable, store.ImportExclusions("relative.json").Failure);
        Assert.HasCount(0, service.Current.Cleanup.ExcludedNames);
    }

    [TestMethod]
    public void エクスポートは書き込めない場合に例外を返し既存ファイルを残す()
    {
        var store = NewStore(out _);
        store.AddExclusion(CleanupExclusionKind.Name, ".log");
        var path = Path.Combine(_directory, "export.json");
        File.WriteAllText(path, "original");

        // 一時ファイルの名前にディレクトリを置いて書き込みを失敗させる。
        Directory.CreateDirectory(path + ".tmp");

        Assert.ThrowsExactly<UnauthorizedAccessException>(() => store.ExportExclusions(path));
        Assert.AreEqual("original", File.ReadAllText(path));
        Assert.Throws<ArgumentException>(() => store.ExportExclusions("relative.json"));
    }

    // ----- 保存 -----

    [TestMethod]
    public async Task 設定は置換保存され再読込で復元できる()
    {
        var store = NewStore(out var service);
        store.AddExclusion(CleanupExclusionKind.Folder, @"C:\keep");
        store.SetRuleOverride("app.x", false);
        store.SecureDelete = true;
        store.CreateRestorePoint = false;
        store.AddCustomFolder(@"D:\Work\cache", Context);
        await store.SaveAsync();
        await service.FlushAsync();

        var reloaded = new CleanupSettingsStore(NewService());

        Assert.IsFalse(File.Exists(_settingsPath + ".tmp"));
        CollectionAssert.AreEqual(new[] { @"C:\keep" }, reloaded.GetExclusions(CleanupExclusionKind.Folder).ToArray());
        Assert.AreEqual(false, reloaded.GetRuleOverride("app.x"));
        Assert.IsTrue(reloaded.SecureDelete);
        Assert.IsFalse(reloaded.CreateRestorePoint);
        Assert.HasCount(1, reloaded.GetCustomFolders(Context));
    }

    [TestMethod]
    public async Task 設定が破損している間は保存せず原本を保持する()
    {
        const string original = "{ invalid json";
        await File.WriteAllTextAsync(_settingsPath, original);
        var store = NewStore(out var service);

        store.AddExclusion(CleanupExclusionKind.Name, ".log");
        store.SetRuleOverride("app.x", false);

        Assert.AreEqual(SettingsLoadStatus.Failed, service.LoadStatus);
        Assert.AreEqual(false, store.GetRuleOverride("app.x"));
        Assert.Throws<InvalidOperationException>(() => store.SaveAsync());
        await service.SaveAsync();
        await service.FlushAsync();
        Assert.AreEqual(original, await File.ReadAllTextAsync(_settingsPath));
    }

    [TestMethod]
    public void Cleanupがnullや欠落した項目を持つ設定も空として読み込む()
    {
        File.WriteAllText(
            _settingsPath,
            "{\"Cleanup\":{\"SchemaVersion\":1,\"RuleOverrides\":null,\"ExcludedFiles\":[null,\"C:\\\\a.txt\"],\"Totals\":null}}");
        var service = NewService();
        var store = new CleanupSettingsStore(service);

        Assert.AreEqual(SettingsLoadStatus.Loaded, service.LoadStatus);
        Assert.IsNull(store.GetRuleOverride("x"));
        CollectionAssert.AreEqual(new[] { @"C:\a.txt" }, store.GetExclusions(CleanupExclusionKind.File).ToArray());
        Assert.AreEqual(0, store.GetTotals().Runs);

        File.WriteAllText(_settingsPath, "{\"Cleanup\":null}");
        Assert.AreEqual(SettingsLoadStatus.Loaded, NewService().LoadStatus);
    }

    // ----- 0 -> 1 移行 -----

    private static CleanupRule Rule(string id, string[]? legacy = null, string? targetPath = null) => new()
    {
        Id = id,
        Legacy = [.. legacy ?? []],
        Targets = targetPath is null ? [] : [new CleanupRuleTarget { Path = targetPath }],
    };

    [TestMethod]
    public void 旧除外に当たる規則だけを規則オフへ移し旧除外は残す()
    {
        var settings = new AppSettings
        {
            CleanupExclusions = new()
            {
                ["cleanup-app-cache"] = ["example"],
                ["cleanup-user-temp"] = [@"%LOCALAPPDATA%\CrashDumps", "unrelated"],
            },
        };
        CleanupRule[] rules =
        [
            Rule("by.legacy", legacy: ["cleanup-app-cache|Example"]),
            Rule("by.path", targetPath: @"%LOCALAPPDATA%\CrashDumps"),
            Rule("untouched", legacy: ["cleanup-app-cache|Other", "cleanup-user-temp", "|x", "nobar"], targetPath: @"%APPDATA%\Untouched"),
        ];

        var migrated = CleanupSettingsMigration.Apply(settings, rules);

        Assert.IsTrue(migrated);
        Assert.AreEqual(1, settings.Cleanup.SchemaVersion);
        Assert.AreEqual(false, settings.Cleanup.RuleOverrides["by.legacy"]);
        Assert.AreEqual(false, settings.Cleanup.RuleOverrides["by.path"]);
        Assert.IsFalse(settings.Cleanup.RuleOverrides.ContainsKey("untouched"));
        Assert.HasCount(2, settings.CleanupExclusions);
        CollectionAssert.AreEqual(new[] { "example" }, settings.CleanupExclusions["cleanup-app-cache"].ToArray());
    }

    [TestMethod]
    public void 移行済みの設定は再移行せず利用者の上書きを保つ()
    {
        var settings = new AppSettings { CleanupExclusions = new() { ["a"] = ["k"] } };
        var rules = new[] { Rule("r", legacy: ["a|k"]) };
        CleanupSettingsMigration.Apply(settings, rules);
        settings.Cleanup.RuleOverrides["r"] = true;

        var again = CleanupSettingsMigration.Apply(settings, rules);

        Assert.IsFalse(again);
        Assert.AreEqual(true, settings.Cleanup.RuleOverrides["r"]);
    }

    [TestMethod]
    public async Task 旧設定を読み込むと移行され保存後も旧除外が残り旧版へ戻しても移行をやり直せる()
    {
        await File.WriteAllTextAsync(
            _settingsPath,
            "{\"Locale\":\"ja_JP\",\"CleanupExclusions\":{\"cleanup-user-temp\":[\"%LOCALAPPDATA%\\\\Temp\"]}}");
        var service = NewService();

        Assert.AreEqual(SettingsLoadStatus.Loaded, service.LoadStatus);
        Assert.AreEqual(CleanupSettings.CurrentSchemaVersion, service.Current.Cleanup.SchemaVersion);

        await SaveAsync(service);
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(_settingsPath)))
        {
            var root = document.RootElement;
            Assert.AreEqual(1, root.GetProperty("Cleanup").GetProperty("SchemaVersion").GetInt32());
            Assert.AreEqual(
                @"%LOCALAPPDATA%\Temp",
                root.GetProperty("CleanupExclusions").GetProperty("cleanup-user-temp")[0].GetString());
        }

        // 旧版が Cleanup を知らずに保存し直した状態でも、旧除外から移行をやり直せる。
        await File.WriteAllTextAsync(
            _settingsPath,
            "{\"CleanupExclusions\":{\"cleanup-user-temp\":[\"%LOCALAPPDATA%\\\\Temp\",\"added-by-old\"]}}");
        var downgraded = NewService();

        Assert.AreEqual(CleanupSettings.CurrentSchemaVersion, downgraded.Current.Cleanup.SchemaVersion);
        Assert.HasCount(2, downgraded.Current.CleanupExclusions["cleanup-user-temp"]);
    }

    [TestMethod]
    public async Task 破損した旧設定は移行も保存もしない()
    {
        const string original = "{\"CleanupExclusions\":{\"a\":[\"k\"]";
        await File.WriteAllTextAsync(_settingsPath, original);
        var service = NewService();

        await SaveAsync(service);

        Assert.AreEqual(SettingsLoadStatus.Failed, service.LoadStatus);
        Assert.AreEqual(original, await File.ReadAllTextAsync(_settingsPath));
    }
}
