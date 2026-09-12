using Lumin4ti.Core.Services;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class MaintenanceOperationProcessLockTests
{
    [TestMethod]
    public void 同名のプロセス間ロックは同時取得できず解放後に再取得できる()
    {
        var name = $@"Local\Lumin4tiTest.Maintenance.{Guid.NewGuid():N}";
        using var first = MaintenanceOperationProcessLock.TryAcquire(name);
        var competing = MaintenanceOperationProcessLock.TryAcquire(name);

        Assert.IsNotNull(first);
        Assert.IsNull(competing);

        first.Dispose();
        using var afterRelease = MaintenanceOperationProcessLock.TryAcquire(name);
        Assert.IsNotNull(afterRelease);
    }
}
