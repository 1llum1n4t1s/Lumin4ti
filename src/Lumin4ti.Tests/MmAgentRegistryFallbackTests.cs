using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class MmAgentRegistryFallbackTests
{
    [TestMethod]
    [DataRow(null, 3)]
    [DataRow(2, 3)]
    public void 現在値のアプリ起動ビットだけを有効にする(int? current, int expected)
    {
        int? written = null;
        var error = MmAgentRegistryFallback.TrySetState("ApplicationLaunchPrefetching", true,
            () => current, value => { written = value; return null; });
        Assert.IsNull(error);
        Assert.AreEqual(expected, written);
    }

    [TestMethod]
    public void 無効化してもブートプリフェッチのビットを維持する()
    {
        int? written = null;
        Assert.IsNull(MmAgentRegistryFallback.TrySetState("ApplicationLaunchPrefetching", false,
            () => 3, value => { written = value; return null; }));
        Assert.AreEqual(2, written);
    }

    [TestMethod]
    public void 代替手段が無い機能は書き込みを試みずに理由を返す()
    {
        // 管理者権限が無い環境でも、対象外なら実書き込みへ進まないので安全に検証できる
        var error = MmAgentRegistryFallback.TrySetState("MemoryCompression", on: false);

        Assert.IsNotNull(error);
        StringAssert.Contains(error, "代替手段");
    }

}
