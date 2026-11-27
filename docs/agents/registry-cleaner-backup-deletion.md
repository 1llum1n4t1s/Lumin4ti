# レジストリクリーナーのバックアップと削除

[registry-cleaner-guards.md](registry-cleaner-guards.md) の詳細。バックアップ形式・保存先・削除の実行順を変える前に読む。

- 正本は型付き JSON（reg.exe export は値単位で戻せず不採用）。形式の版、UTC 時刻、SID、項目ごとのハイブ・表示（64/32）・キーパス・値の名前・型・元バイト列（base64）・カテゴリーを記録する。キー単位はサブツリー全体。ACL は保存しない。
- 保存先 `%ProgramData%\Lumin4ti\backups\registry\<UTC時刻>-<GUID>.json`（`ProtectedBackupStorage.WriteNewAtomically`、Administrators と SYSTEM のみ）、結果は `<id>.result.json`。自動削除しない。列挙上限 1000 件超は fail closed。
- 実行順（lease、`showProcessing:true`）: 1. 走査世代を CAS で照合 2. 復元ポイント（既定オン、失敗時は続行確認） 3. ハンドルを開いたまま再照合し、ハッシュ（キーはサブツリーの正規化ハッシュと LastWriteTime）が違うか欠損が確定しなければ `Retained` 4. 通った項目だけをバックアップ（WriteThrough、読み戻し検証）。書けなければ 1 件も削除せず `Failed` 5. 同じハンドルで直前に再照合して削除（子から再帰、リンク拒否） 6. 結果ファイルを書く。
- 同じ物理キーは `NtQueryKey(KeyNameInformation)` で判定して二重処理しない。選択済みキーの配下は飛ばす。
- 結果: 削除・`Retained` は成功。アクセス拒否・リンク・TrustedInstaller 所有は項目 `Skipped`・全体 `Partial`。理由は `CleanupMessage` の符号、代表例は最大 8 件。
