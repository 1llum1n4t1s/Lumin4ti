using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class DeadAssociationCleanupTests
{
    [TestMethod]
    public void 同じ実行ファイル名の削除判定は一度だけ行う()
    {
        var cache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var probeCount = 0;

        bool Probe(string _)
        {
            probeCount++;
            return true;
        }

        Assert.IsTrue(DeadAssociationCleanupAction.GetCachedRemovalDecision("sample.exe", cache, Probe));
        Assert.IsTrue(DeadAssociationCleanupAction.GetCachedRemovalDecision("SAMPLE.EXE", cache, Probe));
        Assert.AreEqual(1, probeCount);
    }

    [TestMethod]
    public void 全候補の欠損を確定できた場合だけ関連付け候補を削除する()
    {
        Assert.IsTrue(DeadAssociationCleanupAction.AllCandidatesAreConfirmedMissing([true, true]));
        Assert.IsFalse(DeadAssociationCleanupAction.AllCandidatesAreConfirmedMissing([true, false]));
        Assert.IsFalse(DeadAssociationCleanupAction.AllCandidatesAreConfirmedMissing([]));
    }

    [TestMethod]
    public void 未処理があれば結果を部分成功にする()
    {
        var success = DeadAssociationCleanupAction.CreateResult(["削除完了"], skipped: 0);
        var partial = DeadAssociationCleanupAction.CreateResult(["アクセス不能"], skipped: 1);

        Assert.AreEqual(Lumin4ti.Core.Models.MaintenanceActionStatus.Success, success.Status);
        Assert.AreEqual(Lumin4ti.Core.Models.MaintenanceActionStatus.Partial, partial.Status);
    }
}
