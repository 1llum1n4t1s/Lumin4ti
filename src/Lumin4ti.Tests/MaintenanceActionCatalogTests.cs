using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class MaintenanceActionCatalogTests
{
    private sealed class NoopExecutor : ICommandExecutor
    {
        public Task<CommandExecutionResult> RunAsync(string fileName, string arguments, CancellationToken ct = default, IProgress<string>? onOutputLine = null) =>
            Task.FromResult(new CommandExecutionResult(true, $"{fileName} {arguments}", 0, string.Empty, string.Empty));
    }

    private static MaintenanceActionCatalog CreateCatalog() => new(new NoopExecutor());

    [TestMethod]
    public void Idは一意である()
    {
        var ids = CreateCatalog().Items.Select(a => a.Id).ToList();

        CollectionAssert.AllItemsAreUnique(ids);
    }

    [TestMethod]
    public void 全項目が実行型かトグル型のどちらかである()
    {
        foreach (var item in CreateCatalog().Items)
        {
            Assert.IsTrue(item is IMaintenanceAction or IMaintenanceToggle, item.Id);
        }
    }

    [TestMethod]
    public void ラベルと説明が空でない()
    {
        foreach (var item in CreateCatalog().Items)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(item.Label), item.Id);
            // 「丁寧な説明」の担保: 効果と注意点を書くと最低でも 40 文字は超える
            Assert.IsTrue(item.Description.Length >= 40, $"{item.Id} の説明が短すぎます ({item.Description.Length} 文字)");
        }
    }

    [TestMethod]
    public void 全カテゴリに1件以上の項目がある()
    {
        var items = CreateCatalog().Items;
        foreach (var category in Enum.GetValues<CommandCategory>())
        {
            Assert.IsTrue(items.Any(a => a.Category == category), category.ToString());
        }
    }

    [TestMethod]
    public void エクスプローラー影響フラグはシェル系項目に付いている()
    {
        var items = CreateCatalog().Items.ToDictionary(a => a.Id);

        Assert.IsTrue(items["repair-shell-folder-names"].AffectsExplorer);
        Assert.IsTrue(items["remove-dead-associations"].AffectsExplorer);
        Assert.IsTrue(items["tray-icon-reset"].AffectsExplorer);
        Assert.IsTrue(items["folder-template-general"].AffectsExplorer);
        Assert.IsTrue(items["menu-delay-zero"].AffectsExplorer);
        // レジストリ tweak や DISM 系は Explorer 再起動では反映されないので付けない
        Assert.IsFalse(items["svchost-split-threshold"].AffectsExplorer);
        Assert.IsFalse(items["wu-component-cleanup"].AffectsExplorer);
        Assert.IsFalse(items["quick-access-sort"].AffectsExplorer);
    }

    [TestMethod]
    public void レジストリトグルは全て1仕様以上を持つ()
    {
        foreach (var toggle in CreateCatalog().Items.OfType<RegistryToggle>())
        {
            Assert.IsTrue(toggle.Specs.Count >= 1, toggle.Id);
        }
    }

    [TestMethod]
    public void 既定値に戻せるトグルの既定値は適用値と異なる()
    {
        foreach (var spec in CreateCatalog().Items.OfType<RegistryToggle>().SelectMany(t => t.Specs))
        {
            if (spec.DefaultValue is not null)
            {
                Assert.AreNotEqual(
                    spec.AppliedValue.ToString(),
                    spec.DefaultValue.ToString(),
                    $"{spec.KeyPath}\\{spec.Name}");
            }
        }
    }

    [TestMethod]
    public void HKLMを触るトグルは再起動要否が明示されている()
    {
        // 再起動が必要なトグルの取り決めが崩れていないかの回帰チェック
        var items = CreateCatalog().Items.ToDictionary(a => a.Id);

        Assert.IsTrue(items["svchost-split-threshold"].RequiresReboot);
        Assert.IsTrue(items["vbs-hvci-off"].RequiresReboot);
        Assert.IsTrue(items["mpo-off"].RequiresReboot);
        Assert.IsTrue(items["power-throttling-off"].RequiresReboot);
        Assert.IsTrue(items["recall-off"].RequiresReboot);
        Assert.IsFalse(items["mouse-accel-off"].RequiresReboot);
        Assert.IsFalse(items["hibernate-off"].RequiresReboot);
    }
}
