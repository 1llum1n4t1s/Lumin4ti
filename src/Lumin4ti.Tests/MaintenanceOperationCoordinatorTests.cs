using Lumin4ti.UI.Services;

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
}
