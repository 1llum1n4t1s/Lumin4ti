# レビュー修正の安全な操作フロー検証

これは実 GUI E2E ではなく、実 ViewModel / Core 操作と OS 境界 fake を組み合わせた操作フロー検証です。実レジストリ、SCM、外部プロセス、通信、インストール、利用者データの削除は行いません。診断はメモリ内 logger で検査します。新しい package や temporary directory は追加しません。

## 現在の契約と確認する失敗条件

ON/OFF 型の Registry / VBS と UWP の一括設定は変更前の値を永続保存しません。Registry / VBS の OFF は明示設定の解除または解除値への変更、UWP の一括 OFF は両 DWORD を1、一括 ON は両 DWORD を0にする明示的な禁止・許可です。失敗・取消時の補償には操作直前の実値をメモリ内で保持します。PATH 整理と QuickAccess のデータバックアップは維持します。

| フロー | 起こり得る失敗 | 確認する結果 |
| --- | --- | --- |
| 起動 | 古い assembly、異なる runtime、fake 境界の漏れ | 読み込んだ assembly の絶対パスと SHA256、runtime、OS を成果物へ記録。fake のみを明示的に構築 |
| 共有レジストリ | 旧所有者管理によって OFF が共有 HKLM の解除を拒む、別ユーザー HKCU まで変更する | 二ユーザー ON/ON/A-OFF で HKLM と A-HKCU を解除、B-HKCU は維持。B の状態読込は OFF、B-OFF で自身の HKCU を解除。繰返し OFF は冪等 |
| レジストリ再適用 | 外部変更後の失敗を歴史的な原値へ戻す | ON→外部値5→再 ON 失敗で操作直前の5へ補償、OFF は保存値9へ戻さず値を削除 |
| レジストリ失敗・取消 | 部分適用やキャンセル後に変更を残す、補償失敗を隠す | 混合 HKLM/HKCU の途中失敗と取消をメモリ補償。補償不能は Failed。OFF の値削除と明示既定0の両方を確認 |
| UWP 一括設定の再実行 | 以前の適用状態や対象一覧に依存し、追加アプリを取りこぼす | 同じインスタンスで一括OFF(両値1)と一括ON(両値0)を各々再実行し、途中で追加されたfamilyも反対方向の操作なしで含める |
| UWP 部分失敗 | ペア途中書込失敗で不整合、補償不能が成功扱い、外部変更を上書き | 両方向の途中失敗は操作直前へ補償。補償失敗はFailed、再実行は現在の対象を更新し、第三者変更は保持 |
| VBS | OFF で過去の独自構成を復元、途中の失敗で BCD/レジストリが混在 | fake platform で ON→別インスタンス OFF を通し BCD と3値の明示設定を解除。途中失敗は操作直前の独自構成へメモリ補償 |
| 旧バックアップの境界 | 撤去した toggle が古い保存ファイルを読む・移行・削除する | Registry/UWP/VBS 実装の永続状態型・保護ストレージ非参照を静的補助検査。UWPは対象判別の固定manifest読み取りだけを許容。旧データの実ファイルは作成・操作しない |
| 終了 | キャンセル後に新操作を受付、finally より先に idle | 終了 gate を保持し、idle は finally/lease 解放まで未完了 |
| NTP | 停止失敗後の再開漏れ、未停止で設定変更 | mock stop 失敗時に設定を書かず、補償 start を呼ぶ |
| サービス・cleanup | 依存サービスの確認失敗を無視、取消が再開失敗を隠す | SCM の状態遷移と拒否を確認し、非作動の相対 canary で engine 到達前取消、再開失敗のサービス名を結果へ残す |
| MMAgent | 外部変化を再読込しない、照会重複、不正出力の診断消失 | キャッシュ reset と進行中照会の集約、失敗/空/不正 JSON/数値、秘密値の伏字と retry を確認 |
| 状態と結果 | 不明診断が操作結果を上書き、回復後も error 残留 | 親子 VM の操作可否、StateErrorText と ResultText の分離、既知状態で診断だけを消す |
| デバイス列挙 | 例外を空一覧成功と表示、retry 不可 | 失敗時 IsEmpty=false、retry 空成功時 true。fake の削除呼出しは禁止 |
| パーサ | canary を shell へ渡す、解決不能コマンドを欠損と断定 | 引用符なし空白パス・`.exe` ディレクトリ・環境変数を純粋 parser で検査し、曖昧な入力を欠損確定へ渡さない |
| コマンド診断 | 失敗 tool の stdout を捨てる | mock 終了コード/出力を保持し秘密値を伏せる。OEM 復号は ProcessCommandExecutorDecodeTests の責務 |
| 項目の状態読込ログ | null・例外・timeout が項目名や理由なし | 実 VM の状態照会へ fake toggle/choice を渡し診断付き/理由なし null、IOException、30ms timeout を確認。名前/ID/原因、token 伏字、既知 OFF の失敗ログなし |
| 項目の操作ログ | Failed/Partial の Detail 消失、Busy 理由欠落、自動実行の失敗消失 | 実 category/card VM の toggle/choice/action で Failed/Partial/Busy を確認。名前/ID/原因と伏字、拒否時未実行、自動実行の後続続行 |
| MSI 取得 | headers 後 body が無期限に待つ、cancel と timeout を混同 | mock HTTP body の timeout / OCE / 正常 copy を区別。外部通信なし |
| 自動実行の選択解除 | 大文字小文字違いの重複 ID が OFF 後も残る | 実 checklist 操作→メモリ保存→選択で全重複解除。無関係 ID/順序/冪等性を保持し、task 実行と削除対象解決はしない |
| 更新スキップ解除 | 保存例外が生成 command へ伝播 | 正常と IOException の両方で実 command を完了、画面と tag を解除。空 tag の再実行は保存不要 |
| 移行保留 JSON | 旧原本を読めない、null・日本語・順序・UTF-8 変化 | 移行用 JSON の旧 serializer と生成 metadata の等価性を確認。製品移行・ストレージ書込みは呼ばない |
| ローカライズ | 状態診断や既定値、更新 getter、操作名の言語が古い | 実 App.SetLocale と VersionVM を17言語で切替。既定ON/OFF/環境による/選択型の表示と通知、Recall の fake 診断、generic/explicit error、同じ singleton getter と設定保存数を確認 |
| 静的補助 | locale key/Automation名欠落 | 17 locale XML と親子 Run/Cancel/一括OFF/一括ON の名前を補助検査。実 GUI accessibility 検証とは区別 |
| runner | 一件失敗や取消回帰で成果物を失う | case ごとに15秒上限、全件を JSON へ出力し失敗時 exit1。上限は元 task を停止しないので実 OS 操作へ転用しない |

旧契約専用だった共有原本の所有者追跡、journal の保存・削除・SID 隔離、再起動後の原値復元、保存原本 JSON の互換性ケースは廃止しました。保存機構を撤去したため、それらの成功を新しい完了条件にせず、共有解除・既定化・操作中のメモリ補償へ統合しています。ケース数を維持するための追加は行いません。

## 再現

言語切替フローでは、開発版・バージョン取得失敗・設定保存失敗・読込失敗による保存抑止の表示を17言語で再評価します。更新マネージャーの生成失敗は注入した factory で確認し、未インストールとの区別を保ちます。設定はメモリ fake のみで、読込失敗時に保存境界を呼ばず、保存再試行成功時に警告が消えることを確認します。

実画面プレビューの「実行中のチェック操作（メモリ内）」では、対象一覧を開いて実行し、15秒間の模擬処理中にチェックできないこと、中断後に再び操作できることを確認します。親子の診断・結果は共通テンプレートを使用します。中断時に対象一覧を再走査せず表示済み選択を保持する条件は `CommandCategoryStateQueryTests` でも確認します。

「空の一覧の再確認（メモリ内）」は最初の展開で0件を表示し、閉じて開き直すと模擬候補を表示します。「選択保存失敗（メモリ内）」はチェックを変更すると保存失敗を返します。警告が実行・中断後の操作結果と共存し、日本語/English切替にも追従することを確認します。

上部の「設定読込失敗（模擬）」「設定保存失敗（模擬）」はメモリ設定を渡した実 `MainWindow` を開きます。全タブ共通の保存警告とバージョン表示の言語追従を確認できます。このウィンドウのクリーンアップタブにある「削除一覧の案内（メモリ内）」は対象が空配列の実 `FileCleanupAction` で、展開すると動的一覧専用の再取得案内を表示します。実際の設定保存・ファイル削除・OS変更は行いません。

`pwsh -NoProfile -File scripts/verify-review-fixes.ps1`

成果物は `local-release/review-fixes-verification.json`。これは保持する指定成果物です。製品 version と配信設定は変更しません。logger はメモリ内 provider のみで、ファイル出力を停止します。

既定値と状態診断の実画面確認は既存の --preview-state-diagnostics モードで行います。製品テンプレートで既定ON/OFF/環境依存・選択型・親子カードを表示し、親OFF時の子の操作禁止、日本語/English切替と再読込、現在値と既定値の違いを確認します。カスタム値は同じ位置のON/OFF選択欄からどちらにも直接変更できることを確認します。UWPは一括OFF/一括ONを同じ方向に続けて実行でき、実行中は両ボタンとも操作不可になることを確認します。状態変更はメモリ内のfakeのみで、製品の初期化・設定保存・OS操作は行いません。

同じプレビューの設定画面では、メモリ圧縮の切替（6秒）・チェック選択保存（3秒）・模擬実行（15秒）を使い、Shisui型オーバーレイ、背面操作禁止、ライブ進捗、中断後の後処理待ちと再操作を確認します。Debugビルドでは実際のCRDebuggerが自動表示され、Releaseには含まれません。LUMIN4TI_PREVIEW_LOGへ今回の作業領域内の絶対パスを渡すと、同じ模擬ログのファイル出力とデバッグ画面への転送も確認できます。既存利用者のログ・設定・OSは変更しません。

watchdog の異常系は `dotnet run --project scripts/verification/Lumin4ti.Verification.csproj --no-build -- D:\Dev\Lumin4ti D:\Dev\Lumin4ti\local-release\verification-watchdog.json --simulate-missing-download-cancellation` で再現します。mock body だけが cancellation を無視し、15秒後に取得ケースの失敗、後続ケース継続、JSON 出力、exit1 を確認します。製品 timeout は変更しません。
