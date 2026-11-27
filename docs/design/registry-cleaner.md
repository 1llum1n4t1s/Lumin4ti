# レジストリクリーナー

クリーンアップの「レジストリ」タブ。実体が消えたファイルを指す登録だけを、走査・選択・バックアップのうえで削除し、バックアップから戻せるようにする。動作を速くする機能ではなく、Microsoft もレジストリクリーナーをサポートしないため、バックアップ・復元を必須とし、自動・予約実行はしない。

## 範囲

| カテゴリー | 対象 | 削除単位 | 既定選択 |
|---|---|---|---|
| App Paths | HKU・HKLM の `App Paths\*`（32 ビット表示は 64 ビット側と共有のため走査しない） | サブキー | 選ぶ |
| スタートアップ | HKCU・HKLM（WOW6432Node 含む）の `Run` | 値と `StartupApproved\Run`（`Run32`）の値 | 選ぶ |
| フォント | HKLM（64 ビット側）・HKU の `Fonts` | 値 | 選ぶ |
| 関連付け | `OpenWithList`、`Classes\Applications\*`、`OpenWithProgids` の値（利用者分はリンクを通らず `HKEY_USERS\<SID>_Classes` を開く） | ProgID と `UserChoice` は触れない | 選ぶ |
| アンインストール | HKLM（同）・HKU の `Uninstall\*` | サブキー | 選ばない（個別承認） |
| 共有 DLL | `SharedDLLs`（同） | 値 | 選ばない。「すべて選択」にも含めない |

ActiveX/COM・ヘルプ・サウンド・ファイアウォール・タスクなど、無効と断定できないか推測に頼るものは収録しない。履歴・MRU は「痕跡」規則が担う。旧項目 `remove-broken-startup` と `remove-dead-associations` はバックアップなしで消していたため、判定をこのタブへ移し、カタログと実装から撤去した（`StartupCommandParser` は他でも使うため残す）。

## 根拠

カテゴリーごとに tier A（Microsoft 公式文書）か tier B（独立した二次資料 2 件）を C# の固定表に持ち、テストで固定する。規則データ JSON の検証器は Run・Classes を禁止するため使わない。共有 DLL のパスは tier B で、本文を確認した資料だけ採用する。

## 判定と削除

欠損を確定できた値だけを候補にし、不明・OS 所有の場所は保持して件数と理由を表示する。削除は世代照合・復元ポイント・再照合・型付き JSON バックアップの検証を経て同じハンドルで行い、バックアップを書けなければ 1 件も消さない。バックアップは `%ProgramData%\Lumin4ti\backups\registry\` に型付き JSON（`<UTC時刻>-<GUID>.json`）と結果ファイル（`<id>.result.json`）で置き、自動では消さない。復元は上書きせず、`Restored`・`AlreadyPresent`・`Conflict`（全体 `Partial`）で返し、SID 不一致と検証違反は拒否する。復元後は走査結果を失効させる。詳細は [registry-cleaner-guards.md](../agents/registry-cleaner-guards.md)。

## 画面

「スリム化」と「設定」の間のタブとし、4 型の項目にはしない。`SlimViewModel` を雛形に、走査 → カテゴリー別選択 → 確認 → 実行と、バックアップ一覧・復元・削除を置く。「動作が速くなるものではなく、バックアップから戻せる」旨を 17 言語で表示する。
