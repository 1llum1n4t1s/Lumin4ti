using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class MmAgentOperationApiChoiceTests
{
    /// <summary>コマンド文字列に応じた結果を返すスタブ。実行されたコマンドも記録する。</summary>
    private sealed class FakeExecutor(Func<string, CommandExecutionResult> respond) : ICommandExecutor
    {
        public List<string> Commands { get; } = [];

        public Task<CommandExecutionResult> RunAsync(
            string fileName,
            string arguments,
            CancellationToken ct = default,
            IProgress<string>? onOutputLine = null,
            TimeSpan? timeout = null)
        {
            Commands.Add(arguments);
            return Task.FromResult(respond(arguments));
        }
    }

    private static CommandExecutionResult Ok(string stdout = "") =>
        new(true, "cmd", 0, stdout, string.Empty);

    private static CommandExecutionResult Fail(string stderr) =>
        new(false, "cmd", 1, string.Empty, stderr);

    private static MmAgentOperationApiChoice CreateChoice(FakeExecutor executor) =>
        new(executor, new MmAgentStateProvider(executor));

    [TestMethod]
    public void 選択肢は記録ファイル数だけを持ち既定は512()
    {
        var choice = CreateChoice(new FakeExecutor(_ => Ok()));

        // 「無効」はアプリ起動プリフェッチを OFF にするのと同義で単独では選べないため、選択肢に持たない。
        CollectionAssert.AreEqual(
            new[] { "128", "256", "512", "1024" },
            choice.Options.Select(o => o.Value).ToArray());
        // 既定印と公開する既定値は同じ情報源から導出されること (片方だけ変えられない)
        Assert.AreEqual(
            MmAgentOperationApiChoice.DefaultValue,
            choice.Options.Single(o => o.IsDefault).Value);
    }

    [TestMethod]
    public async Task 無効化は行わない()
    {
        // 回帰防止: どの選択値でも Disable-MMAgent を実行しない。
        var executor = new FakeExecutor(_ => Ok("{\"OperationAPI\":true}"));

        _ = await CreateChoice(executor).SetSelectedValueAsync("128");

        Assert.IsFalse(executor.Commands.Any(c => c.Contains("Disable-MMAgent", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task 数値を選ぶとSetMMAgentで記録ファイル数を設定する()
    {
        var executor = new FakeExecutor(command =>
            command.Contains("Get-MMAgent | ConvertTo-Json", StringComparison.Ordinal)
                ? Ok("{\"OperationAPI\":true}")
                : Ok());

        var result = await CreateChoice(executor).SetSelectedValueAsync("256");

        Assert.AreEqual(MaintenanceActionStatus.Success, result.Status);
        Assert.IsTrue(executor.Commands.Any(c => c.Contains("Set-MMAgent -MaxOperationAPIFiles 256", StringComparison.Ordinal)));
        // 有効な状態から数値を選んだだけなので Enable は呼ばない
        Assert.IsFalse(executor.Commands.Any(c => c.Contains("Enable-MMAgent", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task 無効状態から数値を選ぶと機能を有効に戻してから設定する()
    {
        // Enable-MMAgent が通る環境: 有効化後の読み取りでは True を返す
        var enabled = false;
        FakeExecutor? executor = null;
        executor = new FakeExecutor(command =>
        {
            if (command.Contains("Enable-MMAgent", StringComparison.Ordinal))
            {
                enabled = true;
                return Ok();
            }

            return command.Contains("Get-MMAgent | ConvertTo-Json", StringComparison.Ordinal)
                ? Ok($"{{\"OperationAPI\":{(enabled ? "true" : "false")}}}")
                : Ok();
        });

        var result = await CreateChoice(executor).SetSelectedValueAsync("128");

        Assert.AreEqual(MaintenanceActionStatus.Success, result.Status);
        Assert.IsTrue(executor.Commands.Any(c => c.Contains("Enable-MMAgent -OperationAPI", StringComparison.Ordinal)));
        Assert.IsTrue(executor.Commands.Any(c => c.Contains("Set-MMAgent -MaxOperationAPIFiles 128", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task 数値を設定しても機能が無効のままなら部分成功として理由を出す()
    {
        // プリフェッチ機構が OFF で OperationAPI が連動無効の環境
        var executor = new FakeExecutor(command =>
            command.Contains("Get-MMAgent | ConvertTo-Json", StringComparison.Ordinal)
                ? Ok("{\"OperationAPI\":false}")
                : Ok());

        var result = await CreateChoice(executor).SetSelectedValueAsync("512");

        Assert.AreEqual(MaintenanceActionStatus.Partial, result.Status);
        StringAssert.Contains(result.Detail, "記録ファイル数を 512 に設定しました");
        StringAssert.Contains(result.Detail, "無効のまま");
        StringAssert.Contains(result.Detail, "アプリ起動プリフェッチ");
    }

    [TestMethod]
    public async Task 解釈できない値は実行せず失敗にする()
    {
        var executor = new FakeExecutor(_ => Ok());

        var result = await CreateChoice(executor).SetSelectedValueAsync("たくさん");

        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        Assert.AreEqual(0, executor.Commands.Count);
    }

    [TestMethod]
    public async Task 現在値は記録ファイル数だけを読んで返す()
    {
        var executor = new FakeExecutor(_ => Ok("256"));

        var value = await CreateChoice(executor).GetSelectedValueAsync();

        Assert.AreEqual("256", value);
        // 有効・無効は親トグルが表すので、OperationAPI の状態は問い合わせない
        // (選択肢に無い値を返すと UI が未知の選択肢を作ってしまう)。
        Assert.AreEqual(1, executor.Commands.Count);
        StringAssert.Contains(executor.Commands[0], "MaxOperationAPIFiles");
    }
}
