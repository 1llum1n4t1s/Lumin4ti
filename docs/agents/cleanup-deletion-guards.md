# クリーンアップの走査と削除の安全ガード (回帰厳禁)

[cleanup-rules-and-deletion.md](cleanup-rules-and-deletion.md) から分割した詳細。実運用では他人の PC の実データを消すため、次を外さない。

- 走査は何も変更しない。走査していない対象は削除しない。走査結果は世代番号を持ち、設定変更・削除開始・再走査で失効し、失効した結果は実行しない。
- 削除は、同じ規則・除外で再列挙し、走査完了時刻より後に作られたファイルを保持する。高度なクリーンと孤児 Installer・無効ショートカットは、FileId・サイズ・更新時刻が走査時と一致した同一ハンドルだけを削除し、不一致は保持する。
- 列挙・属性変更・削除は、再解析を拒否して開いた同じハンドルで行う ([FileCleanupEngine](../../src/Lumin4ti.Core/Services/Windows/Actions/FileCleanupEngine.cs)、[NativeCleanupEntry](../../src/Lumin4ti.Core/Services/Windows/Actions/NativeCleanupEntry.cs))。ジャンクション・シンボリックリンクはリンク自体を保持し、リンク先へ作用しない。パスを再解決して削除する経路 (Shell の削除、`MoveFileEx`) を使わない。
- 削除はごみ箱へ移さず完全削除とし、確認画面に「元に戻せない」と表示する。除外などで意図して残したものは `Retained` として数え、親フォルダーの削除失敗を Blocked にしない。
- 所有権の奪取・ACL の変更 (`takeown`・`icacls`・`SetNamedSecurityInfo`) はしない。権限不足は件数と代表例を表示して `Partial` にする。
- `TryResolve` は、未解決の環境変数、相対パス、ドライブ直下、基点フォルダー自体を拒否する。基点フォルダーは `Files` 指定のときだけ許可する。
- 部分失敗・サービス停止失敗・プロセス終了拒否・DB ロック・復元ポイント失敗・DISM 非 0・キャンセルのどの経路も成功と表示しない。削除不能の記録は `RecordBlocked` を通し、代表例を件数・長さの上限内で残す。
- 安全な削除（既定オフ）は同一ハンドルで 1 パス上書きしてから削除する。リンク数が 2 以上、圧縮・スパース・EFS・読取専用のファイルは上書きせず削除だけ行う。SSD では保証しないと表示する。
- 準備済みの固定ドライブだけを対象にする。UNC・SUBST などの未対応マッピングは拒否する。
- 規則どうしで対象フォルダーが重なる場合（例: `%TEMP%` 全体と `%TEMP%\NeroSupport`）、同じファイルを二重に数えず、二重に失敗にしない。走査は `ClaimSet.ScanResolver` が先の規則に属するものを `Overlaps` へ分け、`CleanupScanSnapshot.Measure` が先の規則も選ばれているときだけ後の規則の分から引く（`TotalFiles`・`TotalBytes` も同じ）。実行は `RunResolver` が、先に処理した対象が扱ったものを飛ばす。同一性は同じ絶対パスで決め、古さの基準時刻は走査・実行で揃える。
- 固定ハンドラーの `Failed` は、他の対象で消した件数にかかわらず規則全体を `Failed` にし、`Partial` は規則を `Partial` にする（サービス復帰の失敗は取消より優先して `Failed`）。ハンドラーが取消を返したら取消として扱い、名前から解決できないハンドラーは失敗にする。
- 画面へ返す理由・結果の文言は `CleanupMessage` の符号（`Cleanup.Msg.*` のキーと引数）で運び、UI が辞書から解決する。ログ・テキスト書き出しでは `CleanupMessage.ToJapanese` で日本語へ戻す。引数は翻訳しない技術値で、符号は入れ子にできる。新しい理由は日本語のマスター（`CleanupMessages`）と 17 言語の辞書へ足す。
- 状態変更は `MaintenanceOperationCoordinator` の lease を通す。走査は `showProcessing:false`、削除は `showProcessing:true` で取り、サービスの停止と復帰は [WindowsServiceControl](../../src/Lumin4ti.Core/Services/Windows/WindowsServiceControl.cs) の補償を `finally` で必ず行う。
