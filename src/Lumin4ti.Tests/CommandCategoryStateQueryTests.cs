using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.UI.Services;
using Lumin4ti.UI.ViewModels;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class CommandCategoryStateQueryTests
{
    [TestMethod]
    public void カタログ順と親子の項目参照を構築時に保つ()
    {
        var catalog = new MaintenanceActionCatalog(new UnusedExecutor());
        var childCount = 0;
        foreach (var category in Enum.GetValues<CommandCategory>())
        {
            var items = catalog.Items.Where(item => item.Category == category).ToList();
            var ids = items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var expectedRoots = items.Where(item => item.ParentId is not { } id || !ids.Contains(id)).ToList();
            var vm = new CommandCategoryViewModel(catalog, new MaintenanceOperationCoordinator(), category, "", "");

            CollectionAssert.AreEqual(expectedRoots, vm.Items.Select(root => root.Item).ToList());
            foreach (var root in vm.Items)
            {
                var expectedChildren = items.Where(item => item.ParentId == root.Item.Id).ToList();
                CollectionAssert.AreEqual(expectedChildren, root.Children.Select(child => child.Item).ToList());
                for (var index = 0; index < root.Children.Count; index++)
                {
                    var child = root.Children[index];
                    Assert.AreSame(root, child.Parent);
                    Assert.AreSame(expectedChildren[index], child.Item);
                }
                childCount += root.Children.Count;
            }
        }
        Assert.IsGreaterThan(0, childCount);
    }

    private sealed class UnusedExecutor : ICommandExecutor
    {
        public Task<CommandExecutionResult> RunAsync(string fileName, string arguments,
            CancellationToken ct = default, IProgress<string>? onOutputLine = null, TimeSpan? timeout = null)
            => throw new InvalidOperationException("構築時に外部コマンドを実行しない");
    }

    [TestMethod]
    public async Task 項目固有の上限で状態取得を打ち切る()
    {
        var result = await CommandCategoryViewModel.QueryStateAsync<bool?>(
            async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return true;
            },
            "SlowItem",
            timeout: TimeSpan.FromMilliseconds(20));

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task 呼び出し元のキャンセルはタイムアウトとして握り潰さない()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CommandCategoryViewModel.QueryStateAsync<bool?>(
                async ct =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return true;
                },
                "CanceledItem",
                cts.Token,
                TimeSpan.FromSeconds(1)));
    }
}
