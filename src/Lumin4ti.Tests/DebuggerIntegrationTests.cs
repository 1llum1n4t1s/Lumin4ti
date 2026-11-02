using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.UI;
using Lumin4ti.UI.Services;
using Lumin4ti.UI.ViewModels;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class DebuggerIntegrationTests
{
    [TestMethod]
    public void デバッグ画面の失敗を表示し次の成功で解除する()
    {
        var debugger = new FakeDebuggerService();
        var vm = CreateViewModel(new PendingSettings(), debugger);
        var statusNotifications = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.DebuggerStatusText)) statusNotifications++;
        };

        debugger.Succeeds = true;
        vm.OpenDebuggerCommand.Execute(null);
        Assert.AreEqual(string.Empty, vm.DebuggerStatusText);

        debugger.Succeeds = false;
        vm.OpenDebuggerCommand.Execute(null);
        Assert.AreEqual(App.Text("Version.DebuggerOpenFailed",
            "デバッグ画面を開けませんでした。ログを確認してください。"), vm.DebuggerStatusText);

        debugger.Succeeds = true;
        vm.OpenDebuggerCommand.Execute(null);
        Assert.AreEqual(string.Empty, vm.DebuggerStatusText);
        Assert.AreEqual(3, debugger.ShowCount);
        Assert.AreEqual(3, statusNotifications);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task 重複した設定保存は最後の保存が成功または失敗するまで実行中を維持する(bool lastSaveFails)
    {
        var settings = new PendingSettings();
        var vm = CreateViewModel(settings, new FakeDebuggerService());
        var states = new List<bool>();
        TaskCompletionSource? nextState = null;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(vm.IsSavingSettings)) return;
            states.Add(vm.IsSavingSettings);
            nextState?.TrySetResult();
        };

        Assert.IsFalse(vm.IsSavingSettings);
        vm.CheckForUpdatesOnStartup = true;
        var secondSave = vm.ClearIgnoredUpdateTagCommand.ExecuteAsync(null);
        Assert.AreEqual(2, settings.Pending.Count);
        Assert.IsTrue(vm.IsSavingSettings);

        // 最初の保存が完了しても、もう一方を待機していることを通知まで含めて確認する。
        nextState = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        settings.Pending[0].SetResult();
        await nextState.Task.WaitAsync(TimeSpan.FromSeconds(5));
        nextState = null;
        Assert.IsTrue(vm.IsSavingSettings);
        Assert.IsTrue(states.All(value => value));

        if (lastSaveFails)
            settings.Pending[1].SetException(new IOException("保存失敗の再現"));
        else
            settings.Pending[1].SetResult();
        await secondSave.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsFalse(vm.IsSavingSettings);
        Assert.IsFalse(states.Last());
        Assert.AreEqual(lastSaveFails, !string.IsNullOrEmpty(vm.SettingsSaveStatusText));
    }

    private static VersionViewModel CreateViewModel(PendingSettings settings, IDebuggerService debugger) =>
        new(new UpdateService(() => null), settings,
            new MaintenanceOperationCoordinator(() => throw new AssertFailedException("この操作はOS変更leaseを取得しません。")),
            debugger);

    private sealed class FakeDebuggerService : IDebuggerService
    {
        public bool Succeeds { get; set; }
        public int ShowCount { get; private set; }
        public bool Show()
        {
            ShowCount++;
            return Succeeds;
        }
    }

    private sealed class PendingSettings : ISettingsService
    {
        public AppSettings Current { get; } = new()
        {
            CheckForUpdatesOnStartup = false,
            IgnoreUpdateTag = "ignored-version",
        };
        public object SyncRoot { get; } = new();
        public List<TaskCompletionSource> Pending { get; } = [];
        public Task SaveAsync(CancellationToken ct = default)
        {
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending.Add(pending);
            return pending.Task;
        }
        public Task FlushAsync(CancellationToken ct = default) => Task.WhenAll(Pending.Select(item => item.Task));
    }
}
