using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.UI.ViewModels;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class MaintenanceActionResultTests
{
    [TestMethod]
    public void Explorer再起動失敗は元の成功詳細を保った部分成功になる()
    {
        var result = CommandCategoryViewModel.MarkExplorerRestartFailed(
            MaintenanceActionResult.Ok("  - 設定変更済み"),
            "  - Explorer再起動失敗");

        Assert.AreEqual(MaintenanceActionStatus.Partial, result.Status);
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Detail, "設定変更済み");
        StringAssert.Contains(result.Detail, "Explorer再起動失敗");
    }

    [TestMethod]
    public void チェックリストの再検出で存在しなくなった行を非表示にする()
    {
        var action = new MutableCheckListAction();
        var item = new CommandItemViewModel(
            action,
            _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask);

        Assert.IsTrue(item.HasCheckList);
        Assert.HasCount(1, item.CheckListEntries);

        action.Entries.Clear();
        item.RefreshCheckListEntries();

        Assert.IsFalse(item.HasCheckList);
        Assert.HasCount(0, item.CheckListEntries);
        Assert.AreEqual("0/0", item.CheckListSummary);
    }

    private sealed class MutableCheckListAction : IMaintenanceAction, IMaintenanceCheckList
    {
        public List<MaintenanceCheckListEntry> Entries { get; } =
        [
            new("cache", "Cache", true),
        ];

        public string Id => "test-check-list";

        public string Label => "チェックリストテスト";

        public string Description => "チェックリストテスト";

        public CommandCategory Category => CommandCategory.Cleanup;

        public bool RequiresReboot => false;

        public string CheckListCaption => "削除する対象を選ぶ";

        public IReadOnlyList<MaintenanceCheckListEntry> GetCheckListEntries() => [.. Entries];

        public Task SetCheckListEntrySelectedAsync(
            string value,
            bool selected,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default) =>
            Task.FromResult(MaintenanceActionResult.Ok());
    }
}
