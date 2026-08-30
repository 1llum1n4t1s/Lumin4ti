using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class HibernateToggleTests
{
    private sealed class NoopExecutor : ICommandExecutor
    {
        public Task<CommandExecutionResult> RunAsync(
            string fileName,
            string arguments,
            CancellationToken ct = default,
            IProgress<string>? onOutputLine = null,
            TimeSpan? timeout = null) =>
            Task.FromResult(new CommandExecutionResult(true, fileName, 0, string.Empty, string.Empty));
    }

    [TestMethod]
    public async Task Hiberfilが無ければ無効化適用済みと判定する()
    {
        var toggle = new HibernateToggle(new NoopExecutor(), () => false);

        Assert.AreEqual(true, await toggle.GetStateAsync());
    }

    [TestMethod]
    public async Task Hiberfilがあれば休止状態有効と判定する()
    {
        var toggle = new HibernateToggle(new NoopExecutor(), () => true);

        Assert.AreEqual(false, await toggle.GetStateAsync());
    }
}
