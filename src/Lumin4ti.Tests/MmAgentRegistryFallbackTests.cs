using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class MmAgentRegistryFallbackTests
{
    [TestMethod]
    public void アプリ起動プリフェッチだけレジストリ経由の代替手段を持つ()
    {
        Assert.IsTrue(MmAgentRegistryFallback.CanFallBack("ApplicationLaunchPrefetching"));
        // 大文字小文字の違いは同一機能として扱う
        Assert.IsTrue(MmAgentRegistryFallback.CanFallBack("applicationlaunchprefetching"));

        // 実体となるレジストリ値が無い機能は cmdlet 以外の手段を持たない
        Assert.IsFalse(MmAgentRegistryFallback.CanFallBack("MemoryCompression"));
        Assert.IsFalse(MmAgentRegistryFallback.CanFallBack("PageCombining"));
        Assert.IsFalse(MmAgentRegistryFallback.CanFallBack("OperationAPI"));
        Assert.IsFalse(MmAgentRegistryFallback.CanFallBack("ApplicationPreLaunch"));
    }

    [TestMethod]
    public void 代替手段が無い機能は書き込みを試みずに理由を返す()
    {
        // 管理者権限が無い環境でも、対象外なら実書き込みへ進まないので安全に検証できる
        var error = MmAgentRegistryFallback.TrySetState("MemoryCompression", on: false);

        Assert.IsNotNull(error);
        StringAssert.Contains(error, "代替手段");
    }

    [TestMethod]
    public void 対象外の機能の状態は読まない()
    {
        Assert.IsNull(MmAgentRegistryFallback.TryReadState("MemoryCompression"));
    }

    [TestMethod]
    public void アプリ起動プリフェッチの状態はレジストリから読める()
    {
        // 読み取りは管理者権限が不要。値が無い環境でも「既定 = 有効」で bool が返る。
        var state = MmAgentRegistryFallback.TryReadState("ApplicationLaunchPrefetching");

        Assert.IsNotNull(state);
    }
}
