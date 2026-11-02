using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Xml.Linq;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class RebootCacheCleanupTests
{
    private string _root = string.Empty;
    private const string Sid = "S-1-5-21-100-200-300-1001";
    private const string OtherSid = "S-1-5-21-100-200-300-1002";

    private sealed class Store : IRebootCleanupStore
    {
        internal readonly Dictionary<string, string> Files = [];
        internal string? FailWritePrefix;
        internal int Reads;
        internal bool ThrowAfterJobWrite;
        internal bool FailReads;
        internal bool ReverseNames;
        internal string? FailDelete;
        public IReadOnlyList<string> Names()
        {
            Reads++;
            if (FailReads) throw new IOException("queue read failure");
            return ReverseNames ? Files.Keys.OrderByDescending(name => name, StringComparer.Ordinal).ToArray() : Files.Keys.Order(StringComparer.Ordinal).ToArray();
        }
        public bool Exists(string name) => Files.ContainsKey(name);
        public string Read(string name) { Reads++; return Files[name]; }
        public void WriteNew(string name, string json)
        {
            if (FailWritePrefix is not null && name.StartsWith(FailWritePrefix, StringComparison.Ordinal)) throw new IOException("fake write failure");
            if (!Files.TryAdd(name, json)) throw new IOException("duplicate file");
            if (ThrowAfterJobWrite && name.StartsWith("job-", StringComparison.Ordinal))
            { ThrowAfterJobWrite = false; throw new IOException("validation failed after final move"); }
        }
        public void Delete(string name)
        {
            if (name == FailDelete) throw new IOException("delete failure");
            Files.Remove(name);
        }
    }

    private sealed class TaskRegistration : IRebootCleanupTask
    {
        internal readonly HashSet<string> RegisteredSids = [];
        internal bool Registered => RegisteredSids.Count > 0;
        internal bool FailEnsure;
        internal bool Foreign;
        internal int Ensures;
        internal int Removals;
        internal DateTimeOffset Expires;
        public void Ensure(string sid, DateTimeOffset expires)
        {
            Ensures++;
            if (Foreign) throw new SecurityException("foreign task");
            if (FailEnsure) throw new IOException("registration failure");
            RegisteredSids.Add(sid);
            Expires = expires;
        }
        public void RemoveIfOwned(string sid)
        {
            if (Foreign) throw new SecurityException("foreign task");
            Removals++;
            RegisteredSids.Remove(sid);
        }
    }

    private sealed class EnvironmentState(RebootCleanupProfile profile) : IRebootCleanupEnvironment
    {
        public string CurrentSid { get; set; } = Sid;
        public string HelperPath => @"C:\Program Files\Lumin4ti\current\Lumin4ti.RebootCleanup.exe";
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        public bool IsBootAccount { get; set; }
        public bool IsTrustedLauncher { get; set; } = true;
        public bool IsTrustedHelper { get; set; } = true;
        internal bool RejectProfile;
        internal bool RejectEnqueue;
        internal readonly List<string> ProfileReads = [];
        internal readonly Dictionary<string, SettingsState> SettingsBySid = [];
        internal SettingsState Settings => SettingsBySid.TryGetValue(CurrentSid, out var settings) ? settings : SettingsBySid[CurrentSid] = new SettingsState();
        internal ISettingsService? LoadedSettings;
        public RebootCleanupProfile GetProfile(string sid)
        {
            ProfileReads.Add(sid);
            if (RejectProfile) throw new IOException("profile unavailable");
            return profile;
        }
        public void ValidateEnqueueProfile(RebootCleanupProfile value, IReadOnlyList<CleanupTarget> selected)
        {
            if (RejectEnqueue) throw new IOException("redirected profile / original identity mismatch");
            Assert.AreEqual(profile, value);
        }
        public ISettingsService LoadSettings(RebootCleanupProfile value) => LoadedSettings ?? Settings;
    }

    private sealed class SettingsState : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public object SyncRoot { get; } = new();
        public SettingsLoadStatus LoadStatus { get; set; } = SettingsLoadStatus.Loaded;
        internal bool FailSave;
        internal int Saves;
        internal int Flushes;
        public Task SaveAsync(CancellationToken ct = default)
        {
            Saves++;
            if (FailSave) throw new IOException("settings save failure");
            LoadStatus = SettingsLoadStatus.Loaded;
            return Task.CompletedTask;
        }
        public Task FlushAsync(CancellationToken ct = default) { Flushes++; return Task.CompletedTask; }
    }

    private sealed class Lease : IDisposable { public void Dispose() { } }
    private sealed class Executor : ICommandExecutor
    {
        internal int Starts;
        public Task<CommandExecutionResult> RunAsync(string fileName, string arguments, CancellationToken ct = default,
            IProgress<string>? onOutputLine = null, TimeSpan? timeout = null)
        {
            if (arguments.StartsWith("start", StringComparison.Ordinal)) Starts++;
            return Task.FromResult(new CommandExecutionResult(true, string.Empty, 0, string.Empty, string.Empty));
        }
    }
    private sealed class Progress(Action<string> action) : IProgress<string> { public void Report(string value) => action(value); }
    private sealed class FailedSaveSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public object SyncRoot { get; } = new();
        public Task SaveAsync(CancellationToken ct = default) => Task.FromException(new IOException("settings save failure"));
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private (RebootCacheCleanupService Service, Store Store, TaskRegistration Task, EnvironmentState Environment) Create(
        Func<RebootCleanupProfile, string[], CancellationToken, Task<MaintenanceActionResult>>? execute = null, Func<IDisposable?>? lease = null,
        bool guiSettings = true, Action<RebootCleanupResult>? report = null)
    {
        var store = new Store();
        var task = new TaskRegistration();
        var environment = new EnvironmentState(new RebootCleanupProfile(Path.Combine(_root, "profile"), Path.Combine(_root, "windows")));
        var service = new RebootCacheCleanupService(store, task, environment,
            execute ?? ((_, _, _) => Task.FromResult(MaintenanceActionResult.Ok("done"))), lease ?? (() => new Lease()), (_, _) => Task.CompletedTask,
            settings: guiSettings ? environment.Settings : null, reportResult: report);
        return (service, store, task, environment);
    }

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "Lumin4tiRebootCleanupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }
    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public async Task 新規予約は設定の正常保存を必須としFailedLoad原本を保存しない()
    {
        var state = Create();
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        state.Environment.Settings.FailSave = true;
        Assert.IsFalse((await state.Service.QueueAsync([icon], [icon], default)).Success);
        Assert.AreEqual(1, state.Environment.Settings.Saves);
        Assert.HasCount(0, state.Store.Files);
        Assert.AreEqual(0, state.Task.Ensures);
        state.Environment.Settings.FailSave = false;
        state.Environment.Settings.LoadStatus = SettingsLoadStatus.Failed;
        Assert.IsFalse((await state.Service.QueueAsync([icon], [icon], default)).Success);
        Assert.AreEqual(1, state.Environment.Settings.Saves, "読込失敗の原本へ Save しない");
        state.Environment.Settings.LoadStatus = SettingsLoadStatus.Missing;
        Assert.IsTrue((await state.Service.QueueAsync([icon], [icon], default)).Success);
        Assert.AreEqual(2, state.Environment.Settings.Saves);
        Assert.AreEqual(1, state.Environment.Settings.Flushes);
    }

    [TestMethod]
    public async Task サインインの別設定instanceは保存せずUIが保存した最新設定を上書きしない()
    {
        var state = Create(guiSettings: false);
        var profile = state.Environment.GetProfile(Sid);
        var directory = Path.Combine(profile.RoamingAppData, "Lumin4ti");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new AppSettings { Locale = "ja_JP" }));
        var stale = new SettingsService(directory, path);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new AppSettings { Locale = "en_US" }));
        state.Environment.LoadedSettings = stale;
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        Assert.IsTrue((await state.Service.QueueAsync([icon], [icon], default)).Success);
        Assert.AreEqual("en_US", JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(path))!.Locale);
        Assert.AreEqual("ja_JP", stale.Current.Locale);
        Assert.AreEqual(0, state.Environment.Settings.Saves);
        state.Environment.LoadedSettings = null;
        state.Environment.Settings.LoadStatus = SettingsLoadStatus.Missing;
        Assert.IsFalse((await state.Service.QueueAsync([icon], [icon], default)).Success);
        Assert.AreEqual(0, state.Environment.Settings.Saves);
    }

    [TestMethod]
    public async Task 標準Roaming設定はsamehandleのbounded内容から既存共通JSON入口で読取専用にする()
    {
        var profile = new RebootCleanupProfile(Path.Combine(_root, "snapshot-profile"), Path.Combine(_root, "windows"));
        var directory = Path.Combine(profile.RoamingAppData, "Lumin4ti");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        const string json = "{\"Locale\":\"en_US\",\"CleanupExclusions\":{\"cleanup-shell-cache\":null}}";
        await File.WriteAllTextAsync(path, json);
        var snapshot = new RebootCleanupEnvironment().LoadSettings(profile);
        var ordinary = new SettingsService(directory, path);
        Assert.AreEqual(SettingsLoadStatus.Loaded, snapshot.LoadStatus);
        Assert.AreEqual(JsonSerializer.Serialize(ordinary.Current), JsonSerializer.Serialize(snapshot.Current));
        await Assert.ThrowsAsync<InvalidOperationException>(() => snapshot.SaveAsync());
        Assert.AreEqual(json, await File.ReadAllTextAsync(path));
        await File.WriteAllTextAsync(path, "{invalid");
        Assert.AreEqual(SettingsLoadStatus.Failed, new RebootCleanupEnvironment().LoadSettings(profile).LoadStatus);
    }

    [TestMethod]
    public async Task 旧世代の実成功も停止扱いへ置き換えず元結果を利用者ログへ出す()
    {
        var reported = new List<RebootCleanupResult>();
        var state = Create((_, _, _) => Task.FromResult(MaintenanceActionResult.Ok("gen1 boot success detail")),
            guiSettings: false, report: reported.Add);
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        var font = FileCleanupGroups.ShellCacheTargets[2];
        await state.Service.QueueAsync([icon], [icon], default);
        state.Environment.IsBootAccount = true;
        await state.Service.RunBootAsync();
        state.Environment.IsBootAccount = false;
        await state.Service.QueueAsync([font], [font], default);
        state.Service.ReportCurrentUserResults();
        Assert.HasCount(1, reported);
        Assert.IsTrue(reported[0].Success);
        StringAssert.Contains(reported[0].Detail, "gen1 boot success detail");
        Assert.AreEqual(Sid, reported[0].UserSid);
        Assert.AreEqual(1, state.Store.Files.Count, "最新pendingは保持する");
        Assert.IsTrue(state.Task.RegisteredSids.Contains(Sid));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task 起動時OFFを取消し再ONでも古い予約を復活させない(bool legacyExclusion)
    {
        var runs = 0;
        var state = Create((_, _, _) => { runs++; return Task.FromResult(MaintenanceActionResult.Ok()); });
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        await state.Service.QueueAsync([icon], [icon], default);
        var preferences = new CleanupPreferences(state.Environment.Settings);
        var key = legacyExclusion ? FileCleanupAction.DescribeTarget(icon) : "Windows Explorer";
        preferences.SetTargetEnabled("cleanup-shell-cache", key, false);
        state.Environment.IsBootAccount = true;
        await state.Service.RunBootAsync();
        Assert.AreEqual(0, runs);
        preferences.SetTargetEnabled("cleanup-shell-cache", key, true);
        await state.Service.RunBootAsync();
        Assert.AreEqual(0, runs);
        Assert.IsFalse(state.Task.Registered);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task 部分OFFの履歴も結果保存前crashや再ONで復活させずScheduledGroupIdsは混ぜない(bool crashBeforeResult)
    {
        var seen = new List<string[]>();
        var state = Create((_, ids, _) => { seen.Add(ids); return Task.FromResult(MaintenanceActionResult.Partial("locked")); });
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        var font = FileCleanupGroups.ShellCacheTargets[2];
        state.Environment.Settings.Current.ScheduledCleanupGroupIds = [];
        await state.Service.QueueAsync([icon, font], [icon, font], default);
        var preferences = new CleanupPreferences(state.Environment.Settings);
        preferences.SetTargetEnabled("cleanup-shell-cache", "Windows Explorer", false);
        state.Environment.IsBootAccount = true;
        if (crashBeforeResult) state.Store.FailWritePrefix = "result-";
        await state.Service.RunBootAsync();
        Assert.IsTrue(state.Store.Files.Keys.Any(name => name.StartsWith("selection-", StringComparison.Ordinal)));
        if (crashBeforeResult) Assert.IsFalse(state.Store.Files.Keys.Any(name => name.StartsWith("result-", StringComparison.Ordinal)));
        state.Store.FailWritePrefix = null;
        preferences.SetTargetEnabled("cleanup-shell-cache", "Windows Explorer", true);
        await state.Service.RunBootAsync();
        Assert.AreEqual(2, seen.Count);
        foreach (var ids in seen) CollectionAssert.AreEqual(new[] { "user-font" }, ids);
    }

    [TestMethod]
    public async Task 起動時の設定MissingやFailedは既定値で削除せず再試行へ残す()
    {
        var runs = 0;
        var state = Create((_, _, _) => { runs++; return Task.FromResult(MaintenanceActionResult.Ok()); });
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        await state.Service.QueueAsync([icon], [icon], default);
        state.Environment.IsBootAccount = true;
        state.Environment.Settings.LoadStatus = SettingsLoadStatus.Missing;
        await state.Service.RunBootAsync();
        state.Environment.Settings.LoadStatus = SettingsLoadStatus.Failed;
        await state.Service.RunBootAsync();
        Assert.AreEqual(0, runs);
        Assert.IsTrue(state.Task.Registered);
    }

    [TestMethod]
    public async Task 最終名作成後のthrowもcommitを再照合し複数SIDの意図を取り違えない()
    {
        var seen = new List<string>();
        var state = Create((_, ids, _) => { seen.AddRange(ids); return Task.FromResult(MaintenanceActionResult.Ok()); });
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        var font = FileCleanupGroups.ShellCacheTargets[2];
        state.Store.ThrowAfterJobWrite = true;
        Assert.IsTrue((await state.Service.QueueAsync([icon], [icon], default)).Success);
        state.Environment.CurrentSid = OtherSid;
        state.Store.ThrowAfterJobWrite = true;
        Assert.IsTrue((await state.Service.QueueAsync([font], [font], default)).Success);
        state.Environment.CurrentSid = Sid;
        state.Store.ThrowAfterJobWrite = true;
        var canceled = await state.Service.QueueAsync([], [], default);
        Assert.IsTrue(canceled.Success);
        Assert.AreEqual(0, canceled.PendingTargets);
        state.Environment.IsBootAccount = true;
        await state.Service.RunBootAsync();
        Assert.HasCount(0, seen);
        Assert.IsTrue(state.Task.RegisteredSids.Contains(OtherSid));
        state.Environment.CurrentSid = OtherSid;
        await state.Service.RunBootAsync();
        CollectionAssert.AreEqual(new[] { "user-font" }, seen);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task 旧marker失敗とGUID順逆でも最新tombstoneは旧job回収まで保持する(bool reverse)
    {
        var runs = 0;
        var state = Create((_, _, _) => { runs++; return Task.FromResult(MaintenanceActionResult.Ok()); });
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        await state.Service.QueueAsync([icon], [icon], default);
        state.Store.FailWritePrefix = "result-";
        await state.Service.QueueAsync([icon], [icon], default);
        Assert.IsTrue((await state.Service.QueueAsync([], [], default)).Success);
        state.Store.FailWritePrefix = null;
        state.Store.ReverseNames = reverse;
        var jobs = state.Store.Files.Where(item => item.Key.StartsWith("job-", StringComparison.Ordinal))
            .OrderBy(item => JsonSerializer.Deserialize<RebootCleanupRequest>(item.Value)!.Generation).ToArray();
        Assert.AreEqual(3, jobs.Length);
        state.Store.FailDelete = jobs[0].Key;
        state.Service.ReportCurrentUserResults();
        Assert.IsTrue(state.Store.Files.ContainsKey(jobs[^1].Key), "最新tombstoneを先に消してはならない");
        state.Environment.IsBootAccount = true;
        await state.Service.RunBootAsync();
        Assert.AreEqual(0, runs);
        state.Store.FailDelete = null;
        state.Service.ReportCurrentUserResults();
        Assert.HasCount(0, state.Store.Files);
        await state.Service.RunBootAsync();
        Assert.AreEqual(0, runs);
    }

    [TestMethod]
    public async Task 通常予約64件の上限でも既存SIDの取消をcommitし他SIDを保持する()
    {
        var state = Create();
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        for (var index = 0; index < 64; index++)
        {
            state.Environment.CurrentSid = $"S-1-5-21-100-200-300-{1001 + index}";
            Assert.IsTrue((await state.Service.QueueAsync([icon], [icon], default)).Success);
        }
        state.Environment.CurrentSid = "S-1-5-21-100-200-300-9999";
        Assert.IsFalse((await state.Service.QueueAsync([icon], [icon], default)).Success);
        state.Environment.CurrentSid = Sid;
        Assert.IsTrue((await state.Service.QueueAsync([], [], default)).Success);
        Assert.IsFalse(state.Task.RegisteredSids.Contains(Sid));
        Assert.AreEqual(63, state.Task.RegisteredSids.Count);
        var files = state.Store.Files.Count;
        Assert.IsTrue((await state.Service.QueueAsync([], [], default)).Success);
        Assert.AreEqual(files, state.Store.Files.Count, "取消済みの再取消で容量を増やさない");
        state.Service.ReportCurrentUserResults();
        Assert.AreEqual(63, state.Store.Files.Count);
        Assert.AreEqual(63, state.Task.RegisteredSids.Count);
    }

    [TestMethod]
    public async Task 全外しの取消保存失敗も読取不能もFailedにして旧予約の残存を明示する()
    {
        var state = Create();
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        await state.Service.QueueAsync([icon], [icon], default);
        var action = new FileCleanupAction("empty-failure", "test", "test", () => [], scheduleBlockedForReboot: true, rebootScheduler: state.Service);
        state.Store.FailWritePrefix = "job-";
        var failedSave = await action.ExecuteAsync();
        Assert.AreEqual(MaintenanceActionStatus.Failed, failedSave.Status);
        StringAssert.Contains(failedSave.Detail, "以前の起動時予約は変更できず残っています");
        state.Store.FailWritePrefix = null;
        state.Store.FailReads = true;
        var failedRead = await action.ExecuteAsync();
        Assert.AreEqual(MaintenanceActionStatus.Failed, failedRead.Status);
        StringAssert.Contains(failedRead.Detail, "残っている可能性");
    }

    [TestMethod]
    public async Task 選択済みの固定IDとSIDだけをcommitし起動時に再列挙する()
    {
        var executed = new List<string>();
        var state = Create((profile, ids, ct) =>
        {
            executed.AddRange(ids);
            return new FileCleanupAction("boot-fixture", "boot", "boot", () => RebootCleanupTargets.Resolve(profile, ids)).ExecuteAsync(ct);
        });
        var profile = state.Environment.GetProfile(Sid);
        Directory.CreateDirectory(profile.LocalAppData);
        var keep = Path.Combine(profile.LocalAppData, "keep.txt");
        var cache = Path.Combine(profile.LocalAppData, "IconCache.db");
        await File.WriteAllTextAsync(cache, "old cache");
        await File.WriteAllTextAsync(keep, "user data");
        var queued = await state.Service.QueueAsync([FileCleanupGroups.ShellCacheTargets[0]], [FileCleanupGroups.ShellCacheTargets[0]], default);
        Assert.IsTrue(queued.Success);
        Assert.AreEqual(1, queued.PendingTargets);
        var json = state.Store.Files.Single().Value;
        StringAssert.Contains(json, Sid);
        StringAssert.Contains(json, "user-icon-db");
        Assert.IsFalse(json.Contains(_root, StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("RawPath", StringComparison.Ordinal));
        await File.WriteAllTextAsync(cache, "new cache generated before reboot");
        state.Environment.IsBootAccount = true;
        Assert.AreEqual(0, await state.Service.RunBootAsync());
        CollectionAssert.AreEqual(new[] { "user-icon-db" }, executed);
        Assert.IsFalse(File.Exists(cache));
        Assert.AreEqual("user data", await File.ReadAllTextAsync(keep));
        Assert.IsFalse(state.Task.Registered);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task 手動の実lock検出から固定snapshot予約と起動時同handle削除まで通す()
    {
        var seen = new List<string>();
        var state = Create((profile, ids, ct) =>
        {
            seen.AddRange(ids);
            return new FileCleanupAction("boot-e2e", "boot", "boot", () => RebootCleanupTargets.Resolve(profile, ids)).ExecuteAsync(ct);
        });
        var profile = state.Environment.GetProfile(Sid);
        Directory.CreateDirectory(profile.LocalAppData);
        var iconFile = Path.Combine(profile.LocalAppData, "IconCache.db");
        var explorer = Path.Combine(profile.LocalAppData, "Microsoft", "Windows", "Explorer");
        Directory.CreateDirectory(explorer);
        var thumb = Path.Combine(explorer, "thumb.bin");
        var keep = Path.Combine(profile.LocalAppData, "persistent.bin");
        await File.WriteAllTextAsync(iconFile, "locked cache");
        await File.WriteAllTextAsync(thumb, "unlocked cache");
        await File.WriteAllTextAsync(keep, "user data");
        var previous = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", profile.LocalAppData);
            using var held = new FileStream(iconFile, FileMode.Open, FileAccess.Read, FileShare.None);
            var action = new FileCleanupAction("manual-e2e", "manual", "manual",
                () => [FileCleanupGroups.ShellCacheTargets[0], FileCleanupGroups.ShellCacheTargets[1]],
                scheduleBlockedForReboot: true, rebootScheduler: state.Service);
            var result = await action.ExecuteAsync();
            Assert.AreEqual(MaintenanceActionStatus.Partial, result.Status);
            var request = JsonSerializer.Deserialize<RebootCleanupRequest>(state.Store.Files.Single().Value)!;
            CollectionAssert.AreEquivalent(new[] { "user-icon-db", "user-explorer" }, request.Selected);
            CollectionAssert.AreEqual(new[] { "user-icon-db" }, request.Pending);
            Assert.IsTrue(File.Exists(iconFile));
            Assert.IsFalse(File.Exists(thumb));
        }
        finally { Environment.SetEnvironmentVariable("LOCALAPPDATA", previous); }
        state.Environment.IsBootAccount = true;
        Assert.AreEqual(0, await state.Service.RunBootAsync());
        CollectionAssert.AreEqual(new[] { "user-icon-db" }, seen);
        Assert.IsFalse(File.Exists(iconFile));
        Assert.AreEqual("user data", await File.ReadAllTextAsync(keep));
        Assert.IsFalse(state.Task.Registered);
    }

    [TestMethod]
    public async Task 全外しと失敗0件の最新意図で以前のpendingを取り消す()
    {
        foreach (var emptySelection in new[] { true, false })
        {
            var executions = 0;
            var state = Create((_, _, _) => { executions++; return Task.FromResult(MaintenanceActionResult.Ok()); });
            var icon = FileCleanupGroups.ShellCacheTargets[0];
            await state.Service.QueueAsync([icon], [icon], default);
            var result = await state.Service.QueueAsync(emptySelection ? [] : [icon], [], default);
            Assert.IsTrue(result.Success);
            Assert.AreEqual(0, result.PendingTargets);
            state.Environment.IsBootAccount = true;
            await state.Service.RunBootAsync();
            Assert.AreEqual(0, executions);
            Assert.IsFalse(state.Task.Registered);
        }
    }

    [TestMethod]
    public async Task 他利用者のpendingを保持して元SIDのsnapshotだけを実行する()
    {
        var state = Create();
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        await state.Service.QueueAsync([icon], [icon], default);
        state.Environment.CurrentSid = OtherSid;
        var font = FileCleanupGroups.ShellCacheTargets[2];
        await state.Service.QueueAsync([font], [font], default);
        state.Environment.CurrentSid = Sid;
        await state.Service.QueueAsync([], [], default);
        Assert.IsTrue(state.Task.Registered, "別利用者の pending がある間は task を削除しない");
        state.Environment.ProfileReads.Clear();
        state.Environment.IsBootAccount = true;
        await state.Service.RunBootAsync();
        Assert.HasCount(0, state.Environment.ProfileReads, "このSIDのhelperは別SIDのjobを実行しない");
        Assert.IsTrue(state.Task.RegisteredSids.Contains(OtherSid));
        state.Environment.CurrentSid = OtherSid;
        await state.Service.RunBootAsync();
        CollectionAssert.AreEqual(new[] { OtherSid }, state.Environment.ProfileReads);
    }

    [TestMethod]
    public async Task 未知のtargetと未選択targetとprofile不一致は登録前に拒否する()
    {
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        var state = Create();
        Assert.IsFalse((await state.Service.QueueAsync([CleanupTarget.Contents(Path.Combine(_root, "outside"))], [], default)).Success);
        Assert.IsFalse((await state.Service.QueueAsync([], [icon], default)).Success);
        state.Environment.RejectEnqueue = true;
        Assert.IsFalse((await state.Service.QueueAsync([icon], [icon], default)).Success);
        Assert.AreEqual(0, state.Task.Ensures);
        Assert.HasCount(0, state.Store.Files);
    }

    [TestMethod]
    public async Task task登録失敗とqueuecommit失敗は成功にせず孤立taskを回収する()
    {
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        var state = Create();
        state.Task.FailEnsure = true;
        Assert.IsFalse((await state.Service.QueueAsync([icon], [icon], default)).Success);
        Assert.HasCount(0, state.Store.Files);
        state.Task.FailEnsure = false;
        state.Store.FailWritePrefix = "job-";
        Assert.IsFalse((await state.Service.QueueAsync([icon], [icon], default)).Success);
        Assert.IsFalse(state.Task.Registered);
        Assert.HasCount(0, state.Store.Files);
    }

    [TestMethod]
    public async Task 他利用者または不明なタスク定義は上書きも削除もしない()
    {
        var state = Create();
        state.Task.Foreign = true;
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        Assert.IsFalse((await state.Service.QueueAsync([icon], [icon], default)).Success);
        Assert.AreEqual(0, state.Task.Removals);
        Assert.HasCount(0, state.Store.Files);
        Assert.IsTrue(state.Task.Foreign);
    }

    [TestMethod]
    public async Task 新intent書込失敗では旧snapshotを失わない()
    {
        var seen = new List<string>();
        var state = Create((_, ids, _) => { seen.AddRange(ids); return Task.FromResult(MaintenanceActionResult.Ok()); });
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        await state.Service.QueueAsync([icon], [icon], default);
        state.Store.FailWritePrefix = "job-";
        var failed = await state.Service.QueueAsync([], [], default);
        Assert.IsFalse(failed.Success);
        StringAssert.Contains(failed.Detail, "以前の起動時予約は変更できず残っています");
        Assert.IsTrue(state.Task.Registered);
        state.Store.FailWritePrefix = null;
        state.Environment.IsBootAccount = true;
        await state.Service.RunBootAsync();
        CollectionAssert.AreEqual(new[] { "user-icon-db" }, seen);
    }

    [TestMethod]
    public async Task 最新intentのcommit後に旧結果保存が失敗しても古いpendingを実行しない()
    {
        var seen = new List<string>();
        var state = Create((_, ids, _) => { seen.AddRange(ids); return Task.FromResult(MaintenanceActionResult.Ok()); });
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        var font = FileCleanupGroups.ShellCacheTargets[2];
        await state.Service.QueueAsync([icon], [icon], default);
        state.Store.FailWritePrefix = "result-";
        var newest = await state.Service.QueueAsync([icon, font], [font], default);
        Assert.IsTrue(newest.Success, "commit 済みの予約を未保存と表示しない");
        Assert.AreEqual(1, newest.PendingTargets);
        state.Store.FailWritePrefix = null;
        state.Environment.IsBootAccount = true;
        await state.Service.RunBootAsync();
        CollectionAssert.AreEqual(new[] { "user-font" }, seen);
    }

    [TestMethod]
    public async Task 未知schemaの最新intentを捨てて以前の未完了予約へfallbackしない()
    {
        var runs = 0;
        var state = Create((_, _, _) => { runs++; return Task.FromResult(MaintenanceActionResult.Ok()); });
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        await state.Service.QueueAsync([icon], [icon], default);
        state.Store.FailWritePrefix = "result-";
        await state.Service.QueueAsync([icon], [icon], default);
        state.Store.FailWritePrefix = null;
        var newest = state.Store.Files.Where(entry => entry.Key.StartsWith("job-", StringComparison.Ordinal))
            .OrderByDescending(entry => JsonSerializer.Deserialize<RebootCleanupRequest>(entry.Value)!.Generation).First();
        state.Store.Files[newest.Key] = newest.Value.Replace("\"Schema\":1", "\"Schema\":999", StringComparison.Ordinal);
        state.Environment.IsBootAccount = true;
        Assert.AreEqual(1, await state.Service.RunBootAsync());
        Assert.AreEqual(0, runs);
    }

    [TestMethod]
    public async Task 実行失敗は3回または7日期限でterminalにして結果を利用者ログへ移す()
    {
        var runs = 0;
        var state = Create((_, _, _) => { runs++; return Task.FromResult(MaintenanceActionResult.Partial("locked")); });
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        await state.Service.QueueAsync([icon], [icon], default);
        state.Environment.IsBootAccount = true;
        for (var index = 0; index < 4; index++) await state.Service.RunBootAsync();
        Assert.AreEqual(3, runs);
        Assert.IsFalse(state.Task.Registered);
        state.Environment.IsBootAccount = false;
        state.Service.ReportCurrentUserResults();
        Assert.HasCount(0, state.Store.Files);

        var expired = Create((_, _, _) => { Assert.Fail("期限切れでは削除しない"); return Task.FromResult(MaintenanceActionResult.Ok()); });
        await expired.Service.QueueAsync([icon], [icon], default);
        expired.Environment.UtcNow = expired.Environment.UtcNow.AddDays(8);
        expired.Environment.IsBootAccount = true;
        await expired.Service.RunBootAsync();
        Assert.IsFalse(expired.Task.Registered);
    }

    [TestMethod]
    public async Task 削除開始前のmarker失敗と未知schemaは削除へ到達しない()
    {
        var executions = 0;
        var state = Create((_, _, _) => { executions++; return Task.FromResult(MaintenanceActionResult.Ok()); });
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        await state.Service.QueueAsync([icon], [icon], default);
        state.Store.FailWritePrefix = "start-";
        state.Environment.IsBootAccount = true;
        Assert.AreEqual(1, await state.Service.RunBootAsync());
        Assert.AreEqual(0, executions);
        state.Store.FailWritePrefix = null;
        var job = state.Store.Files.Keys.Single();
        state.Store.Files[job] = state.Store.Files[job].Replace("\"Schema\":1", "\"Schema\":999", StringComparison.Ordinal);
        Assert.AreEqual(1, await state.Service.RunBootAsync());
        Assert.AreEqual(0, executions);
        Assert.IsTrue(state.Task.Registered);
    }

    [TestMethod]
    public async Task crashの開始markerも1回として消費し旧schemaへfallbackしない()
    {
        var executions = 0;
        var state = Create((_, _, _) => { executions++; return Task.FromResult(MaintenanceActionResult.Ok()); });
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        await state.Service.QueueAsync([icon], [icon], default);
        var request = JsonSerializer.Deserialize<RebootCleanupRequest>(state.Store.Files.Values.Single())!;
        for (var index = 1; index <= 3; index++) state.Store.Files.Add($"start-{request.Id}-{index}.json",
            JsonSerializer.Serialize(new RebootCleanupStarted(1, request.Id, index, state.Environment.UtcNow)));
        state.Environment.IsBootAccount = true;
        await state.Service.RunBootAsync();
        Assert.AreEqual(0, executions);
        Assert.IsFalse(state.Task.Registered);
    }

    [TestMethod]
    public async Task SYSTEMでない起動と信頼不能helperとlease取得失敗はstorageや削除を変更しない()
    {
        var state = Create();
        Assert.AreEqual(1, await state.Service.RunBootAsync());
        Assert.AreEqual(0, state.Store.Reads);
        state.Environment.IsBootAccount = true;
        state.Environment.IsTrustedHelper = false;
        Assert.AreEqual(1, await state.Service.RunBootAsync());
        Assert.AreEqual(0, state.Store.Reads);
        var busy = Create(lease: () => null);
        busy.Environment.IsBootAccount = true;
        Assert.AreEqual(0, await busy.Service.RunBootAsync());
        Assert.AreEqual(0, busy.Store.Reads);
    }

    [TestMethod]
    public async Task 起動時leaseが最初衝突しても期限内に解放されたら同起動で処理する()
    {
        var acquisitions = 0;
        var runs = 0;
        var state = Create((_, _, _) => { runs++; return Task.FromResult(MaintenanceActionResult.Ok()); },
            () => ++acquisitions < 3 ? null : new Lease());
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        await state.Service.QueueAsync([icon], [icon], default);
        state.Environment.IsBootAccount = true;
        Assert.AreEqual(0, await state.Service.RunBootAsync());
        Assert.AreEqual(3, acquisitions);
        Assert.AreEqual(1, runs);
        Assert.IsFalse(state.Task.Registered);
    }

    [TestMethod]
    public async Task offlineprofileはcache不存在成功にせず再試行へ残す()
    {
        var state = Create((_, _, _) => { Assert.Fail("profile失敗時には削除しない"); return Task.FromResult(MaintenanceActionResult.Ok()); });
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        await state.Service.QueueAsync([icon], [icon], default);
        state.Environment.RejectProfile = true;
        state.Environment.IsBootAccount = true;
        await state.Service.RunBootAsync();
        Assert.IsTrue(state.Task.Registered);
        Assert.IsTrue(state.Store.Files.Keys.Any(name => name.StartsWith("result-", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task 実操作の空選択経路も旧pendingを取り消し設定保存失敗に依存しない()
    {
        var state = Create();
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        await state.Service.QueueAsync([icon], [icon], default);
        var action = new FileCleanupAction("empty-fixture", "test", "test", () => [], scheduleBlockedForReboot: true, rebootScheduler: state.Service);
        var result = await action.ExecuteAsync();
        Assert.AreEqual(MaintenanceActionStatus.Success, result.Status);
        Assert.IsFalse(state.Task.Registered);
        StringAssert.Contains(result.Detail, "取り消し");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task サービス補償が終わるまで予約更新を始めず取消時も復帰する(bool cancel)
    {
        var executor = new Executor();
        var scheduler = new ObservingScheduler(() => Assert.AreEqual(1, executor.Starts));
        var path = Path.Combine(_root, "compensation");
        Directory.CreateDirectory(path);
        using var cancellation = new CancellationTokenSource();
        var action = new FileCleanupAction("service-fixture", "test", "test", () => [CleanupTarget.Contents(path)], executor,
            servicesToStop: ["fake-font"], scheduleBlockedForReboot: true,
            suspendServices: (_, _, _, _) => Task.FromResult(new ServiceSuspension(executor, ["fake-font"], [], _ => WindowsServiceState.Stopped, _ => Task.CompletedTask)),
            rebootScheduler: scheduler);
        var result = await action.ExecuteAsync(new Progress(_ => { if (cancel) cancellation.Cancel(); }), cancellation.Token);
        Assert.AreEqual(1, scheduler.Calls);
        Assert.AreEqual(cancel ? MaintenanceActionStatus.Canceled : MaintenanceActionStatus.Success, result.Status);
    }

    [TestMethod]
    public async Task 設定保存失敗でも今回全外しのsnapshotで以前の予約を取り消す()
    {
        var runs = 0;
        var state = Create((_, _, _) => { runs++; return Task.FromResult(MaintenanceActionResult.Ok()); });
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        await state.Service.QueueAsync([icon], [icon], default);
        var preferences = new CleanupPreferences(new FailedSaveSettings());
        var target = CleanupTarget.Contents(Path.Combine(_root, "unchecked"));
        preferences.SetTargetEnabled("save-failed", FileCleanupAction.DescribeTarget(target), false);
        await Assert.ThrowsAsync<IOException>(() => preferences.SaveAsync());
        var action = new FileCleanupAction("save-failed", "test", "test", () => [target], scheduleBlockedForReboot: true,
            preferences: preferences, rebootScheduler: state.Service);
        await action.ExecuteAsync();
        state.Environment.IsBootAccount = true;
        await state.Service.RunBootAsync();
        Assert.AreEqual(0, runs);
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public async Task 途中取消は新規予約を作らず旧利用者予約を解除し失敗時は残存を明示する(bool failCancel)
    {
        var seen = new List<string>();
        var state = Create((_, ids, _) => { seen.AddRange(ids); return Task.FromResult(MaintenanceActionResult.Ok()); });
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        var font = FileCleanupGroups.ShellCacheTargets[2];
        await state.Service.QueueAsync([icon], [icon], default);
        state.Environment.CurrentSid = OtherSid;
        await state.Service.QueueAsync([font], [font], default);
        state.Environment.CurrentSid = Sid;
        if (failCancel) state.Store.FailWritePrefix = "job-";
        var local = state.Environment.GetProfile(Sid).LocalAppData;
        Directory.CreateDirectory(local);
        var cache = Path.Combine(local, "IconCache.db");
        await File.WriteAllTextAsync(cache, "keep on cancel");
        var previous = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", local);
            using var cancellation = new CancellationTokenSource();
            var action = new FileCleanupAction("cancel-fixture", "test", "test", () => [icon],
                scheduleBlockedForReboot: true, rebootScheduler: state.Service);
            var result = await action.ExecuteAsync(new Progress(_ => cancellation.Cancel()), cancellation.Token);
            Assert.AreEqual(failCancel ? MaintenanceActionStatus.Failed : MaintenanceActionStatus.Canceled, result.Status);
            StringAssert.Contains(result.Detail, failCancel ? "以前の起動時予約は変更できず残っています" : "取り消し");
            Assert.AreEqual("keep on cancel", await File.ReadAllTextAsync(cache));
        }
        finally { Environment.SetEnvironmentVariable("LOCALAPPDATA", previous); }
        state.Store.FailWritePrefix = null;
        state.Environment.IsBootAccount = true;
        await state.Service.RunBootAsync();
        CollectionAssert.AreEquivalent(failCancel ? new[] { "user-icon-db" } : Array.Empty<string>(), seen);
        Assert.IsTrue(state.Task.RegisteredSids.Contains(OtherSid));
        state.Environment.CurrentSid = OtherSid;
        await state.Service.RunBootAsync();
        CollectionAssert.AreEquivalent(failCancel ? new[] { "user-icon-db", "user-font" } : new[] { "user-font" }, seen);
    }

    private sealed class ObservingScheduler(Action observe) : IRebootCacheCleanupScheduler
    {
        internal int Calls;
        public Task<RebootCleanupQueueResult> QueueAsync(IReadOnlyList<CleanupTarget> selected, IReadOnlyList<CleanupTarget> blocked, CancellationToken ct)
        { observe(); Calls++; return Task.FromResult(new RebootCleanupQueueResult(true, 0, string.Empty)); }
    }

    private sealed class CancelAtQueue(IRebootCacheCleanupScheduler scheduler, CancellationTokenSource cancellation) : IRebootCacheCleanupScheduler
    {
        public Task<RebootCleanupQueueResult> QueueAsync(IReadOnlyList<CleanupTarget> selected, IReadOnlyList<CleanupTarget> blocked, CancellationToken ct)
        {
            Assert.IsFalse(ct.CanBeCanceled, "旧予約の取消補償は独立 token で完了を待つ");
            cancellation.Cancel();
            return scheduler.QueueAsync(selected, blocked, ct);
        }
    }

    private sealed class CommitGate(IRebootCacheCleanupScheduler scheduler) : IRebootCacheCleanupScheduler
    {
        internal readonly TaskCompletionSource Committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public async Task<RebootCleanupQueueResult> QueueAsync(IReadOnlyList<CleanupTarget> selected, IReadOnlyList<CleanupTarget> blocked, CancellationToken ct)
        {
            var result = await scheduler.QueueAsync(selected, blocked, ct);
            if (Interlocked.Increment(ref _calls) == 1 && result.PendingTargets > 0)
            {
                Committed.TrySetResult();
                await Release.Task;
            }
            return result;
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Queue完了待ち中の取消も同lease内で新pendingを解除し失敗はFailedにする(bool failCompensation)
    {
        var runs = 0;
        var state = Create((_, _, _) => { runs++; return Task.FromResult(MaintenanceActionResult.Ok()); });
        var profile = state.Environment.GetProfile(Sid);
        Directory.CreateDirectory(profile.LocalAppData);
        var file = Path.Combine(profile.LocalAppData, "IconCache.db");
        await File.WriteAllTextAsync(file, "locked");
        var previous = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", profile.LocalAppData);
            using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
            using var cancellation = new CancellationTokenSource();
            var gate = new CommitGate(state.Service);
            var action = new FileCleanupAction("commit-cancel", "test", "test", () => [FileCleanupGroups.ShellCacheTargets[0]],
                scheduleBlockedForReboot: true, rebootScheduler: gate);
            var execution = action.ExecuteAsync(cancellation.Token);
            try
            {
                await gate.Committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                if (failCompensation) state.Store.FailWritePrefix = "job-";
            }
            finally { gate.Release.TrySetResult(); }
            var result = await execution;
            Assert.AreEqual(failCompensation ? MaintenanceActionStatus.Failed : MaintenanceActionStatus.Canceled, result.Status);
            StringAssert.Contains(result.Detail, failCompensation ? "以前の起動時予約は変更できず残っています" : "取り消し");
        }
        finally { Environment.SetEnvironmentVariable("LOCALAPPDATA", previous); }
        state.Store.FailWritePrefix = null;
        state.Environment.IsBootAccount = true;
        await state.Service.RunBootAsync();
        Assert.AreEqual(failCompensation ? 1 : 0, runs);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task 実ETL探索中の取消も旧予約の解除を待ち解除失敗はFailedにする(bool failCancel)
    {
        var seen = new List<string>();
        var state = Create((_, ids, _) => { seen.AddRange(ids); return Task.FromResult(MaintenanceActionResult.Ok()); });
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        var font = FileCleanupGroups.ShellCacheTargets[2];
        await state.Service.QueueAsync([icon], [icon], default);
        state.Environment.CurrentSid = OtherSid;
        await state.Service.QueueAsync([font], [font], default);
        state.Environment.CurrentSid = Sid;
        if (failCancel) state.Store.FailWritePrefix = "job-";
        var etlRoot = Path.Combine(_root, "enumeration-cancel");
        Directory.CreateDirectory(Path.Combine(etlRoot, "child"));
        using var cancellation = new CancellationTokenSource();
        var observed = 0;
        IEnumerable<CleanupTarget> Targets()
        {
            foreach (var target in FileCleanupGroups.EnumerateKnownTreeEtlTargets(etlRoot))
            {
                if (++observed == 2) cancellation.Cancel();
                yield return target;
            }
        }
        var action = new FileCleanupAction("enumeration-cancel", "test", "test", Targets,
            scheduleBlockedForReboot: true, rebootScheduler: state.Service);
        var result = await action.ExecuteAsync(cancellation.Token);
        Assert.AreEqual(failCancel ? MaintenanceActionStatus.Failed : MaintenanceActionStatus.Canceled, result.Status);
        Assert.AreEqual(2, observed);
        StringAssert.Contains(result.Detail, failCancel ? "以前の起動時予約は変更できず残っています" : "取り消し");
        state.Store.FailWritePrefix = null;
        state.Environment.IsBootAccount = true;
        await state.Service.RunBootAsync();
        CollectionAssert.AreEquivalent(failCancel ? new[] { "user-icon-db" } : Array.Empty<string>(), seen);
        Assert.IsTrue(state.Task.RegisteredSids.Contains(OtherSid));
        state.Environment.CurrentSid = OtherSid;
        await state.Service.RunBootAsync();
        CollectionAssert.AreEquivalent(failCancel ? new[] { "user-icon-db", "user-font" } : new[] { "user-font" }, seen);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task 空選択の予約取消中に元tokenがcancelしても補償commitを完了する(bool failCancel)
    {
        var runs = 0;
        var state = Create((_, _, _) => { runs++; return Task.FromResult(MaintenanceActionResult.Ok()); });
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        await state.Service.QueueAsync([icon], [icon], default);
        if (failCancel) state.Store.FailWritePrefix = "job-";
        using var cancellation = new CancellationTokenSource();
        var action = new FileCleanupAction("empty-cancel", "test", "test", () => [], scheduleBlockedForReboot: true,
            rebootScheduler: new CancelAtQueue(state.Service, cancellation));
        var result = await action.ExecuteAsync(cancellation.Token);
        Assert.AreEqual(failCancel ? MaintenanceActionStatus.Failed : MaintenanceActionStatus.Canceled, result.Status);
        StringAssert.Contains(result.Detail, failCancel ? "以前の起動時予約は変更できず残っています" : "取り消し");
        state.Store.FailWritePrefix = null;
        state.Environment.IsBootAccount = true;
        await state.Service.RunBootAsync();
        Assert.AreEqual(failCancel ? 1 : 0, runs);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task 通常起動の結果回収は空queueも期限結果もSchedulerへ接続しない(bool expired)
    {
        var state = Create();
        if (expired)
        {
            var icon = FileCleanupGroups.ShellCacheTargets[0];
            await state.Service.QueueAsync([icon], [icon], default);
            state.Environment.UtcNow = state.Environment.UtcNow.AddDays(8);
        }
        var ensures = state.Task.Ensures;
        var removals = state.Task.Removals;
        state.Service.ReportCurrentUserResults();
        Assert.AreEqual(ensures, state.Task.Ensures);
        Assert.AreEqual(removals, state.Task.Removals);
        Assert.HasCount(0, state.Store.Files);
    }

    [TestMethod]
    public async Task 起動時helperが一度も動かず期限切れでも通常起動で自SIDだけを確定回収する()
    {
        var state = Create((_, _, _) => { Assert.Fail("通常の期限回収では削除を実行しない"); return Task.FromResult(MaintenanceActionResult.Ok()); });
        var icon = FileCleanupGroups.ShellCacheTargets[0];
        await state.Service.QueueAsync([icon], [icon], default);
        state.Environment.CurrentSid = OtherSid;
        await state.Service.QueueAsync([icon], [icon], default);
        var other = state.Store.Files.Single(item => JsonSerializer.Deserialize<RebootCleanupRequest>(item.Value)!.UserSid == OtherSid);
        state.Environment.CurrentSid = Sid;
        state.Environment.UtcNow = state.Environment.UtcNow.AddDays(8);
        state.Environment.ProfileReads.Clear();
        state.Service.ReportCurrentUserResults();
        Assert.AreEqual(other.Value, state.Store.Files[other.Key]);
        Assert.AreEqual(1, state.Store.Files.Count, "自SIDのjobとterminal結果はログへ移して回収する");
        Assert.IsTrue(state.Task.Registered, "他SIDの予約は変更しない");
        Assert.AreEqual(0, state.Environment.ProfileReads.Count, "期限確定にprofileやSYSTEM設定を読み直さない");
    }

    [TestMethod]
    public void machinequeueは未知ObjectACEをGetAccessRulesで落とさず拒否する()
    {
        var security = ProtectedBackupStorage.CreateDirectorySecurity();
        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        var acl = new RawAcl(4, raw.DiscretionaryAcl!.Count + 1);
        foreach (GenericAce ace in raw.DiscretionaryAcl) acl.InsertAce(acl.Count, ace);
        acl.InsertAce(acl.Count, new ObjectAce(AceFlags.None, AceQualifier.AccessAllowed, (int)FileSystemRights.WriteAttributes,
            new SecurityIdentifier("S-1-1-0"), ObjectAceFlags.ObjectAceTypePresent, Guid.NewGuid(), Guid.Empty, false, null));
        raw.DiscretionaryAcl = acl;
        var bytes = new byte[raw.BinaryLength];
        raw.GetBinaryForm(bytes, 0);
        security.SetSecurityDescriptorBinaryForm(bytes);
        Assert.IsFalse(ProtectedBackupStorage.IsProtectedAcl(security, out _));
        Assert.IsFalse(ScheduledTaskExecutableTrust.IsSafeAcl(security, out _));
    }

    [TestMethod]
    public void 起動時対象は元SIDのprofileと固定templateだけから構成する()
    {
        var profile = new RebootCleanupProfile(Path.Combine(_root, "user"), Path.Combine(_root, "windows"));
        var targets = RebootCleanupTargets.Resolve(profile, ["user-icon-db", "system-font-db"]);
        Assert.AreEqual(Path.Combine(profile.LocalAppData, "IconCache.db"), FileCleanupAction.DescribeTarget(targets[0]));
        Assert.AreEqual(Path.Combine(profile.WindowsRoot, "System32", "FNTCACHE.DAT"), FileCleanupAction.DescribeTarget(targets[1]));
        Assert.IsFalse(RebootCleanupTargets.NeedsFontServices(["user-icon-db", "user-explorer"]));
        Assert.IsTrue(RebootCleanupTargets.NeedsFontServices(["user-font"]));
        Assert.ThrowsExactly<IOException>(() => RebootCleanupTargets.Resolve(profile, ["..\\outside"]));
        Directory.CreateDirectory(profile.LocalAppData);
        RebootCleanupEnvironment.ValidateRootIdentity(profile.LocalAppData, profile.LocalAppData);
        var different = Path.Combine(_root, "different");
        Directory.CreateDirectory(different);
        Assert.ThrowsExactly<IOException>(() => RebootCleanupEnvironment.ValidateRootIdentity(profile.LocalAppData, different));
    }
}
