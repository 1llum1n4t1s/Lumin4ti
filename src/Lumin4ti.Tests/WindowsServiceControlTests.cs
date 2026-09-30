using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows;

namespace Lumin4ti.Tests;

/// <summary>
/// SuspendAsync は停止したサービスの一覧 (再開手段) を呼び出し側へ必ず返す必要がある。
/// ここで例外が出ると FileCleanupAction の finally が ResumeAsync を呼べず、
/// Windows Update・検索・フォントキャッシュ等が停止したまま残る。
/// </summary>
[TestClass]
public sealed class WindowsServiceControlTests
{
    [TestMethod]
    [DataRow(WindowsServiceState.Running, WindowsServiceState.Stopped, true, false)]
    [DataRow(WindowsServiceState.Running, WindowsServiceState.Stopped, false, false)]
    [DataRow(WindowsServiceState.Running, WindowsServiceState.Running, true, true)]
    [DataRow(WindowsServiceState.Running, WindowsServiceState.Transitioning, true, true)]
    [DataRow(WindowsServiceState.Running, null, true, true)]
    [DataRow(null, null, true, true)]
    [DataRow(WindowsServiceState.Transitioning, null, true, true)]
    [DataRow(WindowsServiceState.Stopped, null, true, false)]
    [DataRow(WindowsServiceState.NotInstalled, null, true, false)]
    public async Task 停止要求と実状態を照合して未確認のまま掃除を許可しない(
        WindowsServiceState? initialState, WindowsServiceState? finalState, bool success, bool failed)
    {
        const string service = "WSearch";
        using var cancellation = new CancellationTokenSource();
        var executor = new RecordingExecutor((_, _, _) => Result(success));
        var states = new Queue<WindowsServiceState?>([initialState, finalState]);

        var suspension = await WindowsServiceControl.SuspendAsync(
            executor,
            [service],
            progress: null,
            cancellation.Token,
            _ => states.Dequeue());

        var requested = initialState is WindowsServiceState.Running;
        Assert.HasCount(requested ? 1 : 0, executor.Invocations);
        if (requested)
        {
            Assert.IsFalse(executor.Invocations[0].Token.CanBeCanceled);
            Assert.AreEqual(WindowsServiceControl.ServiceStopTimeout, executor.Invocations[0].Timeout);
        }
        CollectionAssert.AreEqual(requested ? new[] { service } : [], suspension.Stopped.ToArray());
        CollectionAssert.AreEqual(failed ? new[] { service } : [], suspension.FailedToStop.ToArray());
    }

    [TestMethod]
    [DataRow(WindowsServiceState.Stopped, false)]
    [DataRow(null, false)]
    [DataRow(WindowsServiceState.Transitioning, true)]
    public async Task 停止中にキャンセルや例外が起きても要求済みサービスを再開対象として返す(
        WindowsServiceState? finalState, bool throwOnStop)
    {
        string[] services = ["WSearch", "wuauserv"];
        using var cancellation = new CancellationTokenSource();
        var executor = new RecordingExecutor((call, _, ct) =>
        {
            if (call == 1)
            {
                // 1 件目の停止コマンド実行中に利用者がキャンセルした状況を再現する。
                cancellation.Cancel();
            }

            // ProcessCommandExecutor は呼び出し元トークンがキャンセル済みなら OCE を伝播する。
            ct.ThrowIfCancellationRequested();
            if (throwOnStop)
            {
                throw new InvalidOperationException("停止要求後の失敗");
            }
            return Result(success: true);
        });

        var suspension = await WindowsServiceControl.SuspendAsync(
            executor,
            services,
            progress: null,
            cancellation.Token,
            _ => executor.Invocations.Count == 0 ? WindowsServiceState.Running : finalState);

        CollectionAssert.AreEqual(
            new[] { services[0] },
            suspension.Stopped.ToArray(),
            "停止できた 1 件目は再開対象として返す必要があります");
        Assert.HasCount(1, executor.Invocations, "2 件目はキャンセル後なので停止しません");
        Assert.HasCount(finalState is WindowsServiceState.Stopped && !throwOnStop ? 0 : 1, suspension.FailedToStop);
    }

    [TestMethod]
    [DataRow(WindowsServiceState.Stopped, 1)]
    [DataRow(WindowsServiceState.Running, 0)]
    public async Task 再開はキャンセル不能なトークンで実行し稼働済みなら開始を省略する(
        WindowsServiceState state, int expectedCalls)
    {
        var executor = new RecordingExecutor((_, _, _) => Result(success: true));
        var suspension = new ServiceSuspension(executor, ["WSearch"], [], _ => state, _ => Task.CompletedTask);

        var failures = await suspension.ResumeAsync();

        Assert.HasCount(0, failures);
        Assert.HasCount(expectedCalls, executor.Invocations);
        if (expectedCalls > 0)
        {
            StringAssert.StartsWith(executor.Invocations[0].Arguments, "start");
            Assert.IsFalse(executor.Invocations[0].Token.CanBeCanceled);
            Assert.AreEqual(WindowsServiceControl.ServiceStartTimeout, executor.Invocations[0].Timeout);
        }
    }

    [TestMethod]
    public async Task 再開要求が失敗してもSCMの自動回復で稼働すれば成功とする()
    {
        var states = new Queue<WindowsServiceState?>(
            [WindowsServiceState.Stopped, WindowsServiceState.Stopped, WindowsServiceState.Transitioning, WindowsServiceState.Running]);
        var delays = new List<TimeSpan>();
        var executor = new RecordingExecutor((_, _, _) => Result(success: false));
        var suspension = new ServiceSuspension(
            executor,
            stopped: ["WSearch"],
            failedToStop: [],
            _ => states.Dequeue(),
            delay =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var failures = await suspension.ResumeAsync();

        Assert.HasCount(0, failures);
        Assert.HasCount(1, executor.Invocations, "net start を再試行してはいけません");
        CollectionAssert.AreEqual(
            new[]
            {
                WindowsServiceControl.ServiceStartRecoveryPollInterval,
                WindowsServiceControl.ServiceStartRecoveryPollInterval,
            },
            delays.ToArray());
    }

    [TestMethod]
    public async Task 再開要求失敗後に停止したままなら上限で失敗する()
    {
        var queryCount = 0;
        var elapsed = TimeSpan.Zero;
        var executor = new RecordingExecutor((_, _, _) => Result(success: false));
        var suspension = new ServiceSuspension(
            executor,
            stopped: ["WSearch"],
            failedToStop: [],
            _ =>
            {
                queryCount++;
                return WindowsServiceState.Stopped;
            },
            delay =>
            {
                elapsed += delay;
                return Task.CompletedTask;
            });

        var failures = await suspension.ResumeAsync();

        CollectionAssert.AreEqual(new[] { "WSearch" }, failures.ToArray());
        Assert.AreEqual(WindowsServiceControl.ServiceStartRecoveryTimeout, elapsed);
        Assert.AreEqual(32, queryCount, "開始前の確認と初回・2秒間隔の確認を含め、60秒で打ち切ります");
        Assert.HasCount(1, executor.Invocations, "net start を再試行してはいけません");
    }

    [TestMethod]
    public async Task 再開要求失敗後に状態を照会できなければ直ちに失敗する()
    {
        var delayCalled = false;
        var executor = new RecordingExecutor((_, _, _) => Result(success: false));
        var suspension = new ServiceSuspension(
            executor,
            stopped: ["WSearch"],
            failedToStop: [],
            _ => null,
            _ =>
            {
                delayCalled = true;
                return Task.CompletedTask;
            });

        var failures = await suspension.ResumeAsync();

        CollectionAssert.AreEqual(new[] { "WSearch" }, failures.ToArray());
        Assert.IsFalse(delayCalled);
        Assert.HasCount(1, executor.Invocations);
    }

    private static CommandExecutionResult Result(bool success) =>
        new(success, "net.exe", success ? 0 : 1, string.Empty, string.Empty);

    private sealed record Invocation(string Arguments, CancellationToken Token, TimeSpan? Timeout);

    private sealed class RecordingExecutor(
        Func<int, string, CancellationToken, CommandExecutionResult> callback) : ICommandExecutor
    {
        private int _callCount;

        public List<Invocation> Invocations { get; } = [];

        public Task<CommandExecutionResult> RunAsync(
            string fileName,
            string arguments,
            CancellationToken ct = default,
            IProgress<string>? onOutputLine = null,
            TimeSpan? timeout = null)
        {
            var call = Interlocked.Increment(ref _callCount);
            Invocations.Add(new Invocation(arguments, ct, timeout));
            return Task.FromResult(callback(call, arguments, ct));
        }
    }
}
