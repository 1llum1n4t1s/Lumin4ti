using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class CleanupPreferencesTests
{
    private sealed class NoopExecutor : ICommandExecutor
    {
        public Task<CommandExecutionResult> RunAsync(
            string fileName,
            string arguments,
            CancellationToken ct = default,
            IProgress<string>? onOutputLine = null,
            TimeSpan? timeout = null) =>
            Task.FromResult(new CommandExecutionResult(true, string.Empty, 0, string.Empty, string.Empty));
    }

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

    [TestMethod]
    public void 未保存の対象は既定で有効()
    {
        var preferences = new CleanupPreferences(new FakeSettingsService());

        Assert.IsTrue(preferences.IsTargetEnabled("cleanup-user-temp", @"%LOCALAPPDATA%\Temp"));
    }

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
    public void 戻したときは除外リストごと畳む()
    {
        var settings = new FakeSettingsService();
        var preferences = new CleanupPreferences(settings);

        preferences.SetTargetEnabled("cleanup-user-temp", @"%LOCALAPPDATA%\Temp", enabled: false);
        preferences.SetTargetEnabled("cleanup-user-temp", @"%LOCALAPPDATA%\Temp", enabled: true);

        Assert.IsFalse(
            settings.Current.CleanupExclusions.ContainsKey("cleanup-user-temp"),
            "空の除外リストを残すと設定ファイルが項目 Id で埋まっていきます");
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
    public void 定期実行の項目は未設定なら既定セットを返す()
    {
        var preferences = new CleanupPreferences(new FakeSettingsService());

        CollectionAssert.AreEqual(
            CleanupPreferences.DefaultScheduledGroupIds.ToArray(),
            preferences.ScheduledGroupIds.ToArray());
    }

    [TestMethod]
    public void 定期実行の項目を外すと既定セットから減る()
    {
        var settings = new FakeSettingsService();
        var preferences = new CleanupPreferences(settings);

        preferences.SetScheduledGroupEnabled("cleanup-package-cache", enabled: false);

        CollectionAssert.DoesNotContain(preferences.ScheduledGroupIds.ToArray(), "cleanup-package-cache");
        CollectionAssert.Contains(preferences.ScheduledGroupIds.ToArray(), "cleanup-user-temp");
        Assert.IsNotNull(
            settings.Current.ScheduledCleanupGroupIds,
            "初回変更時に実効値を書き出しておかないと、既定セットの変更で選択が勝手に増減します");
    }

    [TestMethod]
    public void 定期実行を全て外した状態は既定セットへ戻らない()
    {
        var settings = new FakeSettingsService();
        var preferences = new CleanupPreferences(settings);

        foreach (var id in CleanupPreferences.DefaultScheduledGroupIds.ToArray())
        {
            preferences.SetScheduledGroupEnabled(id, enabled: false);
        }

        Assert.AreEqual(0, preferences.ScheduledGroupIds.Count);
    }

    [TestMethod]
    public void 削除対象の一覧は外した対象を除く()
    {
        var preferences = new CleanupPreferences(new FakeSettingsService());
        var action = FileCleanupGroups.CreateUserTemp(preferences);
        var excluded = FileCleanupAction.DescribeTarget(FileCleanupGroups.UserTempTargets[0]);

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
        var action = FileCleanupGroups.CreateUserTemp(preferences);
        foreach (var target in action.EnumerateAllTargets())
        {
            preferences.SetTargetEnabled(action.Id, FileCleanupAction.DescribeTarget(target), enabled: false);
        }

        var result = await action.ExecuteAsync();

        Assert.AreEqual(MaintenanceActionStatus.Success, result.Status);
        StringAssert.Contains(result.Detail, "対象が選ばれていない");
    }

    [TestMethod]
    public void ブラウザのチェックリストはブラウザ単位に畳む()
    {
        // プロファイル数 × キャッシュ種別で対象は数千件になるため、チェックは
        // インストール済みブラウザの数までしか増えてはいけない。
        var action = FileCleanupGroups.CreateBrowserCache();
        var installedRoots = FileCleanupGroups.BrowserRoots
            .Count(raw => FileCleanupEngine.TryResolve(raw, out var root, out _) && Directory.Exists(root));

        var entries = action.GetCheckListEntries();

        Assert.AreEqual(installedRoots, entries.Count);
        CollectionAssert.IsSubsetOf(
            entries.Select(e => e.Value).ToArray(),
            FileCleanupGroups.BrowserRoots,
            "保存キーは未展開のルートにして、ユーザー名に依存させないでください");
        Assert.IsTrue(
            entries.All(e => !e.Label.Contains('%') && !Path.IsPathFullyQualified(e.Label)),
            "画面には保存キーのパスでなくブラウザ名を表示してください");
    }

    [TestMethod]
    public void ブラウザを1つ外すとその配下の対象がすべて外れる()
    {
        var preferences = new CleanupPreferences(new FakeSettingsService());
        var action = FileCleanupGroups.CreateBrowserCache(preferences);
        var entries = action.GetCheckListEntries();
        if (entries.Count == 0)
        {
            Assert.Inconclusive("Chromium 系ブラウザが 1 つも入っていない環境です");
        }

        var excluded = entries[0].Value;
        preferences.SetTargetEnabled(action.Id, excluded, enabled: false);

        Assert.IsFalse(
            action.EnumerateSelectedTargets().Any(t => action.GetCheckListKey(t) == excluded),
            "チェックを外したブラウザの対象が残っています");
        Assert.IsTrue(
            action.EnumerateAllTargets().Any(t => action.GetCheckListKey(t) == excluded),
            "一覧には外したブラウザも表示し続ける必要があります");
    }

    [TestMethod]
    public void アプリキャッシュのチェックリストはアプリ単位に畳む()
    {
        var action = FileCleanupGroups.CreateAppCache();

        var entries = action.GetCheckListEntries();

        CollectionAssert.AreEquivalent(
            new[]
            {
                "Antigravity", "Aqua Voice", "Claude", "Cursor", "Discord",
                "AMD", "NVIDIA", "Logitech G HUB",
            },
            entries.Select(e => e.Value).ToArray());
        Assert.IsTrue(entries.All(e => e.Label == e.Value));
        Assert.IsTrue(
            FileCleanupGroups.AppCacheTargets.All(t =>
                entries.Any(e => string.Equals(
                    e.Value,
                    FileCleanupGroups.GetAppCacheGroupName(t),
                    StringComparison.OrdinalIgnoreCase))),
            "すべての削除対象がアプリ名へ分類されている必要があります");
    }

    [TestMethod]
    public void アプリを1つ外すとその配下のキャッシュとログがすべて外れる()
    {
        var preferences = new CleanupPreferences(new FakeSettingsService());
        var action = FileCleanupGroups.CreateAppCache(preferences);

        preferences.SetTargetEnabled(action.Id, "Claude", enabled: false);

        Assert.IsFalse(
            action.EnumerateSelectedTargets().Any(t =>
                FileCleanupGroups.GetAppCacheGroupName(t) == "Claude"));
        Assert.IsTrue(
            action.EnumerateSelectedTargets().Any(t =>
                FileCleanupGroups.GetAppCacheGroupName(t) == "Cursor"));
    }

    [TestMethod]
    public async Task アプリ単位の選択時に旧パス単位の除外を整理する()
    {
        var preferences = new CleanupPreferences(new FakeSettingsService());
        var action = FileCleanupGroups.CreateAppCache(preferences);
        var claudeTargets = FileCleanupGroups.AppCacheTargets.Where(t =>
            FileCleanupGroups.GetAppCacheGroupName(t) == "Claude").ToArray();
        Assert.IsTrue(claudeTargets.Length > 1, "複数パスを 1 アプリへ畳む回帰ケースが必要です");
        var claudeTarget = claudeTargets[0];
        var legacyKey = FileCleanupAction.DescribeTarget(claudeTarget);
        preferences.SetTargetEnabled(action.Id, legacyKey, enabled: false);

        Assert.IsFalse(action.GetCheckListEntries().Single(e => e.Value == "Claude").IsSelected);
        Assert.IsFalse(
            action.EnumerateSelectedTargets().Any(t =>
                FileCleanupGroups.GetAppCacheGroupName(t) == "Claude"),
            "画面で OFF のアプリは、旧形式の除外が一部だけでもアプリ全体を削除対象から外してください");

        await action.SetCheckListEntrySelectedAsync("Claude", selected: true);

        Assert.IsTrue(preferences.IsTargetEnabled(action.Id, legacyKey));
        Assert.IsTrue(action.EnumerateSelectedTargets().Contains(claudeTarget));
    }

    [TestMethod]
    public void 各削除項目のチェックリストはアプリまたは用途単位に畳む()
    {
        var executor = new NoopExecutor();
        var expected = new Dictionary<FileCleanupAction, string[]>
        {
            [FileCleanupGroups.CreateUserTemp()] =
            [
                "Windows Temp", "CrashDumps", "Direct3D Shader Cache", "Internet Cache",
                "Microsoft IME", "Microsoft Office / Outlook", "CryptnetUrlCache",
            ],
            [FileCleanupGroups.CreateSystemTemp(executor)] =
            [
                "Windows Temp", "ETL Trace Logs", "Windows Update Logs", "Microsoft Edge Update",
                "Delivery Optimization", "Microsoft Defender",
            ],
            [FileCleanupGroups.CreatePackageCache()] =
            [
                "pip", "Yarn", "NuGet", "uv", "npm", "pnpm", "Cargo", "Gradle", "Bun",
            ],
            [FileCleanupGroups.CreateShellCache(executor)] = ["Windows Explorer", "Windows Font Cache"],
            [FileCleanupGroups.CreateWindowsUpdateCache(executor)] = ["Update Downloads"],
            [FileCleanupGroups.CreateOsIndex(executor)] = ["Prefetch", "Windows Search"],
        };

        foreach (var (action, expectedLabels) in expected)
        {
            var entries = action.GetCheckListEntries();
            CollectionAssert.AreEquivalent(expectedLabels, entries.Select(e => e.Label).ToArray(), action.Id);
            Assert.IsTrue(
                entries.All(e => !e.Label.Contains('%') && !Path.IsPathFullyQualified(e.Label)),
                $"{action.Id}: 画面へフルパスを表示しないでください");
        }
    }

    [TestMethod]
    public void 開発ツールを1つ外すと同じツールの全キャッシュが外れる()
    {
        var preferences = new CleanupPreferences(new FakeSettingsService());
        var action = FileCleanupGroups.CreatePackageCache(preferences);

        preferences.SetTargetEnabled(action.Id, "npm", enabled: false);

        var npmTargets = FileCleanupGroups.PackageCacheTargets
            .Where(t => FileCleanupGroups.GetPackageCacheGroupName(t) == "npm")
            .ToArray();
        Assert.HasCount(4, npmTargets);
        Assert.IsFalse(action.EnumerateSelectedTargets().Intersect(npmTargets).Any());
        Assert.IsTrue(
            action.EnumerateSelectedTargets().Any(t => FileCleanupGroups.GetPackageCacheGroupName(t) == "pnpm"));
    }

    [TestMethod]
    public async Task チェックの変更は設定の保存まで行う()
    {
        var settings = new FakeSettingsService();
        var preferences = new CleanupPreferences(settings);
        IMaintenanceCheckList action = FileCleanupGroups.CreateUserTemp(preferences);

        await action.SetCheckListEntrySelectedAsync(@"%LOCALAPPDATA%\Temp", selected: false);

        Assert.AreEqual(1, settings.SaveCount);
        Assert.IsFalse(preferences.IsTargetEnabled("cleanup-user-temp", @"%LOCALAPPDATA%\Temp"));
    }
}
