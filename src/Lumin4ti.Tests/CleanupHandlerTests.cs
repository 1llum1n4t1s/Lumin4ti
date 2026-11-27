using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.Core.Services.Windows.Actions;
using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.Core.Services.Windows.Cleanup.Handlers;

namespace Lumin4ti.Tests;

/// <summary>
/// 特殊ハンドラーの実行結果の区分。OS 操作 (レジストリ・COM・サービス・ファイル) は全て偽の境界に差し替え、
/// 実機の履歴・サービス・ネットワーク使用量には触れない。
/// </summary>
[TestClass]
public sealed class CleanupHandlerTests
{
    [TestMethod]
    public async Task IE履歴は全工程が成功した場合だけ成功にし_キー間の件数は時刻値を数えない()
    {
        var store = new FakeIeStore { Removed = { [IeHistoryRegistryTarget.TypedUrls] = 3, [IeHistoryRegistryTarget.TypedUrlsTime] = 3, [IeHistoryRegistryTarget.IntelliFormsStorage1] = 2 } };

        var result = await new IeHistoryHandler(store).RunAsync(null, CancellationToken.None);

        Assert.AreEqual(CleanupHandlerStatus.Succeeded, result.Status);
        Assert.AreEqual(5, result.Items);
        Assert.AreEqual(1, store.ClearCalls);
        CollectionAssert.AreEqual(
            new[] { IeHistoryRegistryTarget.TypedUrls, IeHistoryRegistryTarget.TypedUrlsTime, IeHistoryRegistryTarget.IntelliFormsStorage1 },
            store.Deleted);
    }

    [TestMethod]
    public async Task IE履歴は一部の工程の失敗を部分成功にし_全工程の失敗を失敗にする()
    {
        var partial = new FakeIeStore { ClearHr = unchecked((int)0x80004005) };
        var failed = new FakeIeStore
        {
            ClearHr = unchecked((int)0x80004005),
            Errors = { [IeHistoryRegistryTarget.TypedUrls] = 5, [IeHistoryRegistryTarget.TypedUrlsTime] = 5, [IeHistoryRegistryTarget.IntelliFormsStorage1] = 1920 },
        };

        var partialResult = await new IeHistoryHandler(partial).RunAsync(null, CancellationToken.None);
        var failedResult = await new IeHistoryHandler(failed).RunAsync(null, CancellationToken.None);

        Assert.AreEqual(CleanupHandlerStatus.Partial, partialResult.Status);
        Assert.IsFalse(partialResult.IsSuccess);
        Assert.AreEqual(1, partialResult.Details.Count);
        Assert.AreEqual(CleanupHandlerStatus.Failed, failedResult.Status);
        Assert.AreEqual(4, failedResult.Details.Count);
    }

    [TestMethod]
    public async Task IE履歴は取消後の工程を実行せず_失敗が無ければ取消にする()
    {
        using var cts = new CancellationTokenSource();
        var store = new FakeIeStore { OnClear = cts.Cancel };

        var result = await new IeHistoryHandler(store).RunAsync(null, cts.Token);
        var before = await new IeHistoryHandler(new FakeIeStore()).RunAsync(null, cts.Token);

        Assert.AreEqual(CleanupHandlerStatus.Canceled, result.Status);
        Assert.AreEqual(0, store.Deleted.Count);
        Assert.AreEqual(CleanupHandlerStatus.Canceled, before.Status);
    }

    [TestMethod]
    public async Task IE履歴の走査は何も消さず値の数を返し_照会失敗は利用不可にする()
    {
        var store = new FakeIeStore { Counts = { [IeHistoryRegistryTarget.TypedUrls] = 4, [IeHistoryRegistryTarget.IntelliFormsStorage1] = 1 } };
        var broken = new FakeIeStore { CountError = 5 };

        var scan = await new IeHistoryHandler(store).ScanAsync(CancellationToken.None);
        var brokenScan = await new IeHistoryHandler(broken).ScanAsync(CancellationToken.None);

        Assert.AreEqual(CleanupHandlerScanState.Unmeasurable, scan.State);
        Assert.AreEqual(5, scan.Items);
        Assert.AreEqual(0, store.ClearCalls);
        Assert.AreEqual(0, store.Deleted.Count);
        Assert.AreEqual(CleanupHandlerScanState.Unavailable, brokenScan.State);
    }

    [TestMethod]
    public async Task ネットワーク使用量はDPSを停止確認してから削除し_成功しても復帰する()
    {
        var env = new NetworkEnv { Outcome = Outcome(files: 3, bytes: 300) };

        var result = await env.Handler().RunAsync(null, CancellationToken.None);

        Assert.AreEqual(CleanupHandlerStatus.Succeeded, result.Status);
        Assert.AreEqual(3, result.Items);
        Assert.AreEqual(300, result.Bytes);
        Assert.AreEqual(1, env.Store.DeleteCalls);
        Assert.AreEqual(1, env.StartCalls);
    }

    [TestMethod]
    public async Task ネットワーク使用量は停止を確認できなければ削除せず失敗にして復帰する()
    {
        var env = new NetworkEnv { FailedToStop = ["DPS"] };

        var result = await env.Handler().RunAsync(null, CancellationToken.None);

        Assert.AreEqual(CleanupHandlerStatus.Failed, result.Status);
        Assert.AreEqual(0, env.Store.DeleteCalls);
        Assert.AreEqual(1, env.StartCalls);
    }

    [TestMethod]
    public async Task ネットワーク使用量は削除の例外でも復帰し_失敗にする()
    {
        var env = new NetworkEnv { Throw = new IOException("locked") };

        var result = await env.Handler().RunAsync(null, CancellationToken.None);

        Assert.AreEqual(CleanupHandlerStatus.Failed, result.Status);
        Assert.IsTrue(result.Details.Any(d => d.Contains("locked", StringComparison.Ordinal)));
        Assert.AreEqual(1, env.StartCalls);
    }

    [TestMethod]
    public async Task ネットワーク使用量は停止後の取消でも削除せず復帰して取消にする()
    {
        using var cts = new CancellationTokenSource();
        var env = new NetworkEnv { AfterSuspend = cts.Cancel };

        var result = await env.Handler().RunAsync(null, cts.Token);

        Assert.AreEqual(CleanupHandlerStatus.Canceled, result.Status);
        Assert.AreEqual(0, env.Store.DeleteCalls);
        Assert.AreEqual(1, env.StartCalls);
    }

    [TestMethod]
    public async Task ネットワーク使用量はDPSを戻せなければ成功にも取消にもしない()
    {
        using var cts = new CancellationTokenSource();
        var deleted = new NetworkEnv { Outcome = Outcome(files: 2, bytes: 20), StartSucceeds = false };
        var canceled = new NetworkEnv { StartSucceeds = false, AfterSuspend = cts.Cancel };

        var deletedResult = await deleted.Handler().RunAsync(null, CancellationToken.None);
        var canceledResult = await canceled.Handler().RunAsync(null, cts.Token);

        Assert.AreEqual(CleanupHandlerStatus.Failed, deletedResult.Status);
        Assert.AreEqual(2, deletedResult.Items);
        Assert.IsTrue(deletedResult.Details.Any(d => d.Contains("DPS", StringComparison.Ordinal)));
        Assert.AreEqual(CleanupHandlerStatus.Failed, canceledResult.Status);
        Assert.IsFalse(canceledResult.IsSuccess);
    }

    [TestMethod]
    public async Task ネットワーク使用量は一部だけ消せた場合を部分成功にし_何も消せなければ失敗にする()
    {
        var partialOutcome = Outcome(files: 2, bytes: 20);
        partialOutcome.RecordBlocked(@"C:\Windows\System32\sru\SRUDB.dat", "locked");
        var failedOutcome = Outcome(files: 0, bytes: 0);
        failedOutcome.RecordBlocked(@"C:\Windows\System32\sru\SRUDB.dat", "locked");
        var rejectedOutcome = Outcome(files: 0, bytes: 0);
        rejectedOutcome.RejectedTargets.Add("sru (リンク非追従で開けませんでした)");
        var nothing = Outcome(files: 0, bytes: 0);

        var partial = await new NetworkEnv { Outcome = partialOutcome }.Handler().RunAsync(null, CancellationToken.None);
        var failed = await new NetworkEnv { Outcome = failedOutcome }.Handler().RunAsync(null, CancellationToken.None);
        var rejected = await new NetworkEnv { Outcome = rejectedOutcome }.Handler().RunAsync(null, CancellationToken.None);
        var empty = await new NetworkEnv { Outcome = nothing }.Handler().RunAsync(null, CancellationToken.None);

        Assert.AreEqual(CleanupHandlerStatus.Partial, partial.Status);
        Assert.AreEqual(CleanupHandlerStatus.Failed, failed.Status);
        Assert.AreEqual(CleanupHandlerStatus.Failed, rejected.Status);
        Assert.AreEqual(CleanupHandlerStatus.NothingToDo, empty.Status);
    }

    [TestMethod]
    public async Task ネットワーク使用量の走査はサービスを止めず_拒否と存在しない場合を区別する()
    {
        var found = new NetworkEnv { Scan = new CleanupScanResult { Files = 4, Bytes = 400 } };
        var rejected = new NetworkEnv { Scan = new CleanupScanResult { Rejection = "拒否" } };
        var missing = new NetworkEnv { Scan = new CleanupScanResult { Missing = true } };

        var foundScan = await found.Handler().ScanAsync(CancellationToken.None);
        var rejectedScan = await rejected.Handler().ScanAsync(CancellationToken.None);
        var missingScan = await missing.Handler().ScanAsync(CancellationToken.None);

        Assert.AreEqual(CleanupHandlerScanState.Found, foundScan.State);
        Assert.AreEqual(4, foundScan.Items);
        Assert.AreEqual(CleanupHandlerScanState.Unavailable, rejectedScan.State);
        Assert.AreEqual(CleanupHandlerScanState.Empty, missingScan.State);
        Assert.AreEqual(0, found.SuspendCalls);
        Assert.AreEqual(0, found.Store.DeleteCalls);
    }

    private static CleanupOutcome Outcome(long files, long bytes) =>
        new() { DeletedFiles = files, FreedBytes = bytes };

    private sealed class FakeIeStore : IIeHistoryStore
    {
        public Dictionary<IeHistoryRegistryTarget, int> Counts { get; } = [];
        public Dictionary<IeHistoryRegistryTarget, int> Removed { get; } = [];
        public Dictionary<IeHistoryRegistryTarget, int> Errors { get; } = [];
        public List<IeHistoryRegistryTarget> Deleted { get; } = [];
        public int ClearHr { get; init; }
        public int CountError { get; init; }
        public int ClearCalls { get; private set; }
        public Action? OnClear { get; init; }

        public int CountValues(IeHistoryRegistryTarget target, out int count)
        {
            count = Counts.GetValueOrDefault(target);
            return CountError;
        }

        public int DeleteValues(IeHistoryRegistryTarget target, out int removed)
        {
            Deleted.Add(target);
            removed = Removed.GetValueOrDefault(target);
            return Errors.GetValueOrDefault(target);
        }

        public int ClearUrlHistory()
        {
            ClearCalls++;
            OnClear?.Invoke();
            return ClearHr;
        }
    }

    private sealed class FakeNetworkStore(NetworkEnv env) : INetworkUsageStore
    {
        public int DeleteCalls { get; private set; }

        public CleanupScanResult Scan(CancellationToken ct) => env.Scan ?? new CleanupScanResult();

        public CleanupOutcome Delete(IProgress<string>? progress, CancellationToken ct)
        {
            DeleteCalls++;
            return env.Throw is null ? env.Outcome ?? new CleanupOutcome() : throw env.Throw;
        }
    }

    /// <summary>DPS の停止・復帰を偽の実行器と状態照会で再現する。</summary>
    private sealed class NetworkEnv : ICommandExecutor
    {
        public NetworkEnv() => Store = new FakeNetworkStore(this);

        public FakeNetworkStore Store { get; }
        public CleanupScanResult? Scan { get; init; }
        public CleanupOutcome? Outcome { get; init; }
        public Exception? Throw { get; init; }
        public IReadOnlyList<string> FailedToStop { get; init; } = [];
        public bool StartSucceeds { get; init; } = true;
        public Action? AfterSuspend { get; init; }
        public int StartCalls { get; private set; }
        public int SuspendCalls { get; private set; }

        public NetworkUsageHandler Handler() => new(Store, (names, _, _) =>
        {
            SuspendCalls++;
            var suspension = new ServiceSuspension(this, names, FailedToStop, _ => WindowsServiceState.Stopped, _ => Task.CompletedTask);
            AfterSuspend?.Invoke();
            return Task.FromResult(suspension);
        });

        public Task<CommandExecutionResult> RunAsync(
            string fileName, string arguments, CancellationToken ct = default, IProgress<string>? onOutputLine = null, TimeSpan? timeout = null)
        {
            StartCalls++;
            Assert.AreEqual("net.exe", fileName);
            Assert.AreEqual("start \"DPS\"", arguments);
            return Task.FromResult(new CommandExecutionResult(StartSucceeds, "net start", StartSucceeds ? 0 : 2, string.Empty, string.Empty));
        }
    }
}
