using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class RecallToggleTests
{
    private sealed class StubExecutor(CommandExecutionResult result) : ICommandExecutor
    {
        public Task<CommandExecutionResult> RunAsync(
            string fileName,
            string arguments,
            CancellationToken ct = default,
            IProgress<string>? onOutputLine = null,
            TimeSpan? timeout = null) => Task.FromResult(result);
    }

    [TestMethod]
    [DataRow("State : Disabled")]
    [DataRow("State : Disable Pending")]
    [DataRow("State : DisablePending")]
    [DataRow("State : Disabled with Payload Removed")]
    [DataRow("State : Removed")]
    public void 無効化済みと無効化保留はONとして解釈する(string line)
    {
        Assert.AreEqual(true, RecallToggle.ParseFeatureState(line));
    }

    [TestMethod]
    [DataRow("State : Enabled")]
    [DataRow("State : Enable Pending")]
    [DataRow("State : EnablePending")]
    public void 有効化済みと有効化保留はOFFとして解釈する(string line)
    {
        Assert.AreEqual(false, RecallToggle.ParseFeatureState(line));
    }

    [TestMethod]
    [DataRow("State : Staged")]
    [DataRow("State : Partially Installed")]
    [DataRow("Status : Disabled")]
    public void 未知または形式違いの状態は不明として扱う(string line)
    {
        Assert.IsNull(RecallToggle.ParseFeatureState(line));
    }

    [TestMethod]
    public async Task 状態照会が再起動要求終了でも出力を解釈する()
    {
        var result = new CommandExecutionResult(
            false,
            "dism.exe ...",
            3010,
            "Feature Name : Recall\r\nState : Disable Pending\r\n",
            string.Empty);
        var toggle = new RecallToggle(new StubExecutor(result));

        var state = await toggle.GetStateAsync();

        Assert.AreEqual(true, state);
    }

    [TestMethod]
    public async Task 状態照会失敗は状態不明として扱う()
    {
        var result = new CommandExecutionResult(
            false,
            "dism.exe ...",
            87,
            "State : Disabled",
            "feature not found");
        var toggle = new RecallToggle(new StubExecutor(result));

        var state = await toggle.GetStateAsync();

        Assert.IsNull(state);
    }
}
