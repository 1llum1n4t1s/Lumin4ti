using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services;

public sealed class SettingsService : ISettingsService
{
    private readonly object _saveQueueLock = new();
    private readonly string _appDataDirectory;
    private readonly string _settingsFilePath;
    private Task _pendingSave = Task.CompletedTask;
    private int _failedLoadSaveWarningLogged;

    public AppSettings Current { get; }

    /// <inheritdoc />
    public SettingsLoadStatus LoadStatus { get; }

    /// <inheritdoc />
    public object SyncRoot { get; } = new();

    public SettingsService() : this(AppPaths.AppDataDirectory, AppPaths.SettingsFilePath)
    {
    }

    internal SettingsService(string appDataDirectory, string settingsFilePath)
    {
        _appDataDirectory = appDataDirectory;
        _settingsFilePath = settingsFilePath;
        var loaded = Load(settingsFilePath);
        Current = loaded.Settings;
        LoadStatus = loaded.Status;
    }

    /// <summary>
    /// 設定を読み込む。どの経路でも既定値へ落とせるが、「初回起動で無い」「壊れている」
    /// 「権限が無い」を後から区別できるよう、理由を必ずログへ残す。
    /// </summary>
    private static SettingsLoadResult Load(string settingsFilePath)
    {
        try
        {
            if (!File.Exists(settingsFilePath))
            {
                // 初回起動では正常。異常時と区別できるよう、探した場所を残す。
                LoggerBootstrap.Log.Info($"設定ファイルが無いため既定値で開始します: {settingsFilePath}");
                return new SettingsLoadResult(new AppSettings(), SettingsLoadStatus.Missing);
            }

            var json = File.ReadAllText(settingsFilePath);
            var settings = Lumin4tiJson.Deserialize<AppSettings>(json);
            if (settings is not null)
            {
                LoggerBootstrap.Log.Info($"設定ファイルを読み込みました: {settingsFilePath}");
                return new SettingsLoadResult(settings, SettingsLoadStatus.Loaded);
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

        return new SettingsLoadResult(new AppSettings(), SettingsLoadStatus.Failed);
    }

    public Task SaveAsync(CancellationToken ct = default)
    {
        // 読み込めなかった既存ファイルを、フォールバックした既定値で上書きしない。
        // UI は利用可能なままにし、元ファイルを利用者が復旧できる状態で保護する。
        if (LoadStatus == SettingsLoadStatus.Failed)
        {
            if (Interlocked.Exchange(ref _failedLoadSaveWarningLogged, 1) == 0)
            {
                LoggerBootstrap.Log.Error(
                    $"設定ファイルの読込に失敗しているため、上書きを中止しました: {_settingsFilePath}");
            }

            return ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;
        }

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

            // 保存は先行保存の完了後にスレッドプールで走るため、シリアライズ中に画面側が
            // 除外リストを書き換えられる。ファイル書き込みはロックの外に出したまま、
            // JSON 化だけを更新側と同じロックで挟んでスナップショットにする。
            string json;
            lock (SyncRoot)
            {
                json = Lumin4tiJson.Serialize(Current);
            }

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

    private readonly record struct SettingsLoadResult(AppSettings Settings, SettingsLoadStatus Status);
}
