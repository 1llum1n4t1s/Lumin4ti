namespace Lumin4ti.Core.Services.Windows.Cleanup.Handlers;

/// <summary>走査で分かった状態。</summary>
public enum CleanupHandlerScanState
{
    /// <summary>削除できるものがある。</summary>
    Found,

    /// <summary>対象は利用できるが、削除するものがない。</summary>
    Empty,

    /// <summary>実行できるが、件数・サイズは事前に数えられない (DNS キャッシュなど)。</summary>
    Unmeasurable,

    /// <summary>この環境では実行できない・確認できない。理由は <see cref="CleanupHandlerScan.Detail"/>。</summary>
    Unavailable,
}

/// <summary>走査結果。走査は何も変更しない。</summary>
/// <param name="State">状態。</param>
/// <param name="Items">件数。数えられなければ null。</param>
/// <param name="Bytes">サイズ。数えられなければ null。</param>
/// <param name="Detail">利用できない理由・補足 (表示用)。</param>
public sealed record CleanupHandlerScan(
    CleanupHandlerScanState State,
    long? Items = null,
    long? Bytes = null,
    string? Detail = null);

/// <summary>実行結果の区分。<see cref="Succeeded"/> 以外を成功として表示してはならない。</summary>
public enum CleanupHandlerStatus
{
    /// <summary>対象を全て処理できた。</summary>
    Succeeded,

    /// <summary>削除するものがなく、何も変更しなかった。</summary>
    NothingToDo,

    /// <summary>一部だけ処理できた。</summary>
    Partial,

    /// <summary>処理できなかった、またはサービスの復帰に失敗した。</summary>
    Failed,

    /// <summary>取消された (補償は完了済み)。</summary>
    Canceled,
}

/// <summary>実行結果。</summary>
/// <param name="Status">区分。</param>
/// <param name="Items">処理した件数。</param>
/// <param name="Bytes">解放したサイズ。</param>
public sealed record CleanupHandlerResult(
    CleanupHandlerStatus Status,
    long Items = 0,
    long Bytes = 0)
{
    /// <summary>失敗・部分成功・補償失敗の理由 (表示用)。</summary>
    public IReadOnlyList<string> Details { get; init; } = [];

    /// <summary>成功として表示してよいか (対象なしの無変更を含む)。</summary>
    public bool IsSuccess => Status is CleanupHandlerStatus.Succeeded or CleanupHandlerStatus.NothingToDo;

    internal static CleanupHandlerResult Failed(string reason) =>
        new(CleanupHandlerStatus.Failed) { Details = [reason] };
}

/// <summary>
/// ルールの <c>handler</c> 対象を実装する特殊処理。実機への変更は <see cref="RunAsync"/> だけで行い、
/// OS 操作は境界を介するため、テストでは実機を変えずに差し替えられる。
/// </summary>
/// <remarks>
/// 呼び出し側 (走査・実行サービス) の約束:
/// <list type="bullet">
/// <item>走査は件数・サイズ・状態を返すだけで、ファイル・レジストリ・サービスを変更しない。取消は <see cref="OperationCanceledException"/>。</item>
/// <item>実行は例外を投げず結果で返す。サービスを止めた場合は、失敗・取消でも <c>finally</c> で必ず復帰し、
/// 復帰に失敗したら <see cref="CleanupHandlerStatus.Failed"/> にする (取消や成功で隠さない)。</item>
/// <item>利用者スコープ (HKCU・ごみ箱・履歴) のハンドラーは、対話利用者と昇格利用者の SID が違う場合は呼び出し側が無効にする。</item>
/// </list>
/// </remarks>
public interface ICleanupHandler
{
    /// <summary>規則の <c>handler</c> に書く名前。</summary>
    string Id { get; }

    /// <summary>実行中に一時停止するサービス (確認画面の表示用)。</summary>
    IReadOnlyList<string> Services { get; }

    /// <summary>削除せずに件数・サイズ・状態を調べる。</summary>
    Task<CleanupHandlerScan> ScanAsync(CancellationToken ct);

    /// <summary>削除する。進捗は行単位で通知する。</summary>
    Task<CleanupHandlerResult> RunAsync(IProgress<string>? progress, CancellationToken ct);
}
