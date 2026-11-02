using System.Text.Json;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class SettingsServiceTests
{
    private string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "Lumin4ti.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [TestMethod]
    public void 正常な設定ファイルは読込済みとして扱う()
    {
        var settingsPath = Path.Combine(_directory, "settings.json");
        File.WriteAllText(settingsPath, "{\"Locale\":\"en_US\"}");

        var service = new SettingsService(_directory, settingsPath);

        Assert.AreEqual("en_US", service.Current.Locale);
        Assert.AreEqual(SettingsLoadStatus.Loaded, service.LoadStatus);
    }

    [TestMethod]
    [DataRow("{ invalid json")]
    [DataRow("null")]
    public void 読込不能な設定は既定値へフォールバックする(string content)
    {
        var settingsPath = Path.Combine(_directory, "settings.json");
        File.WriteAllText(settingsPath, content);

        var service = new SettingsService(_directory, settingsPath);

        Assert.AreEqual(string.Empty, service.Current.Locale);
        Assert.IsTrue(service.Current.CheckForUpdatesOnStartup);
        Assert.AreEqual(SettingsLoadStatus.Failed, service.LoadStatus);
    }

    [TestMethod]
    public void CleanupExclusionsがnullなら空の除外として読み込む()
    {
        var settingsPath = Path.Combine(_directory, "settings.json");
        File.WriteAllText(
            settingsPath,
            "{\"CleanupExclusions\":null,\"ScheduledCleanupGroupIds\":null}");

        var service = new SettingsService(_directory, settingsPath);
        var preferences = new CleanupPreferences(service);

        Assert.HasCount(0, service.Current.CleanupExclusions);
        Assert.IsNull(service.Current.ScheduledCleanupGroupIds);
        Assert.IsTrue(preferences.IsTargetEnabled("cleanup-user-temp", @"%LOCALAPPDATA%\Temp"));
        Assert.AreEqual(SettingsLoadStatus.Loaded, service.LoadStatus);
    }

    [TestMethod]
    public void CleanupExclusions内のnullリストは空の除外として読み込む()
    {
        var settingsPath = Path.Combine(_directory, "settings.json");
        File.WriteAllText(
            settingsPath,
            "{\"CleanupExclusions\":{\"cleanup-user-temp\":null}}");

        var service = new SettingsService(_directory, settingsPath);
        var preferences = new CleanupPreferences(service);

        Assert.HasCount(0, service.Current.CleanupExclusions["cleanup-user-temp"]);
        Assert.IsTrue(preferences.IsTargetEnabled("cleanup-user-temp", @"%LOCALAPPDATA%\Temp"));
        Assert.AreEqual(SettingsLoadStatus.Loaded, service.LoadStatus);
    }

    [TestMethod]
    public void 設定ファイルが無い場合も既定値で起動できる()
    {
        var service = new SettingsService(_directory, Path.Combine(_directory, "settings.json"));

        Assert.AreEqual(string.Empty, service.Current.Locale);
        Assert.AreEqual(SettingsLoadStatus.Missing, service.LoadStatus);
    }

    [TestMethod]
    public void 読み取れない設定ファイルでも起動を止めない()
    {
        // 他プロセスが排他で開いている状況。既定値へ落ちるだけで例外を投げないことを固定する。
        var settingsPath = Path.Combine(_directory, "settings.json");
        File.WriteAllText(settingsPath, "{}");
        using var exclusive = new FileStream(
            settingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var service = new SettingsService(_directory, settingsPath);

        Assert.AreEqual(string.Empty, service.Current.Locale);
        Assert.AreEqual(SettingsLoadStatus.Failed, service.LoadStatus);
    }

    [TestMethod]
    public async Task 読込失敗後の保存は元の設定ファイルを上書きしない()
    {
        var settingsPath = Path.Combine(_directory, "settings.json");
        const string original = "{ invalid json";
        await File.WriteAllTextAsync(settingsPath, original);
        var service = new SettingsService(_directory, settingsPath);

        service.Current.Locale = "en_US";
        await service.SaveAsync();
        await service.FlushAsync();

        Assert.AreEqual(SettingsLoadStatus.Failed, service.LoadStatus);
        Assert.AreEqual(original, await File.ReadAllTextAsync(settingsPath));
    }

    [TestMethod]
    public async Task クリーンアップ選択は読込失敗時も一時反映し保存抑止を通知して原本を保持する()
    {
        var settingsPath = Path.Combine(_directory, "settings.json");
        const string original = "{ invalid json";
        await File.WriteAllTextAsync(settingsPath, original);
        var service = new SettingsService(_directory, settingsPath);
        var preferences = new CleanupPreferences(service);

        preferences.SetTargetEnabled("cleanup-test", "cache", enabled: false);
        preferences.SetScheduledGroupEnabled("cleanup-user-temp", enabled: false);
        Assert.IsFalse(preferences.IsTargetEnabled("cleanup-test", "cache"));
        Assert.IsFalse(preferences.ScheduledGroupIds.Contains("cleanup-user-temp"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => preferences.SaveAsync());

        // SettingsService 自体の保存抑止契約も維持する。
        await service.SaveAsync();
        await service.FlushAsync();
        Assert.AreEqual(SettingsLoadStatus.Failed, service.LoadStatus);
        Assert.AreEqual(original, await File.ReadAllTextAsync(settingsPath));
        Assert.IsFalse(preferences.IsTargetEnabled("cleanup-test", "cache"));
    }

    [TestMethod]
    public async Task 保存に失敗した場合は呼び出し側へ例外を返す()
    {
        // 保存できないのに成功扱いにすると、設定が消えたことに気付けない。
        var settingsPath = Path.Combine(_directory, "settings.json");
        var service = new SettingsService(_directory, settingsPath);

        // 保存先と同名のディレクトリを置いて File.Move を失敗させる。
        Directory.CreateDirectory(settingsPath);

        // 例外の型は OS とファイルシステム次第 (IOException / UnauthorizedAccessException) なので、
        // 「握り潰さずに呼び出し側へ返すこと」だけを固定する。
        Exception? thrown = null;
        try
        {
            await service.SaveAsync();
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        Assert.IsNotNull(thrown, "保存失敗が呼び出し側へ伝わっていません");
    }

    [TestMethod]
    public async Task FlushAsyncは要求済みの最新設定まで保存する()
    {
        var settingsPath = Path.Combine(_directory, "settings.json");
        var service = new SettingsService(_directory, settingsPath);

        service.Current.Locale = "en_US";
        _ = service.SaveAsync();
        service.Current.Locale = "ja_JP";
        _ = service.SaveAsync();

        await service.FlushAsync();

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
        Assert.AreEqual("ja_JP", json.RootElement.GetProperty("Locale").GetString());
    }

    [TestMethod]
    public async Task 保存はSyncRootを取ってからシリアライズする()
    {
        // 保存は先行保存の完了後にスレッドプールで走るため、シリアライズ中に画面側が
        // 除外リストを書き換えられる。同じロックで挟んでおかないと「列挙中に変更された」で
        // 保存が落ち、利用者の除外設定が失われる。
        var service = new SettingsService(_directory, Path.Combine(_directory, "settings.json"));
        using var lockTaken = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            lock (service.SyncRoot)
            {
                lockTaken.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        });

        Task? save = null;
        try
        {
            Assert.IsTrue(lockTaken.Wait(TimeSpan.FromSeconds(5)));
            save = Task.Run(() => service.SaveAsync());

            Assert.IsFalse(
                save.Wait(TimeSpan.FromMilliseconds(300)),
                "SyncRoot を取っている間はシリアライズが進んではいけません");
        }
        finally
        {
            release.Set();
            await holder;
            if (save is not null)
                await save;
        }
    }
}
