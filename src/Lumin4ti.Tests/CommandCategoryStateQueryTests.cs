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
    public async Task 未登録タスク等は明示OFF操作の対象にしない()
    {
        var toggle = new MaintenanceActionCatalog(new UnusedExecutor()).Items.Single(item => item.Id == "scheduled-temp-cleanup");
        var calls = 0;
        var item = new CommandItemViewModel(toggle, _ => Task.CompletedTask,
            (_, _) => { calls++; return Task.CompletedTask; });
        item.ApplyState(false);
        Assert.IsFalse(item.HasExplicitOff);
        Assert.IsFalse(item.SetOffCommand.CanExecute(null));
        await item.SetOffCommand.ExecuteAsync(null);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task OFF表示のトグルでも直接OFFを既存の操作先へ渡す()
    {
        var calls = new List<(CommandItemViewModel Item, bool On)>();
        var item = new CommandItemViewModel(new FakeToggle("test-toggle"), _ => Task.CompletedTask,
            (target, on) =>
            {
                calls.Add((target, on));
                return Task.CompletedTask;
            });
        item.ApplyState(false);

        Assert.IsTrue(item.SetOffCommand.CanExecute(null));
        await item.SetOffCommand.ExecuteAsync(null);

        Assert.HasCount(1, calls);
        Assert.AreSame(item, calls[0].Item);
        Assert.IsFalse(calls[0].On);
        Assert.IsFalse(item.IsChecked);
        StringAssert.Contains(item.SetOffAutomationName, item.Label);
    }

    [TestMethod]
    public async Task 直接OFFは状態不明と操作中には実行しない()
    {
        var calls = 0;
        var item = new CommandItemViewModel(new FakeToggle("test-toggle"), _ => Task.CompletedTask,
            (_, _) =>
            {
                calls++;
                return Task.CompletedTask;
            });
        var notifications = 0;
        item.SetOffCommand.CanExecuteChanged += (_, _) => notifications++;

        Assert.IsFalse(item.SetOffCommand.CanExecute(null));
        await item.SetOffCommand.ExecuteAsync(null);
        item.ApplyState(false);
        Assert.IsTrue(item.SetOffCommand.CanExecute(null));
        item.IsRunning = true;
        Assert.IsFalse(item.SetOffCommand.CanExecute(null));
        await item.SetOffCommand.ExecuteAsync(null);
        item.IsRunning = false;
        Assert.IsTrue(item.SetOffCommand.CanExecute(null));
        item.ApplyState(null);
        Assert.IsFalse(item.SetOffCommand.CanExecute(null));

        Assert.AreEqual(0, calls);
        Assert.IsGreaterThanOrEqualTo(4, notifications);
    }

    [TestMethod]
    public async Task 直接OFFは親の状態既知とONに追従する()
    {
        var calls = 0;
        var parent = new CommandItemViewModel(new FakeToggle("parent"), _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask);
        var child = new CommandItemViewModel(new FakeToggle("child"), _ => Task.CompletedTask,
            (_, _) =>
            {
                calls++;
                return Task.CompletedTask;
            });
        parent.AttachChildren([child]);
        child.ApplyState(false);
        var notifications = 0;
        child.SetOffCommand.CanExecuteChanged += (_, _) => notifications++;

        Assert.IsFalse(child.SetOffCommand.CanExecute(null));
        parent.ApplyState(false);
        Assert.IsFalse(child.SetOffCommand.CanExecute(null));
        await child.SetOffCommand.ExecuteAsync(null);
        parent.ApplyState(true);
        Assert.IsTrue(child.SetOffCommand.CanExecute(null));
        await child.SetOffCommand.ExecuteAsync(null);
        parent.ApplyState(null);
        Assert.IsFalse(child.SetOffCommand.CanExecute(null));
        await child.SetOffCommand.ExecuteAsync(null);
        parent.ApplyState(false);
        Assert.IsFalse(child.SetOffCommand.CanExecute(null));

        Assert.AreEqual(1, calls);
        Assert.IsGreaterThanOrEqualTo(3, notifications);
        Assert.AreNotEqual(parent.SetOffAutomationName, child.SetOffAutomationName);
    }

    [TestMethod]
    public async Task 直接OFFの実行中は二重送信しない()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var item = new CommandItemViewModel(new FakeToggle("test-toggle"), _ => Task.CompletedTask,
            async (target, _) =>
            {
                calls++;
                target.IsRunning = true;
                await completion.Task;
                target.IsRunning = false;
            });
        item.ApplyState(false);

        var first = item.SetOffCommand.ExecuteAsync(null);
        Assert.IsFalse(item.SetOffCommand.CanExecute(null));
        await item.SetOffCommand.ExecuteAsync(null);
        completion.SetResult();
        await first;

        Assert.AreEqual(1, calls);
        Assert.IsTrue(item.SetOffCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task 直接OFFはトグル以外では実行しない()
    {
        var calls = 0;
        var action = new MaintenanceActionCatalog(new UnusedExecutor()).Items.First(item => item is IMaintenanceAction);
        var item = new CommandItemViewModel(action, _ => Task.CompletedTask,
            (_, _) =>
            {
                calls++;
                return Task.CompletedTask;
            });
        item.ApplyState(false);

        Assert.IsFalse(item.SetOffCommand.CanExecute(null));
        await item.SetOffCommand.ExecuteAsync(null);

        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task 直接OFFの受付拒否ではOFF表示を反転させない()
    {
        var calls = 0;
        var item = new CommandItemViewModel(new FakeToggle("test-toggle"), _ => Task.CompletedTask,
            (target, _) =>
            {
                calls++;
                target.RestorePreviousToggleState();
                return Task.CompletedTask;
            });
        item.ApplyState(false);

        await item.SetOffCommand.ExecuteAsync(null);

        Assert.AreEqual(1, calls);
        Assert.IsFalse(item.IsChecked);
        Assert.IsTrue(item.IsStateKnown);
    }

    [TestMethod]
    public void スイッチ操作の受付拒否では最後の確認済み値へ戻し再操作を発火しない()
    {
        var calls = 0;
        var item = new CommandItemViewModel(new FakeToggle("test-toggle"), _ => Task.CompletedTask,
            (target, _) =>
            {
                calls++;
                target.RestorePreviousToggleState();
                return Task.CompletedTask;
            });
        item.ApplyState(false);
        item.IsChecked = true;
        Assert.IsFalse(item.IsChecked);
        Assert.AreEqual(1, calls);

        item.ApplyState(true);
        item.IsChecked = false;
        Assert.IsTrue(item.IsChecked);
        Assert.AreEqual(2, calls);
    }

    private sealed class FakeToggle(string id) : IMaintenanceToggle
    {
        public string Id => id;
        public string Label => id;
        public string Description => id;
        public CommandCategory Category => CommandCategory.Performance;
        public bool RequiresReboot => false;
        public bool SupportsExplicitOff => true;
        public Task<bool?> GetStateAsync(CancellationToken ct = default) => Task.FromResult<bool?>(false);
        public Task<MaintenanceActionResult> SetStateAsync(bool on, CancellationToken ct = default) =>
            Task.FromResult(MaintenanceActionResult.Ok());
    }

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
