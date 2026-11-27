using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows.Cleanup;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class RestorePointServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class QueueExecutor(params object[] steps) : ICommandExecutor
    {
        private readonly Queue<object> _steps = new(steps);

        public int Calls { get; private set; }

        public Task<CommandExecutionResult> RunAsync(
            string fileName,
            string arguments,
            CancellationToken ct = default,
            IProgress<string>? onOutputLine = null,
            TimeSpan? timeout = null)
        {
            Calls++;
            var step = _steps.Dequeue();
            return step is Exception ex ? Task.FromException<CommandExecutionResult>(ex) : Task.FromResult((CommandExecutionResult)step);
        }
    }

    private static CommandExecutionResult Ok(string output) => new(true, "ps", 0, output, string.Empty);

    private static CommandExecutionResult Exit(int code) => new(false, "ps", code, string.Empty, "err");

    private static string Query(bool enabled, params (long Seq, DateTimeOffset At)[] points)
    {
        var lines = new List<string> { enabled ? "L4T_PROTECTION=Enabled" : "L4T_PROTECTION=Disabled" };
        lines.AddRange(points.Select(p => $"L4T_POINT={p.Seq}|{p.At:o}|desc"));
        lines.Add("L4T_END");
        return string.Join("\r\n", lines);
    }

    private static RestorePointService Service(QueueExecutor executor) => new(executor, new FakeTime());

    [TestMethod]
    public async Task 連番が増えれば作成済みとして返す()
    {
        var executor = new QueueExecutor(
            Ok(Query(true, (5, Now.AddDays(-3)))),
            Ok("L4T_RETURN=0"),
            Ok(Query(true, (5, Now.AddDays(-3)), (6, Now))));

        var result = await Service(executor).CreateAsync("Lumin4ti");

        Assert.AreEqual(RestorePointOutcome.Created, result.Outcome);
        Assert.AreEqual(6, result.Point!.SequenceNumber);
    }

    [TestMethod]
    public async Task 連番が増えず24時間以内の既存点があれば利用する()
    {
        var executor = new QueueExecutor(
            Ok(Query(true, (5, Now.AddHours(-2)))),
            Ok("L4T_RETURN=0"),
            Ok(Query(true, (5, Now.AddHours(-2)))));

        var result = await Service(executor).CreateAsync("Lumin4ti");

        Assert.AreEqual(RestorePointOutcome.ReusedExisting, result.Outcome);
        Assert.IsTrue(result.IsUsable);
        Assert.AreEqual(5, result.Point!.SequenceNumber);
    }

    [TestMethod]
    public async Task 連番が増えず既存点も古ければ失敗する()
    {
        var executor = new QueueExecutor(
            Ok(Query(true, (5, Now.AddHours(-25)))),
            Ok("L4T_RETURN=0"),
            Ok(Query(true, (5, Now.AddHours(-25)))));

        var result = await Service(executor).CreateAsync("Lumin4ti");

        Assert.AreEqual(RestorePointOutcome.Failed, result.Outcome);
        Assert.IsFalse(result.IsUsable);
    }

    [TestMethod]
    public async Task 保護が無効なら作成を呼ばず区別して返す()
    {
        var executor = new QueueExecutor(Ok(Query(false)));

        var result = await Service(executor).CreateAsync("Lumin4ti");

        Assert.AreEqual(RestorePointOutcome.ProtectionDisabled, result.Outcome);
        Assert.AreEqual(1, executor.Calls);
    }

    [TestMethod]
    public async Task 作成の戻り値が非0なら連番が増えていても失敗する()
    {
        var executor = new QueueExecutor(
            Ok(Query(true, (5, Now.AddHours(-2)))),
            Ok("L4T_RETURN=1"));

        var result = await Service(executor).CreateAsync("Lumin4ti");

        Assert.AreEqual(RestorePointOutcome.Failed, result.Outcome);
        Assert.AreEqual(2, executor.Calls);
    }

    [TestMethod]
    public async Task 作成の終了コードが非0なら失敗する()
    {
        var executor = new QueueExecutor(Ok(Query(true)), Exit(1));

        var result = await Service(executor).CreateAsync("Lumin4ti");

        Assert.AreEqual(RestorePointOutcome.Failed, result.Outcome);
    }

    [TestMethod]
    public async Task 作成出力が解釈できなければ失敗する()
    {
        var executor = new QueueExecutor(Ok(Query(true)), Ok("done"));

        var result = await Service(executor).CreateAsync("Lumin4ti");

        Assert.AreEqual(RestorePointOutcome.Failed, result.Outcome);
    }

    [TestMethod]
    public async Task 取消は成功にならない()
    {
        var executor = new QueueExecutor(Ok(Query(true)), new OperationCanceledException());

        var result = await Service(executor).CreateAsync("Lumin4ti");

        Assert.AreEqual(RestorePointOutcome.Cancelled, result.Outcome);
        Assert.IsFalse(result.IsUsable);
    }

    [TestMethod]
    public async Task 時間超過で非0終了した状態確認は失敗にする()
    {
        var executor = new QueueExecutor(Exit(-1));

        var result = await Service(executor).CreateAsync("Lumin4ti");

        Assert.AreEqual(RestorePointOutcome.Failed, result.Outcome);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("L4T_PROTECTION=Enabled")]
    [DataRow("L4T_PROTECTION=Enabled\r\nL4T_PROTECTION=Disabled\r\nL4T_END")]
    [DataRow("L4T_PROTECTION=Maybe\r\nL4T_END")]
    [DataRow("L4T_PROTECTION=Enabled\r\nL4T_POINT=x|y|z\r\nL4T_END")]
    [DataRow("L4T_PROTECTION=Enabled\r\nL4T_POINT=-1|2026-01-01T00:00:00Z|d\r\nL4T_END")]
    [DataRow("L4T_PROTECTION=Enabled\r\nL4T_UNKNOWN=1\r\nL4T_END")]
    public void 不完全または不正な状態出力は解釈不能にする(string output)
    {
        var snapshot = RestorePointService.ParseSnapshot(Ok(output));

        Assert.IsNotNull(snapshot.Error);
    }

    [TestMethod]
    public void 状態出力のうち関係ない行は無視する()
    {
        var snapshot = RestorePointService.ParseSnapshot(
            Ok("WARNING: noise\r\nL4T_PROTECTION=Enabled\r\nL4T_POINT=7|2026-11-25T01:00:00.0000000Z|a|b\r\nL4T_END"));

        Assert.IsNull(snapshot.Error);
        Assert.IsTrue(snapshot.ProtectionEnabled);
        Assert.AreEqual("a|b", snapshot.Points[0].Description);
    }

    [TestMethod]
    [DataRow("L4T_RETURN=0\r\nL4T_RETURN=0")]
    [DataRow("L4T_RETURN=abc")]
    [DataRow("L4T_RETURN=-1")]
    [DataRow("")]
    public void 戻り値が重複または不正なら解釈不能にする(string output)
    {
        Assert.IsNull(RestorePointService.ParseReturnValue(Ok(output)));
    }

    [TestMethod]
    public async Task 保護の有効化は再照合で有効を確認できたときだけ成功する()
    {
        var ok = await Service(new QueueExecutor(Ok(string.Empty), Ok(Query(true)))).EnableProtectionAsync();
        var stillOff = await Service(new QueueExecutor(Ok(string.Empty), Ok(Query(false)))).EnableProtectionAsync();
        var failed = await Service(new QueueExecutor(Exit(1))).EnableProtectionAsync();

        Assert.IsTrue(ok.Enabled);
        Assert.IsFalse(stillOff.Enabled);
        Assert.IsFalse(failed.Enabled);
    }
}
