namespace Lumin4ti.Core.Models;

/// <summary>メンテナンスアクションの実行結果。Detail は画面にそのまま表示する複数行テキスト。</summary>
public sealed record MaintenanceActionResult(bool Success, string Detail)
{
    public static MaintenanceActionResult Ok(IEnumerable<string> lines) =>
        new(true, string.Join(Environment.NewLine, lines));

    public static MaintenanceActionResult Ok(string detail = "") => new(true, detail);

    public static MaintenanceActionResult Fail(string detail) => new(false, detail);
}
