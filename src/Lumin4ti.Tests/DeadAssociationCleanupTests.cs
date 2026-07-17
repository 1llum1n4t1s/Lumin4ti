using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class DeadAssociationCleanupTests
{
    [TestMethod]
    public void 同じ実行ファイル名の存在確認は一度だけ行う()
    {
        var cache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var probeCount = 0;

        bool Probe(string _)
        {
            probeCount++;
            return true;
        }

        Assert.IsTrue(DeadAssociationCleanupAction.GetCachedAppExists("sample.exe", cache, Probe));
        Assert.IsTrue(DeadAssociationCleanupAction.GetCachedAppExists("SAMPLE.EXE", cache, Probe));
        Assert.AreEqual(1, probeCount);
    }
}
