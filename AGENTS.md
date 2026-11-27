# AGENTS.md

This file provides guidance to Codex when working in this repository.

## 概要

Lumin4ti は Windows 10/11 向けのメンテナンス・最適化 GUI ツール。管理者権限で自己昇格して起動し、HKLM/HKCU レジストリ・DISM・powercfg・bcdedit・winget・regsvr32・Shell COM・WinRT を操作する。元は AegisOverhaul の巨大バッチ (`Maintenance.bat`) から機能を移植したもので、**バッチのコマンド丸投げをやめて可能な限り C# ネイティブに制御する**方針を継続すること。

## 必須の確認

- 変更後は `dotnet build Lumin4ti.slnx` (0 warnings) と `dotnet test Lumin4ti.slnx` を両方通す。
- 操作フロー (復元・サービス補償・終了時排他・状態表示など) の変更後は `pwsh -NoProfile -File scripts/verify-review-fixes.ps1` も通す。
- バージョンは `/vava` 経由でのみ更新する。
- テストでレジストリ・SCM・COM・デバイスを実変更しない。ファイル削除はテスト専用一時領域に限る。

ビルド・テスト・CI を扱う前に [commands.md](docs/agents/commands.md) を読む。

## 共通の規約

- Core から UI を参照しない。DI は `App.axaml.cs` の手動構成 (全 Singleton) を維持する。
- 外部プロセスは `ICommandExecutor` を通し、`SystemProcessResolver` のフルパス解決を外さない。
- 状態変更は `MaintenanceOperationCoordinator` の lease を通す。補償を含む `finally` より先に終了しない。
- クリーンアップは、根拠 (tier A/B/L) を持つ規則データと利用者が明示した範囲だけを対象にする。保存パスワード・認証情報・鍵・アプリ設定は対象外で、リンク先を辿らず、同一ハンドルで削除し、所有権の奪取や ACL の変更をしない。Cookie・フォーム履歴は既定オフで、利用者が明示的に選んだときだけ消せる。実機のデバイスやデータを消して確認しない。
- 部分失敗を成功と偽らない。UI 文言を直書きせず 17 言語の辞書を更新する。
- 配布契約 (PerMachine MSI・Velopack・`win-x64`・channel `win`) は、利用者が個別に明示しない限り変えない。レビューや「全部直してよい」という許可も変更権限に含めない。
- 梱包・署名・R2 公開は、リリースまたは `/vava` が明示された場合だけ行う。

## 変更前に読む詳細

- メンテナンス項目・カタログを変える前に [architecture.md](docs/agents/architecture.md) を読む。4 型の契約と親子表示を定める。
- 外部プロセス・復元・ファイル削除・排他・昇格・デバイス削除を変える前に [security.md](docs/agents/security.md) を読む。回帰厳禁の安全ガードを定める。
- クリーンアップの規則・走査・削除・高度・スリム化を変える前に [cleanup-rules-and-deletion.md](docs/agents/cleanup-rules-and-deletion.md) を読む。規則データの根拠・影響度・既定選択と、世代失効・lease・補償の契約を定める。
- ログ・デバッグ画面・winget・AUMID・ローカライズ・状態表示の変更時とコードレビュー前に [conventions.md](docs/agents/conventions.md) を読む。
- 配布・署名・リリース・移行コードを変える前に [cloudflare-r2-sigilune.md](docs/agents/cloudflare-r2-sigilune.md) を読む。
- ドメインや更新 URL を変える前に [2026-07-2027-05-31.md](docs/agents/2026-07-2027-05-31.md) を読む。旧ホストの経路は期限 (2027/05/31) まで消さない。
- 構造と設計判断は [DESIGN.md](DESIGN.md) を正本とする。
