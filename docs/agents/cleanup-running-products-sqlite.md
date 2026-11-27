# 実行中の製品・SQLite・INI の扱い

[cleanup-rules-and-deletion.md](cleanup-rules-and-deletion.md) から分割した詳細。

## 実行中の製品

実行中の製品は、規則の `processes` に一致し、同一セッション・同一利用者 SID のものだけを扱う。手順は次のとおり。

1. 確認（終了する・この項目を飛ばす・中止）。
2. WM_CLOSE（最大 15 秒）。
3. 残った場合だけ、未保存の入力が失われることを示して再確認。
4. 同意があれば、開いたハンドルで SID と開始時刻を再照合して `TerminateProcess`。

強制終了できるのは browserCache・browserTrace・cookies の規則だけで、Explorer と自プロセスは対象にしない。拒否・失敗はその規則だけを `Skipped` にする。

## SQLite

- `winsqlite3.dll`（System32）への P/Invoke だけを使い、NuGet を足さない。利用者が書ける非信頼入力として扱い、DEFENSIVE・`trusted_schema=OFF`・トリガー無効・拡張無効で開く。
- CREATE なしの READWRITE、`locking_mode=EXCLUSIVE`、`secure_delete=ON`、`BEGIN IMMEDIATE` を使い、走査は変更しない。
- `-journal`・`-wal` は `DatabaseSidecars` が、検査から操作の終了まで 1 つのハンドルで固定する。方式は DB ヘッダーで決め、どちらの方式でも両方を固定する（空の `-wal` は SQLite が無いものと扱う）。ロールバック方式は `journal_mode=TRUNCATE`、WAL 方式は EXCLUSIVE ロックで `-shm` を使わせず、`PERSIST_WAL` で閉じる時の削除の再試行を避ける。
- 再解析点・ディレクトリ・ハードリンク・中身のあるサイドカー（使用中または未回復）は無変更で拒否し、他者の削除・改名・置換を許さない。自分で作った空のサイドカーだけを、実体を照合してから削除する。
- 期待する表・列と合わない、仮想表、整合性エラー、ロック中は無変更で `Partial` にする。

## INI

`iniValues` は 1MB 以下で可逆に変換できるファイルだけを対象にし、プロセス停止を確認して共有なしの同一ハンドルへ書き戻す。書込み失敗時は原文を書き戻して再照合し、補償も失敗なら `Failed` とする。
