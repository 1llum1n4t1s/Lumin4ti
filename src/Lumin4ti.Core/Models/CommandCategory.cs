namespace Lumin4ti.Core.Models;

/// <summary>左メニューのカテゴリ (サイドバータブと 1:1 対応)。</summary>
public enum CommandCategory
{
    /// <summary>アプリ・定義の更新と Windows セキュリティ関連。</summary>
    Update,
    /// <summary>ディスク・レジストリの不要なファイル・登録情報の削除。</summary>
    Cleanup,
    /// <summary>壊れた登録状態や表示不具合の再登録・再構築による修復。</summary>
    Repair,
    /// <summary>パフォーマンス関連のシステム設定。</summary>
    Performance,
    /// <summary>電源・入力・時刻などのシステム設定。</summary>
    System,
    /// <summary>ピン留め・環境変数などの整理 (ソート)。</summary>
    Organize,
}
