using Lumin4ti.UI.Services;
using Lumin4ti.UI.ViewModels;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class MaintenanceOperationCoordinatorTests
{
    [TestMethod]
    public async Task 終了要求は全操作をキャンセルし補償完了まで待つ()
    {
        var coordinator = new MaintenanceOperationCoordinator();
        Assert.IsTrue(coordinator.TryBegin(out var first));
        Assert.IsNotNull(first);

        var idle = coordinator.WaitForIdleAsync();
        coordinator.RequestCancellation();

        Assert.IsTrue(first!.Token.IsCancellationRequested);
        Assert.IsFalse(idle.IsCompleted);

        first.Dispose();
        await idle;
        Assert.AreEqual(0, coordinator.ActiveCount);
    }


    [TestMethod]
    public void 状態変更操作はアプリ全体で一つに直列化する()
    {
        var coordinator = new MaintenanceOperationCoordinator();
        Assert.IsTrue(coordinator.TryBegin(out var first));
        Assert.IsFalse(coordinator.TryBegin(out var rejected));
        Assert.IsNull(rejected);

        first!.Dispose();
        Assert.IsTrue(coordinator.TryBegin(out var next));
        next!.Dispose();
    }

    [TestMethod]
    public async Task 操作がなければ待機は即座に完了する()
    {
        var coordinator = new MaintenanceOperationCoordinator();

        await coordinator.WaitForIdleAsync();

        Assert.AreEqual(0, coordinator.ActiveCount);
    }

    [TestMethod]
    public async Task 状態再読込中は状態変更操作を開始できない()
    {
        var coordinator = new MaintenanceOperationCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var reload = MainWindowViewModel.RunStateReloadAsync(coordinator, async _ =>
        {
            started.TrySetResult();
            await release.Task;
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, coordinator.ActiveCount);
        Assert.IsFalse(coordinator.TryBegin(out var competingOperation));
        Assert.IsNull(competingOperation);

        release.TrySetResult();
        Assert.IsTrue(await reload);
        Assert.AreEqual(0, coordinator.ActiveCount);
    }

    [TestMethod]
    public async Task 状態変更中は状態再読込を開始しない()
    {
        var coordinator = new MaintenanceOperationCoordinator();
        Assert.IsTrue(coordinator.TryBegin(out var operation));
        var invoked = false;

        var loaded = await MainWindowViewModel.RunStateReloadAsync(coordinator, _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        Assert.IsFalse(loaded);
        Assert.IsFalse(invoked);
        operation!.Dispose();
    }
}
