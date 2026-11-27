using System.Runtime.InteropServices;
using System.Text;
using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows.Actions;
using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.Core.Services.Windows.Cleanup.Handlers;
using EngineTarget = Lumin4ti.Core.Services.Windows.Actions.CleanupTarget;

namespace Lumin4ti.Tests;

/// <summary>審査指摘の回帰テスト。実機のレジストリ・環境変数・既存ファイルを変えず、一時領域と注入した環境だけを使う。</summary>
[TestClass]
public sealed class CleanupReviewFixTests
{
    private static readonly CustomFolderGuardContext Context = new(
        @"C:\Windows",
        [@"C:\Program Files", @"C:\Program Files (x86)"],
        @"C:\ProgramData",
        [@"C:\Users"],
        @"C:\Users\me");

    private CleanupHarness _h = null!;

    [TestInitialize]
    public void Setup() => _h = new CleanupHarness();

    [TestCleanup]
    public void Cleanup() => _h.Dispose();

    // ----- 1. カスタムフォルダー: 認証情報の祖先と基点 -----

    [TestMethod]
    [DataRow(@"C:\Users\me\AppData\Roaming\Microsoft", CustomFolderRejection.ProfileRoot)]
    [DataRow(@"C:\Users\me\AppData\Local\Microsoft", CustomFolderRejection.ProfileRoot)]
    [DataRow(@"C:\Users\me\AppData\LocalLow", CustomFolderRejection.ProfileRoot)]
    [DataRow(@"C:\Users\me\Documents", CustomFolderRejection.ProfileRoot)]
    [DataRow(@"C:\Users\me\AppData\Local\Google", CustomFolderRejection.Credential)]
    [DataRow(@"C:\Users\me\AppData\Local\Google\Chrome", CustomFolderRejection.Credential)]
    [DataRow(@"C:\Users\me\AppData\Local\Google\Chrome\User Data", CustomFolderRejection.Credential)]
    [DataRow(@"C:\Users\me\AppData\Local\Microsoft\Edge\User Data", CustomFolderRejection.Credential)]
    [DataRow(@"C:\Users\me\AppData\Roaming\Mozilla", CustomFolderRejection.Credential)]
    [DataRow(@"C:\Users\me\AppData\Roaming\Mozilla\Firefox", CustomFolderRejection.Credential)]
    [DataRow(@"C:\Users\me\AppData\Roaming\Opera Software\Opera Stable", CustomFolderRejection.Credential)]
    [DataRow(@"C:\Users\me\AppData\Roaming\Thunderbird", CustomFolderRejection.Credential)]
    [DataRow(@"C:\Users\me\AppData\Local\Microsoft\Outlook", CustomFolderRejection.Credential)]
    [DataRow(@"C:\Users\me\AppData\Local\Google\Chrome\User Data\Default\Cache", CustomFolderRejection.Credential)]
    [DataRow(@"C:\Users\me\AppData\Roaming\Mozilla\Firefox\Profiles\x.default\cache2", CustomFolderRejection.Credential)]
    [DataRow(@"C:\Users\me\AppData\Local\Mozilla\Firefox\Profiles\x.default\cache2", CustomFolderRejection.Credential)]
    [DataRow(@"C:\Users\me\AppData\Roaming\Thunderbird\Profiles\x.default", CustomFolderRejection.Credential)]
    [DataRow(@"C:\Users\me\AppData\Roaming\Opera Software\Opera Stable\Default", CustomFolderRejection.Credential)]
    [DataRow(@"C:\Users\me\AppData\Local\Microsoft\Outlook\RoamCache", CustomFolderRejection.Credential)]
    [DataRow(@"C:\Users\me\Downloads", CustomFolderRejection.None)]
    [DataRow(@"C:\Users\me\AppData\Roaming\Microsoft\Teams\Cache", CustomFolderRejection.None)]
    [DataRow(@"D:\Work\%TEMP%\x", CustomFolderRejection.Invalid)]
    [DataRow(@"D:\Work\report%20x", CustomFolderRejection.Invalid)]
    public void カスタムフォルダーは認証情報の祖先とプロファイル基点を拒否し環境変数の形を拒否する(string path, CustomFolderRejection expected) =>
        Assert.AreEqual(expected, CustomFolderGuard.Check(path, Context).Rejection);

    [TestMethod]
    public void 保存済みの値にも実行時の検査が効く()
    {
        var store = new CleanupSettingsStore(_h.Service);
        _h.Service.Current.Cleanup.CustomFolders.Add(@"C:\Users\me\AppData\Roaming\Microsoft");
        _h.Service.Current.Cleanup.CustomFolders.Add(@"C:\Users\me\AppData\Local\Google\Chrome\User Data");

        var checks = store.GetCustomFolders(Context);

        Assert.AreEqual(CustomFolderRejection.ProfileRoot, checks[0].Rejection);
        Assert.AreEqual(CustomFolderRejection.Credential, checks[1].Rejection);
    }

    [TestMethod]
    public void 走査と削除は認証情報の名前の子孫を同じ判定で残す()
    {
        var root = _h.Full("Custom");
        _h.Write(@"Custom\Login Data", "secret");
        _h.Write(@"Custom\Vault\k.bin", "key");
        _h.Write(@"Custom\id_rsa", "key");
        _h.Write(@"Custom\keep.pem", "key");
        _h.Write(@"Custom\Cache\a.tmp", "12");
        _h.Write(@"Custom\b.txt", "123");
        var target = EngineTarget.CustomFolder(root);

        var scan = FileCleanupScanner.Scan(target, null, CancellationToken.None);
        var run = FileCleanupEngine.Run([target], false, null, CancellationToken.None);

        Assert.AreEqual(2, scan.Files, "認証情報の名前を走査で数えた");
        Assert.AreEqual(scan.Files, run.DeletedFiles, "走査と削除の判定が食い違う");
        Assert.AreEqual(scan.Retained, run.Retained);
        Assert.AreEqual(4, run.Retained);
        Assert.AreEqual(0, run.Blocked, "残したものを含むフォルダーの削除失敗を失敗として数えた");
        foreach (var kept in new[] { @"Custom\Login Data", @"Custom\Vault\k.bin", @"Custom\id_rsa", @"Custom\keep.pem" })
        {
            Assert.IsTrue(File.Exists(_h.Full(kept)), kept);
        }

        Assert.IsFalse(File.Exists(_h.Full(@"Custom\b.txt")));
        Assert.IsFalse(Directory.Exists(_h.Full(@"Custom\Cache")));
    }

    [TestMethod]
    public async Task カスタム規則の走査と実行は認証情報の名前の子孫を残す()
    {
        _h.Write(@"Custom\Login Data", "secret");
        var junk = _h.Write(@"Custom\junk.tmp", "123");
        Assert.IsTrue(_h.Store.AddCustomFolder(_h.Full("Custom"), _h.Guard).IsAllowed);
        Thread.Sleep(150);
        var scan = _h.NewScan();
        var snapshot = await scan.ScanAsync(null, null, CancellationToken.None);
        var id = snapshot.Items.Single().Rule.Id;

        var result = await new CleanupRunService(scan, new RunningProcessGuard(new FakeProcessControl()), new FakeServiceHost().Suspend, () => DateTimeOffset.UtcNow)
            .RunAsync(new CleanupRunRequest(snapshot, [id]) { ConfirmedUserFileRuleIds = [id] }, null, CancellationToken.None);

        Assert.AreEqual(1, snapshot.Items.Single().Files);
        Assert.AreEqual(CleanupRunStatus.Succeeded, result.Items[0].Status);
        Assert.IsFalse(File.Exists(junk));
        Assert.IsTrue(File.Exists(_h.Full(@"Custom\Login Data")));
    }

    // ----- 2. 基点の検証 -----

    private static CleanupBaseTrust Trust(Func<string, string?>? machine = null) =>
        new(Context, machine ?? (name => name == "PROGRAMDATA" ? @"C:\ProgramData" : null), _ => true);

    [TestMethod]
    [DataRow("LOCALAPPDATA", @"C:\Users\me\AppData\Local", null)]
    [DataRow("USERPROFILE", @"C:\Users\me", null)]
    [DataRow("APPDATA", @"D:\Evil\Roaming", "BaseOutsideProfile")]
    [DataRow("LOCALAPPDATA", @"C:\Users\other\AppData\Local", "BaseOutsideProfile")]
    [DataRow("TEMP", @"C:\Users\me\AppData\Local\Temp", null)]
    [DataRow("TEMP", @"D:\Temp", null)]
    [DataRow("TMP", @"D:\", "BaseProtectedLocation")]
    [DataRow("TEMP", @"C:\Windows\Temp", "BaseProtectedLocation")]
    [DataRow("TEMP", @"C:\Program Files\x", "BaseProtectedLocation")]
    [DataRow("TEMP", @"C:\ProgramData\x", "BaseProtectedLocation")]
    [DataRow("TEMP", @"C:\Users\other\Temp", "BaseProtectedLocation")]
    [DataRow("TEMP", @"C:\Users", "BaseProtectedLocation")]
    [DataRow("PROGRAMDATA", @"c:\programdata\", null)]
    [DataRow("PROGRAMDATA", @"D:\ProgramData", "BaseMachineMismatch")]
    [DataRow("PUBLIC", @"C:\Users\Public", "BaseMachineMismatch")]
    public void 基点の環境変数は利用者が差し替えても信頼できる場所だけを使う(string variable, string value, string? rejectionKey)
    {
        var rejection = Trust().Check(variable, value);

        if (rejectionKey is null)
        {
            Assert.IsNull(rejection);
            return;
        }

        Assert.IsTrue(CleanupMessage.TryParse(rejection, out var key, out var args), rejection);
        Assert.AreEqual("Cleanup.Msg." + rejectionKey, key);
        Assert.AreEqual(variable, args[0]);
    }

    [TestMethod]
    public void プロファイルが確認できないときは利用者スコープの基点を使わない()
    {
        var trust = new CleanupBaseTrust(Context with { CurrentProfile = null }, _ => null, _ => true);

        Assert.IsNotNull(trust.Check("LOCALAPPDATA", @"C:\Users\me\AppData\Local"));
        Assert.IsNull(trust.Check("TEMP", @"D:\Temp"));
    }

    [TestMethod]
    public void リゾルバーは信頼できない基点を理由付きで拒否し走査結果にも出す()
    {
        _h.Env["LOCALAPPDATA"] = @"D:\Evil\Local";
        _h.Rules.Add(CleanupHarness.MakeRule("app.test", [CleanupHarness.Contents(@"%LOCALAPPDATA%\Test\Cache")]));

        var resolved = new CleanupTargetResolver(name => _h.Env.GetValueOrDefault(name), _h.Probe, _h.Trust).Resolve(_h.Rules[0]);
        Assert.IsNull(resolved.Targets[0].Path);
        Assert.IsTrue(CleanupMessage.TryParse(resolved.Targets[0].Rejection, out var key, out _));
        Assert.AreEqual("Cleanup.Msg.BaseOutsideProfile", key);

        var snapshot = _h.NewScan().ScanAsync(null, null, CancellationToken.None).GetAwaiter().GetResult();
        var item = snapshot.Items.Single();
        Assert.AreEqual(CleanupItemState.Unavailable, item.State, "信頼できない基点を「この PC に無い」と偽った");
        Assert.IsTrue(CleanupMessage.TryParse(item.Detail[0], out var detailKey, out _), item.Detail[0]);
        Assert.AreEqual("Cleanup.Msg.BaseOutsideProfile", detailKey);
    }

    [TestMethod]
    public void プロファイルは環境変数ではなくProfileListと照合して確認する()
    {
        var known = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!.TrimEnd('\\');

        Assert.IsTrue(VerifiedProfile.Matches($@"{drive}\Users\me", @"%SystemDrive%\Users\me"));
        Assert.IsTrue(VerifiedProfile.Matches($@"{drive}\Users\me", $@"{drive}\USERS\me\"));
        Assert.IsFalse(VerifiedProfile.Matches($@"{drive}\Users\me", @"%SystemDrive%\Users\other"));
        Assert.IsFalse(VerifiedProfile.Matches($@"{drive}\Users\me", @"%UserNameX%\Users\me"));
        Assert.IsFalse(VerifiedProfile.Matches($@"{drive}\Users\me", null));
        Assert.IsNotNull(VerifiedProfile.Current(), "この PC のプロファイルを HKLM の ProfileList と照合できない");
        Assert.AreEqual(Path.TrimEndingDirectorySeparator(Path.GetFullPath(known)), VerifiedProfile.Current(), ignoreCase: true);
    }

    [TestMethod]
    public void 短縮名は長い名前へ戻してから使う()
    {
        var longDirectory = Path.Combine(_h.Root, "Long Directory Name For Short Test");
        Directory.CreateDirectory(longDirectory);
        var buffer = new StringBuilder(1024);
        _ = GetShortPathNameW(longDirectory, buffer, buffer.Capacity);
        var shortName = buffer.ToString();

        Assert.AreEqual(longDirectory, PathNames.ToLongName(longDirectory), "短縮名を含まないパスを変えた");
        if (!shortName.Contains('~'))
        {
            return; // このボリュームは 8.3 名を作らない。
        }

        Assert.AreEqual(longDirectory, PathNames.ToLongName(shortName), ignoreCase: true);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, int bufferLength);

    // ----- 3. ハンドラーの Failed -----

    [TestMethod]
    public async Task ハンドラーがFailedを返したら削除件数にかかわらず規則をFailedにする()
    {
        _h.Handlers[CleanupHandlerKind.DnsCache] = new FakeHandler("dnsCache") { RunResult = new(CleanupHandlerStatus.Failed, 5, 50) { Details = ["復帰失敗"] } };
        _h.Rules.Add(CleanupHarness.HandlerRule("win.one", CleanupHandlerKind.DnsCache));
        Thread.Sleep(150);
        var scan = _h.NewScan();
        var snapshot = await scan.ScanAsync(null, null, CancellationToken.None);

        var result = await new CleanupRunService(scan, new RunningProcessGuard(new FakeProcessControl()), new FakeServiceHost().Suspend, () => DateTimeOffset.UtcNow)
            .RunAsync(new CleanupRunRequest(snapshot, ["win.one"]), null, CancellationToken.None);

        Assert.AreEqual(CleanupRunStatus.Failed, result.Items[0].Status);
        Assert.AreEqual(5, result.Items[0].Items);
    }

    // ----- 4. DeleteVerified は実パスを展開しない -----

    [TestMethod]
    public void 検証付き削除は環境変数を展開せず百分率を含む実在の名前を拒否しない()
    {
        var file = _h.Write(@"Pct\report%20x.tmp", "123");
        var scan = FileCleanupScanner.Scan(EngineTarget.Contents(_h.Full("Pct")), new CleanupScanOptions(CollectIdentities: true), CancellationToken.None);

        var outcome = FileCleanupEngine.DeleteVerified(scan.Identities, null, CancellationToken.None);

        Assert.IsEmpty(outcome.RejectedTargets, string.Join(" / ", outcome.RejectedTargets));
        Assert.AreEqual(1, outcome.DeletedFiles);
        Assert.IsFalse(File.Exists(file));
    }

    // ----- 5. 重複・時刻・145・並び -----

    [TestMethod]
    public void 先の規則が扱ったファイルが残るフォルダーの削除失敗を二重の失敗にしない()
    {
        var kept = _h.Write(@"Tree\sub\keep.tmp", "1");
        var gone = _h.Write(@"Tree\sub\gone.tmp", "2");
        var options = new CleanupRunOptions { AlreadyHandled = (path, _, _) => path.EndsWith("keep.tmp", StringComparison.OrdinalIgnoreCase) };

        var outcome = FileCleanupEngine.Run([EngineTarget.Contents(_h.Full("Tree"))], false, null, CancellationToken.None, options);

        Assert.AreEqual(0, outcome.Blocked, string.Join(" / ", outcome.BlockedExamples));
        Assert.AreEqual(1, outcome.DeletedFiles);
        Assert.IsTrue(File.Exists(kept));
        Assert.IsFalse(File.Exists(gone));
    }

    [TestMethod]
    public void 先の規則の領域は実行と同じ基準時刻で古さを判定する()
    {
        var dir = _h.Full("Aged");
        Directory.CreateDirectory(dir);
        var target = new EngineTarget(dir, CleanupTargetKind.Contents, null, false, false, 1);
        var plan = new TargetPlan(new CleanupRuleTarget { Type = CleanupTargetType.Contents, Path = dir }, dir, target, null);
        // 基準時刻を 2020-01-01 に固定すると、2020-01-01 正午の更新は「1 日より古い」に当たらない (現在を基準にすると当たる)。
        var fixedNow = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var lastWrite = new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var path = Path.Combine(dir, "f.tmp");

        var shared = new ClaimSet { Now = fixedNow };
        shared.Add("app.a", [plan]);
        var current = new ClaimSet();
        current.Add("app.a", [plan]);

        Assert.IsFalse(shared.RunResolver(target)!(path, false, lastWrite), "実行と異なる基準時刻で判定した");
        Assert.IsTrue(current.RunResolver(target)!(path, false, lastWrite));
    }

    [TestMethod]
    public async Task 走査の例外文は整形してから返す()
    {
        _h.Handlers[CleanupHandlerKind.DnsCache] = new ThrowingHandler("dnsCache", "line1\r\n\u001b[31mred\u001b[0m");
        _h.Rules.Add(CleanupHarness.HandlerRule("win.one", CleanupHandlerKind.DnsCache));

        var snapshot = await _h.NewScan().ScanAsync(null, null, CancellationToken.None);

        var item = snapshot.Items.Single();
        Assert.AreEqual(CleanupItemState.Unavailable, item.State);
        Assert.AreEqual("line1 red", item.Detail[0]);
    }

    [TestMethod]
    public async Task レジストリとDBとINIの実行は呼び出しスレッドを塞がない()
    {
        _h.Write(@"Roaming\Mozilla\Firefox\Profiles\x.default\cookies.sqlite", "x");
        _h.Rules.Add(CleanupHarness.MakeRule(
            "app.cookies",
            [new CleanupRuleTarget { Type = CleanupTargetType.Sqlite, Path = @"%APPDATA%\Mozilla\Firefox\Profiles\*\cookies.sqlite", Operation = CleanupSqliteOperation.GeckoCookies }],
            detect: @"%APPDATA%",
            category: CleanupCategory.Cookies,
            impact: CleanupImpact.Signout,
            recommended: false));
        Thread.Sleep(150);
        var scan = _h.NewScan();
        var snapshot = await scan.ScanAsync(null, null, CancellationToken.None);
        var service = new CleanupRunService(scan, new RunningProcessGuard(new FakeProcessControl()), new FakeServiceHost().Suspend, () => DateTimeOffset.UtcNow);
        Task<CleanupRunResult>? running = null;
        var callerThread = 0;

        // 専用スレッドで呼び出し、DB の処理がそのスレッドで同期的に走らないことを見る。
        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            running = service.RunAsync(new CleanupRunRequest(snapshot, ["app.cookies"]), null, CancellationToken.None);
        });
        caller.Start();
        caller.Join();
        var result = await running!;

        Assert.AreEqual(CleanupRunStatus.Succeeded, result.Items[0].Status);
        Assert.HasCount(1, _h.Databases.CleanThreads);
        Assert.AreNotEqual(callerThread, _h.Databases.CleanThreads[0], "DB の処理が呼び出しスレッドで同期的に走った");
    }

    [TestMethod]
    public void 照合に失敗したプロセスがあれば他のプロセスにもWMCLOSEを送らない()
    {
        var control = new SwappableProcessControl();
        var guard = new RunningProcessGuard(control);
        var rule = CleanupHarness.MakeRule("app.browser", [CleanupHarness.Contents(@"%LOCALAPPDATA%\Test\Cache")], category: CleanupCategory.BrowserCache, processes: ["chrome"]);
        var detection = guard.Detect(rule);
        Assert.HasCount(2, detection.Verified);
        control.Swapped = true; // 検出後に 2 件目の実体が別のプロセスへ入れ替わった。

        var result = guard.CloseAsync(rule, detection, (_, _) => Task.FromResult(false), CancellationToken.None).GetAwaiter().GetResult();

        Assert.AreEqual(ProcessGuardSkipReason.IdentityMismatch, result.Reason);
        Assert.IsEmpty(control.Closed, "照合が全件済む前に WM_CLOSE を送った");
    }
}

/// <summary>2 件の chrome を見せ、<see cref="Swapped"/> の後は 2 件目を別の名前に見せる。WM_CLOSE の宛先を記録する。</summary>
internal sealed class SwappableProcessControl : IProcessControl
{
    private static readonly System.Security.Principal.SecurityIdentifier Me = new(System.Security.Principal.WellKnownSidType.WorldSid, null);

    public bool Swapped { get; set; }

    public List<int> Closed { get; } = [];

    public int CurrentPid => 1;

    public uint CurrentSessionId => 1;

    public System.Security.Principal.SecurityIdentifier? CurrentUserSid => Me;

    public IReadOnlyList<ProcessSnapshotEntry> Snapshot() => [new(10, "chrome", 1), new(11, "chrome", 1)];

    public IProcessLease? Open(int pid) => new Lease(this, pid);

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;

    private sealed class Lease(SwappableProcessControl owner, int pid) : IProcessLease
    {
        public int Pid => pid;

        public string? ImageName => pid == 11 && owner.Swapped ? "notepad" : "chrome";

        public uint SessionId => 1;

        public System.Security.Principal.SecurityIdentifier? UserSid => Me;

        public long StartTime => 100;

        public bool HasExited() => false;

        public bool WaitForExit(TimeSpan timeout) => false;

        public int RequestClose()
        {
            owner.Closed.Add(pid);
            return 1;
        }

        public bool Terminate() => false;

        public void Dispose()
        {
        }
    }
}

internal sealed class ThrowingHandler(string id, string message) : ICleanupHandler
{
    public string Id { get; } = id;

    public IReadOnlyList<string> Services => [];

    public Task<CleanupHandlerScan> ScanAsync(CancellationToken ct) => throw new InvalidOperationException(message);

    public Task<CleanupHandlerResult> RunAsync(IProgress<string>? progress, CancellationToken ct) => throw new InvalidOperationException(message);
}
