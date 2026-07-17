using Lumin4ti.Core.Services;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class SingleInstanceGuardTests
{
    [TestMethod]
    public void 同じセッション名の二重取得を拒否し解放後は再取得できる()
    {
        var appName = $"Lumin4tiTest_{Guid.NewGuid():N}";
        var first = new SingleInstanceGuard(appName);
        var second = new SingleInstanceGuard(appName);

        Assert.IsTrue(first.TryAcquire());
        Assert.IsFalse(second.TryAcquire());

        second.Dispose();
        first.Dispose();
        using var third = new SingleInstanceGuard(appName);
        Assert.IsTrue(third.TryAcquire());
    }

    [TestMethod]
    public void 不正なmutex名を拒否する()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new SingleInstanceGuard(@"..\unsafe"));
    }
}
