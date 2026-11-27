# クリーンアップと関連する整理操作

[security.md](security.md) から分割した詳細。

## 画面と構成

「クリーンアップ」は `CleanupView` が持つ 6 タブで構成する。

| タブ | 内容 |
| --- | --- |
| 通常 | 規則データから走査した項目を、分類別に件数とサイズ付きで選んで削除する。おすすめ・すべて選択・すべて解除、無視、場所を開く、テキスト書き出しがある |
| 高度 | 準備済みの固定ドライブを拡張子パターンで走査し、ファイル単位で選んで削除する |
| スリム化 | Installer の孤児、更新の残骸、言語の入力補助機能、ヘルプ・壁紙・サンプルを固定の項目表で扱う |
| レジストリ | 実体が消えた登録を、走査・カテゴリー別の選択・バックアップのうえで削除し、バックアップ一覧から復元する。動作が速くなるものではない。[registry-cleaner.md](registry-cleaner.md) |
| 設定 | 除外（ファイル・フォルダー・拡張子とファイル名・Cookie のドメイン）とそのインポート・エクスポート、カスタムフォルダー、安全な削除、復元ポイントの自動作成、累計と前回の実績 |
| その他 | 従来のカード（アイコン・サムネイル・フォントキャッシュ、検索インデックス、SSD の最適化。旧リンク切れスタートアップと旧関連付け候補はレジストリタブへ移した）。`CommandCategoryView` |

走査は lease を `showProcessing:false` で取り、画面内に進捗と中断を出す。削除は `showProcessing:true` で取る。走査・削除は起動時や再アクティブ時に自動で走らせない。旧版の予約実行・CLI・1 クリック起動はない。

## 詳細の分割先

- [cleanup-rules-data.md](cleanup-rules-data.md): 埋め込み JSON の規則、影響度、根拠 tier、検証器、基点の信頼確認。
- [cleanup-scan-deletion.md](cleanup-scan-deletion.md): 走査の世代、同一ハンドル削除、安全な削除、重なる規則、結果の集計と設定。
- [cleanup-guards-handlers.md](cleanup-guards-handlers.md): カスタムフォルダーの拒否、実行中の製品、レジストリ・INI・SQLite、固定ハンドラー。
- [cleanup-advanced-slim.md](cleanup-advanced-slim.md): 高度なクリーン、スリム化と復元ポイント、Defender の定義キャッシュ。
- [registry-cleaner.md](registry-cleaner.md): レジストリタブの範囲・根拠・バックアップと復元。
