using Lumin4ti.Core.Services;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class ExecutableTrustVerifierTests
{
    [TestMethod]
    public void 署名者CommonNameは属性境界まで一致させる()
    {
        Assert.IsTrue(ExecutableTrustVerifier.HasExpectedCommonName(
            "CN=Microsoft Windows, O=Microsoft Corporation, C=US",
            "Microsoft Windows"));
        Assert.IsFalse(ExecutableTrustVerifier.HasExpectedCommonName(
            "CN=Microsoft Windows Attacker, O=Example",
            "Microsoft Windows"));
    }

    [TestMethod]
    public void 失効確認モードはオンラインとキャッシュ限定を区別する()
    {
        var online = ExecutableTrustVerifier.BuildProviderFlags(AuthenticodeRevocationMode.Online);
        var cacheOnly = ExecutableTrustVerifier.BuildProviderFlags(AuthenticodeRevocationMode.CacheOnly);

        Assert.IsTrue(online.HasFlag(
            ExecutableTrustVerifier.WinTrustProviderFlags.RevocationCheckChainExcludeRoot));
        Assert.IsFalse(online.HasFlag(
            ExecutableTrustVerifier.WinTrustProviderFlags.CacheOnlyUrlRetrieval));
        Assert.IsTrue(cacheOnly.HasFlag(
            ExecutableTrustVerifier.WinTrustProviderFlags.RevocationCheckChainExcludeRoot));
        Assert.IsTrue(cacheOnly.HasFlag(
            ExecutableTrustVerifier.WinTrustProviderFlags.CacheOnlyUrlRetrieval));
    }

    [TestMethod]
    public void 不存在実行ファイルはfailClosedで拒否する()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe");

        Assert.IsFalse(ExecutableTrustVerifier.TryVerify(
            missing,
            "Microsoft Windows",
            out var reason));
        StringAssert.Contains(reason, "見つかりません");
    }
}
