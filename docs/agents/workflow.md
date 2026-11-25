# 起動・終了・タスク実行の規約

[security.md](security.md) から分割した詳細。

## 起動時再試行とサインイン時クリーンアップ (回帰厳禁)

- 使用中キャッシュの起動時再試行を変更したら、`FileCleanupTests`、`RebootCacheCleanupTests`、`RebootCleanupTaskTests` と操作フロー検証を通す。[DESIGN.md の使用中キャッシュの起動時再試行](../design/workflow.md#使用中キャッシュの起動時再試行) の対象・利用者・信頼確認・期限の境界を維持する。任意パスや SYSTEM 実行へ広げず、旧 MoveFileEx 予約を読み取り・変更・移行しない。Explorer を kill しない。
- サインイン時クリーンアップは画面と同じ `FileCleanupGroups` と利用者設定を使い、タスクスケジューラーから最高権限で実行する。タスクへ登録できるのは、署名・MSI マーカー・`Program Files\Lumin4ti\current` の配置と各パス要素の所有者／ACL／再解析ポイントを検証できた実行ファイルだけとする。登録用 XML は `%TEMP%` へ置かず、Administrators / SYSTEM だけが書ける保護ストレージで作成し、登録後に削除する。
- 固定名の既存タスクは Principal の利用者を照合し、別利用者・所有者不明の定義を上書き・削除しない。照会失敗時の新規登録に `/f` を付けない。起動時の修復条件は [DESIGN.md の起動と終了](../design/workflow.md#起動と終了) を参照し、変更時は `ScheduledTempCleanupTests` の所有者・旧定義・照会失敗の検証を通す。

## 同時実行と終了処理

状態変更は [MaintenanceOperationCoordinator](../../src/Lumin4ti.UI/Services/MaintenanceOperationCoordinator.cs) と [MaintenanceOperationProcessLock](../../src/Lumin4ti.Core/Services/MaintenanceOperationProcessLock.cs) の lease を通し、マシン全体で同時に1件へ制限する。取得失敗時は実行せず、サインイン時クリーンアップは待たずにスキップする。終了処理では `RequestCancellation()` → `WaitForIdleAsync()` の順を維持し、補償・再検証を含む `finally` より先に閉じない。受付閉鎖と再読込の境界は [DESIGN.md の起動と終了](../design/workflow.md#起動と終了) に従い、lease を跨いで生き残る後始末を作らない。

## 昇格とデバッグ起動

通常起動とサインイン時クリーンアップの分岐順序は [DESIGN.md の起動と終了](../design/workflow.md#起動と終了) を維持する。旧版移行を自己昇格より後へ移さず、自己昇格前に `SingleInstanceGuard` を取得しない。**`Debugger.IsAttached` のときは昇格をスキップ**するため、F5 での HKLM 系操作・`Get-MMAgent` の権限エラーは正常である。管理者系までデバッグするなら IDE 自体を管理者起動する。
