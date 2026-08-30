using System.Xml.Linq;
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

    [TestMethod]
    public void 未接続デバイスタブの文字列は全対応ロケールに揃っている()
    {
        string[] requiredKeys =
        [
            "Text.Tab.Devices",
            "Text.DeviceCleanup.Title",
            "Text.DeviceCleanup.Description",
            "Text.DeviceCleanup.Warning",
            "Text.DeviceCleanup.Refresh",
            "Text.DeviceCleanup.SelectAll",
            "Text.DeviceCleanup.ClearSelection",
            "Text.DeviceCleanup.RemoveSelected",
            "Text.DeviceCleanup.Empty",
            "Text.DeviceCleanup.ConfirmRemove",
            "Text.DeviceCleanup.Loading",
            "Text.DeviceCleanup.Selection",
            "Text.DeviceCleanup.ConfirmText",
            "Text.DeviceCleanup.Loaded",
            "Text.DeviceCleanup.LoadFailed",
            "Text.DeviceCleanup.Removing",
            "Text.DeviceCleanup.Completed",
            "Text.DeviceCleanup.CompletedRestart",
        ];
        var localeDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Lumin4ti.UI",
            "Resources",
            "Locales");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var localeFiles = Directory.GetFiles(localeDirectory, "*.axaml");
        Assert.HasCount(App.SupportedLocales.Count, localeFiles);
        foreach (var localeFile in localeFiles)
        {
            var document = XDocument.Load(localeFile, LoadOptions.None);
            var keys = document
                .Descendants()
                .Select(element => element.Attribute(x + "Key")?.Value)
                .Where(key => key is not null)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var requiredKey in requiredKeys)
            {
                Assert.IsTrue(
                    keys.Contains(requiredKey),
                    $"{Path.GetFileName(localeFile)} に {requiredKey} がありません。");
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Lumin4ti.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Lumin4ti.slnx を含むリポジトリルートを特定できません。");
    }
}
