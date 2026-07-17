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

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lumin4ti.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
