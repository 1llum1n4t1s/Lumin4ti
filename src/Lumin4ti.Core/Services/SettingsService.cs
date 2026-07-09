using System.Text.Json;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services;

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public AppSettings Current { get; }

    public SettingsService()
    {
        Current = Load();
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFilePath))
            {
                var json = File.ReadAllText(AppPaths.SettingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 破損・読み取り不可時は既定値にフォールバックする
        }

        return new AppSettings();
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _saveLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(AppPaths.AppDataDirectory);
            var json = JsonSerializer.Serialize(Current, JsonOptions);
            var tempPath = AppPaths.SettingsFilePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, ct);
            File.Move(tempPath, AppPaths.SettingsFilePath, overwrite: true);
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
