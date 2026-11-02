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

        Assert.IsTrue(item.HasCheckList);
        Assert.IsFalse(item.HasCheckListEntries);
        Assert.HasCount(0, item.CheckListEntries);
        Assert.AreEqual("0/0", item.CheckListSummary);
    }

    [TestMethod]
    public void チェック選択の保存失敗は操作結果と分け次の保存成功まで保持する()
    {
        var action = new MutableCheckListAction { FailSave = true };
        var item = new CommandItemViewModel(action, _ => Task.CompletedTask, (_, _) => Task.CompletedTask);
        item.ResultText = "前の実行結果";
        item.CheckListEntries.Single().IsSelected = false;
        Assert.IsTrue(item.HasCheckListSaveError);
        Assert.IsFalse(string.IsNullOrEmpty(item.CheckListSaveErrorText));
        Assert.AreEqual("前の実行結果", item.ResultText);

        item.ResultText = "次の実行結果";
        Assert.IsTrue(item.HasCheckListSaveError);
        action.FailSave = false;
        item.CheckListEntries.Single().IsSelected = true;
        Assert.IsFalse(item.HasCheckListSaveError);
        Assert.AreEqual(string.Empty, item.CheckListSaveErrorText);
        Assert.AreEqual("次の実行結果", item.ResultText);
    }

    [TestMethod]
    public void 空の対象一覧を開き直すと新候補を除外でき実行中は一覧を再構築しない()
    {
        var action = new MutableCheckListAction();
        action.Entries.Clear();
        var item = new CommandItemViewModel(action, _ => Task.CompletedTask, (_, _) => Task.CompletedTask);
        Assert.IsTrue(item.HasCheckList);
        Assert.IsFalse(item.HasCheckListEntries);
        Assert.IsTrue(item.CanExpandCheckList);
        item.IsCheckListExpanded = true;
        Assert.IsTrue(item.IsCheckListExpanded);
        Assert.AreEqual("0/0", item.CheckListSummary);

        action.Entries.Add(new("new-cache", "New cache", true));
        item.IsCheckListExpanded = false;
        item.IsCheckListExpanded = true;
        Assert.IsTrue(item.HasCheckListEntries);
        item.CheckListEntries.Single().IsSelected = false;
        Assert.IsFalse(action.Entries.Single().IsSelected);
        Assert.AreEqual("0/1", item.CheckListSummary);

        item.IsCheckListExpanded = false;
        item.IsRunning = true;
        action.Entries.Add(new("another-cache", "Another cache", true));
        Assert.IsFalse(item.CanExpandCheckList);
        item.IsCheckListExpanded = true;
        Assert.IsFalse(item.IsCheckListExpanded);
        Assert.HasCount(1, item.CheckListEntries);
        item.IsRunning = false;
        item.IsCheckListExpanded = true;
        Assert.HasCount(2, item.CheckListEntries);
    }

    private sealed class MutableCheckListAction : IMaintenanceAction, IMaintenanceCheckList
    {
        public bool FailSave { get; set; }
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
            CancellationToken ct = default)
        {
            var index = Entries.FindIndex(entry => entry.Value == value);
            Entries[index] = Entries[index] with { IsSelected = selected };
            if (FailSave)
            {
                throw new InvalidOperationException("一時変更のみ・原本は保護されています");
            }
            return Task.CompletedTask;
        }

        public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default) =>
            Task.FromResult(MaintenanceActionResult.Ok());
    }
}
