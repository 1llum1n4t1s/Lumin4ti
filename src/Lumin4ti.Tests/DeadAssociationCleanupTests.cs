using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class DeadAssociationCleanupTests
{
    [TestMethod]
    public void 全候補の欠損を確定できた場合だけ関連付け候補を削除する()
    {
        Assert.IsTrue(DeadAssociationCleanupAction.AllCandidatesAreConfirmedMissing([true, true]));
        Assert.IsFalse(DeadAssociationCleanupAction.AllCandidatesAreConfirmedMissing([true, false]));
        Assert.IsFalse(DeadAssociationCleanupAction.AllCandidatesAreConfirmedMissing([]));
    }

}
