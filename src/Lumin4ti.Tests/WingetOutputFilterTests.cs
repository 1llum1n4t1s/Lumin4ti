using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class WingetOutputFilterTests
{
    [TestMethod]
    public void プログレスバーとスピナー行は除外される()
    {
        Assert.IsFalse(WingetUpgradeAction.IsMeaningfulLine("██████████░░  45%"));
        Assert.IsFalse(WingetUpgradeAction.IsMeaningfulLine("▒▒▒▒▒▒"));
        Assert.IsFalse(WingetUpgradeAction.IsMeaningfulLine("-"));
        Assert.IsFalse(WingetUpgradeAction.IsMeaningfulLine("\\"));
        Assert.IsFalse(WingetUpgradeAction.IsMeaningfulLine("   "));
        Assert.IsFalse(WingetUpgradeAction.IsMeaningfulLine("---------------"));
    }

    [TestMethod]
    public void 成功件数を日英ロケールで数えられる()
    {
        string[] lines =
        [
            "見つかりました uv [astral-sh.uv] バージョン 0.11.26",
            "正常にインストールされました",
            "Successfully installed",
            "インストーラーが終了コードで失敗しました: 0x8a150003",
        ];

        Assert.AreEqual(2, WingetUpgradeAction.CountSuccessfulInstalls(lines));
        Assert.AreEqual(0, WingetUpgradeAction.CountSuccessfulInstalls(["失敗しました"]));
    }

    [TestMethod]
    public void 意味のある行は通過する()
    {
        Assert.IsTrue(WingetUpgradeAction.IsMeaningfulLine("Google Chrome を更新しています"));
        Assert.IsTrue(WingetUpgradeAction.IsMeaningfulLine("インストールが完了しました"));
        Assert.IsTrue(WingetUpgradeAction.IsMeaningfulLine("2 個のパッケージにアップグレードがあります"));
    }
}
