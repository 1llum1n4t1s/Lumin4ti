# 起動・終了・タスク実行の規約

[security.md](security.md) から分割した詳細。

## 起動時再試行と旧サインイン時タスクの移行 (回帰厳禁)

- 使用中キャッシュの起動時再試行を変更したら、`FileCleanupTests`、`RebootCacheCleanupTests`、`RebootCleanupTaskTests` と操作フロー検証を通す。[DESIGN.md の使用中キャッシュの起動時再試行](../design/workflow.md#使用中キャッシュの起動時再試行) の対象・利用者・信頼確認・期限の境界を維持する。任意パスや SYSTEM 実行へ広げず、旧 MoveFileEx 予約を読み取り・変更・移行しない。Explorer を kill しない。
- サインイン時クリーンアップ（予約実行）は廃止した。タスクの登録・トグル・旧実行のコードを復活させず、`schtasks /create` などの予約登録を src に置かない。`ScheduledTaskExecutableTrust` は起動時再試行が使うので残す。
- 旧版が登録した `\Lumin4ti\ScheduledTempCleanup` は [LegacyScheduledCleanupMigration](../../src/Lumin4ti.Core/Services/Windows/LegacyScheduledCleanupMigration.cs) だけが取り除く。固定パスのタスク 1 本を対象にし、Principal が現在の利用者、Action が単一の Exec、Arguments が `--scheduled-cleanup-temp` と完全一致、Command のファイル名が `Lumin4ti.UI.exe` の全てを満たすときだけ、削除直前に同じ COM オブジェクトで再照合して削除する。不一致・照会不能は保持してログへ残し、別利用者のタスクは消さない（その利用者の次回サインインで自己削除される）。`\Lumin4tiRebootCleanup` には触れない。
- 契機は 2 つ。昇格済み・単一起動確保後の通常起動（`MaintenanceOperationProcessLock` を取れなければ次回）と、旧タスクから引数付きで起動されたとき。後者は Velopack より前で掃除を一切せず移行だけ試み、失敗しても常に終了コード 0 で終わる。どちらの契機も、COM が応答しなくても起動を止めないよう、移行を別スレッドで実行して全体の期限を 5 秒にする。期限切れでは共有ロックを手放し、取消を要求して作業スレッドを次の手順へ進ませず、次回へ持ち越す（実行中の COM 呼び出し自体は中断できない）。GUI・自己昇格・移行 MSI・Velopack へ流さない。Velopack フックとアンインストールには入れない。COM 移行は 2027-11-30 まで残して以後は本人の指示で撤去し、引数の無害終了は期限なしで残す（消すと残存タスクが昇格 GUI を開くため）。変更時は `LegacyScheduledCleanupMigrationTests` と `ScheduledTaskExecutableTrustTests` の所有者・定義・照会失敗の検証を通す。

## 同時実行と終了処理

状態変更は [MaintenanceOperationCoordinator](../../src/Lumin4ti.UI/Services/MaintenanceOperationCoordinator.cs) と [MaintenanceOperationProcessLock](../../src/Lumin4ti.Core/Services/MaintenanceOperationProcessLock.cs) の lease を通し、マシン全体で同時に1件へ制限する。取得失敗時は実行せず、旧タスクの移行は待たずに次回へ持ち越す。終了処理では `RequestCancellation()` → `WaitForIdleAsync()` の順を維持し、補償・再検証を含む `finally` より先に閉じない。受付閉鎖と再読込の境界は [DESIGN.md の起動と終了](../design/workflow.md#起動と終了) に従い、lease を跨いで生き残る後始末を作らない。

## 昇格とデバッグ起動

通常起動と旧タスクからの引数付き起動の分岐順序は [DESIGN.md の起動と終了](../design/workflow.md#起動と終了) を維持する。旧版移行を自己昇格より後へ移さず、自己昇格前に `SingleInstanceGuard` を取得しない。**`Debugger.IsAttached` のときは昇格をスキップ**するため、F5 での HKLM 系操作・`Get-MMAgent` の権限エラーは正常である。管理者系までデバッグするなら IDE 自体を管理者起動する。
