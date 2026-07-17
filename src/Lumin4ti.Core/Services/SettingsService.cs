using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services;

public sealed class SettingsService : ISettingsService
{
    private readonly object _saveQueueLock = new();
    private readonly string _appDataDirectory;
    private readonly string _settingsFilePath;
    private Task _pendingSave = Task.CompletedTask;

    public AppSettings Current { get; }

    public SettingsService() : this(AppPaths.AppDataDirectory, AppPaths.SettingsFilePath)
    {
    }

    internal SettingsService(string appDataDirectory, string settingsFilePath)
    {
        _appDataDirectory = appDataDirectory;
        _settingsFilePath = settingsFilePath;
        Current = Load(settingsFilePath);
    }

    private static AppSettings Load(string settingsFilePath)
    {
        try
        {
            if (File.Exists(settingsFilePath))
            {
                var json = File.ReadAllText(settingsFilePath);
                var settings = Lumin4tiJson.Deserialize<AppSettings>(json);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            LoggerBootstrap.Log.Error($"設定ファイルを読み込めないため既定値を使用します: {settingsFilePath}", ex);
        }

        return new AppSettings();
    }

    public Task SaveAsync(CancellationToken ct = default)
    {
        lock (_saveQueueLock)
        {
            _pendingSave = SaveAfterAsync(_pendingSave, ct);
            return _pendingSave;
        }
    }

    public Task FlushAsync(CancellationToken ct = default)
    {
        Task pending;
        lock (_saveQueueLock)
        {
            pending = _pendingSave;
        }

        return pending.WaitAsync(ct);
    }

    private async Task SaveAfterAsync(Task previousSave, CancellationToken ct)
    {
        try
        {
            await previousSave.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 直前の呼び出し側には例外を返しつつ、後続の保存は継続できるようにする。
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_appDataDirectory);
            var json = Lumin4tiJson.Serialize(Current);
            var tempPath = _settingsFilePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, ct).ConfigureAwait(false);
            File.Move(tempPath, _settingsFilePath, overwrite: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggerBootstrap.Log.Error($"設定ファイルを保存できませんでした: {_settingsFilePath}", ex);
            throw;
        }
    }
}
