namespace Lumin4ti.Core.Services.Windows.Cleanup.Slim;

/// <summary>スリム化の 15 項目。</summary>
public enum SlimItemId
{
    InstallerOrphans,
    UpdateRollback,
    UpdateBackup,
    UpdateCache,
    PatchCache,
    LanguageJa,
    LanguageKo,
    LanguageZhTw,
    LanguageZhCn,
    Help,
    DefaultWallpapers,
    SampleMusic,
    SamplePictures,
    SampleVideos,
    SampleRecordedTv,
}

/// <summary>警告区分。UI は区分ごとに表示を変える (Irreversible は二段階確認が必要)。</summary>
public enum SlimWarningLevel
{
    /// <summary>警告なし。</summary>
    None,

    /// <summary>再生成・再取得できる。補足の表示だけ。</summary>
    Notice,

    /// <summary>以後の動作に影響し得る。警告を表示して確認する。</summary>
    Caution,

    /// <summary>元に戻せない。警告に加えて二段階確認が必要。</summary>
    Irreversible,
}

/// <summary>項目の定義。UI 文言は辞書キーで持ち、文字列を直書きしない。</summary>
/// <param name="Id">項目。</param>
/// <param name="Key">項目の安定した識別子 (設定・ログ用)。</param>
/// <param name="NameKey">名称の辞書キー。</param>
/// <param name="DescriptionKey">説明の辞書キー。</param>
/// <param name="WarningKey">警告文の辞書キー (警告なしは null)。</param>
/// <param name="Warning">警告区分。</param>
/// <param name="RequiresSecondConfirmation">実行に二段階確認 (<see cref="SlimRunRequest.ConfirmedIrreversible"/>) が要る。</param>
/// <param name="NeedsRestorePoint">実行前の復元ポイントの対象か。</param>
public sealed record SlimItemDefinition(
    SlimItemId Id,
    string Key,
    string NameKey,
    string DescriptionKey,
    string? WarningKey,
    SlimWarningLevel Warning,
    bool RequiresSecondConfirmation,
    bool NeedsRestorePoint)
{
    /// <summary>スリム化の項目は既定で選択しない (おすすめにも含めない)。</summary>
    public bool DefaultSelected => false;
}

/// <summary>走査時の項目の状態。</summary>
public enum SlimScanStatus
{
    /// <summary>実行できる。</summary>
    Available,

    /// <summary>対象がない (理由は <see cref="SlimScanItem.Reason"/>)。</summary>
    NothingToClean,

    /// <summary>安全に判定できないため利用不可 (理由は <see cref="SlimScanItem.Reason"/>)。</summary>
    Unavailable,
}

/// <summary>利用不可・対象なしの理由。UI は区分ごとの文言キーに対応付ける。</summary>
public enum SlimReason
{
    None,

    /// <summary>登録情報などの列挙に 1 件でも失敗した。</summary>
    EnumerationFailed,

    /// <summary>Windows リソース保護または TrustedInstaller 所有のため対象なし。</summary>
    ProtectedByWindows,

    /// <summary>UI 言語・言語一覧・キーボード配列のいずれかで使われている。</summary>
    LanguageInUse,

    /// <summary>未ロードの利用者ハイブがあり、使用状況を確認できない。</summary>
    UserHiveNotLoaded,

    /// <summary>インストールされていない、または対象フォルダー・ファイルが存在しない。</summary>
    NotPresent,

    /// <summary>外部ツール (DISM) の結果を取得・解釈できなかった。</summary>
    ToolFailed,

    /// <summary>安全ガードが対象を拒否した。</summary>
    Rejected,
}

/// <summary>走査した項目 1 件。</summary>
public sealed class SlimScanItem
{
    internal SlimScanItem(SlimItemDefinition definition) => Definition = definition;

    public SlimItemDefinition Definition { get; }

    public SlimScanStatus Status { get; internal set; } = SlimScanStatus.NothingToClean;

    public SlimReason Reason { get; internal set; }

    /// <summary>診断用の詳細 (利用者向けの文言は辞書キー側)。</summary>
    public string Detail { get; internal set; } = string.Empty;

    /// <summary>空きの見込み (バイト)。分からないものは null。</summary>
    public long? Bytes { get; internal set; }

    /// <summary><see cref="Bytes"/> が DISM の推定値など、保証できない値か。</summary>
    public bool BytesIsEstimate { get; internal set; }

    /// <summary>対象ファイル数 (分からないものは 0)。</summary>
    public long Files { get; internal set; }

    /// <summary>代表として挙げた対象のパス。</summary>
    public IReadOnlyList<string> Representatives { get; internal set; } = [];

    public bool Selectable => Status == SlimScanStatus.Available;
}

/// <summary>1 回の走査の結果。世代番号を持ち、再走査・<see cref="SlimService.Invalidate"/> で失効する。</summary>
public sealed class SlimScanResult
{
    internal SlimScanResult(long generation, IReadOnlyList<SlimScanItem> items, Dictionary<SlimItemId, object> payloads)
    {
        Generation = generation;
        Items = items;
        Payloads = payloads;
    }

    public long Generation { get; }

    public IReadOnlyList<SlimScanItem> Items { get; }

    public DateTime CompletedAtUtc { get; internal set; }

    /// <summary>項目ごとの走査時の実体 (削除時の同一性照合に使う)。</summary>
    internal Dictionary<SlimItemId, object> Payloads { get; }

    public SlimScanItem Item(SlimItemId id) => Items.First(i => i.Definition.Id == id);
}

/// <summary>復元ポイントを作れなかったときの利用者の判断。</summary>
public enum RestorePointDecision
{
    /// <summary>復元ポイントなしで続行する。</summary>
    ContinueWithout,

    /// <summary>実行を中止する。</summary>
    Abort,

    /// <summary>システムの保護を有効化して作り直す (保護が無効なときだけ有効)。</summary>
    EnableProtectionAndRetry,
}

/// <summary>実行の要求。</summary>
/// <param name="Scan">実行の元にする走査結果。現在の世代でなければ拒否する。</param>
/// <param name="Items">実行する項目。<see cref="SlimScanStatus.Available"/> でないものは飛ばす。</param>
/// <param name="CreateRestorePoint">対象項目の前に復元ポイントを作る (既定オン)。</param>
/// <param name="ConfirmedIrreversible">元に戻せない項目 (更新のロールバック) の二段階確認を済ませた。</param>
/// <param name="OnRestorePointUnavailable">復元ポイントを利用できないときの判断を聞く。null なら中止する。</param>
public sealed record SlimRunRequest(
    SlimScanResult Scan,
    IReadOnlyCollection<SlimItemId> Items,
    bool CreateRestorePoint = true,
    bool ConfirmedIrreversible = false,
    Func<RestorePointResult, CancellationToken, Task<RestorePointDecision>>? OnRestorePointUnavailable = null);

/// <summary>実行した項目の結果区分。<see cref="Succeeded"/> 以外を成功として表示しない。</summary>
public enum SlimItemStatus
{
    Succeeded,

    /// <summary>一部だけ処理できた (使用中・権限不足など)。</summary>
    Partial,

    Failed,

    /// <summary>実行しなかった (選択外・利用不可・確認なし・中止・前段の失敗)。</summary>
    Skipped,

    Cancelled,
}

/// <summary>項目 1 件の実行結果。</summary>
public sealed record SlimItemResult(SlimItemId Id, SlimItemStatus Status, long FreedBytes, long Files, string Message);

/// <summary>実行全体の結果。</summary>
/// <param name="Items">選択された項目の結果 (走査の順)。</param>
/// <param name="RestorePoint">復元ポイントの結果 (作成しなかった場合は null)。</param>
/// <param name="Rejected">走査結果が古い・要求が不正などで何も実行しなかった理由。</param>
/// <param name="RebootRequired">DISM が再起動を要求した。</param>
public sealed record SlimRunResult(
    IReadOnlyList<SlimItemResult> Items,
    RestorePointResult? RestorePoint,
    string? Rejected,
    bool RebootRequired)
{
    /// <summary>選択された項目がすべて成功し、拒否もない。</summary>
    public bool AllSucceeded => Rejected is null && Items.Count > 0 && Items.All(i => i.Status == SlimItemStatus.Succeeded);
}
