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
    public async Task 停止要求はキャンセル不能なトークンと上限付きで実行する()
    {
        var service = RequireRunningServices(1)[0];
        using var cancellation = new CancellationTokenSource();
        var executor = new RecordingExecutor((_, _, _) => Result(success: true));

        var suspension = await WindowsServiceControl.SuspendAsync(
            executor,
            [service],
            progress: null,
            cancellation.Token);

        Assert.HasCount(1, executor.Invocations);
        Assert.IsFalse(
            executor.Invocations[0].Token.CanBeCanceled,
            "停止を途中で打ち切ると停止できたか確定せず、再開対象から漏れます");
        Assert.AreEqual(WindowsServiceControl.ServiceStopTimeout, executor.Invocations[0].Timeout);
        CollectionAssert.AreEqual(new[] { service }, suspension.Stopped.ToArray());
    }

    [TestMethod]
    public async Task 停止中にキャンセルされても停止済みサービスを再開対象として返す()
    {
        var services = RequireRunningServices(2);
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
            return Result(success: true);
        });

        var suspension = await WindowsServiceControl.SuspendAsync(
            executor,
            services,
            progress: null,
            cancellation.Token);

        CollectionAssert.AreEqual(
            new[] { services[0] },
            suspension.Stopped.ToArray(),
            "停止できた 1 件目は再開対象として返す必要があります");
        Assert.HasCount(1, executor.Invocations, "2 件目はキャンセル後なので停止しません");
        Assert.HasCount(0, suspension.FailedToStop);
    }

    [TestMethod]
    public async Task 再開はキャンセル不能なトークンで実行する()
    {
        var service = RequireRunningServices(1)[0];
        var executor = new RecordingExecutor((_, _, _) => Result(success: true));
        var suspension = await WindowsServiceControl.SuspendAsync(
            executor,
            [service],
            progress: null,
            CancellationToken.None);

        var failures = await suspension.ResumeAsync();

        Assert.HasCount(0, failures);
        Assert.HasCount(2, executor.Invocations);
        StringAssert.StartsWith(executor.Invocations[1].Arguments, "start");
        Assert.IsFalse(executor.Invocations[1].Token.CanBeCanceled);
    }

    /// <summary>
    /// 稼働中でないサービスは <see cref="WindowsServiceControl.SuspendAsync"/> が読み飛ばすため、
    /// 停止経路を通すには実際に Running のサービス名が要る。Windows で常時稼働の候補から選ぶ。
    /// </summary>
    private static string[] RequireRunningServices(int count)
    {
        string[] candidates = ["EventLog", "Schedule", "Dhcp", "Winmgmt", "RpcSs"];
        var running = candidates
            .Where(name => WindowsServiceControl.TryQueryState(name) is WindowsServiceState.Running)
            .Take(count)
            .ToArray();

        if (running.Length < count)
        {
            Assert.Inconclusive($"稼働中のサービスが {count} 件揃わないため停止経路を検証できません");
        }

        return running;
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
