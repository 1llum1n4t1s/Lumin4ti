using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class CleanupPreferencesTests
{
    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public object SyncRoot { get; } = new();

        public int SaveCount { get; private set; }

        public Task SaveAsync(CancellationToken ct = default)
        {
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static readonly CleanupTarget[] SampleTargets =
    [
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Temp"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\CrashDumps"),
    ];

    private static FileCleanupAction CreateAction(ICleanupPreferences preferences) =>
        new("cleanup-user-temp", "test", "test", () => SampleTargets, preferences: preferences);

    [TestMethod]
    public void 外した対象だけが除外として保存される()
    {
        var settings = new FakeSettingsService();
        var preferences = new CleanupPreferences(settings);

        preferences.SetTargetEnabled("cleanup-user-temp", @"%LOCALAPPDATA%\Temp", enabled: false);

        Assert.IsFalse(preferences.IsTargetEnabled("cleanup-user-temp", @"%LOCALAPPDATA%\Temp"));
        Assert.IsTrue(preferences.IsTargetEnabled("cleanup-user-temp", @"%LOCALAPPDATA%\CrashDumps"));
        CollectionAssert.AreEqual(
            new[] { @"%LOCALAPPDATA%\Temp" },
            settings.Current.CleanupExclusions["cleanup-user-temp"]);
    }

    [TestMethod]
    public async Task 除外の更新は設定サービスと同じロックで守る()
    {
        // 自前のロックにすると、保存 (シリアライズ) が別スレッドで走っている最中に
        // 除外リストを書き換えてしまい、保存が「列挙中に変更された」で落ちる。
        var settings = new FakeSettingsService();
        var preferences = new CleanupPreferences(settings);
        using var updated = new ManualResetEventSlim();

        Task update;
        lock (settings.SyncRoot)
        {
            update = Task.Run(() =>
            {
                preferences.SetTargetEnabled("cleanup-user-temp", @"%LOCALAPPDATA%\Temp", enabled: false);
                updated.Set();
            });

            Assert.IsFalse(
                updated.Wait(TimeSpan.FromMilliseconds(300)),
                "保存側がロックを取っている間は除外の更新が進んではいけません");
        }

        await update;
        Assert.IsFalse(preferences.IsTargetEnabled("cleanup-user-temp", @"%LOCALAPPDATA%\Temp"));
    }

    [TestMethod]
    public void 除外の判定は大文字小文字を区別しない()
    {
        var preferences = new CleanupPreferences(new FakeSettingsService());

        preferences.SetTargetEnabled("cleanup-user-temp", @"%LOCALAPPDATA%\Temp", enabled: false);

        Assert.IsFalse(preferences.IsTargetEnabled("cleanup-user-temp", @"%localappdata%\temp"));
    }

    [TestMethod]
    public void 削除対象の一覧は外した対象を除く()
    {
        var preferences = new CleanupPreferences(new FakeSettingsService());
        var action = CreateAction(preferences);
        var excluded = FileCleanupAction.DescribeTarget(SampleTargets[0]);

        preferences.SetTargetEnabled(action.Id, excluded, enabled: false);

        CollectionAssert.DoesNotContain(
            action.EnumerateSelectedTargets().Select(FileCleanupAction.DescribeTarget).ToArray(),
            excluded);
        CollectionAssert.Contains(
            action.EnumerateAllTargets().Select(FileCleanupAction.DescribeTarget).ToArray(),
            excluded,
            "チェックリストには外した対象も表示し続ける必要があります");
    }

    [TestMethod]
    public async Task 全ての対象を外した項目は何も削除しない()
    {
        var preferences = new CleanupPreferences(new FakeSettingsService());
        var action = CreateAction(preferences);
        foreach (var target in action.EnumerateAllTargets())
        {
            preferences.SetTargetEnabled(action.Id, FileCleanupAction.DescribeTarget(target), enabled: false);
        }

        var result = await action.ExecuteAsync();

        Assert.AreEqual(MaintenanceActionStatus.Success, result.Status);
        StringAssert.Contains(result.Detail, "対象が選ばれていない");
    }

    [TestMethod]
    public async Task アプリ単位の選択時に旧パス単位の除外を整理する()
    {
        var preferences = new CleanupPreferences(new FakeSettingsService());
        var root = Path.Combine(Path.GetTempPath(), "Lumin4tiCleanupPreferencesTests", Guid.NewGuid().ToString("N"));
        try
        {
            var targets = new[]
            {
                CleanupTarget.Contents(Path.Combine(root, "Cache")),
                CleanupTarget.Contents(Path.Combine(root, "logs")),
            };
            Directory.CreateDirectory(targets[0].RawPath);
            await File.WriteAllTextAsync(Path.Combine(targets[0].RawPath, "data.bin"), "x");
            var action = new FileCleanupAction(
                "test-app-cache",
                "アプリキャッシュ",
                "アプリキャッシュ",
                () => targets,
                preferences: preferences,
                checkListKeySelector: _ => "Claude");
            var legacyKey = FileCleanupAction.DescribeTarget(targets[0]);
            preferences.SetTargetEnabled(action.Id, legacyKey, enabled: false);

            Assert.IsFalse(action.GetCheckListEntries().Single(e => e.Value == "Claude").IsSelected);
            Assert.HasCount(0, action.EnumerateSelectedTargets(),
                "画面で OFF のアプリは、旧形式の除外が一部だけでもアプリ全体を削除対象から外してください");

            await action.SetCheckListEntrySelectedAsync("Claude", selected: true);

            Assert.IsTrue(preferences.IsTargetEnabled(action.Id, legacyKey));
            Assert.IsTrue(action.EnumerateSelectedTargets().Contains(targets[0]));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task チェックの変更は設定の保存まで行う()
    {
        var settings = new FakeSettingsService();
        var preferences = new CleanupPreferences(settings);
        IMaintenanceCheckList action = CreateAction(preferences);

        await action.SetCheckListEntrySelectedAsync(@"%LOCALAPPDATA%\Temp", selected: false);

        Assert.AreEqual(1, settings.SaveCount);
        Assert.IsFalse(preferences.IsTargetEnabled("cleanup-user-temp", @"%LOCALAPPDATA%\Temp"));
    }
}
