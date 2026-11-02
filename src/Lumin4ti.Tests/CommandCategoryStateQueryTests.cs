using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.UI.Services;
using Lumin4ti.UI.ViewModels;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class CommandCategoryStateQueryTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task チェック付き操作の中断は補償失敗でも対象を再走査せず表示済み選択を保持する(bool compensationFails)
    {
        var action = new CancelableCheckListAction(compensationFails);
        var category = new CommandCategoryViewModel(new MaintenanceActionCatalog([action]),
            new MaintenanceOperationCoordinator(() => new NoopLease()), CommandCategory.Performance, "", "");
        var item = category.Items.Single();
        item.IsCheckListExpanded = true;
        var readsBeforeRun = action.ReadCount;
        var entry = item.CheckListEntries.Single();
        var run = item.RunCommand.ExecuteAsync(null);
        Assert.IsFalse(item.CanExpandCheckList);
        item.IsCheckListExpanded = false;
        Assert.IsTrue(item.IsCheckListExpanded);
        Assert.AreEqual(readsBeforeRun, action.ReadCount);
        item.CancelCommand.Execute(null);
        await run;

        Assert.AreEqual(readsBeforeRun, action.ReadCount);
        Assert.AreSame(entry, item.CheckListEntries.Single());
        Assert.IsFalse(item.IsRunning);
        if (compensationFails)
        {
            Assert.IsTrue(item.LastRunFailed);
            StringAssert.Contains(item.ResultText, "service resume failure");
        }
        else
        {
            StringAssert.Contains(item.ResultText, "キャンセル");
        }
    }

    private sealed class CancelableCheckListAction(bool compensationFails) : IMaintenanceAction, IMaintenanceCheckList
    {
        public int ReadCount { get; private set; }
        public string Id => "cancelable-checklist";
        public string Label => "対象付き操作";
        public string Description => "中断後に再走査しないことの検証";
        public string CheckListCaption => "対象";
        public CommandCategory Category => CommandCategory.Performance;
        public bool RequiresReboot => false;
        public IReadOnlyList<MaintenanceCheckListEntry> GetCheckListEntries()
        {
            ReadCount++;
            return [new("target", "対象", true)];
        }
        public Task SetCheckListEntrySelectedAsync(string value, bool selected, CancellationToken ct = default) => Task.CompletedTask;
        public async Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (compensationFails && ct.IsCancellationRequested)
            {
                return MaintenanceActionResult.Fail("service resume failure");
            }
            return MaintenanceActionResult.Ok();
        }
    }

    [TestMethod]
    public async Task 一括設定は状態取得なしで繰り返せて両ボタンと別操作を排他する()
    {
        var completion = new TaskCompletionSource<MaintenanceActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<bool>();
        var otherCalls = 0;
        var batch = new FakeBatchSetting("batch", (enabled, _) =>
        {
            calls.Add(enabled);
            return completion.Task;
        });
        var other = new FakeBatchSetting("other-batch", (_, _) =>
        {
            otherCalls++;
            return Task.FromResult(MaintenanceActionResult.Ok());
        });
        var coordinator = new MaintenanceOperationCoordinator(() => new NoopLease());
        var category = new CommandCategoryViewModel(new MaintenanceActionCatalog([batch, other]), coordinator,
            CommandCategory.Performance, "", "");
        var item = category.Items[0];
        var otherItem = category.Items[1];
        Assert.IsTrue(item.IsBatchSetting);
        Assert.IsFalse(item.IsToggle);
        Assert.IsFalse(item.IsStateKnown);
        Assert.IsTrue(item.ApplyBatchOffCommand.CanExecute(null));

        var first = item.ApplyBatchOffCommand.ExecuteAsync(null);
        Assert.IsFalse(item.ApplyBatchOffCommand.CanExecute(null));
        Assert.IsFalse(item.ApplyBatchOnCommand.CanExecute(null));
        Assert.AreEqual(string.Empty, item.ProgressText);
        await item.ApplyBatchOnCommand.ExecuteAsync(null);
        await otherItem.ApplyBatchOnCommand.ExecuteAsync(null);
        Assert.HasCount(1, calls);
        Assert.AreEqual(0, otherCalls);
        Assert.IsTrue(otherItem.LastRunFailed);
        StringAssert.Contains(otherItem.ResultText, "ON");

        completion.SetResult(MaintenanceActionResult.Ok());
        await first;
        await item.ApplyBatchOffCommand.ExecuteAsync(null);
        await item.ApplyBatchOnCommand.ExecuteAsync(null);
        CollectionAssert.AreEqual(new[] { false, false, true }, calls);
        Assert.IsTrue(item.ApplyBatchOnCommand.CanExecute(null));
        Assert.IsTrue(item.ApplyBatchOffCommand.CanExecute(null));
        Assert.AreEqual(0, coordinator.ActiveCount);
        StringAssert.Contains(item.ResultText, "ON");

        var parent = new CommandItemViewModel(new FakeToggle("parent"), _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask);
        parent.AttachChildren([item]);
        var notifications = 0;
        item.ApplyBatchOnCommand.CanExecuteChanged += (_, _) => notifications++;
        Assert.IsFalse(item.ApplyBatchOnCommand.CanExecute(null));
        parent.ApplyState(false);
        await item.ApplyBatchOffCommand.ExecuteAsync(null);
        Assert.HasCount(3, calls);
        parent.ApplyState(true);
        Assert.IsTrue(item.ApplyBatchOnCommand.CanExecute(null));
        Assert.IsTrue(item.ApplyBatchOffCommand.CanExecute(null));
        Assert.IsGreaterThan(0, notifications);
    }

    [TestMethod]
    [DataRow("failure")]
    [DataRow("exception")]
    [DataRow("cancellation")]
    public async Task 一括設定の失敗例外とキャンセルは結果を隠さず排他を解放する(string outcome)
    {
        var batch = new FakeBatchSetting("batch-result", async (_, ct) =>
        {
            if (outcome == "cancellation")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            if (outcome == "exception")
            {
                throw new InvalidOperationException("BATCH_FAILURE");
            }
            return MaintenanceActionResult.Fail("BATCH_FAILURE");
        });
        var coordinator = new MaintenanceOperationCoordinator(() => new NoopLease());
        var category = new CommandCategoryViewModel(new MaintenanceActionCatalog([batch]), coordinator,
            CommandCategory.Performance, "", "");
        var item = category.Items.Single();
        var execution = item.ApplyBatchOnCommand.ExecuteAsync(null);
        if (outcome == "cancellation")
        {
            Assert.IsTrue(item.CanCancel);
            item.CancelCommand.Execute(null);
        }
        await execution;

        StringAssert.Contains(item.ResultText, "ON");
        if (outcome == "cancellation")
        {
            StringAssert.Contains(item.ResultText, "キャンセル");
            Assert.IsFalse(item.LastRunFailed);
        }
        else
        {
            StringAssert.Contains(item.ResultText, "BATCH_FAILURE");
            Assert.IsTrue(item.LastRunFailed);
        }
        Assert.IsFalse(item.IsRunning);
        Assert.IsFalse(item.CanCancel);
        Assert.IsTrue(item.ApplyBatchOffCommand.CanExecute(null));
        Assert.AreEqual(0, coordinator.ActiveCount);
    }

    private sealed class NoopLease : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class FakeBatchSetting(string id,
        Func<bool, CancellationToken, Task<MaintenanceActionResult>> apply) : IMaintenanceBatchSetting
    {
        public string Id => id;
        public string Label => "一括設定";
        public string Description => "一括設定の検証";
        public CommandCategory Category => CommandCategory.Performance;
        public bool RequiresReboot => false;
        public Task<MaintenanceActionResult> ApplyAsync(bool enabled, CancellationToken ct = default) => apply(enabled, ct);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void カスタム状態の選択は安全条件を守りONまたはOFFを一度だけ渡して再照会へ追従する(bool on)
    {
        var toggle = new FakeToggle("custom") { IsCustomState = true };
        var calls = new List<bool>();
        var selected = on ? "ON" : "OFF";
        var parent = new CommandItemViewModel(new FakeToggle("parent"), _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask);
        var child = new CommandItemViewModel(toggle, _ => Task.CompletedTask,
            (target, requestedState) =>
            {
                calls.Add(requestedState);
                target.IsRunning = true;
                return Task.CompletedTask;
            });
        parent.AttachChildren([child]);

        child.ApplyState(null);
        Assert.IsFalse(child.IsCustomState);
        child.SelectedCustomToggleOption = selected;
        child.ApplyState(false);
        Assert.IsTrue(child.IsCustomState);
        Assert.IsNull(child.ToggleState);
        Assert.IsFalse(child.CanShowToggle);
        Assert.IsFalse(child.IsChecked);
        child.SelectedCustomToggleOption = selected; // 親の状態不明
        parent.ApplyState(false);
        child.SelectedCustomToggleOption = selected; // 親 OFF
        parent.ApplyState(true);
        child.IsRunning = true;
        child.SelectedCustomToggleOption = selected;
        child.IsRunning = false;
        child.SelectedCustomToggleOption = null;
        child.SelectedCustomToggleOption = "invalid";
        Assert.HasCount(0, calls);

        child.SelectedCustomToggleOption = selected;
        child.SelectedCustomToggleOption = selected; // 実行中の二重操作
        Assert.HasCount(1, calls);
        Assert.AreEqual(on, calls[0]);

        // Core の再照会結果を ApplyState で反映する。
        toggle.IsCustomState = false;
        var notifications = new List<string?>();
        child.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        child.ApplyState(on);
        child.IsRunning = false;
        Assert.IsFalse(child.IsCustomState);
        Assert.AreEqual(on, child.ToggleState);
        Assert.IsNull(child.SelectedCustomToggleOption);
        Assert.IsTrue(child.CanShowToggle);
        CollectionAssert.Contains(notifications, nameof(CommandItemViewModel.ToggleState));
        CollectionAssert.Contains(notifications, nameof(CommandItemViewModel.IsCustomState));
        CollectionAssert.Contains(notifications, nameof(CommandItemViewModel.SelectedCustomToggleOption));
        child.SelectedCustomToggleOption = selected;
        Assert.HasCount(1, calls);
    }

    [TestMethod]
    public void 既定表示は現在状態と独立しトグル三値と選択肢の既定を区別する()
    {
        CommandItemViewModel Vm(IMaintenanceItem item) =>
            new(item, _ => Task.CompletedTask, (_, _) => Task.CompletedTask);

        var on = Vm(new FakeToggle("default-on", true));
        var off = Vm(new FakeToggle("default-off", false));
        var environment = Vm(new FakeToggle("default-environment"));
        on.ApplyState(false);
        off.ApplyState(true);
        environment.ApplyState(null);

        Assert.AreEqual("既定: ON", on.DefaultStateText);
        Assert.AreEqual("既定: OFF", off.DefaultStateText);
        Assert.AreEqual("既定: 環境による／不明", environment.DefaultStateText);

        var choice = Vm(new FakeChoice([
            new MaintenanceChoiceOption("custom", "カスタム"),
            new MaintenanceChoiceOption("default", "無効", IsDefault: true)]));
        Assert.AreEqual("既定: 無効", choice.DefaultStateText);
        Assert.AreEqual("既定: 環境による／不明",
            Vm(new FakeChoice([new MaintenanceChoiceOption("custom", "カスタム")])).DefaultStateText);
        Assert.AreEqual("既定: Windows / 各アプリの標準設定",
            Vm(new FakeBatchSetting("batch-default", (_, _) => Task.FromResult(MaintenanceActionResult.Ok()))).DefaultStateText);
    }

    [TestMethod]
    public void スイッチは状態不明と実行中および親が無効な間は操作できない()
    {
        var parent = new CommandItemViewModel(new FakeToggle("parent"), _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask);
        var child = new CommandItemViewModel(new FakeToggle("child"), _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask);
        parent.AttachChildren([child]);

        Assert.IsFalse(parent.CanToggle);
        parent.ApplyState(false);
        Assert.IsTrue(parent.CanToggle);
        parent.IsRunning = true;
        Assert.IsFalse(parent.CanToggle);
        parent.IsRunning = false;
        Assert.IsTrue(parent.CanToggle);

        child.ApplyState(false);
        Assert.IsFalse(child.CanToggle);
        parent.ApplyState(true);
        Assert.IsTrue(child.CanToggle);
        child.IsRunning = true;
        Assert.IsFalse(child.CanToggle);
        child.IsRunning = false;
        parent.ApplyState(null);
        Assert.IsFalse(child.CanToggle);
        parent.ApplyState(false);
        Assert.IsFalse(child.CanToggle);
    }

    [TestMethod]
    public void スイッチ操作の受付拒否では最後の確認済み値へ戻し再操作を発火しない()
    {
        var calls = 0;
        var item = new CommandItemViewModel(new FakeToggle("test-toggle"), _ => Task.CompletedTask,
            (target, _) =>
            {
                calls++;
                target.RestorePreviousToggleState();
                return Task.CompletedTask;
            });
        item.ApplyState(false);
        item.IsChecked = true;
        Assert.IsFalse(item.IsChecked);
        Assert.AreEqual(1, calls);

        item.ApplyState(true);
        item.IsChecked = false;
        Assert.IsTrue(item.IsChecked);
        Assert.AreEqual(2, calls);
    }

    private sealed class FakeToggle(string id, bool? defaultState = null) : IMaintenanceToggle
    {
        public string Id => id;
        public string Label => id;
        public string Description => id;
        public CommandCategory Category => CommandCategory.Performance;
        public bool RequiresReboot => false;
        public bool? DefaultState => defaultState;
        public bool IsCustomState { get; set; }
        public Task<bool?> GetStateAsync(CancellationToken ct = default) => Task.FromResult<bool?>(false);
        public Task<MaintenanceActionResult> SetStateAsync(bool on, CancellationToken ct = default) =>
            Task.FromResult(MaintenanceActionResult.Ok());
    }

    private sealed class FakeChoice(IReadOnlyList<MaintenanceChoiceOption> options) : IMaintenanceChoice
    {
        public string Id => "test-choice";
        public string Label => "選択設定";
        public string Description => "選択設定";
        public CommandCategory Category => CommandCategory.Performance;
        public bool RequiresReboot => false;
        public IReadOnlyList<MaintenanceChoiceOption> Options => options;
        public Task<string?> GetSelectedValueAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<MaintenanceActionResult> SetSelectedValueAsync(string value, CancellationToken ct = default) =>
            Task.FromResult(MaintenanceActionResult.Ok());
    }

    [TestMethod]
    public void カタログ順と親子の項目参照を構築時に保つ()
    {
        var catalog = new MaintenanceActionCatalog(new UnusedExecutor());
        var childCount = 0;
        foreach (var category in Enum.GetValues<CommandCategory>())
        {
            var items = catalog.Items.Where(item => item.Category == category).ToList();
            var ids = items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var expectedRoots = items.Where(item => item.ParentId is not { } id || !ids.Contains(id)).ToList();
            var vm = new CommandCategoryViewModel(catalog, new MaintenanceOperationCoordinator(), category, "", "");

            CollectionAssert.AreEqual(expectedRoots, vm.Items.Select(root => root.Item).ToList());
            foreach (var root in vm.Items)
            {
                var expectedChildren = items.Where(item => item.ParentId == root.Item.Id).ToList();
                CollectionAssert.AreEqual(expectedChildren, root.Children.Select(child => child.Item).ToList());
                for (var index = 0; index < root.Children.Count; index++)
                {
                    var child = root.Children[index];
                    Assert.AreSame(root, child.Parent);
                    Assert.AreSame(expectedChildren[index], child.Item);
                }
                childCount += root.Children.Count;
            }
        }
        Assert.IsGreaterThan(0, childCount);
    }

    private sealed class UnusedExecutor : ICommandExecutor
    {
        public Task<CommandExecutionResult> RunAsync(string fileName, string arguments,
            CancellationToken ct = default, IProgress<string>? onOutputLine = null, TimeSpan? timeout = null)
            => throw new InvalidOperationException("構築時に外部コマンドを実行しない");
    }

    [TestMethod]
    public async Task 項目固有の上限で状態取得を打ち切る()
    {
        var result = await CommandCategoryViewModel.QueryStateAsync<bool?>(
            async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return true;
            },
            "SlowItem",
            timeout: TimeSpan.FromMilliseconds(20));

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task 呼び出し元のキャンセルはタイムアウトとして握り潰さない()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CommandCategoryViewModel.QueryStateAsync<bool?>(
                async ct =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return true;
                },
                "CanceledItem",
                cts.Token,
                TimeSpan.FromSeconds(1)));
    }
}
