using System;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services;
using Velopack;
using Velopack.Sources;

namespace Lumin4ti.UI.Services;

/// <summary>
/// Velopack の SimpleWebSource ベースで Cloudflare R2 (lumin4ti.kagayoi.com) から更新を取得する。
/// 実際のチェック / ダウンロード / 適用 UI は VelopackUpdateDialog.Avalonia が担うため、本サービスは
/// UpdateManager の生成と現在バージョンの取得だけを提供する。
/// 配信元 URL は <see cref="AppSettings.UpdateBaseUrl"/> にハードコード固定 (settings.json から書き換え不可)。
/// </summary>
public sealed class UpdateService
{
    private readonly Func<UpdateManager?> _createManager;
    private readonly string? _currentVersion;
    private bool _versionReadFailed;

    public UpdateService(AppSettings settings)
        : this(() => BuildManager(settings))
    {
    }

    internal UpdateService(Func<UpdateManager?> createManager)
    {
        _createManager = createManager;
        _currentVersion = ResolveCurrentVersion();
    }

    /// <summary>実行中アプリのバージョン。開発実行時は "開発ビルド"。</summary>
    public string CurrentVersion => _versionReadFailed
        ? App.Text("Version.Unavailable", "バージョンを取得できませんでした")
        : _currentVersion ?? App.Text("Version.DevelopmentBuild", "開発ビルド");

    /// <summary>
    /// インストール版なら Velopack の <see cref="UpdateManager"/> を新規生成して返す。
    /// 開発実行 (dotnet run 等・未インストール) では更新機構が働かないため null を返す。
    /// </summary>
    public UpdateManager? TryCreateInstalledManager()
    {
        var manager = _createManager();
        return manager is { IsInstalled: true } ? manager : null;
    }

    private static UpdateManager BuildManager(AppSettings settings)
    {
        // SimpleWebSource は {baseUrl}/releases.{channel}.json を取得する。
        var source = new SimpleWebSource(settings.UpdateBaseUrl);
        var options = new UpdateOptions { ExplicitChannel = settings.UpdateChannel };
        return new UpdateManager(source, options);
    }

    private string? ResolveCurrentVersion()
    {
        try
        {
            var manager = _createManager();
            return manager is { IsInstalled: true, CurrentVersion: not null }
                ? manager.CurrentVersion.ToString()
                : null;
        }
        catch (Exception ex)
        {
            _versionReadFailed = true;
            LoggerBootstrap.Log.Error("現在バージョンを取得できませんでした", ex);
            return null;
        }
    }
}
