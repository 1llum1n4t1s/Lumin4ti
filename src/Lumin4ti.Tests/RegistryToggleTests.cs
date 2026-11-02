using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows.Actions;
using Microsoft.Win32;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class RegistryToggleTests
{
    private const string KeyPath = @"Software\Lumin4ti.Tests\RegistryToggle";

    [TestMethod]
    public async Task ONは適用値を書きOFFは元の独自設定でなく既定値または削除へ戻す()
    {
        var specs = new[]
        {
            Spec("Dword", RegistryValueKind.DWord, uint.MaxValue, 2),
            Spec("Qword", RegistryValueKind.QWord, 100L, 9_876_543_210L),
            Spec("String", RegistryValueKind.String, "applied", "default"),
            Spec("ExpandString", RegistryValueKind.ExpandString, "%TEMP%", "%SystemRoot%"),
            Spec("MultiString", RegistryValueKind.MultiString, new[] { "applied" }, new[] { "one", "two" }),
            Spec("Binary", RegistryValueKind.Binary, new byte[] { 1 }, new byte[] { 0, 255 }),
            Spec("None", RegistryValueKind.None, new byte[] { 2 }, new byte[] { 10, 20 }),
            Spec("Missing", RegistryValueKind.String, "applied"),
        };
        var registry = new FakeRegistryValueAccessor();
        foreach (var spec in specs) registry.Set(spec, RegistryValueSnapshot.FromRegistry(RegistryValueKind.String, "custom"));
        var toggle = CreateToggle(specs, registry);

        Assert.AreEqual(false, await toggle.GetStateAsync());
        Assert.IsTrue(toggle.IsCustomState);
        Assert.IsTrue((await toggle.SetStateAsync(true)).Success);
        Assert.AreEqual(true, await toggle.GetStateAsync());
        Assert.IsFalse(toggle.IsCustomState);
        Assert.IsTrue((await toggle.SetStateAsync(false)).Success);
        foreach (var spec in specs)
        {
            var expected = spec.DefaultValue is null ? RegistryValueSnapshot.Missing()
                : RegistryValueSnapshot.FromRegistry(spec.Kind, spec.DefaultValue);
            AssertSnapshot(expected, registry.Read(spec), spec.Name);
        }
        Assert.AreEqual(false, await toggle.GetStateAsync());
        Assert.IsFalse(toggle.IsCustomState);
    }

    [TestMethod]
    public async Task メニュー遅延の独自値からONを経由せずOFFの既定値へ変更する()
    {
        var spec = Spec("MenuShowDelay", RegistryValueKind.String, "0", "400");
        var registry = new FakeRegistryValueAccessor();
        registry.Set(spec, RegistryValueSnapshot.FromRegistry(RegistryValueKind.String, "100"));
        var toggle = CreateToggle([spec], registry);

        Assert.AreEqual(false, await toggle.GetStateAsync());
        Assert.IsTrue(toggle.IsCustomState);
        Assert.IsTrue((await toggle.SetStateAsync(false)).Success);
        AssertSnapshot(RegistryValueSnapshot.FromRegistry(RegistryValueKind.String, "400"), registry.Read(spec), spec.Name);
        Assert.AreEqual(false, await toggle.GetStateAsync());
        Assert.IsFalse(toggle.IsCustomState);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task 複数値の途中失敗は開始直前の全レジストリ型と存在有無へ補償する(bool on)
    {
        var specs = new[]
        {
            Spec("Dword", RegistryValueKind.DWord, 1, 0),
            Spec("String", RegistryValueKind.String, "applied"),
            Spec("Missing", RegistryValueKind.Binary, new byte[] { 1 }),
            Spec("Binary", RegistryValueKind.Binary, new byte[] { 2 }),
            Spec("MultiString", RegistryValueKind.MultiString, new[] { "applied" }),
            Spec("None", RegistryValueKind.None, new byte[] { 3 }),
            Spec("Plain", RegistryValueKind.String, "applied"),
            Spec("Last", RegistryValueKind.DWord, 1),
        };
        var originals = new[]
        {
            RegistryValueSnapshot.FromRegistry(RegistryValueKind.QWord, 100L),
            RegistryValueSnapshot.FromRegistry(RegistryValueKind.ExpandString, "%TEMP%"),
            RegistryValueSnapshot.Missing(),
            RegistryValueSnapshot.FromRegistry(RegistryValueKind.Binary, new byte[] { 0, 255 }),
            RegistryValueSnapshot.FromRegistry(RegistryValueKind.MultiString, new[] { "one", "two" }),
            RegistryValueSnapshot.FromRegistry(RegistryValueKind.None, new byte[] { 10, 20 }),
            RegistryValueSnapshot.FromRegistry(RegistryValueKind.String, "custom"),
            RegistryValueSnapshot.Dword(unchecked((int)uint.MaxValue)),
        };
        var registry = new FakeRegistryValueAccessor();
        for (var i = 0; i < specs.Length; i++) registry.Set(specs[i], originals[i]);
        registry.FailAfterWrite(specs.Length);

        var result = await CreateToggle(specs, registry).SetStateAsync(on);

        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        StringAssert.Contains(result.Detail, "開始前の状態へ補償しました");
        for (var i = 0; i < specs.Length; i++) AssertSnapshot(originals[i], registry.Read(specs[i]), specs[i].Name);
    }

    [TestMethod]
    public async Task 再ONの失敗は初回値でなく今回の開始前状態へ補償する()
    {
        var specs = new[] { Spec("One", RegistryValueKind.DWord, 1), Spec("Two", RegistryValueKind.DWord, 1) };
        var registry = new FakeRegistryValueAccessor();
        var toggle = CreateToggle(specs, registry);
        Assert.IsTrue((await toggle.SetStateAsync(true)).Success);
        registry.Set(specs[0], RegistryValueSnapshot.Dword(7));
        registry.Set(specs[1], RegistryValueSnapshot.Dword(8));
        registry.FailAfterWrite(registry.WriteAttempts + 2);

        Assert.IsFalse((await toggle.SetStateAsync(true)).Success);
        AssertSnapshot(RegistryValueSnapshot.Dword(7), registry.Read(specs[0]), "One");
        AssertSnapshot(RegistryValueSnapshot.Dword(8), registry.Read(specs[1]), "Two");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task 準備中のキャンセルは書き込み前に中断する(bool on)
    {
        var specs = new[] { Spec("Value", RegistryValueKind.DWord, 1) };
        using var cancellation = new CancellationTokenSource();
        var registry = new FakeRegistryValueAccessor { AfterRead = cancellation.Cancel };
        registry.Set(specs[0], RegistryValueSnapshot.Dword(7));

        await Assert.ThrowsAsync<OperationCanceledException>(() => CreateToggle(specs, registry).SetStateAsync(on, cancellation.Token));

        Assert.AreEqual(0, registry.WriteAttempts);
        AssertSnapshot(RegistryValueSnapshot.Dword(7), registry.Read(specs[0]), "Value");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task 書き込み途中のキャンセルは全値を補償してから通知する(bool on)
    {
        var specs = new[] { Spec("One", RegistryValueKind.DWord, 1), Spec("Two", RegistryValueKind.DWord, 1) };
        using var cancellation = new CancellationTokenSource();
        var registry = new FakeRegistryValueAccessor { AfterWrite = cancellation.Cancel };
        registry.Set(specs[0], RegistryValueSnapshot.Dword(7));
        registry.Set(specs[1], RegistryValueSnapshot.Dword(8));

        await Assert.ThrowsAsync<OperationCanceledException>(() => CreateToggle(specs, registry).SetStateAsync(on, cancellation.Token));

        AssertSnapshot(RegistryValueSnapshot.Dword(7), registry.Read(specs[0]), "One");
        AssertSnapshot(RegistryValueSnapshot.Dword(8), registry.Read(specs[1]), "Two");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task 補償の一件が失敗しても残りを補償して部分適用を報告する(bool on)
    {
        var specs = new[] { Spec("One", RegistryValueKind.DWord, 1), Spec("Two", RegistryValueKind.DWord, 1) };
        var registry = new FakeRegistryValueAccessor();
        registry.Set(specs[0], RegistryValueSnapshot.Dword(7));
        registry.Set(specs[1], RegistryValueSnapshot.Dword(8));
        registry.FailAfterWrite(2);
        registry.FailBeforeWrite(3);

        var result = await CreateToggle(specs, registry).SetStateAsync(on);

        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        StringAssert.Contains(result.Detail, "補償にも失敗");
        StringAssert.Contains(result.Detail, "部分適用");
        AssertSnapshot(RegistryValueSnapshot.Dword(7), registry.Read(specs[0]), "One");
        Assert.AreEqual(4, registry.WriteAttempts);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task 未着手値の外部変更は途中キャンセルの補償で上書きしない(bool on)
    {
        var specs = new[] { Spec("One", RegistryValueKind.DWord, 1), Spec("Two", RegistryValueKind.DWord, 1) };
        using var cancellation = new CancellationTokenSource();
        var registry = new FakeRegistryValueAccessor();
        registry.Set(specs[0], RegistryValueSnapshot.Dword(7));
        registry.Set(specs[1], RegistryValueSnapshot.Dword(8));
        registry.AfterWrite = () =>
        {
            if (registry.WriteAttempts != 1) return;
            registry.Set(specs[1], RegistryValueSnapshot.Dword(99));
            cancellation.Cancel();
        };

        var result = await CreateToggle(specs, registry).SetStateAsync(on, cancellation.Token);

        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        StringAssert.Contains(result.Detail, "Two の実状態が開始前と一致しません");
        StringAssert.Contains(result.Detail, "外部変更または補償不一致");
        AssertSnapshot(RegistryValueSnapshot.Dword(7), registry.Read(specs[0]), "One");
        AssertSnapshot(RegistryValueSnapshot.Dword(99), registry.Read(specs[1]), "Two");
        CollectionAssert.AreEqual(new[] { "One", "One" }, registry.WrittenNames.ToArray());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task 試行済み値の外部変更は補償で上書きせず失敗として報告する(bool on)
    {
        var specs = new[] { Spec("One", RegistryValueKind.DWord, 1), Spec("Two", RegistryValueKind.DWord, 1) };
        using var cancellation = new CancellationTokenSource();
        var registry = new FakeRegistryValueAccessor();
        registry.Set(specs[0], RegistryValueSnapshot.Dword(7));
        registry.Set(specs[1], RegistryValueSnapshot.Dword(8));
        registry.AfterWrite = () =>
        {
            registry.Set(specs[0], RegistryValueSnapshot.Dword(99));
            cancellation.Cancel();
        };

        var result = await CreateToggle(specs, registry).SetStateAsync(on, cancellation.Token);

        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        StringAssert.Contains(result.Detail, "外部変更");
        StringAssert.Contains(result.Detail, "その値を保持");
        AssertSnapshot(RegistryValueSnapshot.Dword(99), registry.Read(specs[0]), "One");
        AssertSnapshot(RegistryValueSnapshot.Dword(8), registry.Read(specs[1]), "Two");
        CollectionAssert.AreEqual(new[] { "One" }, registry.WrittenNames.ToArray());
    }

    [TestMethod]
    public async Task 未着手値の補償後照会が失敗しても全件の診断を残す()
    {
        var specs = new[]
        {
            Spec("One", RegistryValueKind.DWord, 1),
            Spec("Two", RegistryValueKind.DWord, 1),
            Spec("Three", RegistryValueKind.DWord, 1),
        };
        using var cancellation = new CancellationTokenSource();
        var registry = new FakeRegistryValueAccessor();
        foreach (var spec in specs) registry.Set(spec, RegistryValueSnapshot.Dword(7));
        registry.AfterWrite = () =>
        {
            if (registry.WriteAttempts != 1) return;
            registry.BeforeRead = spec =>
            {
                if (spec.Name != "One") throw new IOException($"{spec.Name} 読取拒否");
            };
            cancellation.Cancel();
        };

        var result = await CreateToggle(specs, registry).SetStateAsync(true, cancellation.Token);

        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        StringAssert.Contains(result.Detail, "Two の補償後の実状態を確認できません");
        StringAssert.Contains(result.Detail, "Three の補償後の実状態を確認できません");
        CollectionAssert.AreEqual(new[] { "One", "One" }, registry.WrittenNames.ToArray());
        registry.BeforeRead = null;
        foreach (var spec in specs) AssertSnapshot(RegistryValueSnapshot.Dword(7), registry.Read(spec), spec.Name);
    }

    [TestMethod]
    public async Task キャンセル後の補償失敗をキャンセル扱いで隠さない()
    {
        var specs = new[] { Spec("Value", RegistryValueKind.DWord, 1) };
        using var cancellation = new CancellationTokenSource();
        var registry = new FakeRegistryValueAccessor { AfterWrite = cancellation.Cancel };
        registry.Set(specs[0], RegistryValueSnapshot.Dword(7));
        registry.FailBeforeWrite(2);

        var result = await CreateToggle(specs, registry).SetStateAsync(true, cancellation.Token);

        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        StringAssert.Contains(result.Detail, "補償にも失敗");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task 書き込みが反映されなければ成功とせず開始前へ補償する(bool on)
    {
        var specs = new[] { Spec("Value", RegistryValueKind.DWord, 1, 0) };
        var registry = new FakeRegistryValueAccessor { IgnoreFirstWrite = true };
        registry.Set(specs[0], RegistryValueSnapshot.Dword(7));

        var result = await CreateToggle(specs, registry).SetStateAsync(on);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Detail, "実状態が要求値と一致しません");
        AssertSnapshot(RegistryValueSnapshot.Dword(7), registry.Read(specs[0]), "Value");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task 後半の仕様型が不正なら一件も変更しない(bool on)
    {
        var specs = new[] { Spec("One", RegistryValueKind.DWord, 1, 0), Spec("Two", RegistryValueKind.DWord, "invalid", "invalid") };
        var registry = new FakeRegistryValueAccessor();

        Assert.IsFalse((await CreateToggle(specs, registry).SetStateAsync(on)).Success);
        Assert.AreEqual(0, registry.WriteAttempts);
    }

    [TestMethod]
    public async Task 状態判定は値だけでなくRegistryValueKindも照合する()
    {
        var spec = Spec("Value", RegistryValueKind.String, "1");
        var registry = new FakeRegistryValueAccessor();
        registry.Set(spec, RegistryValueSnapshot.Dword(1));
        var toggle = CreateToggle([spec], registry);
        Assert.AreEqual(false, await toggle.GetStateAsync());
        registry.Set(spec, RegistryValueSnapshot.FromRegistry(RegistryValueKind.String, "1"));
        Assert.AreEqual(true, await toggle.GetStateAsync());
    }

    [TestMethod]
    public async Task 状態取得と切替は呼出元SynchronizationContext上でレジストリへアクセスしない()
    {
        var specs = new[] { Spec("Value", RegistryValueKind.DWord, 1) };
        var registry = new FakeRegistryValueAccessor();
        registry.Set(specs[0], RegistryValueSnapshot.Dword(0));
        var toggle = CreateToggle(specs, registry);
        var callingContext = new SynchronizationContext();
        var previousContext = SynchronizationContext.Current;

        Task<bool?> stateTask;
        SynchronizationContext.SetSynchronizationContext(callingContext);
        try
        {
            stateTask = toggle.GetStateAsync();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        Assert.AreEqual(false, await stateTask);
        Assert.IsGreaterThan(0, registry.AccessContexts.Count);
        Assert.IsFalse(registry.AccessContexts.Any(context => ReferenceEquals(context, callingContext)));

        registry.AccessContexts.Clear();
        Task<MaintenanceActionResult> setTask;
        SynchronizationContext.SetSynchronizationContext(callingContext);
        try
        {
            setTask = toggle.SetStateAsync(true);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        Assert.IsTrue((await setTask).Success);
        Assert.IsGreaterThan(0, registry.AccessContexts.Count);
        Assert.IsFalse(registry.AccessContexts.Any(context => ReferenceEquals(context, callingContext)));
    }

    private static RegistryToggle CreateToggle(IReadOnlyList<RegistryToggleSpec> specs, FakeRegistryValueAccessor registry) =>
        new("registry-toggle-test", "テスト項目", "テスト説明", CommandCategory.Performance, specs, false, false, registry);

    private static RegistryToggleSpec Spec(string name, RegistryValueKind kind, object applied, object? defaultValue = null) =>
        new(RegistryHive.CurrentUser, KeyPath, name, kind, applied, defaultValue);

    private static void AssertSnapshot(RegistryValueSnapshot expected, RegistryValueSnapshot actual, string name) =>
        Assert.IsTrue(expected.EquivalentTo(actual), name);

    private sealed class FakeRegistryValueAccessor : IRegistryValueAccessor
    {
        private readonly Dictionary<string, RegistryValueSnapshot> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> _failBeforeWrites = [];
        private readonly HashSet<int> _failAfterWrites = [];
        public int WriteAttempts { get; private set; }
        public Action? AfterRead { get; init; }
        public Action<RegistryToggleSpec>? BeforeRead { get; set; }
        public Action? AfterWrite { get; set; }
        public bool IgnoreFirstWrite { get; init; }
        public List<SynchronizationContext?> AccessContexts { get; } = [];
        public List<string> WrittenNames { get; } = [];

        public RegistryValueSnapshot Read(RegistryToggleSpec spec)
        {
            AccessContexts.Add(SynchronizationContext.Current);
            BeforeRead?.Invoke(spec);
            var value = _values.TryGetValue(spec.Name, out var current) ? current : RegistryValueSnapshot.Missing();
            AfterRead?.Invoke();
            return value;
        }

        public void Write(RegistryToggleSpec spec, RegistryValueSnapshot value)
        {
            AccessContexts.Add(SynchronizationContext.Current);
            WriteAttempts++;
            WrittenNames.Add(spec.Name);
            if (_failBeforeWrites.Remove(WriteAttempts)) throw new IOException($"{WriteAttempts} 回目の書き込み前に失敗");
            if (!(IgnoreFirstWrite && WriteAttempts == 1)) Set(spec, value);
            AfterWrite?.Invoke();
            if (_failAfterWrites.Remove(WriteAttempts)) throw new IOException($"{WriteAttempts} 回目の書き込み後に失敗");
        }

        public void Set(RegistryToggleSpec spec, RegistryValueSnapshot value)
        {
            value.Validate();
            _values[spec.Name] = value;
        }
        public void FailBeforeWrite(int attempt) => _failBeforeWrites.Add(attempt);
        public void FailAfterWrite(int attempt) => _failAfterWrites.Add(attempt);
    }
}
