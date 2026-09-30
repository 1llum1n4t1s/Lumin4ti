using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class MaintenanceActionCatalogTests
{
    private sealed class NoopExecutor : ICommandExecutor
    {
        public Task<CommandExecutionResult> RunAsync(string fileName, string arguments, CancellationToken ct = default, IProgress<string>? onOutputLine = null, TimeSpan? timeout = null) =>
            Task.FromResult(new CommandExecutionResult(true, $"{fileName} {arguments}", 0, string.Empty, string.Empty));
    }

    private static MaintenanceActionCatalog CreateCatalog() => new(new NoopExecutor());

    [TestMethod]
    public void 復活対象以外の復元不能な削除アクションはカタログへ登録しない()
    {
        var ids = CreateCatalog().Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        string[] removedIds =
        [
            "gpu-preference-reset",
            "remove-ghost-packages",
            "event-log-clear",
            "wu-component-cleanup",
            "cleanup-drive-root-leftovers",
            "cleanup-outlook-offline-cache",
            "cleanup-nul-files",
            "cleanup-recycle-bin",
        ];

        foreach (var id in removedIds)
        {
            Assert.IsFalse(ids.Contains(id), id);
        }
    }

}
