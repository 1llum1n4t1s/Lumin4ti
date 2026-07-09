using System.Text.Json.Serialization;

namespace Lumin4ti.Core.Models;

public sealed class AppSettings
{
    /// <summary>表示言語のロケールキー (例: "ja_JP")。空なら OS ロケールから自動判定する。</summary>
    public string Locale { get; set; } = string.Empty;

    /// <summary>起動時に自動で更新を確認するか。</summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>「このバージョンをスキップ」で保存した更新タグ。以降この版の自動更新通知を出さない。</summary>
    public string? IgnoreUpdateTag { get; set; }

    /// <summary>
    /// Velopack 自動更新の配信元。Cloudflare R2 (カスタムドメイン) をハードコード固定する。
    /// <see cref="JsonIgnore"/> なので settings.json から書き換え不可 (悪意ある第三者ホストへの誘導を防ぐ)。
    /// </summary>
    [JsonIgnore]
    public string UpdateBaseUrl => "https://lumin4ti.nephilim.jp";

    /// <summary>Velopack channel。Windows 単独配信なので "win" 固定。</summary>
    [JsonIgnore]
    public string UpdateChannel => "win";
}
