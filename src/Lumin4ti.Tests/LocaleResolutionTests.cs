using Lumin4ti.UI;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class LocaleResolutionTests
{
    [TestMethod]
    public void 対応済み保存ロケールはそのまま使う()
    {
        Assert.AreEqual("ja_JP", App.ResolveLocaleKey("ja_JP"));
        Assert.AreEqual("en_US", App.ResolveLocaleKey("en_US"));
    }

    [TestMethod]
    public void 未対応保存ロケールは必ず対応済みロケールへ正規化する()
    {
        var resolved = App.ResolveLocaleKey("xx_YY");

        Assert.IsTrue(App.SupportedLocales.Any(locale => locale.Key == resolved));
        Assert.AreNotEqual("xx_YY", resolved);
    }
}
