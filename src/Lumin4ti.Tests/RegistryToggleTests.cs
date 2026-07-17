using System.Text;
using System.Text.Json;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows.Actions;
using Microsoft.Win32;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class RegistryToggleTests
{
    private const string ToggleId = "registry-toggle-test";
    private const string KeyPath = @"Software\Lumin4ti.Tests\RegistryToggle";

    [TestMethod]
    public void バックアップは存在有無と全レジストリ型を保持して復元する()
    {
        var specs = new[]
        {
            Spec("Dword", RegistryValueKind.DWord, 0),
            Spec("Qword", RegistryValueKind.QWord, 0L),
            Spec("String", RegistryValueKind.String, "applied"),
            Spec("ExpandString", RegistryValueKind.ExpandString, "%TEMP%"),
            Spec("MultiString", RegistryValueKind.MultiString, new[] { "applied" }),
            Spec("Binary", RegistryValueKind.Binary, new byte[] { 1 }),
            Spec("None", RegistryValueKind.None, new byte[] { 2 }),
            Spec("Missing", RegistryValueKind.String, "applied"),
        };
        var originals = new[]
        {
            RegistryValueSnapshot.Dword(unchecked((int)0xFFFFFFFF)),
            RegistryValueSnapshot.FromRegistry(RegistryValueKind.QWord, 9_876_543_210L),
            RegistryValueSnapshot.FromRegistry(RegistryValueKind.String, ""),
            RegistryValueSnapshot.FromRegistry(RegistryValueKind.ExpandString, @"%SystemRoot%\System32"),
            RegistryValueSnapshot.FromRegistry(RegistryValueKind.MultiString, new[] { "one", "two" }),
            RegistryValueSnapshot.FromRegistry(RegistryValueKind.Binary, new byte[] { 0, 127, 255 }),
            RegistryValueSnapshot.FromRegistry(RegistryValueKind.None, new byte[] { 10, 20 }),
            RegistryValueSnapshot.Missing(),
        };
        var registry = new FakeRegistryValueAccessor();
        for (var index = 0; index < specs.Length; index++)
        {
            registry.Set(specs[index], originals[index]);
        }

        var storage = new MemoryRegistryBackupStorage();
        var backup = new RegistryValueBackup(storage, registry);
        backup.Save(ToggleId, specs);

        var json = storage.GetJson(BackupPath(ToggleId));
        using (var document = JsonDocument.Parse(json))
        {
            Assert.AreEqual(
                RegistryValueBackupDocument.CurrentSchemaVersion,
                document.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.AreEqual(specs.Length, document.RootElement.GetProperty("Entries").GetArrayLength());
            Assert.IsFalse(document.RootElement.GetProperty("Entries")[7].GetProperty("Value").GetProperty("Exists").GetBoolean());
        }

        foreach (var spec in specs)
        {
            registry.Set(spec, RegistryValueSnapshot.Missing());
        }

        var lines = new List<string>();
        var restored = backup.TryRestore(ToggleId, specs, lines);

        Assert.AreEqual(RegistryBackupRestoreStatus.Restored, restored.Status);
        Assert.IsFalse(storage.FileExists(BackupPath(ToggleId)));
        Assert.AreEqual(specs.Length, registry.WriteAttempts);
        Assert.AreEqual(specs.Length, lines.Count);
        for (var index = 0; index < specs.Length; index++)
        {
            AssertSnapshot(originals[index], registry.Read(specs[index]), specs[index].Name);
        }
    }

    [TestMethod]
    public void 既存の現行バックアップは再保存で上書きしない()
    {
        var specs = new[] { Spec("Value", RegistryValueKind.DWord, 1) };
        var original = RegistryValueSnapshot.FromRegistry(RegistryValueKind.String, "legacy");
        var registry = new FakeRegistryValueAccessor();
        registry.Set(specs[0], original);
        var storage = new MemoryRegistryBackupStorage();
        var backup = new RegistryValueBackup(storage, registry);

        backup.Save(ToggleId, specs);
        var firstJson = storage.GetJson(BackupPath(ToggleId));
        registry.Set(specs[0], RegistryValueSnapshot.Dword(123));
        backup.Save(ToggleId, specs);

        Assert.AreEqual(firstJson, storage.GetJson(BackupPath(ToggleId)));
        var restored = backup.TryRestore(ToggleId, specs, []);
        Assert.AreEqual(RegistryBackupRestoreStatus.Restored, restored.Status);
        AssertSnapshot(original, registry.Read(specs[0]), specs[0].Name);
    }

    [TestMethod]
    public void 旧Dictionary形式は復元も再保存も拒否して一件も書かない()
    {
        var specs = new[] { Spec("Value", RegistryValueKind.DWord, 1) };
        var registry = new FakeRegistryValueAccessor();
        registry.Set(specs[0], RegistryValueSnapshot.Dword(9));
        var storage = new MemoryRegistryBackupStorage();
        storage.SetJson(BackupPath(ToggleId), "{\"CurrentUser|legacy\":\"9\"}");
        var backup = new RegistryValueBackup(storage, registry);

        var restored = backup.TryRestore(ToggleId, specs, []);

        Assert.AreEqual(RegistryBackupRestoreStatus.Invalid, restored.Status);
        Assert.AreEqual(0, registry.WriteAttempts);
        Assert.IsTrue(storage.FileExists(BackupPath(ToggleId)));
        Assert.ThrowsExactly<InvalidDataException>(() => backup.Save(ToggleId, specs));
        Assert.AreEqual(0, registry.WriteAttempts);
        AssertSnapshot(RegistryValueSnapshot.Dword(9), registry.Read(specs[0]), specs[0].Name);
    }

    [TestMethod]
    public void 未対応schemaVersionはfailClosedで拒否する()
    {
        var specs = new[] { Spec("Value", RegistryValueKind.DWord, 1) };
        var registry = new FakeRegistryValueAccessor();
        registry.Set(specs[0], RegistryValueSnapshot.Dword(9));
        var storage = new MemoryRegistryBackupStorage();
        storage.SetJson(
            BackupPath(ToggleId),
            Lumin4tiJson.Serialize(new RegistryValueBackupDocument
            {
                SchemaVersion = RegistryValueBackupDocument.CurrentSchemaVersion + 1,
                Entries = [RegistryValueBackupEntry.Create(specs[0], RegistryValueSnapshot.Dword(5))],
            }));
        var backup = new RegistryValueBackup(storage, registry);

        var restored = backup.TryRestore(ToggleId, specs, []);

        Assert.AreEqual(RegistryBackupRestoreStatus.Invalid, restored.Status);
        StringAssert.Contains(restored.FailureReason, "schema version");
        Assert.AreEqual(0, registry.WriteAttempts);
        AssertSnapshot(RegistryValueSnapshot.Dword(9), registry.Read(specs[0]), specs[0].Name);
        Assert.IsTrue(storage.FileExists(BackupPath(ToggleId)));
    }

    [TestMethod]
    public void 復元計画の後半が不正でも先頭を含め一件も書かない()
    {
        var specs = new[]
        {
            Spec("First", RegistryValueKind.DWord, 1),
            Spec("Second", RegistryValueKind.DWord, 1),
        };
        var registry = new FakeRegistryValueAccessor();
        registry.Set(specs[0], RegistryValueSnapshot.Dword(100));
        registry.Set(specs[1], RegistryValueSnapshot.Dword(200));
        var storage = new MemoryRegistryBackupStorage();
        var invalid = new RegistryValueBackupDocument
        {
            SchemaVersion = RegistryValueBackupDocument.CurrentSchemaVersion,
            Entries =
            [
                RegistryValueBackupEntry.Create(specs[0], RegistryValueSnapshot.Dword(10)),
                RegistryValueBackupEntry.Create(specs[1], new RegistryValueSnapshot
                {
                    Exists = true,
                    Kind = RegistryValueKind.DWord,
                    StringValue = "wrong-field",
                }),
            ],
        };
        storage.SetJson(BackupPath(ToggleId), Lumin4tiJson.Serialize(invalid));
        var backup = new RegistryValueBackup(storage, registry);

        var restored = backup.TryRestore(ToggleId, specs, []);

        Assert.AreEqual(RegistryBackupRestoreStatus.Invalid, restored.Status);
        Assert.AreEqual(0, registry.WriteAttempts);
        AssertSnapshot(RegistryValueSnapshot.Dword(100), registry.Read(specs[0]), specs[0].Name);
        AssertSnapshot(RegistryValueSnapshot.Dword(200), registry.Read(specs[1]), specs[1].Name);
        Assert.IsTrue(storage.FileExists(BackupPath(ToggleId)));
    }

    [TestMethod]
    public async Task 複数specの適用途中で失敗すると保存済み全スナップショットへ即時補償する()
    {
        var specs = new[]
        {
            Spec("First", RegistryValueKind.DWord, 1),
            Spec("Second", RegistryValueKind.DWord, 2),
        };
        var originals = new[]
        {
            RegistryValueSnapshot.FromRegistry(RegistryValueKind.QWord, 123L),
            RegistryValueSnapshot.FromRegistry(RegistryValueKind.String, "legacy"),
        };
        var registry = new FakeRegistryValueAccessor();
        registry.Set(specs[0], originals[0]);
        registry.Set(specs[1], originals[1]);
        registry.FailAfterWrite(2);
        var storage = new MemoryRegistryBackupStorage();
        var toggle = CreateToggle(specs, registry, storage);

        var result = await toggle.SetStateAsync(true);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Detail, "開始前の状態へ補償しました");
        Assert.AreEqual(4, registry.WriteAttempts);
        AssertSnapshot(originals[0], registry.Read(specs[0]), specs[0].Name);
        AssertSnapshot(originals[1], registry.Read(specs[1]), specs[1].Name);
        Assert.IsFalse(storage.FileExists(BackupPath(ToggleId)));
    }

    [TestMethod]
    public async Task 適用失敗後の補償も失敗すると明示してバックアップを保持する()
    {
        var specs = new[]
        {
            Spec("First", RegistryValueKind.DWord, 1),
            Spec("Second", RegistryValueKind.DWord, 2),
        };
        var registry = new FakeRegistryValueAccessor();
        registry.Set(specs[0], RegistryValueSnapshot.Dword(10));
        registry.Set(specs[1], RegistryValueSnapshot.Dword(20));
        registry.FailAfterWrite(2);
        registry.FailBeforeWrite(3);
        var storage = new MemoryRegistryBackupStorage();
        var toggle = CreateToggle(specs, registry, storage);

        var result = await toggle.SetStateAsync(true);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Detail, "補償にも失敗しました");
        StringAssert.Contains(result.Detail, "部分適用の可能性があります");
        Assert.AreEqual(3, registry.WriteAttempts);
        Assert.IsTrue(storage.FileExists(BackupPath(ToggleId)));
    }

    [TestMethod]
    public async Task ON時に旧バックアップがあれば適用前に停止する()
    {
        var specs = new[] { Spec("Value", RegistryValueKind.DWord, 1) };
        var registry = new FakeRegistryValueAccessor();
        registry.Set(specs[0], RegistryValueSnapshot.Dword(9));
        var storage = new MemoryRegistryBackupStorage();
        storage.SetJson(BackupPath(ToggleId), "{\"legacy\":\"9\"}");
        var toggle = CreateToggle(specs, registry, storage);

        var result = await toggle.SetStateAsync(true);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Detail, "設定を変更しませんでした");
        Assert.AreEqual(0, registry.WriteAttempts);
        AssertSnapshot(RegistryValueSnapshot.Dword(9), registry.Read(specs[0]), specs[0].Name);
    }

    [TestMethod]
    public async Task OFF時に旧バックアップがあれば既定値へ逃げず停止する()
    {
        var specs = new[] { new RegistryToggleSpec(
            RegistryHive.CurrentUser,
            KeyPath,
            "Value",
            RegistryValueKind.DWord,
            1,
            7) };
        var registry = new FakeRegistryValueAccessor();
        registry.Set(specs[0], RegistryValueSnapshot.Dword(1));
        var storage = new MemoryRegistryBackupStorage();
        storage.SetJson(BackupPath(ToggleId), "{\"legacy\":\"1\"}");
        var toggle = CreateToggle(specs, registry, storage);

        var result = await toggle.SetStateAsync(false);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Detail, "設定を変更しませんでした");
        Assert.AreEqual(0, registry.WriteAttempts);
        AssertSnapshot(RegistryValueSnapshot.Dword(1), registry.Read(specs[0]), specs[0].Name);
    }

    [TestMethod]
    public async Task OFFの元値復元途中で失敗すると開始前の適用状態へ補償して再試行可能にする()
    {
        var specs = new[]
        {
            Spec("First", RegistryValueKind.DWord, 1),
            Spec("Second", RegistryValueKind.DWord, 2),
        };
        var originals = new[]
        {
            RegistryValueSnapshot.Dword(10),
            RegistryValueSnapshot.Dword(20),
        };
        var registry = new FakeRegistryValueAccessor();
        registry.Set(specs[0], originals[0]);
        registry.Set(specs[1], originals[1]);
        var storage = new MemoryRegistryBackupStorage();
        var backup = new RegistryValueBackup(storage, registry);
        backup.Save(ToggleId, specs);
        registry.Set(specs[0], RegistryValueSnapshot.Dword(1));
        registry.Set(specs[1], RegistryValueSnapshot.Dword(2));
        registry.FailAfterWrite(2);
        var toggle = CreateToggle(specs, registry, storage);

        var result = await toggle.SetStateAsync(false);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Detail, "開始前の状態へ補償しました");
        AssertSnapshot(RegistryValueSnapshot.Dword(1), registry.Read(specs[0]), specs[0].Name);
        AssertSnapshot(RegistryValueSnapshot.Dword(2), registry.Read(specs[1]), specs[1].Name);
        Assert.IsTrue(storage.FileExists(BackupPath(ToggleId)), "再試行用バックアップを保持する必要があります");
    }

    [TestMethod]
    public async Task OFFの既定値適用途中で失敗すると開始前の状態へ補償する()
    {
        var specs = new[]
        {
            new RegistryToggleSpec(RegistryHive.CurrentUser, KeyPath, "First", RegistryValueKind.DWord, 1, 10),
            new RegistryToggleSpec(RegistryHive.CurrentUser, KeyPath, "Second", RegistryValueKind.DWord, 2, 20),
        };
        var registry = new FakeRegistryValueAccessor();
        registry.Set(specs[0], RegistryValueSnapshot.Dword(1));
        registry.Set(specs[1], RegistryValueSnapshot.Dword(2));
        registry.FailAfterWrite(2);
        var toggle = CreateToggle(specs, registry, new MemoryRegistryBackupStorage());

        var result = await toggle.SetStateAsync(false);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Detail, "開始前の状態へ補償しました");
        AssertSnapshot(RegistryValueSnapshot.Dword(1), registry.Read(specs[0]), specs[0].Name);
        AssertSnapshot(RegistryValueSnapshot.Dword(2), registry.Read(specs[1]), specs[1].Name);
    }

    [TestMethod]
    public async Task 状態判定は値だけでなく元のRegistryValueKindも照合する()
    {
        var specs = new[] { Spec("Value", RegistryValueKind.String, "1") };
        var registry = new FakeRegistryValueAccessor();
        registry.Set(specs[0], RegistryValueSnapshot.Dword(1));
        var toggle = CreateToggle(specs, registry, new MemoryRegistryBackupStorage());

        Assert.AreEqual(false, await toggle.GetStateAsync());

        registry.Set(specs[0], RegistryValueSnapshot.FromRegistry(RegistryValueKind.String, "1"));
        Assert.AreEqual(true, await toggle.GetStateAsync());
    }

    [TestMethod]
    public async Task 状態取得と切替は呼出元SynchronizationContext上でレジストリへアクセスしない()
    {
        var specs = new[] { Spec("Value", RegistryValueKind.DWord, 1) };
        var registry = new FakeRegistryValueAccessor();
        registry.Set(specs[0], RegistryValueSnapshot.Dword(0));
        var toggle = CreateToggle(specs, registry, new MemoryRegistryBackupStorage());
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

    [TestMethod]
    public async Task ON準備中のキャンセルはレジストリ書込み前の境界で中断する()
    {
        var specs = new[] { Spec("Value", RegistryValueKind.DWord, 1) };
        var registry = new FakeRegistryValueAccessor();
        registry.Set(specs[0], RegistryValueSnapshot.Dword(0));
        using var cancellation = new CancellationTokenSource();
        var storage = new MemoryRegistryBackupStorage
        {
            AfterWriteNew = cancellation.Cancel,
        };
        var toggle = CreateToggle(specs, registry, storage);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => toggle.SetStateAsync(true, cancellation.Token));

        Assert.AreEqual(0, registry.WriteAttempts);
        Assert.IsTrue(storage.FileExists(BackupPath(ToggleId)));
    }

    private static RegistryToggle CreateToggle(
        IReadOnlyList<RegistryToggleSpec> specs,
        FakeRegistryValueAccessor registry,
        MemoryRegistryBackupStorage storage) =>
        new(
            ToggleId,
            "テスト",
            "テスト",
            CommandCategory.System,
            specs,
            requiresReboot: false,
            affectsExplorer: false,
            registry,
            new RegistryValueBackup(storage, registry));

    private static RegistryToggleSpec Spec(string name, RegistryValueKind kind, object appliedValue) =>
        new(RegistryHive.CurrentUser, KeyPath, name, kind, appliedValue);

    private static string BackupPath(string id) => Path.Combine("registry", id + ".json");

    private static void AssertSnapshot(
        RegistryValueSnapshot expected,
        RegistryValueSnapshot actual,
        string name) =>
        Assert.IsTrue(actual.EquivalentTo(expected), $"{name} の型付き値が一致しません。expected={expected}, actual={actual}");

    private sealed class MemoryRegistryBackupStorage : IRegistryBackupStorage
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

        public Action? AfterWriteNew { get; init; }

        public bool FileExists(string relativePath) => _files.ContainsKey(relativePath);

        public string ReadAllText(string relativePath) => _files[relativePath];

        public void WriteNewAtomically(string relativePath, Action<Stream> write)
        {
            if (_files.ContainsKey(relativePath))
            {
                throw new IOException("既存ファイルは上書きできません。");
            }

            using var stream = new MemoryStream();
            write(stream);
            _files.Add(relativePath, Encoding.UTF8.GetString(stream.ToArray()));
            AfterWriteNew?.Invoke();
        }

        public void Delete(string relativePath) => _files.Remove(relativePath);

        public void SetJson(string relativePath, string json) => _files[relativePath] = json;

        public string GetJson(string relativePath) => _files[relativePath];
    }

    private sealed class FakeRegistryValueAccessor : IRegistryValueAccessor
    {
        private readonly Dictionary<string, RegistryValueSnapshot> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> _failBeforeWrites = [];
        private readonly HashSet<int> _failAfterWrites = [];

        public int WriteAttempts { get; private set; }

        public List<SynchronizationContext?> AccessContexts { get; } = [];

        public RegistryValueSnapshot Read(RegistryToggleSpec spec)
        {
            AccessContexts.Add(SynchronizationContext.Current);
            return _values.TryGetValue(Location(spec), out var value) ? value : RegistryValueSnapshot.Missing();
        }

        public void Write(RegistryToggleSpec spec, RegistryValueSnapshot value)
        {
            AccessContexts.Add(SynchronizationContext.Current);
            WriteAttempts++;
            if (_failBeforeWrites.Remove(WriteAttempts))
            {
                throw new IOException($"{WriteAttempts} 回目の書き込み前に失敗");
            }

            value.Validate();
            _values[Location(spec)] = value;
            if (_failAfterWrites.Remove(WriteAttempts))
            {
                throw new IOException($"{WriteAttempts} 回目の書き込み後に失敗");
            }
        }

        public void Set(RegistryToggleSpec spec, RegistryValueSnapshot value)
        {
            value.Validate();
            _values[Location(spec)] = value;
        }

        public void FailBeforeWrite(int attempt) => _failBeforeWrites.Add(attempt);

        public void FailAfterWrite(int attempt) => _failAfterWrites.Add(attempt);

        private static string Location(RegistryToggleSpec spec) =>
            $"{(int)spec.Hive}|{spec.KeyPath}|{spec.Name}";
    }
}
