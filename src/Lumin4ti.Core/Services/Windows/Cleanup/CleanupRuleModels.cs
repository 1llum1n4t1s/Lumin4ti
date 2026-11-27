using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>JSON の列挙値を camelCase の文字列として読む (整数は受け付けない)。</summary>
internal sealed class CleanupEnumConverter<T>() : JsonStringEnumConverter<T>(JsonNamingPolicy.CamelCase, false)
    where T : struct, Enum;

/// <summary>規則の分類 (画面のグループ)。<see cref="Custom"/> は利用者のカスタムフォルダー専用で、埋め込み定義では使えない。</summary>
[JsonConverter(typeof(CleanupEnumConverter<CleanupCategory>))]
internal enum CleanupCategory
{
    Windows,
    StoreApps,
    BrowserCache,
    BrowserTrace,
    Cookies,
    Traces,
    Components,
    Apps,
    Custom,
}

/// <summary>種別。表示名は「{0} のキャッシュ」などの書式キーで組み立てる。</summary>
[JsonConverter(typeof(CleanupEnumConverter<CleanupKind>))]
internal enum CleanupKind
{
    Cache,
    Log,
    Temp,
    Dump,
    Thumbnail,
    Shader,
    Report,
    History,
    Form,
    Session,
    Cookies,
    Recent,
    Backup,
    Downloads,
    Other,
}

/// <summary>削除の影響。<see cref="Junk"/> 以外は recommended=true にできない。</summary>
[JsonConverter(typeof(CleanupEnumConverter<CleanupImpact>))]
internal enum CleanupImpact
{
    /// <summary>再生成可能で、サインアウト・履歴喪失・再構築負荷を伴わない。</summary>
    Junk,
    /// <summary>履歴・痕跡の喪失。</summary>
    Trace,
    /// <summary>サインアウトを伴う (cookies 分類だけ)。</summary>
    Signout,
    /// <summary>再構築の負荷を伴う。</summary>
    Rebuild,
    /// <summary>受信ファイルなど再取得できない利用者データ。「すべて選択」にも含めない。</summary>
    UserFiles,
}

/// <summary>規則が作用する範囲。対話利用者と昇格利用者の SID が異なる場合、user は無効にする。</summary>
[JsonConverter(typeof(CleanupEnumConverter<CleanupScope>))]
internal enum CleanupScope
{
    User,
    Machine,
}

/// <summary>規則の生成元エンジン (browsers.json などから規則を展開する担当向けの印)。</summary>
[JsonConverter(typeof(CleanupEnumConverter<CleanupEngineKind>))]
internal enum CleanupEngineKind
{
    Electron,
    Chromium,

    /// <summary>
    /// 基点フォルダー自体がプロファイルの Chromium 型 (Opera。<c>detect[0]</c> の直下に <c>Preferences</c> などがあり、
    /// 新しい版は <c>Default</c> など下位フォルダーにも持つ)。<c>detect[1]</c> のキャッシュ基点は別ドライブ領域 (Local 側) にある。
    /// </summary>
    ChromiumProfile,
    Gecko,
    MfcMru,
}

[JsonConverter(typeof(CleanupEnumConverter<CleanupTargetType>))]
internal enum CleanupTargetType
{
    /// <summary>フォルダーの中身 (フォルダー自体は残す)。</summary>
    Contents,
    /// <summary>フォルダー内の既知ファイル名・パターンだけ。</summary>
    Files,
    /// <summary>HKCU のキーの値 (キーは残す)。</summary>
    RegistryValues,
    /// <summary>HKCU のキー配下の全値・全サブキー (キー自体は残す)。</summary>
    RegistryTree,
    /// <summary>固定操作の SQLite 編集。</summary>
    Sqlite,
    /// <summary>INI のセクション・キーの削除。</summary>
    IniValues,
    /// <summary>C# 側の固定ハンドラー。</summary>
    Handler,
}

/// <summary>SQLite の固定操作。SQL はデータに書かせない。</summary>
[JsonConverter(typeof(CleanupEnumConverter<CleanupSqliteOperation>))]
internal enum CleanupSqliteOperation
{
    /// <summary>Chromium の <c>Cookies</c>。host_key 単位 (除外ドメインは本体とサブドメインを保持)。</summary>
    ChromiumCookies,
    /// <summary>Chromium の <c>Web Data</c> の autofill 表だけ。</summary>
    ChromiumWebData,
    /// <summary>Gecko の <c>cookies.sqlite</c>。</summary>
    GeckoCookies,
    /// <summary>Gecko の <c>formhistory.sqlite</c>。</summary>
    GeckoFormHistory,
    /// <summary>Gecko の <c>places.sqlite</c> のうちブックマークの無い履歴だけ。</summary>
    GeckoPlaces,
    /// <summary>指定した表を全行削除する (表名と期待する列名だけ指定できる)。</summary>
    ClearTable,
}

/// <summary>固定ハンドラー名。実装は C# 側にあり、データは名前だけを選ぶ。</summary>
[JsonConverter(typeof(CleanupEnumConverter<CleanupHandlerKind>))]
internal enum CleanupHandlerKind
{
    RecycleBin,
    DnsCache,
    IeHistory,
    NetworkUsage,
    InvalidShortcut,
    InvalidScheduledTask,
}

[JsonConverter(typeof(CleanupEnumConverter<CleanupEvidenceTier>))]
internal enum CleanupEvidenceTier
{
    /// <summary>公式文書または製品の公開ソース (1件で可)。</summary>
    A,
    /// <summary>独立した二次資料 (出典ホストの異なる2件が必要)。</summary>
    B,
    /// <summary>
    /// 旧クリーンアップカードが既に対象にしていた場所の継承 (回帰防止)。
    /// 出典は無く (url なし)、<c>legacy.</c> で始まる id で legacy が空でない規則だけに許す。新しい場所を足す根拠にはならない。
    /// </summary>
    L,
}

/// <summary>定義ファイル (<c>Definitions/*.json</c>) の最上位。</summary>
internal sealed record CleanupRuleFile
{
    public int Schema { get; init; }

    public List<CleanupRule> Rules { get; init; } = [];
}

internal sealed record CleanupRule
{
    public string Id { get; init; } = string.Empty;

    /// <summary>製品名 (翻訳しない)。<see cref="NameKey"/> がある Windows 項目では英語の既定名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>辞書キー (Windows 項目の <c>Text.Cleanup.Rule.{id}</c>)。</summary>
    public string? NameKey { get; init; }

    public CleanupKind Kind { get; init; }

    public CleanupCategory Category { get; init; }

    public CleanupScope Scope { get; init; } = CleanupScope.User;

    public bool Recommended { get; init; }

    public CleanupImpact Impact { get; init; }

    public string? WarningKey { get; init; }

    public CleanupEngineKind? Engine { get; init; }

    public List<CleanupDetect> Detect { get; init; } = [];

    public List<CleanupRuleTarget> Targets { get; init; } = [];

    /// <summary>実行中に確認する製品のプロセス名 (拡張子なし)。</summary>
    public List<string> Processes { get; init; } = [];

    /// <summary>削除前に止めるサービス (許可表の名前だけ)。</summary>
    public List<string> Services { get; init; } = [];

    /// <summary>旧 <c>CleanupExclusions</c> の対応キー (<c>{旧項目Id}|{旧キー}</c>)。</summary>
    public List<string> Legacy { get; init; } = [];

    public List<CleanupEvidence> Evidence { get; init; } = [];
}

/// <summary>検出条件。path・registry・package のどれか1つだけを指定する。</summary>
internal sealed record CleanupDetect
{
    public string? Path { get; init; }

    public string? Registry { get; init; }

    /// <summary>registry と一緒に指定すると、値の存在 (<see cref="ValueEquals"/> も指定すれば一致) まで確認する。</summary>
    public string? ValueName { get; init; }

    public string? ValueEquals { get; init; }

    /// <summary>パッケージファミリー名 (PFN)。</summary>
    public string? Package { get; init; }
}

internal sealed record CleanupRuleTarget
{
    public CleanupTargetType Type { get; init; }

    /// <summary>contents / files / sqlite / iniValues の基点パス (先頭は許可された環境変数)。</summary>
    public string? Path { get; init; }

    /// <summary>files は必須、contents は任意。</summary>
    public string? Pattern { get; init; }

    public bool Recurse { get; init; }

    public int? OlderThanDays { get; init; }

    /// <summary>掃除対象内で空になった下位フォルダーを消す (基点自体は残す)。</summary>
    public bool RemoveEmptyDirs { get; init; }

    /// <summary>registryValues / registryTree のキー (<c>HKCU\Software\...</c>)。</summary>
    public string? Key { get; init; }

    /// <summary>registryValues の値名 (末尾 <c>*</c> 可)。</summary>
    public List<string>? Values { get; init; }

    public CleanupSqliteOperation? Operation { get; init; }

    /// <summary>clearTable の表名。</summary>
    public string? Table { get; init; }

    /// <summary>clearTable で期待する列名。</summary>
    public List<string>? Columns { get; init; }

    public List<CleanupIniKey>? IniKeys { get; init; }

    public CleanupHandlerKind? Handler { get; init; }
}

internal sealed record CleanupIniKey
{
    public string Section { get; init; } = string.Empty;

    /// <summary>キー名 (末尾連番は <c>*</c> 可)。</summary>
    public string Key { get; init; } = string.Empty;
}

internal sealed record CleanupEvidence
{
    public CleanupEvidenceTier Tier { get; init; }

    public string Url { get; init; } = string.Empty;

    /// <summary>確認日 (<c>yyyy-MM-dd</c>)。</summary>
    public string Checked { get; init; } = string.Empty;
}
