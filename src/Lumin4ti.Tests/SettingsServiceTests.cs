using System.Text.Json;
using Lumin4ti.Core.Services;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class SettingsServiceTests
{
    [TestMethod]
    public void 破損した設定は既定値へフォールバックする()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            File.WriteAllText(settingsPath, "{ invalid json");

            var service = new SettingsService(directory, settingsPath);

            Assert.AreEqual(string.Empty, service.Current.Locale);
            Assert.IsTrue(service.Current.CheckForUpdatesOnStartup);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void 内容がnullの設定は既定値へフォールバックする()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            File.WriteAllText(settingsPath, "null");

            var service = new SettingsService(directory, settingsPath);

            Assert.AreEqual(string.Empty, service.Current.Locale);
            Assert.IsTrue(service.Current.CheckForUpdatesOnStartup);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void 設定ファイルが無い場合も既定値で起動できる()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var service = new SettingsService(directory, Path.Combine(directory, "settings.json"));

            Assert.AreEqual(string.Empty, service.Current.Locale);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void 読み取れない設定ファイルでも起動を止めない()
    {
        // 他プロセスが排他で開いている状況。既定値へ落ちるだけで例外を投げないことを固定する。
        var directory = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            File.WriteAllText(settingsPath, "{}");
            using var exclusive = new FileStream(
                settingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var service = new SettingsService(directory, settingsPath);

            Assert.AreEqual(string.Empty, service.Current.Locale);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task 保存に失敗した場合は呼び出し側へ例外を返す()
    {
        // 保存できないのに成功扱いにすると、設定が消えたことに気付けない。
        var directory = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            var service = new SettingsService(directory, settingsPath);

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
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task FlushAsyncは要求済みの最新設定まで保存する()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(directory, "settings.json");
            var service = new SettingsService(directory, settingsPath);

            service.Current.Locale = "en_US";
            _ = service.SaveAsync();
            service.Current.Locale = "ja_JP";
            _ = service.SaveAsync();

            await service.FlushAsync();

            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
            Assert.AreEqual("ja_JP", json.RootElement.GetProperty("Locale").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task 保存はSyncRootを取ってからシリアライズする()
    {
        // 保存は先行保存の完了後にスレッドプールで走るため、シリアライズ中に画面側が
        // 除外リストを書き換えられる。同じロックで挟んでおかないと「列挙中に変更された」で
        // 保存が落ち、利用者の除外設定が失われる。
        var directory = CreateTemporaryDirectory();
        try
        {
            var service = new SettingsService(directory, Path.Combine(directory, "settings.json"));
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

            Assert.IsTrue(lockTaken.Wait(TimeSpan.FromSeconds(5)));
            var save = Task.Run(() => service.SaveAsync());

            Assert.IsFalse(
                save.Wait(TimeSpan.FromMilliseconds(300)),
                "SyncRoot を取っている間はシリアライズが進んではいけません");

            release.Set();
            await save;
            await holder;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lumin4ti.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
