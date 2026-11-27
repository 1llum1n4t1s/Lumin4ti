using System.Security.Principal;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.Core.Services.Windows.Cleanup.Handlers;

namespace Lumin4ti.Tests;

/// <summary>net.exe の実行とサービス状態の偽物。サービスの停止・復帰を再現し、実機のサービスは変えない。</summary>
internal sealed class FakeServiceHost : ICommandExecutor
{
    public IReadOnlyList<string> FailedToStop { get; set; } = [];

    public bool StartSucceeds { get; set; } = true;

    public Action? AfterSuspend { get; set; }

    public List<string> Events { get; } = [];

    public Task<ServiceSuspension> Suspend(IReadOnlyList<string> names, IProgress<string>? progress, CancellationToken ct)
    {
        Events.Add("suspend:" + string.Join(",", names));
        var suspension = new ServiceSuspension(this, names, FailedToStop, _ => WindowsServiceState.Stopped, _ => Task.CompletedTask);
        AfterSuspend?.Invoke();
        return Task.FromResult(suspension);
    }

    public Task<CommandExecutionResult> RunAsync(
        string fileName, string arguments, CancellationToken ct = default, IProgress<string>? onOutputLine = null, TimeSpan? timeout = null)
    {
        Events.Add($"{fileName} {arguments}");
        return Task.FromResult(new CommandExecutionResult(StartSucceeds, "net start", StartSucceeds ? 0 : 2, string.Empty, string.Empty));
    }
}

internal sealed class FakeProcess(string name, int pid)
{
    public string Name { get; } = name;

    public int Pid { get; } = pid;

    public bool ExitsOnClose { get; init; } = true;

    public bool ExitsOnTerminate { get; init; } = true;

    public bool Exited { get; set; }

    public bool Terminated { get; set; }

    public int CloseCalls { get; set; }
}

/// <summary>OS のプロセス境界の偽物。実プロセスを開かず、終了もしない。</summary>
internal sealed class FakeProcessControl(params FakeProcess[] processes) : IProcessControl
{
    private static readonly SecurityIdentifier Me = new(WellKnownSidType.WorldSid, null);

    public int CurrentPid => 1;

    public uint CurrentSessionId => 1;

    public SecurityIdentifier? CurrentUserSid => Me;

    public IReadOnlyList<ProcessSnapshotEntry> Snapshot() =>
        [.. processes.Where(p => !p.Exited).Select(p => new ProcessSnapshotEntry(p.Pid, p.Name, 1))];

    public IProcessLease? Open(int pid) => processes.FirstOrDefault(p => p.Pid == pid && !p.Exited) is { } process ? new Lease(process) : null;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;

    private sealed class Lease(FakeProcess process) : IProcessLease
    {
        public int Pid => process.Pid;

        public string? ImageName => process.Name;

        public uint SessionId => 1;

        public SecurityIdentifier? UserSid => Me;

        public long StartTime => 100;

        public bool HasExited() => process.Exited;

        public bool WaitForExit(TimeSpan timeout) => process.Exited;

        public int RequestClose()
        {
            process.CloseCalls++;
            if (process.ExitsOnClose) process.Exited = true;
            return 1;
        }

        public bool Terminate()
        {
            process.Terminated = true;
            if (process.ExitsOnTerminate) process.Exited = true;
            return true;
        }

        public void Dispose()
        {
        }
    }
}

internal sealed class FakePrompts : ICleanupRunPrompts
{
    public CleanupProcessChoice Choice { get; set; } = CleanupProcessChoice.Close;

    public bool Force { get; set; }

    public List<CleanupProcessPrompt> Asked { get; } = [];

    public List<CleanupProcessPrompt> ForceAsked { get; } = [];

    public Task<CleanupProcessChoice> ChooseForRunningProcessesAsync(CleanupProcessPrompt prompt, CancellationToken ct)
    {
        Asked.Add(prompt);
        return Task.FromResult(Choice);
    }

    public Task<bool> ConfirmForceTerminationAsync(CleanupProcessPrompt prompt, CancellationToken ct)
    {
        ForceAsked.Add(prompt);
        return Task.FromResult(Force);
    }
}

[TestClass]
public sealed class CleanupRunServiceTests
{
    private CleanupHarness _h = null!;
    private FakeServiceHost _services = null!;

    [TestInitialize]
    public void Setup()
    {
        _h = new CleanupHarness();
        _services = new FakeServiceHost();
    }

    [TestCleanup]
    public void Cleanup() => _h.Dispose();

    private static CleanupRuleTarget Cache(string path = @"%LOCALAPPDATA%\Test\Cache") => CleanupHarness.Contents(path);

    private CleanupRunService NewRun(CleanupScanService scan, FakeProcessControl? control = null) =>
        new(scan, new RunningProcessGuard(control ?? new FakeProcessControl()), _services.Suspend, () => DateTimeOffset.UtcNow);

    private async Task<(CleanupScanService Scan, CleanupScanSnapshot Snapshot)> ScanAsync()
    {
        // 走査の直前 50 ms に作られたファイルは「走査後に作られた」として残すため、作成から十分待つ。
        Thread.Sleep(150);
        var scan = _h.NewScan();
        return (scan, await scan.ScanAsync(null, null, CancellationToken.None));
    }

    private async Task<CleanupRunResult> RunAsync(
        CleanupScanService scan,
        CleanupScanSnapshot snapshot,
        string[] ids,
        FakePrompts? prompts = null,
        string[]? confirmed = null,
        FakeProcessControl? control = null,
        CancellationToken ct = default) =>
        await NewRun(scan, control).RunAsync(
            new CleanupRunRequest(snapshot, ids) { Prompts = prompts, ConfirmedUserFileRuleIds = confirmed ?? [] }, null, ct);

    // ----- 走査済みの選択だけを実行 -----

    [TestMethod]
    public async Task 選択した走査済みの規則だけを削除し実績を記録する()
    {
        var a = _h.Write(@"Local\Test\a\x.tmp", "12345");
        var b = _h.Write(@"Local\Test\b\y.tmp", "123");
        _h.Rules.AddRange([CleanupHarness.MakeRule("app.a", [Cache(@"%LOCALAPPDATA%\Test\a")]), CleanupHarness.MakeRule("app.b", [Cache(@"%LOCALAPPDATA%\Test\b")])]);
        var (scan, snapshot) = await ScanAsync();

        var result = await RunAsync(scan, snapshot, ["app.a", "app.unknown"]);

        Assert.IsFalse(File.Exists(a));
        Assert.IsTrue(File.Exists(b), "選択していない規則を削除した");
        var ran = result.Items.Single(i => i.Rule.Id == "app.a");
        Assert.AreEqual(CleanupRunStatus.Succeeded, ran.Status);
        Assert.AreEqual(1, ran.Files);
        Assert.AreEqual(5, ran.Bytes);
        var unknown = result.Items.Single(i => i.Rule.Id == "app.unknown");
        Assert.AreEqual(CleanupRunStatus.Skipped, unknown.Status);
        Assert.AreEqual(CleanupSkipReason.NotScanned, unknown.SkipReason);
        Assert.IsFalse(result.IsComplete, "走査していない選択を成功として扱った");
        Assert.AreEqual(1, _h.Store.GetTotals().Runs);
        Assert.AreEqual(1, _h.Store.GetTotals().FilesDeleted);
        Assert.IsTrue(_h.Store.GetLastRun()!.Partial);
    }

    [TestMethod]
    public async Task 走査後に作られたファイルと展開先は削除せず除外は走査時のものを使う()
    {
        var kept = _h.Write(@"Local\Test\p1\Cache\keep.dat", "1");
        var old = _h.Write(@"Local\Test\p1\Cache\old.tmp", "1");
        _h.Rules.Add(CleanupHarness.MakeRule("app.test", [Cache(@"%LOCALAPPDATA%\Test\*\Cache")]));
        _h.Store.AddExclusion(CleanupExclusionKind.File, kept);
        var (scan, snapshot) = await ScanAsync();
        Thread.Sleep(300);
        var created = _h.Write(@"Local\Test\p1\Cache\new.tmp", "1");
        var laterProfile = _h.Write(@"Local\Test\p2\Cache\other.tmp", "1");

        var result = await RunAsync(scan, snapshot, ["app.test"]);

        Assert.IsFalse(File.Exists(old));
        Assert.IsTrue(File.Exists(kept), "走査時の除外を守らなかった");
        Assert.IsTrue(File.Exists(created), "走査後に作られたファイルを消した");
        Assert.IsTrue(File.Exists(laterProfile), "走査していない展開先を消した");
        Assert.AreEqual(CleanupRunStatus.Succeeded, result.Items[0].Status);
        Assert.AreEqual(2, result.Items[0].Retained);
    }

    [TestMethod]
    public async Task 設定変更と再利用で失効した走査結果は何も実行しない()
    {
        var file = _h.Write(@"Local\Test\Cache\a.tmp");
        _h.Rules.Add(CleanupHarness.MakeRule("app.test", [Cache()]));
        var (scan, snapshot) = await ScanAsync();
        _h.Store.AddExclusion(CleanupExclusionKind.Name, "*.log");

        var stale = await RunAsync(scan, snapshot, ["app.test"]);
        Assert.IsTrue(stale.StaleScan);
        Assert.IsTrue(File.Exists(file));

        var (scan2, fresh) = await ScanAsync();
        var first = await RunAsync(scan2, fresh, ["app.test"]);
        Assert.IsFalse(first.StaleScan);
        Assert.IsFalse(File.Exists(file));
        var again = await RunAsync(scan2, fresh, ["app.test"]);
        Assert.IsTrue(again.StaleScan, "同じ走査結果を 2 回実行できた");
    }

    [TestMethod]
    public async Task 検出できない規則と空の規則は削除せずに結果へ区別して残す()
    {
        _h.Write(@"Local\Test\Empty\.keep");
        File.Delete(_h.Full(@"Local\Test\Empty\.keep"));
        _h.Rules.AddRange(
        [
            CleanupHarness.MakeRule("app.missing", [Cache()], detect: @"%LOCALAPPDATA%\NotInstalled"),
            CleanupHarness.MakeRule("app.empty", [Cache(@"%LOCALAPPDATA%\Test\Empty")]),
        ]);
        var (scan, snapshot) = await ScanAsync();

        var result = await RunAsync(scan, snapshot, ["app.missing", "app.empty"]);

        Assert.AreEqual(CleanupSkipReason.NothingFound, result.Items[0].SkipReason);
        Assert.AreEqual(CleanupRunStatus.NothingToDo, result.Items[1].Status);
        Assert.IsFalse(result.IsComplete);
    }

    [TestMethod]
    public async Task 規則内のリンクは辿らず通知として残し成功を妨げない()
    {
        _h.Write(@"Local\Test\Cache\a.tmp");
        var outside = _h.Write(@"Outside\precious.txt", "keep");
        CleanupHarness.CreateJunction(_h.Full(@"Local\Test\Cache\link"), _h.Full("Outside"));
        _h.Rules.Add(CleanupHarness.MakeRule("app.test", [Cache()]));
        var (scan, snapshot) = await ScanAsync();

        var result = await RunAsync(scan, snapshot, ["app.test"]);

        Assert.IsTrue(File.Exists(outside), "リンク先を削除した");
        Assert.AreEqual(CleanupRunStatus.Succeeded, result.Items[0].Status);
        Assert.IsTrue(result.Items[0].Details.Any(d => d.Contains("リンク", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task 安全な削除の設定は走査時の値で上書き削除を使う()
    {
        _h.Write(@"Local\Test\Cache\a.tmp", "payload");
        _h.Store.SecureDelete = true;
        _h.Rules.Add(CleanupHarness.MakeRule("app.test", [Cache()]));
        var (scan, snapshot) = await ScanAsync();

        var result = await RunAsync(scan, snapshot, ["app.test"]);

        Assert.AreEqual(1, result.Items[0].WipedFiles);
        Assert.AreEqual(CleanupRunStatus.Succeeded, result.Items[0].Status);
    }

    // ----- UserFiles・カスタム・スコープ -----

    [TestMethod]
    public async Task UserFilesは専用の再確認があるときだけ削除しカスタムは実行時にも検査する()
    {
        var file = _h.Write(@"Custom\received.bin", "123");
        Assert.IsTrue(_h.Store.AddCustomFolder(_h.Full("Custom"), _h.Guard).IsAllowed);
        var (scan, snapshot) = await ScanAsync();
        var id = snapshot.Items.Single().Rule.Id;

        var refused = await RunAsync(scan, snapshot, [id]);
        Assert.AreEqual(CleanupSkipReason.UserFilesNotConfirmed, refused.Items[0].SkipReason);
        Assert.IsTrue(File.Exists(file));

        var (scan2, fresh) = await ScanAsync();
        _h.Guard = _h.Guard with { WindowsDirectory = _h.Root };
        var rejected = await RunAsync(scan2, fresh, [id], confirmed: [id]);
        Assert.AreEqual(CleanupSkipReason.Unavailable, rejected.Items[0].SkipReason);
        Assert.IsTrue(File.Exists(file), "実行時の検査で拒否したフォルダーを削除した");

        _h.Guard = _h.Guard with { WindowsDirectory = @"C:\Windows" };
        var (scan3, again) = await ScanAsync();
        var done = await RunAsync(scan3, again, [id], confirmed: [id]);
        Assert.AreEqual(CleanupRunStatus.Succeeded, done.Items[0].Status);
        Assert.IsFalse(File.Exists(file));
        Assert.IsTrue(Directory.Exists(_h.Full("Custom")), "カスタムフォルダー自体を消した");
    }

    [TestMethod]
    public async Task 実行時に利用者スコープが使えなくなった規則は実行しない()
    {
        var file = _h.Write(@"Local\Test\Cache\a.tmp");
        _h.Rules.Add(CleanupHarness.MakeRule("app.test", [Cache()]));
        var (scan, snapshot) = await ScanAsync();
        _h.Scope = new InteractiveScopeResult(false, InteractiveScopeReason.DifferentUser);

        var result = await RunAsync(scan, snapshot, ["app.test"]);

        Assert.AreEqual(CleanupSkipReason.Unavailable, result.Items[0].SkipReason);
        Assert.IsTrue(File.Exists(file));
    }

    // ----- サービス補償 -----

    [TestMethod]
    public async Task サービスを止めて削除し必ず復帰する()
    {
        var file = _h.Write(@"Local\Test\Cache\a.tmp");
        _h.Rules.Add(CleanupHarness.MakeRule("app.test", [Cache()], services: ["wuauserv"]));
        var (scan, snapshot) = await ScanAsync();

        var result = await RunAsync(scan, snapshot, ["app.test"]);

        Assert.AreEqual(CleanupRunStatus.Succeeded, result.Items[0].Status);
        Assert.IsFalse(File.Exists(file));
        CollectionAssert.AreEqual(new[] { "suspend:wuauserv", "net.exe start \"wuauserv\"" }, _services.Events);
    }

    [TestMethod]
    public async Task サービスを停止できなければ何も削除せず復帰して失敗にする()
    {
        var file = _h.Write(@"Local\Test\Cache\a.tmp");
        _services.FailedToStop = ["wuauserv"];
        _h.Rules.Add(CleanupHarness.MakeRule("app.test", [Cache()], services: ["wuauserv"]));
        var (scan, snapshot) = await ScanAsync();

        var result = await RunAsync(scan, snapshot, ["app.test"]);

        Assert.AreEqual(CleanupRunStatus.Failed, result.Items[0].Status);
        Assert.IsTrue(File.Exists(file));
        Assert.IsTrue(_services.Events.Any(e => e.StartsWith("net.exe start", StringComparison.Ordinal)), "停止を確認できなくても復帰を実行する");
        Assert.IsFalse(result.IsComplete);
    }

    [TestMethod]
    public async Task 取消でもサービスを復帰し復帰に失敗したら取消に隠さず失敗にする()
    {
        var file = _h.Write(@"Local\Test\Cache\a.tmp");
        _h.Rules.Add(CleanupHarness.MakeRule("app.test", [Cache()], services: ["wuauserv"]));
        var (scan, snapshot) = await ScanAsync();
        using var cts = new CancellationTokenSource();
        _services.AfterSuspend = cts.Cancel;

        var canceled = await RunAsync(scan, snapshot, ["app.test"], ct: cts.Token);

        Assert.AreEqual(CleanupRunStatus.Canceled, canceled.Items[0].Status);
        Assert.IsTrue(canceled.Canceled);
        Assert.IsTrue(File.Exists(file));
        Assert.IsTrue(_services.Events.Any(e => e.StartsWith("net.exe start", StringComparison.Ordinal)));

        _services.StartSucceeds = false;
        var (scan2, fresh) = await ScanAsync();
        using var cts2 = new CancellationTokenSource();
        _services.AfterSuspend = cts2.Cancel;
        var failed = await RunAsync(scan2, fresh, ["app.test"], ct: cts2.Token);
        Assert.AreEqual(CleanupRunStatus.Failed, failed.Items[0].Status);
        Assert.IsTrue(failed.Items[0].Details.Any(d => CleanupMessage.KeyOf(d)?.StartsWith("Cleanup.Msg.ServiceResumeFailed", StringComparison.Ordinal) == true));
    }

    // ----- 部分失敗と取消の区別 -----

    [TestMethod]
    public async Task 部分失敗と失敗と取消を区別し取消後の規則は着手しない()
    {
        var first = new FakeHandler("dnsCache") { RunResult = new(CleanupHandlerStatus.Partial, 2, 20) { Details = ["一部失敗"] } };
        var second = new FakeHandler("networkUsage") { RunResult = new(CleanupHandlerStatus.Failed) { Details = ["失敗"] } };
        using var cts = new CancellationTokenSource();
        var third = new FakeHandler("ieHistory") { RunResult = new(CleanupHandlerStatus.Canceled), OnRun = cts.Cancel };
        var fourth = new FakeHandler("recycleBin");
        _h.Handlers[CleanupHandlerKind.DnsCache] = first;
        _h.Handlers[CleanupHandlerKind.NetworkUsage] = second;
        _h.Handlers[CleanupHandlerKind.IeHistory] = third;
        _h.Handlers[CleanupHandlerKind.RecycleBin] = fourth;
        _h.Rules.AddRange(
        [
            CleanupHarness.HandlerRule("win.one", CleanupHandlerKind.DnsCache),
            CleanupHarness.HandlerRule("win.two", CleanupHandlerKind.NetworkUsage),
            CleanupHarness.HandlerRule("win.three", CleanupHandlerKind.IeHistory),
            CleanupHarness.HandlerRule("win.four", CleanupHandlerKind.RecycleBin),
        ]);
        var (scan, snapshot) = await ScanAsync();

        var result = await RunAsync(scan, snapshot, ["win.one", "win.two", "win.three", "win.four"], ct: cts.Token);

        CollectionAssert.AreEqual(
            new[] { CleanupRunStatus.Partial, CleanupRunStatus.Failed, CleanupRunStatus.Canceled, CleanupRunStatus.NotRun },
            result.Items.Select(i => i.Status).ToArray());
        Assert.AreEqual(2, result.Items[0].Items);
        Assert.IsTrue(result.Canceled);
        Assert.IsFalse(result.IsComplete);
        Assert.AreEqual(0, fourth.RunCalls);
    }

    [TestMethod]
    public async Task 取消されていない部分失敗は取消として表示しない()
    {
        _h.Handlers[CleanupHandlerKind.DnsCache] = new FakeHandler("dnsCache") { RunResult = new(CleanupHandlerStatus.Partial, 1, 1) };
        _h.Rules.Add(CleanupHarness.HandlerRule("win.one", CleanupHandlerKind.DnsCache));
        var (scan, snapshot) = await ScanAsync();

        var result = await RunAsync(scan, snapshot, ["win.one"]);

        Assert.IsFalse(result.Canceled);
        Assert.AreEqual(CleanupRunStatus.Partial, result.Items[0].Status);
        Assert.IsFalse(result.IsComplete);
    }

    // ----- プロセスの 2 段階同意 -----

    private CleanupRule BrowserRule(CleanupCategory category = CleanupCategory.BrowserCache) =>
        CleanupHarness.MakeRule("app.browser", [Cache()], category: category, processes: ["chrome"]);

    [TestMethod]
    public async Task 起動中の製品は確認なしでは閉じず飛ばす()
    {
        var file = _h.Write(@"Local\Test\Cache\a.tmp");
        var chrome = new FakeProcess("chrome", 10);
        _h.Rules.Add(BrowserRule());
        var (scan, snapshot) = await ScanAsync();

        var result = await RunAsync(scan, snapshot, ["app.browser"], control: new FakeProcessControl(chrome));

        Assert.AreEqual(CleanupSkipReason.ProcessRunningDeclined, result.Items[0].SkipReason);
        Assert.AreEqual(0, chrome.CloseCalls);
        Assert.IsTrue(File.Exists(file));
    }

    [TestMethod]
    public async Task 終了の同意でWM_CLOSEが効けば強制終了を尋ねずに削除する()
    {
        var file = _h.Write(@"Local\Test\Cache\a.tmp");
        var chrome = new FakeProcess("chrome", 10);
        var prompts = new FakePrompts();
        _h.Rules.Add(BrowserRule());
        var (scan, snapshot) = await ScanAsync();

        var result = await RunAsync(scan, snapshot, ["app.browser"], prompts, control: new FakeProcessControl(chrome));

        Assert.AreEqual(CleanupRunStatus.Succeeded, result.Items[0].Status);
        Assert.IsFalse(File.Exists(file));
        Assert.HasCount(1, prompts.Asked);
        CollectionAssert.AreEqual(new[] { "chrome" }, prompts.Asked[0].ProcessNames.ToArray());
        Assert.IsEmpty(prompts.ForceAsked);
        Assert.IsFalse(chrome.Terminated);
    }

    [TestMethod]
    public async Task 残った製品は再確認に同意したときだけ強制終了する()
    {
        var file = _h.Write(@"Local\Test\Cache\a.tmp");
        var stubborn = new FakeProcess("chrome", 10) { ExitsOnClose = false };
        var prompts = new FakePrompts { Force = false };
        _h.Rules.Add(BrowserRule());
        var (scan, snapshot) = await ScanAsync();

        var declined = await RunAsync(scan, snapshot, ["app.browser"], prompts, control: new FakeProcessControl(stubborn));
        Assert.AreEqual(CleanupSkipReason.ForceTerminationDeclined, declined.Items[0].SkipReason);
        Assert.IsFalse(stubborn.Terminated);
        Assert.IsTrue(File.Exists(file));

        prompts.Force = true;
        var (scan2, fresh) = await ScanAsync();
        var approved = await RunAsync(scan2, fresh, ["app.browser"], prompts, control: new FakeProcessControl(stubborn));
        Assert.IsTrue(stubborn.Terminated);
        Assert.AreEqual(CleanupRunStatus.Succeeded, approved.Items[0].Status);
        Assert.IsFalse(File.Exists(file));
    }

    [TestMethod]
    public async Task ブラウザー以外は閉じなければ強制終了を尋ねず飛ばす()
    {
        var file = _h.Write(@"Local\Test\Cache\a.tmp");
        var stubborn = new FakeProcess("chrome", 10) { ExitsOnClose = false };
        var prompts = new FakePrompts { Force = true };
        _h.Rules.Add(BrowserRule(CleanupCategory.Apps));
        var (scan, snapshot) = await ScanAsync();

        var result = await RunAsync(scan, snapshot, ["app.browser"], prompts, control: new FakeProcessControl(stubborn));

        Assert.AreEqual(CleanupSkipReason.ProcessStillRunning, result.Items[0].SkipReason);
        Assert.IsEmpty(prompts.ForceAsked);
        Assert.IsFalse(stubborn.Terminated);
        Assert.IsTrue(File.Exists(file));
    }

    [TestMethod]
    public async Task 項目を飛ばす選択はその規則だけ飛ばし中止は残りも実行しない()
    {
        _h.Write(@"Local\Test\Cache\a.tmp");
        var other = _h.Write(@"Local\Test\Other\b.tmp");
        var chrome = new FakeProcess("chrome", 10);
        var prompts = new FakePrompts { Choice = CleanupProcessChoice.SkipRule };
        _h.Rules.AddRange([BrowserRule(), CleanupHarness.MakeRule("app.other", [Cache(@"%LOCALAPPDATA%\Test\Other")])]);
        var (scan, snapshot) = await ScanAsync();

        var skipped = await RunAsync(scan, snapshot, ["app.browser", "app.other"], prompts, control: new FakeProcessControl(chrome));
        Assert.AreEqual(CleanupSkipReason.ProcessRunningDeclined, skipped.Items[0].SkipReason);
        Assert.AreEqual(CleanupRunStatus.Succeeded, skipped.Items[1].Status);
        Assert.IsFalse(File.Exists(other));
        Assert.AreEqual(0, chrome.CloseCalls);

        _h.Write(@"Local\Test\Other\b.tmp");
        _h.Write(@"Local\Test\Cache\a.tmp");
        prompts.Choice = CleanupProcessChoice.Abort;
        var (scan2, fresh) = await ScanAsync();
        var aborted = await RunAsync(scan2, fresh, ["app.browser", "app.other"], prompts, control: new FakeProcessControl(chrome));
        Assert.IsTrue(aborted.Canceled);
        Assert.AreEqual(CleanupRunStatus.NotRun, aborted.Items[0].Status);
        Assert.AreEqual(CleanupRunStatus.NotRun, aborted.Items[1].Status);
        Assert.IsTrue(File.Exists(other));
    }

    // ----- レジストリ・INI・DB -----

    [TestMethod]
    public async Task レジストリとINIとDBを固定操作で実行しCookie除外ドメインを渡す()
    {
        var app = _h.RegistryHive.Path("Software", "Vendor", "App");
        app.Values.UnionWith(["Mru1", "Other"]);
        var ini = _h.Write(@"Roaming\App\app.ini", "[Recent]\r\nFile1=a\r\n[Other]\r\nKeep=1\r\n");
        var db = _h.Write(@"Roaming\Mozilla\Firefox\Profiles\a.default\cookies.sqlite");
        _h.Probe.Registry = true;
        _h.Store.AddExclusion(CleanupExclusionKind.CookieDomain, "example.com");
        _h.Rules.AddRange(
        [
            CleanupHarness.MakeRule("app.registry", [new CleanupRuleTarget { Type = CleanupTargetType.RegistryValues, Key = @"HKCU\Software\Vendor\App", Values = ["Mru*"] }], detect: @"%APPDATA%"),
            CleanupHarness.MakeRule("app.ini", [new CleanupRuleTarget { Type = CleanupTargetType.IniValues, Path = @"%APPDATA%\App\app.ini", IniKeys = [new CleanupIniKey { Section = "Recent", Key = "File*" }] }], detect: @"%APPDATA%"),
            CleanupHarness.MakeRule(
                "app.cookies",
                [new CleanupRuleTarget { Type = CleanupTargetType.Sqlite, Path = @"%APPDATA%\Mozilla\Firefox\Profiles\*\cookies.sqlite", Operation = CleanupSqliteOperation.GeckoCookies }],
                detect: @"%APPDATA%",
                category: CleanupCategory.Cookies,
                impact: CleanupImpact.Signout,
                recommended: false),
        ]);
        var (scan, snapshot) = await ScanAsync();

        var result = await RunAsync(scan, snapshot, ["app.registry", "app.ini", "app.cookies"]);

        Assert.IsTrue(result.Items.All(i => i.Status == CleanupRunStatus.Succeeded), string.Join(",", result.Items.Select(i => i.Status)));
        CollectionAssert.AreEquivalent(new[] { "Other" }, app.Values.ToArray());
        Assert.IsFalse(File.ReadAllText(ini).Contains("File1", StringComparison.Ordinal));
        StringAssert.Contains(File.ReadAllText(ini), "Keep=1");
        Assert.AreEqual(db, _h.Databases.Cleaned.Single().Path);
        CollectionAssert.AreEqual(new[] { "example.com" }, _h.Databases.Cleaned.Single().Domains);
    }

    [TestMethod]
    public async Task DBの操作が完了しなければ成功として表示しない()
    {
        _h.Write(@"Roaming\Mozilla\Firefox\Profiles\a.default\cookies.sqlite");
        _h.Databases.CleanResult = DatabaseCleanResult.Partial("ロック中");
        _h.Rules.Add(CleanupHarness.MakeRule(
            "app.cookies",
            [new CleanupRuleTarget { Type = CleanupTargetType.Sqlite, Path = @"%APPDATA%\Mozilla\Firefox\Profiles\*\cookies.sqlite", Operation = CleanupSqliteOperation.GeckoCookies }],
            detect: @"%APPDATA%",
            category: CleanupCategory.Cookies,
            impact: CleanupImpact.Signout,
            recommended: false));
        var (scan, snapshot) = await ScanAsync();

        var result = await RunAsync(scan, snapshot, ["app.cookies"]);

        Assert.AreEqual(CleanupRunStatus.Failed, result.Items[0].Status);
        Assert.IsTrue(result.Items[0].Details.Any(d => d.Contains("ロック中", StringComparison.Ordinal)));
        Assert.IsFalse(result.IsComplete);
    }

    [TestMethod]
    public async Task 実行結果のテキスト書き出しは状態と理由を含む()
    {
        _h.Write(@"Local\Test\Cache\a.tmp");
        _h.Rules.Add(CleanupHarness.MakeRule("app.test", [Cache()]));
        var (scan, snapshot) = await ScanAsync();
        var result = await RunAsync(scan, snapshot, ["app.test", "app.none"]);

        var text = CleanupResultsText.FromRun(result);

        StringAssert.Contains(text, "Succeeded");
        StringAssert.Contains(text, "NotScanned");
        StringAssert.Contains(text, "Incomplete");
    }
}
