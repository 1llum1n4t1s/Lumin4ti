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
            IProgress<string>? onOutputLine = null)
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
    public void 選択肢は無効と記録ファイル数を持ち既定は512()
    {
        var choice = CreateChoice(new FakeExecutor(_ => Ok()));

        CollectionAssert.AreEqual(
            new[] { "disabled", "128", "256", "512", "1024" },
            choice.Options.Select(o => o.Value).ToArray());
        Assert.AreEqual("512", choice.Options.Single(o => o.IsDefault).Value);
        Assert.AreEqual("512", MmAgentOperationApiChoice.DefaultValue);
    }

    [TestMethod]
    public async Task 無効を選ぶとDisableMMAgentを実行する()
    {
        var executor = new FakeExecutor(_ => Ok());

        var result = await CreateChoice(executor).SetSelectedValueAsync(MmAgentOperationApiChoice.DisabledValue);

        Assert.AreEqual(MaintenanceActionStatus.Success, result.Status);
        Assert.IsTrue(executor.Commands.Any(c => c.Contains("Disable-MMAgent -OperationAPI", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task 無効化を拒否されたら連動手順を案内する()
    {
        // 無効化は拒否され、現在値の再取得では有効のままが返る環境
        var executor = new FakeExecutor(command =>
            command.Contains("Disable-MMAgent", StringComparison.Ordinal)
                ? Fail("この要求はサポートされていません。")
                : Ok("{\"OperationAPI\":true}"));

        var result = await CreateChoice(executor).SetSelectedValueAsync(MmAgentOperationApiChoice.DisabledValue);

        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        StringAssert.Contains(result.Detail, "アプリ起動プリフェッチ");
        StringAssert.Contains(result.Detail, "記録ファイル数");
    }

    [TestMethod]
    public async Task 拒否されても既に無効なら成功として扱う()
    {
        var executor = new FakeExecutor(command =>
            command.Contains("Disable-MMAgent", StringComparison.Ordinal)
                ? Fail("この要求はサポートされていません。")
                : Ok("{\"OperationAPI\":false}"));

        var result = await CreateChoice(executor).SetSelectedValueAsync(MmAgentOperationApiChoice.DisabledValue);

        Assert.AreEqual(MaintenanceActionStatus.Success, result.Status);
        StringAssert.Contains(result.Detail, "既に無効");
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
        var executor = new FakeExecutor(command =>
            command.Contains("Get-MMAgent | ConvertTo-Json", StringComparison.Ordinal)
                ? Ok("{\"OperationAPI\":false}")
                : Ok());

        var result = await CreateChoice(executor).SetSelectedValueAsync("128");

        Assert.AreEqual(MaintenanceActionStatus.Success, result.Status);
        Assert.IsTrue(executor.Commands.Any(c => c.Contains("Enable-MMAgent -OperationAPI", StringComparison.Ordinal)));
        Assert.IsTrue(executor.Commands.Any(c => c.Contains("Set-MMAgent -MaxOperationAPIFiles 128", StringComparison.Ordinal)));
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
    public async Task 無効なら現在値として無効を返す()
    {
        var executor = new FakeExecutor(_ => Ok("{\"OperationAPI\":false}"));

        var value = await CreateChoice(executor).GetSelectedValueAsync();

        Assert.AreEqual(MmAgentOperationApiChoice.DisabledValue, value);
    }

    [TestMethod]
    public async Task 有効なら現在の記録ファイル数を返す()
    {
        var executor = new FakeExecutor(command =>
            command.Contains("MaxOperationAPIFiles", StringComparison.Ordinal)
                ? Ok("256")
                : Ok("{\"OperationAPI\":true}"));

        var value = await CreateChoice(executor).GetSelectedValueAsync();

        Assert.AreEqual("256", value);
    }
}
