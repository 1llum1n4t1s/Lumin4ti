# 実装・レビューの規約

[AGENTS.md](../../AGENTS.md) から分割した詳細。

## ログの保存と移行

ログ保存先・旧ログ移行を変更したら `LegacyLogMigrationTests` を通し、[DESIGN.md の状態と永続化](../design/data-model.md#状態と永続化) にある既知ログ限定・リンク非追従・失敗時保持の境界を維持する。

デバッグ画面・追加ログ出力・進捗オーバーレイを変更したら、`DebuggerIntegrationTests`、`LoggerProviderAttachmentTests`、`MaintenanceOperationCoordinatorTests` の該当検証を通す。デバッグ画面の初期化範囲とログ転送の境界は [DESIGN.md](../design/architecture.md#主要コンポーネント) を維持し、実画面は [WORKFLOWS.md のプレビュー手順](../../scripts/verification/WORKFLOWS.md#再現) で背面操作禁止・中断後の待機・ファイルログとの併存を確認する。

## winget によるアプリ更新

[WingetUpgradeAction](../../src/Lumin4ti.Core/Services/Windows/Actions/UpdateActions.cs) の更新候補は、公式ソース、Package ID、表示名と更新チャネルの整合性を確認してから個別に実行する。`upgrade --all` へ置き換えず、確認不能な一覧では更新を中止し、確認不能な個別候補は除外して他の候補を続行すること。

個別の成功・失敗・除外、終了コードと出力要約を結果・進捗・ログへ残す。判定と診断を変更するときは [WingetOutputFilterTests](../../src/Lumin4ti.Tests/WingetOutputFilterTests.cs) の別チャネル拒否、個別結果、部分成功の検証を通す。データフローと集計結果の条件は [DESIGN.md のインストール済みアプリの更新](../design/workflow.md#インストール済みアプリの更新) を参照する。

## タスクバーアイコンと AUMID (回帰注意)

「**プロセスだけが AUMID を名乗り、ショートカットには何も書かない**」方針を維持する:

- 製品版 (`#if !DEBUG`) だけ起動直後に `WindowsElevationHelper.TrySetCurrentProcessAppUserModelId()` で Velopack の AUMID を設定する。Debug ビルドで名乗ると Windows がインストール済み製品の情報を参照して開発用 EXE のタスクバーアイコンが白紙になるため設定しない。
- ショートカット (.lnk) へ AUMID や明示アイコンを追記しない。埋め込みアイコンの解決は Windows に任せる。v1.0.12 が追記した override は起動時に [WindowsLegacyStartMenuShortcutMigrator.ClearInstalledShortcutOverrides()](../../src/Lumin4ti.UI/Services/WindowsLegacyStartMenuShortcutMigrator.cs) が Start メニュー・デスクトップ・タスクバーピン留めから除去する。
- .lnk のプロパティ操作が必要な場合は [WindowsShortcutPropertyStore](../../src/Lumin4ti.UI/Services/WindowsShortcutPropertyStore.cs) (IPropertyStore 直叩き) を使い、既存リンクへの Commit を維持する。`ShellLink.SetAppUserModelId` へ置き換えない。

## ローカライズ

辞書の構造とフォールバックは [DESIGN.md のローカライズ](../design/architecture.md#ローカライズ) を参照する。UI 文言を直書きせず、XAML は `{DynamicResource Text.Xxx}`、C# は `App.Text("key", 日本語フォールバック, args)` で解決する。

- Core は翻訳キーと日本語フォールバックを持ち、翻訳解決は UI 側で行う。`IMaintenanceItem.LabelKey` は `Action.{Id}.Label`、`DescriptionKey` は `Action.{Id}.Desc`。共通 UI 文字列を追加したら 17 言語すべてに同じ `x:Key` を足す。非 ja 辞書のキー集合は `en_US.axaml` と一致させ、ja の Action・カテゴリ・ステータスはコード内日本語フォールバックを維持する。
- `App.SetLocale()` が辞書を差し替え、`App.LocaleChanged` を購読する VM プロパティが再評価される。既定言語は `App.DetectDefaultLocale()` が OS ロケールから判定、選択は settings.json の `Locale` に保存。
- アクションの実行結果ログ行 (`  - ...しました`) は技術的詳細なので日本語のまま。

## コードレビュー時の注意点 (このコードベース特有)

トグルの操作可否、状態表示と診断、部分失敗、NTP・MMAgent、配布契約テストの注意点は [section-02.md](section-02.md) にある。該当箇所の変更時とコードレビュー前に必ず読む。
