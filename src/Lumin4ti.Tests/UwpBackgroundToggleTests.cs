using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class UwpBackgroundToggleTests
{
    private sealed class FakeSettingsStore : IUwpBackgroundSettingsStore
    {
        public Dictionary<string, UwpBackgroundValues> Values { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<(string FamilyName, UwpBackgroundValues Values)> Writes { get; } = [];

        public int ReadManyCalls { get; private set; }

        public int WriteManyCalls { get; private set; }

        public IReadOnlyDictionary<string, UwpBackgroundValues> ReadMany(
            IReadOnlyList<string> familyNames,
            CancellationToken ct)
        {
            ReadManyCalls++;
            return familyNames.ToDictionary(
                familyName => familyName,
                familyName => Values.TryGetValue(familyName, out var value) ? value : default,
                StringComparer.OrdinalIgnoreCase);
        }

        public void WriteMany(
            IReadOnlyList<KeyValuePair<string, UwpBackgroundValues>> values,
            CancellationToken ct)
        {
            WriteManyCalls++;
            foreach (var (familyName, value) in values)
            {
                Writes.Add((familyName, value));
                Values[familyName] = value;
            }
        }
    }

    private string _tempDirectory = null!;
    private string JournalPath => Path.Combine(_tempDirectory, "uwp-background.json");

    [TestInitialize]
    public void SetUp()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "Lumin4ti.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [TestCleanup]
    public void TearDown()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task 状態は全対象の両方の値が一致した場合だけONになる()
    {
        var targets = new List<string> { "Package.A", "Package.B" };
        var settings = new FakeSettingsStore();
        settings.Values["Package.A"] = UwpBackgroundValues.Applied;
        settings.Values["Package.B"] = new UwpBackgroundValues(1, 0);
        var toggle = CreateToggle(targets, settings);

        Assert.AreEqual(false, await toggle.GetStateAsync());
        Assert.AreEqual(1, settings.ReadManyCalls);

        settings.Values["Package.B"] = UwpBackgroundValues.Applied;
        Assert.AreEqual(true, await toggle.GetStateAsync());
        Assert.AreEqual(2, settings.ReadManyCalls);
    }

    [TestMethod]
    public async Task ONからOFFで変更対象だけをjournalの元値へ戻す()
    {
        var targets = new List<string> { "Package.A", "Package.B" };
        var settings = new FakeSettingsStore();
        var originalA = new UwpBackgroundValues(null, 0);
        settings.Values["Package.A"] = originalA;
        // 適用前から同じ値だった対象は Lumin4ti の所有物にしない。
        settings.Values["Package.B"] = UwpBackgroundValues.Applied;
        var toggle = CreateToggle(targets, settings);

        var applied = await toggle.SetStateAsync(true);

        Assert.IsTrue(applied.Success, applied.Detail);
        Assert.AreEqual(UwpBackgroundValues.Applied, settings.Values["Package.A"]);
        Assert.AreEqual(UwpBackgroundValues.Applied, settings.Values["Package.B"]);
        Assert.AreEqual(true, await toggle.GetStateAsync());

        var load = UwpBackgroundJournalStore.Load(JournalPath);
        Assert.AreEqual(UwpBackgroundJournalLoadStatus.Valid, load.Status);
        Assert.AreEqual(UwpBackgroundJournal.CurrentSchemaVersion, load.Journal!.SchemaVersion);
        Assert.AreEqual(1, load.Journal.Entries!.Count);
        Assert.AreEqual("Package.A", load.Journal.Entries[0].FamilyName);
        Assert.AreEqual(0, Directory.EnumerateFiles(_tempDirectory, "*.tmp").Count());

        var restored = await toggle.SetStateAsync(false);

        Assert.IsTrue(restored.Success, restored.Detail);
        Assert.AreEqual(originalA, settings.Values["Package.A"]);
        Assert.AreEqual(UwpBackgroundValues.Applied, settings.Values["Package.B"]);
        Assert.IsFalse(File.Exists(JournalPath));
    }

    [TestMethod]
    public async Task OFFは外部変更とON後に追加されたパッケージを保持する()
    {
        var targets = new List<string> { "Package.A" };
        var settings = new FakeSettingsStore();
        settings.Values["Package.A"] = default;
        var toggle = CreateToggle(targets, settings);
        Assert.IsTrue((await toggle.SetStateAsync(true)).Success);

        var externalA = new UwpBackgroundValues(0, 1);
        var newPackage = new UwpBackgroundValues(7, 8);
        settings.Values["Package.A"] = externalA;
        settings.Values["Package.New"] = newPackage;
        targets.Add("Package.New");
        settings.Writes.Clear();

        var restored = await toggle.SetStateAsync(false);

        Assert.IsTrue(restored.Success, restored.Detail);
        Assert.AreEqual(externalA, settings.Values["Package.A"]);
        Assert.AreEqual(newPackage, settings.Values["Package.New"]);
        Assert.AreEqual(0, settings.Writes.Count);
        StringAssert.Contains(restored.Detail, "現在値を保持");
    }

    [TestMethod]
    public async Task 破損journalではOFFもONも設定を変更しない()
    {
        var targets = new List<string> { "Package.A" };
        var settings = new FakeSettingsStore();
        var original = new UwpBackgroundValues(5, 6);
        settings.Values["Package.A"] = original;
        File.WriteAllText(JournalPath, "{ broken-json");
        var toggle = CreateToggle(targets, settings);

        var restored = await toggle.SetStateAsync(false);
        var applied = await toggle.SetStateAsync(true);

        Assert.IsFalse(restored.Success);
        Assert.IsFalse(applied.Success);
        Assert.AreEqual(original, settings.Values["Package.A"]);
        Assert.AreEqual(0, settings.Writes.Count);
        Assert.IsTrue(File.Exists(JournalPath));
    }

    [TestMethod]
    public async Task journal欠損時のOFFは現在設定を削除しない()
    {
        var targets = new List<string> { "Package.A" };
        var settings = new FakeSettingsStore();
        var original = new UwpBackgroundValues(1, 0);
        settings.Values["Package.A"] = original;
        var toggle = CreateToggle(targets, settings);

        var restored = await toggle.SetStateAsync(false);

        Assert.IsTrue(restored.Success, restored.Detail);
        Assert.AreEqual(original, settings.Values["Package.A"]);
        Assert.AreEqual(0, settings.Writes.Count);
    }

    private UwpBackgroundToggle CreateToggle(
        List<string> targets,
        IUwpBackgroundSettingsStore settings) =>
        new(() => targets.ToArray(), settings, JournalPath);
}
