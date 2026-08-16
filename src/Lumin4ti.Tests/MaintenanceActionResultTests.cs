using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.UI.ViewModels;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class MaintenanceActionResultTests
{
    [TestMethod]
    public void 既存boolコンストラクタと分解は互換動作を維持する()
    {
        var succeeded = new MaintenanceActionResult(true, "ok");
        var failed = new MaintenanceActionResult(false, "failed");
        var (success, detail) = succeeded;

        Assert.AreEqual(MaintenanceActionStatus.Success, succeeded.Status);
        Assert.AreEqual(MaintenanceActionStatus.Failed, failed.Status);
        Assert.IsTrue(success);
        Assert.AreEqual("ok", detail);
    }

    [TestMethod]
    public void 部分成功とcancelは完全成功にならない()
    {
        var partial = MaintenanceActionResult.Partial("partial");
        var canceled = MaintenanceActionResult.Canceled();

        Assert.AreEqual(MaintenanceActionStatus.Partial, partial.Status);
        Assert.IsFalse(partial.Success);
        Assert.AreEqual(MaintenanceActionStatus.Canceled, canceled.Status);
        Assert.IsFalse(canceled.Success);
    }

    [TestMethod]
    public void Status変更時も後方互換Successは状態から導出される()
    {
        var result = MaintenanceActionResult.Ok("done") with
        {
            Status = MaintenanceActionStatus.Partial,
        };

        Assert.IsFalse(result.Success);
    }

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
    public void 主要UIは部分成功を警告色用状態へ割り当てる()
    {
        var item = new CommandItemViewModel(
            new StubAction(),
            _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask);

        item.ApplyResultStatus(MaintenanceActionStatus.Partial);
        Assert.IsTrue(item.LastRunWarning);
        Assert.IsFalse(item.LastRunFailed);

        item.ApplyResultStatus(MaintenanceActionStatus.Failed);
        Assert.IsFalse(item.LastRunWarning);
        Assert.IsTrue(item.LastRunFailed);

        item.ApplyResultStatus(MaintenanceActionStatus.Canceled);
        Assert.IsFalse(item.LastRunWarning);
        Assert.IsFalse(item.LastRunFailed);
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

    private sealed class StubAction : IMaintenanceAction
    {
        public string Id => "test";

        public string Label => "テスト";

        public string Description => "テスト用";

        public CommandCategory Category => CommandCategory.System;

        public bool RequiresReboot => false;

        public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default) =>
            Task.FromResult(MaintenanceActionResult.Ok());
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
