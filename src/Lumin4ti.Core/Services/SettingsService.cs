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

    /// <summary>
    /// 設定を読み込む。どの経路でも既定値へ落とせるが、「初回起動で無い」「壊れている」
    /// 「権限が無い」を後から区別できるよう、理由を必ずログへ残す。
    /// </summary>
    private static AppSettings Load(string settingsFilePath)
    {
        try
        {
            if (!File.Exists(settingsFilePath))
            {
                // 初回起動では正常。異常時と区別できるよう、探した場所を残す。
                LoggerBootstrap.Log.Info($"設定ファイルが無いため既定値で開始します: {settingsFilePath}");
                return new AppSettings();
            }

            var json = File.ReadAllText(settingsFilePath);
            var settings = Lumin4tiJson.Deserialize<AppSettings>(json);
            if (settings is not null)
            {
                LoggerBootstrap.Log.Info($"設定ファイルを読み込みました: {settingsFilePath}");
                return settings;
            }

            LoggerBootstrap.Log.Error(
                $"設定ファイルの内容が空か null のため既定値を使用します: {settingsFilePath} ({json.Length} 文字)");
        }
        catch (System.Text.Json.JsonException ex)
        {
            // 何行目で壊れているかまで残す。手で直す場合の唯一の手がかりになる。
            LoggerBootstrap.Log.Error(
                $"設定ファイルの JSON が壊れているため既定値を使用します: {settingsFilePath} " +
                $"(行 {ex.LineNumber?.ToString() ?? "不明"} / 位置 {ex.BytePositionInLine?.ToString() ?? "不明"})",
                ex);
        }
        catch (Exception ex)
        {
            // 権限不足・パス不正・長すぎるパス等も含め、起動を止めずに理由を残す。
            LoggerBootstrap.Log.Error(
                $"設定ファイルを読み込めないため既定値を使用します: {settingsFilePath} ({ex.GetType().Name})",
                ex);
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
            // 直前の保存の失敗は、その保存自身が既にログへ残している。
            // ここで再記録すると同じ失敗が二重に出るため、後続の保存継続だけを行う。
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
        catch (Exception ex)
        {
            // 種類を問わず理由を残してから呼び出し側へ返す (以前は IO 系以外が無記録で抜けていた)。
            LoggerBootstrap.Log.Error(
                $"設定ファイルを保存できませんでした: {_settingsFilePath} ({ex.GetType().Name})", ex);
            throw;
        }
    }
}
