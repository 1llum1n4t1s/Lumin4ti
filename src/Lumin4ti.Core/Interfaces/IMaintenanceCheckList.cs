namespace Lumin4ti.Core.Interfaces;

/// <summary>
/// チェックリスト 1 行分。<paramref name="Value"/> は設定へ保存する永続識別子で、
/// 表示は <paramref name="Label"/> をそのまま出す (パスやグループ名なので翻訳しない)。
/// </summary>
/// <param name="Value">保存に使う識別子 (削除対象なら未展開のパス、グループ選択ならグループ Id)。</param>
/// <param name="Label">画面に出す表示名。</param>
/// <param name="IsSelected">チェック状態 (true = 実行対象に含める)。</param>
/// <param name="LabelKey">翻訳が要る表示名のローカライズキー (null なら Label をそのまま出す)。</param>
public sealed record MaintenanceCheckListEntry(
    string Value,
    string Label,
    bool IsSelected,
    string? LabelKey = null);

/// <summary>
/// 項目カードの中に折りたたみのチェックボックス一覧を持てる項目。
/// 「この対象だけ消したくない」を利用者が選べるようにするためのもので、
/// チェック状態は実装側が設定ファイルへ永続化する。
/// </summary>
public interface IMaintenanceCheckList : IMaintenanceItem
{
    /// <summary>一覧の見出し (日本語マスター)。</summary>
    string CheckListCaption { get; }

    /// <summary>見出しのローカライズキー。</summary>
    string CheckListCaptionKey => $"Action.{Id}.CheckList";

    /// <summary>現在の一覧 (実行時に評価する。ブラウザのプロファイル等は環境で増減するため)。</summary>
    IReadOnlyList<MaintenanceCheckListEntry> GetCheckListEntries();

    /// <summary>チェック状態を変更して保存する。</summary>
    Task SetCheckListEntrySelectedAsync(string value, bool selected, CancellationToken ct = default);
}
