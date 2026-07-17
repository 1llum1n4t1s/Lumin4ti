namespace Lumin4ti.Core.Models;

/// <summary>メンテナンスアクションの完了状態。</summary>
public enum MaintenanceActionStatus
{
    Success,
    Partial,
    Failed,
    Canceled,
}

/// <summary>メンテナンスアクションの実行結果。Detail は画面にそのまま表示する複数行テキスト。</summary>
public sealed record MaintenanceActionResult
{
    /// <summary>
    /// 既存呼出しとの互換コンストラクタ。true は完全成功、false は失敗として扱う。
    /// </summary>
    public MaintenanceActionResult(bool Success, string Detail)
        : this(Success ? MaintenanceActionStatus.Success : MaintenanceActionStatus.Failed, Detail)
    {
    }

    private MaintenanceActionResult(MaintenanceActionStatus status, string detail)
    {
        Status = status;
        Detail = detail;
    }

    public MaintenanceActionStatus Status { get; init; }

    /// <summary>後方互換用。完全成功の場合だけ true。</summary>
    public bool Success => Status == MaintenanceActionStatus.Success;

    public string Detail { get; init; }

    public void Deconstruct(out bool Success, out string Detail)
    {
        Success = this.Success;
        Detail = this.Detail;
    }

    public static MaintenanceActionResult Ok(IEnumerable<string> lines) =>
        new(MaintenanceActionStatus.Success, string.Join(Environment.NewLine, lines));

    public static MaintenanceActionResult Ok(string detail = "") =>
        new(MaintenanceActionStatus.Success, detail);

    public static MaintenanceActionResult Partial(IEnumerable<string> lines) =>
        new(MaintenanceActionStatus.Partial, string.Join(Environment.NewLine, lines));

    public static MaintenanceActionResult Partial(string detail) =>
        new(MaintenanceActionStatus.Partial, detail);

    public static MaintenanceActionResult Fail(string detail) =>
        new(MaintenanceActionStatus.Failed, detail);

    public static MaintenanceActionResult Canceled(string detail = "") =>
        new(MaintenanceActionStatus.Canceled, detail);
}
