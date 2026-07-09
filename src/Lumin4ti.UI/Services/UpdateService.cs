using System;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services;
using Velopack;
using Velopack.Sources;

namespace Lumin4ti.UI.Services;

/// <summary>
/// Velopack の SimpleWebSource ベースで Cloudflare R2 (lumin4ti.nephilim.jp) から更新を取得する。
/// 実際のチェック / ダウンロード / 適用 UI は VelopackUpdateDialog.Avalonia が担うため、本サービスは
/// UpdateManager の生成と現在バージョンの取得だけを提供する。
/// 配信元 URL は <see cref="AppSettings.UpdateBaseUrl"/> にハードコード固定 (settings.json から書き換え不可)。
/// </summary>
public sealed class UpdateService
{
    private readonly AppSettings _settings;

    public UpdateService(AppSettings settings)
    {
        _settings = settings;
        CurrentVersion = ResolveCurrentVersion();
    }

    /// <summary>実行中アプリのバージョン。開発実行時は "開発ビルド"。</summary>
    public string CurrentVersion { get; }

    /// <summary>
    /// インストール版なら Velopack の <see cref="UpdateManager"/> を新規生成して返す。
    /// 開発実行 (dotnet run 等・未インストール) では更新機構が働かないため null を返す。
    /// </summary>
    public UpdateManager? TryCreateInstalledManager()
    {
        try
        {
            var manager = BuildManager();
            return manager.IsInstalled ? manager : null;
        }
        catch (Exception ex)
        {
            LoggerBootstrap.Log.Error("UpdateManager の生成に失敗しました", ex);
            return null;
        }
    }

    private UpdateManager BuildManager()
    {
        // SimpleWebSource は {baseUrl}/releases.{channel}.json を取得する。
        var source = new SimpleWebSource(_settings.UpdateBaseUrl);
        var options = new UpdateOptions { ExplicitChannel = _settings.UpdateChannel };
        return new UpdateManager(source, options);
    }

    private string ResolveCurrentVersion()
    {
        try
        {
            var manager = BuildManager();
            return manager.IsInstalled && manager.CurrentVersion is not null
                ? manager.CurrentVersion.ToString()
                : "開発ビルド";
        }
        catch
        {
            return "開発ビルド";
        }
    }
}
