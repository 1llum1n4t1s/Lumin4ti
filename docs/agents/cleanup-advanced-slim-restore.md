# 高度なクリーン・スリム化・復元ポイント・その他タブ

[cleanup-rules-and-deletion.md](cleanup-rules-and-deletion.md) から分割した詳細。

## 高度なクリーン

準備済みの固定ドライブをハンドル列挙する（リムーバブルは既定未選択）。`AdvancedProtectedPaths` の枝刈り（Windows 全体、Program Files 系、ProgramData\Microsoft、ごみ箱、System Volume Information、Recovery、Boot、他利用者のプロファイル、認証情報フォルダー、`.git`、Lumin4ti 自身など）を外さない。既定オンの拡張子と追加（既定オフ）の拡張子を分け、判定不能・24 時間以内に更新・0 バイトは未選択にする。表示上限を超えた分は、超過を明示して削除しない。

## スリム化

- 固定の項目表だけを扱い、`SfcIsFileProtected` が保護と答えるファイル、TrustedInstaller 所有のファイルには触れない。
- Installer の孤児は、MSI API とレジストリの和集合に無く、30 日以上古いものだけを対象にする。列挙に 1 件でも失敗したら項目ごと利用不可にし、実行直前に登録を再照合する。
- WinSxS を直接削除せず DISM を使う。IME の本体は削除せず、言語の入力補助機能だけを `Remove-Capability` で扱う。

## 復元ポイント

スリム化の前に `RestorePointService` で作る（既定オン）。24 時間以内の既存点の利用・保護の無効・失敗を区別し、失敗時は続行可否を利用者へ確認する。頻度のレジストリは変更しない。Lumin4ti は復元ポイントを削除しない（一覧・削除・今すぐ作成の機能は持たない）。

## その他タブに残る項目

- `cleanup-shell-cache`（アイコン・サムネイル・フォントキャッシュ）は旧カードのまま残す。`ShellCacheTargets`、項目 ID、`CleanupExclusions`、起動時再試行（[workflow.md](workflow.md)）を変えない。
- Windows のイベントログ、Defender の検出履歴、GPU 設定、アプリのパッケージ登録、WinSxS の旧コンポーネントは規則データで扱わない。スタートアップ登録とファイル関連付けは旧アクション `remove-broken-startup`・`remove-dead-associations` を撤去し、レジストリタブ（[registry-cleaner-guards.md](registry-cleaner-guards.md)）が担う。`StartupCommandParser` は共有のため残す。
- 無効なショートカット・無効なタスクは `ShellLinkReader` と同じ確定欠損の基準で扱い、PIDL のみ・広告型のショートカットと `\Microsoft\` 配下のタスクは保持する。
