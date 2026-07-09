using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class QuickAccessVerbTests
{
    [TestMethod]
    public void 日本語のピン留めverbを判定できる()
    {
        Assert.IsTrue(QuickAccessSortAction.IsPinVerb("クイック アクセスにピン留めする(&P)"));
        Assert.IsFalse(QuickAccessSortAction.IsPinVerb("クイック アクセスからピン留めを外す(&U)"));
        Assert.IsTrue(QuickAccessSortAction.IsUnpinVerb("クイック アクセスからピン留めを外す(&U)"));
        Assert.IsFalse(QuickAccessSortAction.IsUnpinVerb("クイック アクセスにピン留めする(&P)"));
    }

    [TestMethod]
    public void 英語のverbを判定できる()
    {
        Assert.IsTrue(QuickAccessSortAction.IsPinVerb("Pin to Quick access"));
        Assert.IsTrue(QuickAccessSortAction.IsUnpinVerb("Unpin from Quick access"));
        Assert.IsFalse(QuickAccessSortAction.IsPinVerb("Unpin from Quick access"));
    }

    [TestMethod]
    public void nullや無関係なverbは対象外()
    {
        Assert.IsFalse(QuickAccessSortAction.IsPinVerb(null));
        Assert.IsFalse(QuickAccessSortAction.IsUnpinVerb(null));
        Assert.IsFalse(QuickAccessSortAction.IsPinVerb("開く(&O)"));
    }
}
