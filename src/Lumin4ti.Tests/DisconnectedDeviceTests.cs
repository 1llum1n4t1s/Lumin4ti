using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.UI.Services;
using Lumin4ti.UI.ViewModels;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class DisconnectedDeviceTests
{
    [TestMethod]
    [DataRow(@"ROOT\LEGACY_TEST\0000")]
    [DataRow(@"SWD\PRINTENUM\TEST")]
    [DataRow(@"SW\{00000000-0000-0000-0000-000000000000}\TEST")]
    [DataRow(@"HTREE\ROOT\0")]
    [DataRow(@"UMB\UMBUS\TEST")]
    [DataRow(@"STORAGE\VOLUMESNAPSHOT\HARDDISKVOLUMESNAPSHOT2")]
    [DataRow(@"root\lower-case\0000")]
    public void 未接続ならソフトウェアデバイスも候補にする(string instanceId)
    {
        Assert.IsTrue(WindowsDisconnectedDeviceService.IsDisconnectedCandidate(instanceId, isPresent: false));
        Assert.IsFalse(WindowsDisconnectedDeviceService.IsDisconnectedCandidate(instanceId, isPresent: true));
    }

    [TestMethod]
    public void 未接続の通常PnPデバイスを候補にする()
    {
        const string instanceId = @"USB\VID_1234&PID_5678\TEST";

        Assert.IsTrue(WindowsDisconnectedDeviceService.IsDisconnectedCandidate(instanceId, isPresent: false));
        Assert.IsFalse(WindowsDisconnectedDeviceService.IsDisconnectedCandidate(instanceId, isPresent: true));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    public void InstanceIdが空のデバイスは候補にしない(string instanceId) =>
        Assert.IsFalse(WindowsDisconnectedDeviceService.IsDisconnectedCandidate(instanceId, isPresent: false));

    [TestMethod]
    public async Task 全選択したデバイスを確認後に順次削除して再列挙する()
    {
        var first = new DisconnectedDevice(
            @"USB\VID_1234&PID_0001\FIRST",
            "USB Test Device",
            "USB",
            "Test Manufacturer");
        var second = new DisconnectedDevice(
            @"BTHENUM\DEV_0002\SECOND",
            "Bluetooth Test Device",
            "Bluetooth",
            "Test Manufacturer");
        var service = new FakeDisconnectedDeviceService(
            [first, second],
            new Dictionary<string, DisconnectedDeviceRemovalResult>(StringComparer.OrdinalIgnoreCase)
            {
                [first.InstanceId] = new(
                    DisconnectedDeviceRemovalStatus.Removed,
                    RequiresRestart: true),
                [second.InstanceId] = new(DisconnectedDeviceRemovalStatus.Reconnected),
            });
        var coordinator = new MaintenanceOperationCoordinator();
        var viewModel = new DeviceCleanupViewModel(
            service,
            coordinator,
            action =>
            {
                action();
                return Task.CompletedTask;
            });

        await viewModel.LoadDisconnectedDevicesAsync();
        Assert.HasCount(2, viewModel.Devices);
        Assert.IsFalse(viewModel.IsLoading);

        viewModel.SelectAllCommand.Execute(null);
        Assert.AreEqual(2, viewModel.SelectedCount);
        Assert.IsTrue(viewModel.CanRemove);

        viewModel.RequestRemovalCommand.Execute(null);
        Assert.IsTrue(viewModel.IsConfirmingRemoval);

        await viewModel.ConfirmRemovalCommand.ExecuteAsync(null);

        CollectionAssert.AreEquivalent(
            new[] { first.InstanceId, second.InstanceId },
            service.RemovedInstanceIds.ToArray());
        Assert.HasCount(0, viewModel.Devices);
        Assert.AreEqual(0, viewModel.SelectedCount);
        Assert.IsFalse(viewModel.IsRemoving);
        Assert.AreEqual(0, coordinator.ActiveCount);
        StringAssert.Contains(viewModel.StatusText, "1 件を削除");
        StringAssert.Contains(viewModel.StatusText, "1 件をスキップ");
        StringAssert.Contains(viewModel.StatusText, "再起動が必要");
    }

    [TestMethod]
    public async Task 削除確認時の対象を固定して確認中の選択操作を無効にする()
    {
        var first = new DisconnectedDevice(
            @"USB\VID_1234&PID_0001\FIRST",
            "USB Test Device",
            "USB",
            "Test Manufacturer");
        var second = new DisconnectedDevice(
            @"BTHENUM\DEV_0002\SECOND",
            "Bluetooth Test Device",
            "Bluetooth",
            "Test Manufacturer");
        var removed = new DisconnectedDeviceRemovalResult(DisconnectedDeviceRemovalStatus.Removed);
        var service = new FakeDisconnectedDeviceService(
            [first, second],
            new Dictionary<string, DisconnectedDeviceRemovalResult>(StringComparer.OrdinalIgnoreCase)
            {
                [first.InstanceId] = removed,
                [second.InstanceId] = removed,
            });
        var viewModel = new DeviceCleanupViewModel(
            service,
            new MaintenanceOperationCoordinator(),
            action =>
            {
                action();
                return Task.CompletedTask;
            });

        await viewModel.LoadDisconnectedDevicesAsync();
        viewModel.Devices[0].IsSelected = true;
        viewModel.RequestRemovalCommand.Execute(null);

        Assert.IsTrue(viewModel.IsConfirmingRemoval);
        Assert.IsFalse(viewModel.CanChangeSelection);
        Assert.IsFalse(viewModel.CanRemove);
        viewModel.ClearSelectionCommand.Execute(null);
        viewModel.SelectAllCommand.Execute(null);
        Assert.AreEqual(1, viewModel.SelectedCount);

        viewModel.Devices[0].IsSelected = false;
        StringAssert.Contains(viewModel.ConfirmationText, "1 件");
        viewModel.Devices[1].IsSelected = true;
        await viewModel.ConfirmRemovalCommand.ExecuteAsync(null);

        CollectionAssert.AreEqual(new[] { first.InstanceId }, service.RemovedInstanceIds);
        Assert.HasCount(1, viewModel.Devices);
        Assert.AreEqual(second.InstanceId, viewModel.Devices[0].InstanceId);
    }

    private sealed class FakeDisconnectedDeviceService : IDisconnectedDeviceService
    {
        private readonly List<DisconnectedDevice> _devices;
        private readonly IReadOnlyDictionary<string, DisconnectedDeviceRemovalResult> _results;

        public List<string> RemovedInstanceIds { get; } = [];

        public FakeDisconnectedDeviceService(
            IEnumerable<DisconnectedDevice> devices,
            IReadOnlyDictionary<string, DisconnectedDeviceRemovalResult> results)
        {
            _devices = devices.ToList();
            _results = results;
        }

        public Task<IReadOnlyList<DisconnectedDevice>> GetDisconnectedDevicesAsync(
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<DisconnectedDevice>>(_devices.ToArray());
        }

        public Task<DisconnectedDeviceRemovalResult> RemoveDisconnectedDeviceAsync(
            string instanceId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RemovedInstanceIds.Add(instanceId);
            var result = _results[instanceId];
            if (result.Status is DisconnectedDeviceRemovalStatus.Removed or
                DisconnectedDeviceRemovalStatus.AlreadyAbsent or
                DisconnectedDeviceRemovalStatus.Reconnected)
            {
                _devices.RemoveAll(device =>
                    device.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase));
            }

            return Task.FromResult(result);
        }
    }
}
