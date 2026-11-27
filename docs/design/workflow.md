# 処理の流れ

[DESIGN.md](../../DESIGN.md) から分割した詳細。

## 起動と終了

通常起動は次の順序で進む。

1. ログを初期化する。旧タスクが付けた引数`--scheduled-cleanup-temp`なら、掃除をせず旧タスクの削除だけを試みて、UI・自己昇格・多重起動ガード・Velopackを経由せず常に終了コード0で終了する（[legacy-scheduled-cleanup-removal.md](legacy-scheduled-cleanup-removal.md)）。
2. Velopackのinstall/update hookを処理し、旧ショートカットと旧PerUser版から署名済みPerMachine版への移行を試みる。
3. デバッガ未接続の非管理者プロセスを`runas`で再起動する。昇格後に単一起動mutexを取得する。管理者起動では、共有ロックを取って旧版のサインイン時クリーンアップタスクを自己移行で取り除く。ロックを取れなければ次回へ持ち越し、失敗はログへ残して画面起動を続ける。
4. Avaloniaを起動し、`App.ConfigureServices`が設定、コマンド実行、カタログ、操作コーディネーター、ViewModelをSingletonとして構成する。
5. `SettingsService`から表示言語と利用者設定を読み、`MainWindowViewModel`へカテゴリ別ViewModelを組み立てる。

終了要求時は`MaintenanceOperationCoordinator`が新規操作の受付を同期境界で閉じてから進行中操作へキャンセルを通知し、補償処理と状態再確認を含む`finally`の完了を待つ。操作がゼロでも先に受付を閉じ、キュー済みの状態再読込を開始させない。設定保存キューをflushし、DIコンテナとログを閉じてからプロセスを終了する。

## メンテナンス操作のモデル

`MaintenanceActionCatalog` の Action / BatchSetting / Toggle / Choice を、ViewModel が排他 lease 内で実行し、結果と実状態を画面へ返す。4 型の契約、既定値・カスタム状態の表示、データの流れは [maintenance-action-model.md](maintenance-action-model.md) にまとめる。

## クリーンアップ

単一項目のモデルではなく、走査と実行を分けた専用フローで扱う。`CleanupScanService`が規則データを走査して項目別の件数とサイズを返し、利用者の選択後に`CleanupRunService`が、走査結果が現在のもの（世代・設定が一致）であることを確認して選ばれた規則だけを実行する。実行は確認（実行中の製品の終了、受信ファイル類の再確認、推奨外の警告）を経て、サービスの停止と復帰を`finally`で補償し、成功・`Partial`・`Failed`・`Skipped`・キャンセルを規則ごとに集計して実績へ反映する。高度なクリーンとスリム化も同じ「走査→選択→確認→削除→実績」で、スリム化の前には復元ポイントを作る。レジストリタブは同じ流れに、バックアップの保存と読み戻し検証（失敗なら0件削除）を加え、バックアップから復元できる。詳細は[cleanup-design.md](cleanup-design.md)と[registry-cleaner.md](registry-cleaner.md)を参照する。

## 未接続デバイス管理

複数項目の選択・確認・逐次削除を伴うため、専用フローで扱う。`DeviceCleanupViewModel` が一覧、選択、二段階確認、進捗と結果集計を担当し、OS 操作はCoreの `IDisconnectedDeviceService` 境界へ委譲する。

`WindowsDisconnectedDeviceService` は SetupAPI から接続中 ID 集合とインストール済みデバイスを列挙し、その差をソフトウェア／仮想デバイスも含めてUIへ返す。削除時は対象が再接続されていないか再列挙し、未接続のままならNewDevの `DiUninstallDevice` を呼ぶ。結果は削除済み、既に不在、再接続、失敗と再起動要否に正規化される。

削除対象を開く処理やアンインストールAPIが失敗した場合は、新しいSetupAPI情報セットで同じInstance IDの存在を再照合する。不在を確認できた場合だけ、削除試行前なら「既に不在」、試行後なら「削除済み」へ補正する。まだ存在する場合や再照合できない場合は元の失敗を保持する。この再照合はキャンセル後も完了させる。「未接続デバイスなし」は列挙が成功して空だった場合だけ表示する。

## インストール済みアプリの更新

wingetの更新は、公式カタログとの完全一致を確認した候補だけを逐次更新し、成功・失敗・除外・自動更新の対象外を別に集計する。手順と判定は [winget-upgrade-flow.md](winget-upgrade-flow.md) にまとめる。

## 使用中キャッシュの起動時再試行

固定 5 対象の削除予約を利用者別の保護キューへ保存し、専用コンソールが現在利用者の権限で次回起動時に再試行する（最大 3 実行・7 日）。対象の再構成、タスクの検証、期限と権限の境界は [reboot-cache-retry.md](reboot-cache-retry.md) にまとめる。
