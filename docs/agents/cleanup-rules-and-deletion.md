# クリーンアップ（規則・走査・削除）

[security.md](security.md) から分割した詳細。

クリーンアップは「通常」「高度」「スリム化」「設定」「その他」の 5 タブで構成する。通常は規則データ、高度は拡張子パターン、スリム化は固定の項目表を対象にする。実装は [Cleanup/](../../src/Lumin4ti.Core/Services/Windows/Cleanup/) に置く。構造と設計判断は [DESIGN.md](../../DESIGN.md) と [設計の詳細](../design/cleanup-design.md) を参照する。

## トピック別の詳細

- [cleanup-rule-data.md](cleanup-rule-data.md): 規則データの埋め込み、根拠 tier A/B/L、影響度と既定選択、検証器、認証情報の拒否表、レジストリの範囲。
- [cleanup-deletion-guards.md](cleanup-deletion-guards.md): 走査の世代失効、同一ハンドル削除、リンク非追従、規則の重なり、部分失敗の扱い、`CleanupMessage`、lease と補償。回帰厳禁。
- [cleanup-path-trust.md](cleanup-path-trust.md): カスタムフォルダーの `CustomFolderGuard`、基点の環境変数を検証する `CleanupBaseTrust`、SID 不一致時の無効化。
- [cleanup-running-products-sqlite.md](cleanup-running-products-sqlite.md): 実行中の製品の終了手順、SQLite の非信頼入力としての開き方とサイドカー固定、INI の編集。
- [cleanup-advanced-slim-restore.md](cleanup-advanced-slim-restore.md): 高度なクリーンの枝刈り、スリム化の対象と DISM、復元ポイント、その他タブに残る項目。

## テスト

回帰は [FileCleanupTests](../../src/Lumin4ti.Tests/FileCleanupTests.cs) と各規則・走査・ハンドラーのテストが検出する。「許可リストだけを使う」ではなく「保護・認証情報の拒否」を検証する。リンク非追従・同一ハンドル・削除中の差し替えは新しい経路でも再現して検証し、テストでレジストリ・SCM・COM・デバイスを実変更しない。
