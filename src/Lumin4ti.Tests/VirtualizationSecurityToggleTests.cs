using System.Text.Json;
using Lumin4ti.Core.Services.Windows.Actions;
using Microsoft.Win32;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class VirtualizationSecurityToggleTests
{
    private string _tempDirectory = null!;
    private string _backupPath = null!;

    [TestInitialize]
    public void Initialize()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"Lumin4ti.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _backupPath = Path.Combine(_tempDirectory, "vbs-hvci.json");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ON前の存在型値を保存してOFFで正確に復元する()
    {
        var original = new VirtualizationSecuritySnapshot
        {
            HypervisorLaunchType = BcdValueSnapshot.Present("Auto"),
            DeviceGuard = RegistryValueSnapshot.Dword(1),
            Hvci = RegistryValueSnapshot.FromRegistry(RegistryValueKind.String, "legacy"),
            Lsa = RegistryValueSnapshot.FromRegistry(RegistryValueKind.QWord, 2L),
        };
        var platform = new FakePlatform(original);
        var toggle = CreateToggle(platform);

        var applied = await toggle.SetStateAsync(true);

        Assert.IsTrue(applied.Success, applied.Detail);
        Assert.IsTrue(platform.State.IsFullyApplied);
        Assert.IsTrue(File.Exists(_backupPath));
        using (var document = JsonDocument.Parse(File.ReadAllText(_backupPath)))
        {
            var root = document.RootElement;
            Assert.AreEqual(VirtualizationSecuritySnapshot.CurrentSchemaVersion, root.GetProperty("SchemaVersion").GetInt32());
            Assert.IsTrue(root.GetProperty("HypervisorLaunchType").GetProperty("Exists").GetBoolean());
            Assert.AreEqual("Auto", root.GetProperty("HypervisorLaunchType").GetProperty("Value").GetString());
            Assert.AreEqual(
                (int)RegistryValueKind.String,
                root.GetProperty("Hvci").GetProperty("Kind").GetInt32());
            Assert.AreEqual("legacy", root.GetProperty("Hvci").GetProperty("StringValue").GetString());
        }

        var restored = await toggle.SetStateAsync(false);

        Assert.IsTrue(restored.Success, restored.Detail);
        Assert.IsTrue(platform.State.EquivalentTo(original));
        Assert.IsFalse(File.Exists(_backupPath));
        Assert.AreEqual(0, Directory.GetFiles(_tempDirectory, "*.tmp").Length);
    }

    [TestMethod]
    public async Task 状態判定はBCDと3レジストリ値の全てを照合する()
    {
        var platform = new FakePlatform(NotAppliedState());
        var toggle = CreateToggle(platform);

        Assert.AreEqual(false, await toggle.GetStateAsync());

        platform.State = VirtualizationSecuritySnapshot.Applied();
        Assert.AreEqual(true, await toggle.GetStateAsync());

        platform.State = VirtualizationSecuritySnapshot.Applied() with
        {
            Lsa = RegistryValueSnapshot.Dword(1),
        };
        Assert.IsNull(await toggle.GetStateAsync());
    }

    [TestMethod]
    public async Task ON途中で書込後に失敗しても全変更を元状態へ補償する()
    {
        var original = NotAppliedState();
        var platform = new FakePlatform(original);
        platform.InjectFailure(VirtualizationSecurityComponent.Hvci, afterWrite: true);
        var toggle = CreateToggle(platform);

        var result = await toggle.SetStateAsync(true);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(platform.State.EquivalentTo(original));
        Assert.IsTrue(File.Exists(_backupPath));
        StringAssert.Contains(result.Detail, "開始前の状態へ補償しました");
        StringAssert.Contains(result.Detail, "最終状態: OFF");
        Assert.AreEqual(false, await toggle.GetStateAsync());
    }

    [TestMethod]
    public async Task OFF途中で失敗した場合は適用済み状態へ補償してバックアップを保持する()
    {
        var original = NotAppliedState() with
        {
            DeviceGuard = RegistryValueSnapshot.Dword(1),
            Hvci = RegistryValueSnapshot.Dword(1),
            Lsa = RegistryValueSnapshot.Dword(2),
        };
        var platform = new FakePlatform(original);
        var toggle = CreateToggle(platform);
        Assert.IsTrue((await toggle.SetStateAsync(true)).Success);
        platform.InjectFailure(VirtualizationSecurityComponent.Hvci, afterWrite: true);

        var failedRestore = await toggle.SetStateAsync(false);

        Assert.IsFalse(failedRestore.Success);
        Assert.IsTrue(platform.State.EquivalentTo(VirtualizationSecuritySnapshot.Applied()));
        Assert.IsTrue(File.Exists(_backupPath));
        StringAssert.Contains(failedRestore.Detail, "最終状態: ON");

        var retry = await toggle.SetStateAsync(false);
        Assert.IsTrue(retry.Success, retry.Detail);
        Assert.IsTrue(platform.State.EquivalentTo(original));
        Assert.IsFalse(File.Exists(_backupPath));
    }

    private VirtualizationSecurityToggle CreateToggle(FakePlatform platform) =>
        new(platform, new VirtualizationSecurityBackupStore(_backupPath));

    private static VirtualizationSecuritySnapshot NotAppliedState() => new()
    {
        HypervisorLaunchType = BcdValueSnapshot.Missing(),
        DeviceGuard = RegistryValueSnapshot.Missing(),
        Hvci = RegistryValueSnapshot.Missing(),
        Lsa = RegistryValueSnapshot.Missing(),
    };

    private sealed class FakePlatform(VirtualizationSecuritySnapshot initialState) : IVirtualizationSecurityPlatform
    {
        private VirtualizationSecurityComponent? _failureComponent;
        private bool _failAfterWrite;
        private bool _failurePending;

        public VirtualizationSecuritySnapshot State { get; set; } = initialState;

        public void InjectFailure(VirtualizationSecurityComponent component, bool afterWrite)
        {
            _failureComponent = component;
            _failAfterWrite = afterWrite;
            _failurePending = true;
        }

        public Task<BcdValueSnapshot> ReadHypervisorLaunchTypeAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(State.HypervisorLaunchType);
        }

        public Task WriteHypervisorLaunchTypeAsync(BcdValueSnapshot value, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            MaybeFail(VirtualizationSecurityComponent.HypervisorLaunchType, () =>
                State = State with { HypervisorLaunchType = value });
            return Task.CompletedTask;
        }

        public RegistryValueSnapshot ReadRegistryValue(VirtualizationSecurityComponent component) =>
            State.GetRegistryValue(component);

        public void WriteRegistryValue(VirtualizationSecurityComponent component, RegistryValueSnapshot value) =>
            MaybeFail(component, () => State = component switch
            {
                VirtualizationSecurityComponent.DeviceGuard => State with { DeviceGuard = value },
                VirtualizationSecurityComponent.Hvci => State with { Hvci = value },
                VirtualizationSecurityComponent.Lsa => State with { Lsa = value },
                _ => throw new ArgumentOutOfRangeException(nameof(component), component, null),
            });

        private void MaybeFail(VirtualizationSecurityComponent component, Action write)
        {
            if (!_failurePending || component != _failureComponent)
            {
                write();
                return;
            }

            _failurePending = false;
            if (_failAfterWrite)
            {
                write();
            }

            throw new InvalidOperationException($"{component} の失敗注入");
        }
    }
}
