using Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class RegistryCleanModelsTests
{
    private static IEnumerable<RegistryCleanCategory> AllCategories => Enum.GetValues<RegistryCleanCategory>();

    [TestMethod]
    public void 根拠表は全カテゴリーをちょうど1件ずつ持つ()
    {
        foreach (var category in AllCategories)
            Assert.AreEqual(1, RegistryCleanEvidenceTable.All.Count(entry => entry.Category == category), category.ToString());
        Assert.AreEqual(AllCategories.Count(), RegistryCleanEvidenceTable.All.Count);
    }

    [TestMethod]
    public void 根拠はtierAの1件か別ホストのtierB2件のどちらか()
    {
        foreach (var entry in RegistryCleanEvidenceTable.All)
        {
            var tierA = entry.Sources.Where(source => source.Tier == RegistryEvidenceTier.A).ToList();
            var tierB = entry.Sources.Where(source => source.Tier == RegistryEvidenceTier.B).ToList();
            var name = entry.Category.ToString();
            Assert.AreEqual(entry.Sources.Count, tierA.Count + tierB.Count, name);
            if (tierA.Count == 1)
            {
                Assert.AreEqual(0, tierB.Count, name);
                continue;
            }

            Assert.AreEqual(0, tierA.Count, name);
            Assert.AreEqual(2, tierB.Count, name);
            Assert.AreEqual(2, tierB.Select(source => new Uri(source.Url).Host).Distinct(StringComparer.OrdinalIgnoreCase).Count(), name);
        }
    }

    [TestMethod]
    public void 根拠のURLは空でない絶対HTTPSでtierAはMicrosoftの文書()
    {
        foreach (var source in RegistryCleanEvidenceTable.All.SelectMany(entry => entry.Sources))
        {
            Assert.IsTrue(Uri.TryCreate(source.Url, UriKind.Absolute, out var uri), source.Url);
            Assert.AreEqual(Uri.UriSchemeHttps, uri.Scheme, source.Url);
            if (source.Tier == RegistryEvidenceTier.A) Assert.AreEqual("learn.microsoft.com", uri.Host, source.Url);
        }
    }

    [TestMethod]
    public void 共有DLLの根拠は本文を確認した2件に固定する()
    {
        var urls = RegistryCleanEvidenceTable.For(RegistryCleanCategory.SharedDlls).Sources.Select(source => source.Url).ToList();
        CollectionAssert.AreEquivalent(
            new[]
            {
                "https://www.revenera.com/blog/software-installation/cleaning-up-your-shared-dlls-registry-references-for-msis/",
                "https://jrsoftware.org/ishelp/topic_isxfunc_incrementsharedcount.htm",
            },
            urls);
    }

    [TestMethod]
    public void カテゴリー定義は全カテゴリーを持ち既定選択と承認の設計に従う()
    {
        foreach (var category in AllCategories)
            Assert.AreEqual(1, RegistryCleanCategories.All.Count(definition => definition.Category == category), category.ToString());

        var uninstall = RegistryCleanCategories.Get(RegistryCleanCategory.Uninstall);
        Assert.IsFalse(uninstall.DefaultSelected);
        Assert.IsFalse(uninstall.IncludedInSelectAll);
        Assert.IsTrue(uninstall.RequiresIndividualApproval);

        var shared = RegistryCleanCategories.Get(RegistryCleanCategory.SharedDlls);
        Assert.IsFalse(shared.DefaultSelected);
        Assert.IsFalse(shared.IncludedInSelectAll);
        Assert.IsFalse(shared.RequiresIndividualApproval);

        foreach (var category in new[] { RegistryCleanCategory.AppPaths, RegistryCleanCategory.Startup, RegistryCleanCategory.Fonts, RegistryCleanCategory.FileAssociations })
            Assert.IsTrue(RegistryCleanCategories.Get(category).DefaultSelected, category.ToString());
        Assert.AreEqual(RegistryCleanDeletionUnit.ValuePair, RegistryCleanCategories.Get(RegistryCleanCategory.Startup).DeletionUnit);
    }
}
