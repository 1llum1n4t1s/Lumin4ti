using System.Security.Principal;
using Lumin4ti.Core.Services.Windows.Cleanup;

namespace Lumin4ti.Tests;

/// <summary>実プロセスは開かず終了もしない。OS 境界 (<see cref="IProcessControl"/>) を偽物に差し替えて検証する。</summary>
[TestClass]
public sealed class RunningProcessGuardTests
{
    private static readonly SecurityIdentifier Me = new("S-1-5-21-1-2-3-1001");
    private static readonly SecurityIdentifier Other = new("S-1-5-21-1-2-3-1002");
    private const uint Session = 1;
    private const int SelfPid = 100;

    private sealed class FakeProcess(int pid, string name)
    {
        public int Pid { get; } = pid;

        public string Name { get; set; } = name;

        public uint SessionId { get; set; } = Session;

        public SecurityIdentifier? Sid { get; set; } = Me;

        public long Start { get; set; } = 1000 + pid;

        public bool Exited { get; set; }

        public bool ExitsOnClose { get; set; }

        public bool TerminateSucceeds { get; set; } = true;

        public bool OpenDenied { get; set; }

        public int CloseRequests { get; private set; }

        public int TerminateCalls { get; private set; }

        public int OpenLeases { get; set; }

        public void OnClose()
        {
            CloseRequests++;
            Exited |= ExitsOnClose;
        }

        public bool OnTerminate()
        {
            TerminateCalls++;
            Exited |= TerminateSucceeds;
            return TerminateSucceeds;
        }
    }

    private sealed class FakeLease(FakeProcess process) : IProcessLease
    {
        public int Pid => process.Pid;

        public string? ImageName => process.Name;

        public uint SessionId => process.SessionId;

        public SecurityIdentifier? UserSid => process.Sid;

        public long StartTime => process.Start;

        public bool HasExited() => process.Exited;

        public bool WaitForExit(TimeSpan timeout) => process.Exited;

        public int RequestClose()
        {
            process.OnClose();
            return 1;
        }

        public bool Terminate() => process.OnTerminate();

        public void Dispose() => process.OpenLeases--;
    }

    private sealed class FakeControl(params FakeProcess[] processes) : IProcessControl
    {
        public int CurrentPid => SelfPid;

        public uint CurrentSessionId => Session;

        public SecurityIdentifier? CurrentUserSid => Me;

        public int Delays { get; private set; }

        public IReadOnlyList<ProcessSnapshotEntry> Snapshot() =>
            [.. processes.Where(p => !p.Exited).Select(p => new ProcessSnapshotEntry(p.Pid, p.Name, p.SessionId))];

        public IProcessLease? Open(int pid)
        {
            var found = processes.FirstOrDefault(p => p.Pid == pid && !p.Exited && !p.OpenDenied);
            if (found is null)
            {
                return null;
            }

            found.OpenLeases++;
            return new FakeLease(found);
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays++;
            return Task.CompletedTask;
        }
    }

    private static CleanupRule Rule(CleanupCategory category, params string[] processes) =>
        new() { Id = "test", Category = category, Processes = [.. processes] };

    private static Func<IReadOnlyList<RunningProcess>, CancellationToken, Task<bool>> Confirm(bool answer, List<int> calls) =>
        (_, _) =>
        {
            calls.Add(1);
            return Task.FromResult(answer);
        };

    [TestMethod]
    public void Detect_は同一セッション同一利用者の一致する名前だけを返し_Explorerと自プロセスを除く()
    {
        var chrome = new FakeProcess(200, "chrome");
        var otherSession = new FakeProcess(201, "chrome") { SessionId = 2 };
        var otherUser = new FakeProcess(202, "chrome") { Sid = Other };
        var explorer = new FakeProcess(203, "explorer");
        var self = new FakeProcess(SelfPid, "chrome");
        var unrelated = new FakeProcess(204, "notepad");
        var guard = new RunningProcessGuard(new FakeControl(chrome, otherSession, otherUser, explorer, self, unrelated));

        var detection = guard.Detect(Rule(CleanupCategory.BrowserCache, "chrome", "explorer"));

        CollectionAssert.AreEqual(new[] { 200 }, detection.Verified.Select(p => p.Pid).ToArray());
        Assert.AreEqual(0, detection.UnverifiableCount);
        Assert.IsTrue(detection.IsRunning);
    }

    [TestMethod]
    public void Detect_は利用者を確認できないプロセスを数える()
    {
        var denied = new FakeProcess(200, "chrome") { OpenDenied = true };
        var guard = new RunningProcessGuard(new FakeControl(denied));

        var detection = guard.Detect(Rule(CleanupCategory.BrowserCache, "chrome"));

        Assert.AreEqual(0, detection.Verified.Count);
        Assert.AreEqual(1, detection.UnverifiableCount);
    }

    [TestMethod]
    public async Task CloseAsync_はWM_CLOSEで終われば強制終了も再確認もしない()
    {
        var chrome = new FakeProcess(200, "chrome") { ExitsOnClose = true };
        var guard = new RunningProcessGuard(new FakeControl(chrome));
        var rule = Rule(CleanupCategory.BrowserCache, "chrome");
        var calls = new List<int>();

        var result = await guard.CloseAsync(rule, guard.Detect(rule), Confirm(true, calls), CancellationToken.None);

        Assert.AreEqual(ProcessGuardStatus.Clear, result.Status);
        Assert.AreEqual(1, chrome.CloseRequests);
        Assert.AreEqual(0, chrome.TerminateCalls);
        Assert.AreEqual(0, calls.Count);
        Assert.AreEqual(0, chrome.OpenLeases);
    }

    [TestMethod]
    public async Task CloseAsync_は15秒待って残れば再確認を求め_拒否されたら終了せずSkippedにする()
    {
        var chrome = new FakeProcess(200, "chrome");
        var control = new FakeControl(chrome);
        var guard = new RunningProcessGuard(control);
        var rule = Rule(CleanupCategory.Cookies, "chrome");
        var calls = new List<int>();

        var result = await guard.CloseAsync(rule, guard.Detect(rule), Confirm(false, calls), CancellationToken.None);

        Assert.AreEqual(ProcessGuardStatus.Skipped, result.Status);
        Assert.AreEqual(ProcessGuardSkipReason.ForceDeclined, result.Reason);
        Assert.AreEqual(1, calls.Count);
        Assert.AreEqual(0, chrome.TerminateCalls);
        Assert.AreEqual((int)(RunningProcessGuard.CloseWait / RunningProcessGuard.PollInterval), control.Delays);
        CollectionAssert.AreEqual(new[] { 200 }, result.Remaining.Select(p => p.Pid).ToArray());
    }

    [TestMethod]
    public async Task CloseAsync_は再確認に同意があればブラウザー分類に限り終了する()
    {
        var chrome = new FakeProcess(200, "chrome");
        var guard = new RunningProcessGuard(new FakeControl(chrome));
        var rule = Rule(CleanupCategory.BrowserTrace, "chrome");
        var calls = new List<int>();

        var result = await guard.CloseAsync(rule, guard.Detect(rule), Confirm(true, calls), CancellationToken.None);

        Assert.AreEqual(ProcessGuardStatus.Clear, result.Status);
        Assert.AreEqual(1, chrome.TerminateCalls);
    }

    [TestMethod]
    public async Task CloseAsync_はブラウザー以外の分類では強制終了せず再確認も求めない()
    {
        var app = new FakeProcess(200, "someapp");
        var guard = new RunningProcessGuard(new FakeControl(app));
        var rule = Rule(CleanupCategory.Apps, "someapp");
        var calls = new List<int>();

        var result = await guard.CloseAsync(rule, guard.Detect(rule), Confirm(true, calls), CancellationToken.None);

        Assert.AreEqual(ProcessGuardSkipReason.ForceNotAllowed, result.Reason);
        Assert.AreEqual(0, calls.Count);
        Assert.AreEqual(0, app.TerminateCalls);
    }

    [TestMethod]
    public async Task CloseAsync_は再確認の後にSIDが変わっていたら終了しない()
    {
        var chrome = new FakeProcess(200, "chrome");
        var guard = new RunningProcessGuard(new FakeControl(chrome));
        var rule = Rule(CleanupCategory.BrowserCache, "chrome");

        var result = await guard.CloseAsync(
            rule,
            guard.Detect(rule),
            (_, _) =>
            {
                chrome.Sid = Other;
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.AreEqual(ProcessGuardSkipReason.IdentityMismatch, result.Reason);
        Assert.AreEqual(0, chrome.TerminateCalls);
    }

    [TestMethod]
    public async Task CloseAsync_は開始時刻が変わったPID再利用のプロセスには何もしない()
    {
        var chrome = new FakeProcess(200, "chrome");
        var guard = new RunningProcessGuard(new FakeControl(chrome));
        var rule = Rule(CleanupCategory.BrowserCache, "chrome");
        var detection = guard.Detect(rule);
        chrome.Start += 1;
        var calls = new List<int>();

        var result = await guard.CloseAsync(rule, detection, Confirm(true, calls), CancellationToken.None);

        Assert.AreEqual(ProcessGuardStatus.Clear, result.Status);
        Assert.AreEqual(0, chrome.CloseRequests);
        Assert.AreEqual(0, chrome.TerminateCalls);
    }

    [TestMethod]
    public async Task CloseAsync_は名前が変わっていたら閉じずIdentityMismatchにする()
    {
        var chrome = new FakeProcess(200, "chrome");
        var guard = new RunningProcessGuard(new FakeControl(chrome));
        var rule = Rule(CleanupCategory.BrowserCache, "chrome");
        var detection = guard.Detect(rule);
        chrome.Name = "explorer";

        var result = await guard.CloseAsync(rule, detection, Confirm(true, []), CancellationToken.None);

        Assert.AreEqual(ProcessGuardSkipReason.IdentityMismatch, result.Reason);
        Assert.AreEqual(0, chrome.CloseRequests);
        Assert.AreEqual(0, chrome.TerminateCalls);
    }

    [TestMethod]
    public async Task CloseAsync_はTerminateProcessの拒否をSkippedにして成功としない()
    {
        var chrome = new FakeProcess(200, "chrome") { TerminateSucceeds = false };
        var guard = new RunningProcessGuard(new FakeControl(chrome));
        var rule = Rule(CleanupCategory.BrowserCache, "chrome");

        var result = await guard.CloseAsync(rule, guard.Detect(rule), Confirm(true, []), CancellationToken.None);

        Assert.AreEqual(ProcessGuardStatus.Skipped, result.Status);
        Assert.AreEqual(ProcessGuardSkipReason.TerminateFailed, result.Reason);
        Assert.AreEqual(1, result.Remaining.Count);
    }

    [TestMethod]
    public async Task CloseAsync_は利用者を確認できないプロセスがあればWM_CLOSEも送らずSkippedにする()
    {
        var visible = new FakeProcess(200, "chrome");
        var denied = new FakeProcess(201, "chrome") { OpenDenied = true };
        var guard = new RunningProcessGuard(new FakeControl(visible, denied));
        var rule = Rule(CleanupCategory.BrowserCache, "chrome");

        var result = await guard.CloseAsync(rule, guard.Detect(rule), Confirm(true, []), CancellationToken.None);

        Assert.AreEqual(ProcessGuardSkipReason.Unverifiable, result.Reason);
        Assert.AreEqual(0, visible.CloseRequests);
    }

    [TestMethod]
    public async Task CloseAsync_はキャンセルされたらハンドルを閉じて中断する()
    {
        var chrome = new FakeProcess(200, "chrome");
        var guard = new RunningProcessGuard(new FakeControl(chrome));
        var rule = Rule(CleanupCategory.BrowserCache, "chrome");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => guard.CloseAsync(rule, guard.Detect(rule), Confirm(true, []), cts.Token));

        Assert.AreEqual(0, chrome.TerminateCalls);
        Assert.AreEqual(0, chrome.OpenLeases);
    }
}
