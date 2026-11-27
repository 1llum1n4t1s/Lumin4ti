# Lumin4ti 設計

システム構造と設計判断の正本である。開発時の規約は [AGENTS.md](AGENTS.md) を参照する。

## 目的と対象範囲

Lumin4ti は Windows 10 / 11（x64）向けのメンテナンス・最適化GUIである。利用者がコマンドを直接入力せずに、Windows Update、セキュリティ、クリーンアップ、修復、性能・システム設定、並べ替えをカテゴリ別に実行できるようにする。

アプリは管理者権限で動作し、レジストリ、DISM、powercfg、bcdedit、winget、Shell COM、WinRTなどを操作する。直接表現できる処理はC#で実装し、OS提供ツールが正式な実行手段の場合だけ外部プロセスを使う。

## 主要コンポーネント

`Lumin4ti.Core` が OS 操作、`Lumin4ti.UI` が Avalonia 画面、`Lumin4ti.RebootCleanup` が起動時再試行を担う。Core は UI を参照しない。責務と設計判断は下記の文書にまとめる。

## 処理の流れ

旧配置の移行は自己昇格より前に行い、昇格後に単一起動 mutex を取得する。操作はカタログの Action / BatchSetting / Toggle / Choice を ViewModel が lease 内で実行し、結果と実状態を画面へ返す。クリーンアップは通常・高度・スリム化・レジストリ・設定・その他の 6 タブで、Core が規則データ（埋め込み JSON）と固定の項目表を走査し、選ばれた項目だけを同一ハンドルで削除する。旧サインイン時クリーンアップは廃止し、残存タスクは起動時に自己移行で取り除く。

## OS操作の境界

外部コマンドは完全パスへ解決し、子ごとの Job Object で管理する。サービスは停止を確認できた場合だけ操作し、必ず復帰を試みる。詳細は [security.md](docs/design/security.md) を参照する。

## 設定と状態

ON/OFF 設定は永続バックアップを持たず、直前値をメモリに保持して補償する。変更範囲は [configuration.md](docs/design/configuration.md)、永続化・ログの境界は [data-model.md](docs/design/data-model.md) を参照する。

## 更新と配布

更新元と channel を固定し、Velopack の署名済み PerMachine MSI を `win-x64` 向けに R2 で配信する。manifest の更新を公開の確定点とする。成果物の検証と製品ページの経路は [release-process.md](docs/design/release-process.md) を参照する。

## 重要な不変条件

- 状態変更操作は GUI と旧タスクの移行を含めマシン全体で同時に 1 件とする。
- クリーンアップは根拠を持つ規則と利用者が明示した範囲だけを対象にし、認証情報・鍵・アプリ設定は対象外とする。Cookie・フォーム履歴は既定オフ。
- 重要ステップの失敗は `Failed`、想定内の未処理は `Partial` とし、成功と偽らない。
- 最高権限タスクは、信頼を検証できた PerMachine 実体だけを登録する。
- 更新元と channel は `settings.json` から変更できない。

## 詳細文書

- [architecture.md](docs/design/architecture.md): 責務、ローカライズ、設計判断の理由。
- [debug-and-verification.md](docs/design/debug-and-verification.md): デバッグ画面と模擬 OS の操作フロー検証。
- [workflow.md](docs/design/workflow.md): 起動・終了、クリーンアップ、デバイス管理の流れ。
- [maintenance-action-model.md](docs/design/maintenance-action-model.md): 4 型の契約と進捗表示。
- [winget-upgrade-flow.md](docs/design/winget-upgrade-flow.md): winget 更新の候補検証と結果集計。
- [unelevated-winget-retry.md](docs/design/unelevated-winget-retry.md): 失敗候補だけを非昇格で再試行する条件。
- [reboot-cache-retry.md](docs/design/reboot-cache-retry.md): 使用中キャッシュの次回起動での再試行。
- [data-model.md](docs/design/data-model.md): 設定・ログ・バックアップと同一ハンドル操作。
- [toggle-and-uwp-settings.md](docs/design/toggle-and-uwp-settings.md): ON/OFF・VBS/HVCI・UWP 一括設定と補償。
- [cleanup-design.md](docs/design/cleanup-design.md): クリーンアップのタブ構成。
- [cleanup-rules-data.md](docs/design/cleanup-rules-data.md): 埋め込み規則と検証、基点の信頼確認。
- [cleanup-scan-deletion.md](docs/design/cleanup-scan-deletion.md): 走査の世代と同一ハンドル削除。
- [cleanup-guards-handlers.md](docs/design/cleanup-guards-handlers.md): 拒否ガード、SQLite、固定ハンドラー。
- [cleanup-advanced-slim.md](docs/design/cleanup-advanced-slim.md): 高度なクリーン、スリム化、Defender。
- [registry-cleaner.md](docs/design/registry-cleaner.md): レジストリタブの範囲・根拠・バックアップと復元。
- [legacy-install-migration.md](docs/design/legacy-install-migration.md): 旧 PerUser 版から PerMachine 版への移行。
- [legacy-scheduled-cleanup-removal.md](docs/design/legacy-scheduled-cleanup-removal.md): 廃止した旧タスクの自己移行除去。
