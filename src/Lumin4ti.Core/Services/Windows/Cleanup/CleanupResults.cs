using System.Globalization;
using System.Text;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

// 通常クリーンの走査・実行が画面 (4a/4b) へ返す結果モデルとテキスト書き出し。
// 内部の規則型 (CleanupRule など) は公開しないため、画面が使う値だけをここへ写す。

/// <summary>規則の分類 (画面のグループ)。<c>Custom</c> は利用者のカスタムフォルダー。</summary>
public enum CleanupRuleCategory
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

/// <summary>規則の種別。表示名は「{0} のキャッシュ」などの書式キーで組み立てる。</summary>
public enum CleanupRuleKind
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

/// <summary>削除の影響。<c>UserFiles</c> は「おすすめ」「すべて選択」に含めず、個別選択と再確認を必須にする。</summary>
public enum CleanupRuleImpact
{
    Junk,
    Trace,
    Signout,
    Rebuild,
    UserFiles,
}

/// <summary>画面が表示に使う規則の情報。</summary>
/// <param name="Id">規則 ID。</param>
/// <param name="Name">製品名 (翻訳しない)。<paramref name="NameKey"/> がある Windows 項目では英語の既定名。</param>
/// <param name="NameKey">辞書キー。無ければ <paramref name="Name"/> を種別の書式で表示する。</param>
/// <param name="WarningKey">警告文の辞書キー。</param>
/// <param name="IsCustom">利用者のカスタムフォルダー。</param>
/// <param name="Processes">実行中なら閉じる必要がある製品のプロセス名。</param>
/// <param name="Services">実行中に一時停止するサービス。</param>
/// <param name="AllowsForceClose">WM_CLOSE で閉じない場合に、再確認のうえ強制終了できる分類か。</param>
public sealed record CleanupRuleInfo(
    string Id,
    string Name,
    string? NameKey,
    CleanupRuleKind Kind,
    CleanupRuleCategory Category,
    CleanupRuleImpact Impact,
    bool Recommended,
    string? WarningKey,
    bool IsCustom,
    IReadOnlyList<string> Processes,
    IReadOnlyList<string> Services,
    bool AllowsForceClose)
{
    internal static CleanupRuleInfo From(CleanupRule rule, IReadOnlyList<string> services) => new(
        rule.Id,
        rule.Name,
        rule.NameKey,
        Enum.Parse<CleanupRuleKind>(rule.Kind.ToString()),
        Enum.Parse<CleanupRuleCategory>(rule.Category.ToString()),
        Enum.Parse<CleanupRuleImpact>(rule.Impact.ToString()),
        rule.Recommended,
        rule.WarningKey,
        rule.Category == CleanupCategory.Custom,
        [.. rule.Processes ?? []],
        services,
        RunningProcessGuard.AllowsForceTermination(rule.Category));
}

/// <summary>走査で分かった規則の状態。</summary>
public enum CleanupItemState
{
    /// <summary>この PC に製品・場所が見つからない (一覧には出さない)。</summary>
    NotDetected,

    /// <summary>見つかったが、削除するものがない。</summary>
    Empty,

    /// <summary>削除できるものがある。</summary>
    Found,

    /// <summary>実行できるが、件数・サイズは事前に数えられない (DNS キャッシュなど)。</summary>
    Unmeasurable,

    /// <summary>この環境では実行できない・確認できない。理由は <see cref="CleanupScanItem.Reason"/>。</summary>
    Unavailable,
}

/// <summary><see cref="CleanupItemState.Unavailable"/> の理由。</summary>
public enum CleanupUnavailableReason
{
    None,

    /// <summary>対話利用者と昇格利用者が別人のため、利用者スコープの規則を無効にした。</summary>
    UserScopeDifferentUser,

    /// <summary>対話利用者または昇格利用者を確認できないため、利用者スコープの規則を無効にした。</summary>
    UserScopeUnknown,

    /// <summary>対象を安全ガードで拒否した (保護された場所・認証情報・リンクなど)。</summary>
    TargetRejected,

    /// <summary>走査に失敗した。</summary>
    ScanFailed,

    /// <summary>ハンドラーが利用できないと報告した。</summary>
    HandlerUnavailable,

    /// <summary>カスタムフォルダーの実行時検査で拒否した。</summary>
    CustomFolderRejected,
}

/// <summary>利用者スコープの規則を使えるか。</summary>
public enum CleanupScopeStatus
{
    Available,
    DifferentUser,
    Unknown,
}

/// <summary>1 規則の走査結果。何も変更していない。</summary>
/// <param name="Files">削除対象のファイル数。</param>
/// <param name="Directories">空になるため削除対象のフォルダー数。</param>
/// <param name="Items">ファイル以外の件数 (レジストリの値・キー、DB の行、INI のキー、ハンドラーの件数)。</param>
/// <param name="Bytes">削除対象のサイズ。</param>
/// <param name="Retained">除外のため残す数。</param>
/// <param name="Unreadable">調べられなかった数。</param>
/// <param name="SkippedLinks">リンクのため触れない数。</param>
/// <param name="DefaultSelected">既定で選択するか (利用者の上書き → 規則の推奨の順)。<c>UserFiles</c> とカスタムは常に false。</param>
/// <param name="Representatives">代表として挙げた削除対象のパス (表示用)。</param>
/// <param name="RunningProcesses">いま動いている、この規則の製品のプロセス名 (名前だけの照合)。</param>
/// <param name="Detail">拒否・失敗・補足の理由 (表示用)。</param>
public sealed record CleanupScanItem(
    CleanupRuleInfo Rule,
    CleanupItemState State,
    CleanupUnavailableReason Reason,
    long Files,
    long Directories,
    long Items,
    long Bytes,
    long Retained,
    long Unreadable,
    long SkippedLinks,
    bool DefaultSelected,
    IReadOnlyList<string> Representatives,
    IReadOnlyList<string> RunningProcesses,
    IReadOnlyList<string> Detail,
    IReadOnlyList<CleanupScanOverlap>? Overlaps = null)
{
    /// <summary>選択して実行できる状態か。</summary>
    public bool CanSelect => State is CleanupItemState.Found or CleanupItemState.Unmeasurable;
}

/// <summary>
/// 規則の件数・サイズのうち、先に走査した別の規則も対象にしているファイル・フォルダーの内訳。
/// 規則別の表示には含めたままにし、複数の規則を選んだ合計では、先の規則のどれかが選ばれていればこの分を引く。
/// </summary>
/// <param name="ClaimedBy">同じものを対象にしている先の規則の識別子 (どれか 1 つでも選ばれていれば、この分は先の規則が数える)。</param>
public sealed record CleanupScanOverlap(IReadOnlyList<string> ClaimedBy, long Files, long Directories, long Bytes);

/// <summary>選んだ規則の合計。包含関係にある対象で同じファイルを二重に数えない。</summary>
public readonly record struct CleanupSelectionTotals(long Files, long Directories, long Items, long Bytes);

/// <summary>走査の進捗。</summary>
public sealed record CleanupScanProgress(int Completed, int Total, string? CurrentRuleId, string? CurrentName);

/// <summary>「おすすめ」「すべて選択」の方針。</summary>
public static class CleanupSelection
{
    /// <summary>おすすめ: 再生成可能 (junk) で推奨の規則だけ。</summary>
    public static IReadOnlyList<string> Recommended(CleanupScanSnapshot snapshot) =>
    [
        .. snapshot.Items.Where(i => i.CanSelect && i.Rule is { Recommended: true, Impact: CleanupRuleImpact.Junk }).Select(i => i.Rule.Id),
    ];

    /// <summary>すべて選択: 実行できる規則すべて。受信ファイルなどの <c>UserFiles</c> (カスタムフォルダーを含む) は含めない。</summary>
    public static IReadOnlyList<string> All(CleanupScanSnapshot snapshot) =>
    [
        .. snapshot.Items.Where(i => i.CanSelect && i.Rule.Impact != CleanupRuleImpact.UserFiles).Select(i => i.Rule.Id),
    ];
}

/// <summary>規則 1 件の実行の結果区分。<see cref="Succeeded"/> と <see cref="NothingToDo"/> 以外を成功として表示してはならない。</summary>
public enum CleanupRunStatus
{
    /// <summary>全て処理できた。</summary>
    Succeeded,

    /// <summary>削除するものがなく、何も変更しなかった。</summary>
    NothingToDo,

    /// <summary>一部だけ処理できた。</summary>
    Partial,

    /// <summary>処理できなかった、またはサービスの復帰に失敗した。</summary>
    Failed,

    /// <summary>実行しなかった (理由は <see cref="CleanupRuleRunResult.SkipReason"/>)。</summary>
    Skipped,

    /// <summary>この規則の実行中に取消された (補償は完了済み)。</summary>
    Canceled,

    /// <summary>取消のため着手しなかった。</summary>
    NotRun,
}

/// <summary>実行しなかった理由。</summary>
public enum CleanupSkipReason
{
    None,

    /// <summary>この走査結果に含まれない、または対象が見つからなかった規則。走査していないものは削除しない。</summary>
    NotScanned,

    /// <summary>走査で削除対象がなかった・この PC に無い。</summary>
    NothingFound,

    /// <summary>走査の時点または実行の時点で利用できない (利用者スコープの不一致・拒否など)。</summary>
    Unavailable,

    /// <summary>受信ファイルなどの <c>UserFiles</c> の再確認が無い。</summary>
    UserFilesNotConfirmed,

    /// <summary>製品が動いており、利用者が「この項目を飛ばす」を選んだ (確認の手段が無い場合も含む)。</summary>
    ProcessRunningDeclined,

    /// <summary>利用者を確認できないプロセスがあり、閉じられない。</summary>
    ProcessUnverifiable,

    /// <summary>WM_CLOSE で閉じず、この分類は強制終了の対象外。</summary>
    ProcessStillRunning,

    /// <summary>強制終了の再確認で同意されなかった。</summary>
    ForceTerminationDeclined,

    /// <summary>ハンドルでの再照合 (利用者・セッション・名前) が一致せず、終了しなかった。</summary>
    ProcessIdentityMismatch,

    /// <summary>強制終了が拒否・失敗した、または終了を確認できなかった。</summary>
    TerminateFailed,
}

/// <summary>規則 1 件の実行結果。</summary>
/// <param name="Files">削除したファイル数。</param>
/// <param name="Directories">削除したフォルダー数。</param>
/// <param name="Items">ファイル以外の削除件数。</param>
/// <param name="Bytes">解放したサイズ。</param>
/// <param name="Retained">除外・走査後の作成・走査時との不一致により意図して残した数 (失敗ではない)。</param>
/// <param name="Blocked">使用中・権限不足などで削除できなかった数。</param>
/// <param name="Details">失敗・部分成功・補償失敗・スキップの理由 (表示用)。</param>
public sealed record CleanupRuleRunResult(
    CleanupRuleInfo Rule,
    CleanupRunStatus Status,
    CleanupSkipReason SkipReason,
    long Files,
    long Directories,
    long Items,
    long Bytes,
    long Retained,
    long Blocked,
    long WipedFiles,
    long WipeSkipped,
    IReadOnlyList<string> Details)
{
    /// <summary>成功として表示してよいか (対象なしの無変更を含む)。</summary>
    public bool IsSuccess => Status is CleanupRunStatus.Succeeded or CleanupRunStatus.NothingToDo;
}

/// <summary>実行全体の結果。</summary>
public sealed class CleanupRunResult
{
    internal CleanupRunResult(IReadOnlyList<CleanupRuleRunResult> items, bool staleScan, bool canceled, DateTimeOffset startedAt, DateTimeOffset completedAt)
    {
        Items = items;
        StaleScan = staleScan;
        Canceled = canceled;
        StartedAt = startedAt;
        CompletedAt = completedAt;
    }

    /// <summary>規則ごとの結果 (走査結果の並び順)。</summary>
    public IReadOnlyList<CleanupRuleRunResult> Items { get; }

    /// <summary>走査結果が失効していたため、何も実行しなかった (設定変更・削除開始・再走査・別の実行のため)。</summary>
    public bool StaleScan { get; }

    /// <summary>取消された。</summary>
    public bool Canceled { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset CompletedAt { get; }

    public long FilesDeleted => Items.Sum(i => i.Files);

    public long BytesFreed => Items.Sum(i => i.Bytes);

    /// <summary>残した・削除できなかった数 (除外などの意図した保持 + 失敗)。</summary>
    public long FilesSkipped => Items.Sum(i => i.Retained + i.Blocked);

    /// <summary>選択した全ての規則を成功として処理できたか (取消・失敗・スキップがあれば false)。</summary>
    public bool IsComplete => !StaleScan && !Canceled && Items.All(i => i.IsSuccess);
}

/// <summary>テキスト書き出しの見出し。画面が辞書から渡す (既定は英語)。</summary>
public sealed record CleanupTextLabels(
    string Name = "Name",
    string Category = "Category",
    string State = "State",
    string Files = "Files",
    string Items = "Items",
    string Bytes = "Bytes",
    string Retained = "Retained",
    string Status = "Status",
    string Details = "Details",
    string Total = "Total")
{
    /// <summary>理由の符号 (<see cref="CleanupMessage"/>) を書き出す言語の文言にする関数。null なら日本語のマスターを使う。</summary>
    public Func<string, string>? DescribeMessage { get; init; }

    internal string Describe(string detail) => DescribeMessage is { } describe ? describe(detail) : CleanupMessage.ToJapanese(detail);
}

/// <summary>走査・実行結果のテキスト書き出し (タブ区切り)。</summary>
public static class CleanupResultsText
{
    private const int MaxDetailLength = 512;

    /// <summary>走査結果を書き出す。この PC に無い規則 (NotDetected) は含めない。</summary>
    public static string FromScan(CleanupScanSnapshot snapshot, CleanupTextLabels? labels = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        labels ??= new CleanupTextLabels();
        var text = new StringBuilder();
        AppendRow(text, labels.Name, labels.Category, labels.State, labels.Files, labels.Items, labels.Bytes, labels.Retained, labels.Details);
        foreach (var item in snapshot.Items.Where(i => i.State != CleanupItemState.NotDetected))
        {
            AppendRow(
                text,
                item.Rule.Name,
                item.Rule.Category.ToString(),
                item.State.ToString(),
                Number(item.Files),
                Number(item.Items),
                Number(item.Bytes),
                Number(item.Retained),
                string.Join(" | ", item.Detail.Select(labels.Describe)));
        }

        AppendRow(
            text,
            labels.Total,
            string.Empty,
            string.Empty,
            Number(snapshot.TotalFiles),
            Number(snapshot.TotalItems),
            Number(snapshot.TotalBytes),
            string.Empty,
            string.Empty);
        return text.ToString();
    }

    /// <summary>実行結果を書き出す。</summary>
    public static string FromRun(CleanupRunResult result, CleanupTextLabels? labels = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        labels ??= new CleanupTextLabels();
        var text = new StringBuilder();
        AppendRow(text, labels.Name, labels.Category, labels.Status, labels.Files, labels.Items, labels.Bytes, labels.Retained, labels.Details);
        foreach (var item in result.Items)
        {
            var reason = item.SkipReason == CleanupSkipReason.None ? [] : new[] { item.SkipReason.ToString() };
            AppendRow(
                text,
                item.Rule.Name,
                item.Rule.Category.ToString(),
                item.Status.ToString(),
                Number(item.Files),
                Number(item.Items),
                Number(item.Bytes),
                Number(item.Retained),
                string.Join(" | ", reason.Concat(item.Details.Select(labels.Describe))));
        }

        AppendRow(
            text,
            labels.Total,
            string.Empty,
            result.StaleScan ? "StaleScan" : result.Canceled ? "Canceled" : result.IsComplete ? "Complete" : "Incomplete",
            Number(result.FilesDeleted),
            Number(result.Items.Sum(i => i.Items)),
            Number(result.BytesFreed),
            Number(result.Items.Sum(i => i.Retained)),
            string.Empty);
        return text.ToString();
    }

    /// <summary>
    /// 書き出しを UTF-8 (BOM 付き) で保存する。絶対パスだけを受け付け、既存のリンクには書かない。
    /// </summary>
    public static void Save(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("保存先は絶対パスで指定してください", nameof(path));
        }

        var full = Path.GetFullPath(path);
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(CleanupMessage.Of("ExportLinkRefused"));
        }

        File.WriteAllText(full, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static void AppendRow(StringBuilder text, params string[] cells)
    {
        text.AppendJoin('\t', cells.Select(Clean)).Append("\r\n");
    }

    /// <summary>タブ・改行・制御文字を空白にして、1 行 1 規則を保つ。長すぎる文は切る。</summary>
    private static string Clean(string value)
    {
        var chars = value.Select(c => char.IsControl(c) ? ' ' : c).ToArray();
        var cleaned = new string(chars);
        return cleaned.Length <= MaxDetailLength ? cleaned : cleaned[..MaxDetailLength] + "…";
    }
}
