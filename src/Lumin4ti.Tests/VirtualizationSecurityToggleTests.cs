using Lumin4ti.Core.Services.Windows.Actions;
using Microsoft.Win32;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class VirtualizationSecurityToggleTests
{
    [TestMethod]
    public async Task ONは全項目を無効化しOFFは明示設定を解除する()
    {
        var platform = new FakePlatform(CustomState());
        var toggle = new VirtualizationSecurityToggle(platform);

        var applied = await toggle.SetStateAsync(true);

        Assert.IsTrue(applied.Success, applied.Detail);
        Assert.IsTrue(platform.State.IsFullyApplied);
        Assert.AreEqual(true, await toggle.GetStateAsync());

        var reset = await toggle.SetStateAsync(false);

        Assert.IsTrue(reset.Success, reset.Detail);
        Assert.IsTrue(platform.State.EquivalentTo(VirtualizationSecuritySnapshot.Default()));
        Assert.AreEqual(false, await toggle.GetStateAsync());
    }

    [TestMethod]
    public async Task OFFは初回でも独自構成の明示設定を解除する()
    {
        var platform = new FakePlatform(CustomState());
        var toggle = new VirtualizationSecurityToggle(platform);

        var reset = await toggle.SetStateAsync(false);

        Assert.IsTrue(reset.Success, reset.Detail);
        Assert.IsTrue(platform.State.EquivalentTo(VirtualizationSecuritySnapshot.Default()));
        StringAssert.Contains(reset.Detail!, "Windows の既定値とポリシー");
    }

    [TestMethod]
    public async Task 状態判定は全適用だけONで混在構成はOFFとする()
    {
        var platform = new FakePlatform(VirtualizationSecuritySnapshot.Applied());
        var toggle = new VirtualizationSecurityToggle(platform);

        Assert.AreEqual(true, await toggle.GetStateAsync());
        foreach (var component in Enum.GetValues<VirtualizationSecurityComponent>())
        {
            platform.State = component switch
            {
                VirtualizationSecurityComponent.HypervisorLaunchType =>
                    VirtualizationSecuritySnapshot.Applied() with { HypervisorLaunchType = BcdValueSnapshot.Missing() },
                VirtualizationSecurityComponent.DeviceGuard =>
                    VirtualizationSecuritySnapshot.Applied() with { DeviceGuard = RegistryValueSnapshot.Dword(1) },
                VirtualizationSecurityComponent.Hvci =>
                    VirtualizationSecuritySnapshot.Applied() with { Hvci = RegistryValueSnapshot.Dword(1) },
                _ => VirtualizationSecuritySnapshot.Applied() with { Lsa = RegistryValueSnapshot.Dword(1) },
            };
            Assert.AreEqual(false, await toggle.GetStateAsync(), component.ToString());
            Assert.IsNull(toggle.StateDiagnostic);
        }
    }

    [TestMethod]
    public async Task 状態読込例外は原因を残して不明とし回復後に解除する()
    {
        var platform = new FakePlatform(VirtualizationSecuritySnapshot.Default()) { ReadFailure = true };
        var toggle = new VirtualizationSecurityToggle(platform);

        Assert.IsNull(await toggle.GetStateAsync());
        StringAssert.Contains(toggle.StateFailureReason!, "読込失敗注入");

        platform.ReadFailure = false;
        Assert.AreEqual(false, await toggle.GetStateAsync());
        Assert.IsNull(toggle.StateFailureReason);
    }

    [TestMethod]
    public async Task ON途中で書込後に失敗しても存在型値を開始前へ補償する()
    {
        var original = CustomState();
        var platform = new FakePlatform(original);
        platform.InjectFailure(VirtualizationSecurityComponent.Lsa, afterWrite: true);
        var toggle = new VirtualizationSecurityToggle(platform);

        var result = await toggle.SetStateAsync(true);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(platform.State.EquivalentTo(original));
        StringAssert.Contains(result.Detail!, "開始前の状態へ補償");
        Assert.AreEqual(false, await toggle.GetStateAsync());
    }

    [TestMethod]
    public async Task OFF途中で失敗した場合は開始前へ補償して再試行できる()
    {
        var original = VirtualizationSecuritySnapshot.Applied();
        var platform = new FakePlatform(original);
        platform.InjectFailure(VirtualizationSecurityComponent.Hvci, afterWrite: true);
        var toggle = new VirtualizationSecurityToggle(platform);

        var result = await toggle.SetStateAsync(false);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(platform.State.EquivalentTo(original));
        Assert.AreEqual(true, await toggle.GetStateAsync());

        var retry = await toggle.SetStateAsync(false);
        Assert.IsTrue(retry.Success, retry.Detail);
        Assert.IsTrue(platform.State.EquivalentTo(VirtualizationSecuritySnapshot.Default()));
    }

    [TestMethod]
    public async Task OFF書込が反映されない場合は実読込照合で失敗にする()
    {
        var original = VirtualizationSecuritySnapshot.Applied();
        var platform = new FakePlatform(original) { IgnoreWrites = true };
        var toggle = new VirtualizationSecurityToggle(platform);

        var result = await toggle.SetStateAsync(false);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Detail!, "目標状態と一致しません");
        Assert.IsTrue(platform.State.EquivalentTo(original));
    }

    [TestMethod]
    public async Task 操作途中のキャンセルでも直前の型付き状態へ補償する()
    {
        using var cancellation = new CancellationTokenSource();
        var original = CustomState();
        var platform = new FakePlatform(original) { AfterRegistryWrite = cancellation.Cancel };
        var toggle = new VirtualizationSecurityToggle(platform);

        var result = await toggle.SetStateAsync(true, cancellation.Token);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(platform.State.EquivalentTo(original));
        StringAssert.Contains(result.Detail!, "開始前の状態へ補償");
    }

    [TestMethod]
    public async Task 補償書込が無視された場合は開始前との不一致を報告する()
    {
        var original = CustomState();
        var platform = new FakePlatform(original);
        platform.InjectFailure(VirtualizationSecurityComponent.Lsa, afterWrite: true);
        platform.OnFailure = () => platform.IgnoreWrites = true;
        var toggle = new VirtualizationSecurityToggle(platform);

        var result = await toggle.SetStateAsync(true);

        Assert.IsFalse(result.Success);
        Assert.IsFalse(platform.State.EquivalentTo(original));
        StringAssert.Contains(result.Detail!, "補償にも失敗しました");
        StringAssert.Contains(result.Detail!, "開始前の状態と一致しません");
        Assert.IsFalse(result.Detail!.Contains("開始前の状態へ補償しました", StringComparison.Ordinal));
        StringAssert.Contains(result.Detail!, "最終状態: ON");
    }

    [TestMethod]
    public async Task 補償後の読込失敗では補償成功を断定せず最終状態も不明とする()
    {
        var original = CustomState();
        var platform = new FakePlatform(original);
        platform.InjectFailure(VirtualizationSecurityComponent.Lsa, afterWrite: true);
        platform.OnFailure = () => platform.AfterBcdWrite = () => platform.ReadFailure = true;
        var toggle = new VirtualizationSecurityToggle(platform);

        var result = await toggle.SetStateAsync(true);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(platform.State.EquivalentTo(original));
        StringAssert.Contains(result.Detail!, "補償後の実状態を確認できません");
        StringAssert.Contains(result.Detail!, "読込失敗注入");
        Assert.IsFalse(result.Detail!.Contains("開始前の状態へ補償しました", StringComparison.Ordinal));
        StringAssert.Contains(result.Detail!, "最終状態: 判定不能");
    }

    [TestMethod]
    public async Task 後続処理の失敗前に外部変更された値は補償で上書きしない()
    {
        var original = CustomState();
        var platform = new FakePlatform(original);
        platform.InjectFailure(VirtualizationSecurityComponent.Hvci, afterWrite: true);
        platform.OnFailure = () => platform.State = platform.State with
        {
            DeviceGuard = RegistryValueSnapshot.Dword(7),
        };
        var toggle = new VirtualizationSecurityToggle(platform);

        var result = await toggle.SetStateAsync(true);

        Assert.IsFalse(result.Success);
        Assert.IsTrue(platform.State.DeviceGuard.EquivalentTo(RegistryValueSnapshot.Dword(7)));
        Assert.IsTrue(platform.State.Hvci.EquivalentTo(original.Hvci));
        Assert.IsTrue(platform.State.HypervisorLaunchType.EquivalentTo(original.HypervisorLaunchType));
        StringAssert.Contains(result.Detail!, "外部で変更された値を保持しました");
        StringAssert.Contains(result.Detail!, "開始前の状態と一致しません");
    }

    private static VirtualizationSecuritySnapshot CustomState() => new()
    {
        HypervisorLaunchType = BcdValueSnapshot.Present("Auto"),
        DeviceGuard = RegistryValueSnapshot.Missing(),
        Hvci = RegistryValueSnapshot.FromRegistry(RegistryValueKind.String, "legacy"),
        Lsa = RegistryValueSnapshot.FromRegistry(RegistryValueKind.QWord, 2L),
    };

    private sealed class FakePlatform(VirtualizationSecuritySnapshot initialState) : IVirtualizationSecurityPlatform
    {
        private VirtualizationSecurityComponent? _failureComponent;
        private bool _failAfterWrite;
        private bool _failurePending;

        public VirtualizationSecuritySnapshot State { get; set; } = initialState;
        public bool ReadFailure { get; set; }
        public bool IgnoreWrites { get; set; }
        public Action? AfterRegistryWrite { get; set; }
        public Action? OnFailure { get; set; }
        public Action? AfterBcdWrite { get; set; }

        public void InjectFailure(VirtualizationSecurityComponent component, bool afterWrite)
        {
            _failureComponent = component;
            _failAfterWrite = afterWrite;
            _failurePending = true;
        }

        public Task<BcdValueSnapshot> ReadHypervisorLaunchTypeAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (ReadFailure)
            {
                throw new InvalidOperationException("読込失敗注入");
            }
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
            if (IgnoreWrites)
            {
                return;
            }

            if (!_failurePending || component != _failureComponent)
            {
                write();
                if (component != VirtualizationSecurityComponent.HypervisorLaunchType)
                {
                    AfterRegistryWrite?.Invoke();
                }
                else
                {
                    AfterBcdWrite?.Invoke();
                }
                return;
            }

            _failurePending = false;
            if (_failAfterWrite)
            {
                write();
            }

            OnFailure?.Invoke();
            throw new InvalidOperationException($"{component} の失敗注入");
        }
    }
}
