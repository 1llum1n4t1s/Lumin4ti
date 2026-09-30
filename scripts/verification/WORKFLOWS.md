# レビュー修正の安全な操作フロー検証

これは実 GUI E2E ではなく、実 ViewModel / Core 操作と OS 境界 mock を組み合わせた操作フロー検証です。実レジストリ、SCM、プロセス実行、通信、インストール、利用者データの削除は行いません。診断はメモリ内loggerだけで検査します。新しい unit test や package は追加しません。

## 実装前に列挙した失敗ケースと期待結果

| フロー | 起こり得る失敗 | 確認する結果 |
| --- | --- | --- |
| 起動 | 古い assembly、異なる runtime、mock 境界の漏れ | 読み込んだ assembly の絶対パスと SHA256、runtime、OS を成果物へ記録。mock のみを明示的に構築 |
| 共有レジストリ | 二人目が適用値を原本として保存、一人目の OFF が他者の ON を解除 | ON/ON/OFF/OFF で HKLM 原本を共有し最後の利用者で復元、HKCU は SID 別に復元 |
| 復元失敗 | 一部復元失敗で原本消失、retry が誤った値へ戻す | 注入した書込み失敗を結果へ出し、retry で元値へ復元 |
| 共有原本置換後失敗 | owners 更新後の例外でレジストリだけ ON へ戻り、別利用者 OFF がまだ適用中の共有値を復元 | 全値の復元後は ON へ補償しない。更新済み定義を照合できれば完了、未確定なら原本を保持して Failed、OFF retry で所有解除を完了。単独／二所有者で注入 |
| 共有復元済みの回復 | user 原本削除失敗や初回準備失敗で ON が永久に拒否 | 共有値が原値と一致なら別 SID ON 可・既存 user 原本保持、準備時保存失敗の ON retry 可 |
| 外部変更後の新準備失敗 | 前 cycle owner0 原本7、外部値9、新user原本保存成功後 shared 置換前に失敗し、残user原本が retry を永久拒否 | shared 不変を確認できた場合だけ今回新規 user 原本を取消。値9/UI OFFを保持し、retry ON/OFFで最新元値9を復元 |
| user 原本保存後検証失敗 | 新user原本の rename は成功し validation が throw、共有更新へ到達せず retry が永久拒否 | shared 不変を確認できた場合は保存済み新user原本も取消し、retry ON/OFFで最新元値9を復元 |
| UWP | 別 SID の原本を使用、旧無スコープ原本を誤復元 | SID の journal 分離と不一致拒否 |
| 終了 | キャンセル後に旧 lease が終わると新操作を受付、finally を待たず終了 | 終了 gate を保持し、idle は finally/lease 解放まで未完了 |
| cleanup | キャンセルがサービス復旧失敗を隠す、実ファイルに触れる | 非作動の相対 canary 対象を使い、engine 到達前のキャンセルと復旧失敗したサービス名を失敗結果へ残す |
| NTP | 停止失敗後の再開漏れ、未停止で設定変更 | mock start を必ず補償し設定を書かない |
| サービス | net /y が依存サービスを連鎖停止、SCM 状態不明で削除続行 | plain stop（連鎖承認なし）と要求後 SCM 照合、初期不明/遷移中/依存あり/依存照会不能は停止要求なし |
| MMAgent | snapshot が外部変更を隠す | mock 外部状態変更後 ResetSnapshot と reload で VM 状態更新 |
| MMAgent 並行照会 | 未完了照会中の reset 連打で OS query が多重起動 | 未完了の三 caller は一 query に集約し、完了後 reset は二回目の fresh query |
| 親子 UI | 親不明でも子操作可、親回復後子が無効のまま | 親 null → 子 disabled → 親 true → enabled |
| 状態と結果 | 状態不明が実行結果を上書き、回復しても error 残留 | StateErrorText と ResultText が共存し既知状態で error のみ消える |
| デバイス列挙 | 例外を空一覧成功と表示、retry できない | 失敗時 IsEmpty=false、retry 空成功時 true、削除呼出し禁止 |
| パーサ | canary 入力を shell に渡す、解決不能コマンドを欠損と断定 | RERE_CANARY を純粋パーサにだけ渡し安全に拒否 |
| 診断 | 非英語出力、失敗 tool の stdout を捨てる | mock 失敗の終了コード/出力が診断に残る |
| MSI 取得 | headers 到着で timeout が解除され body が無期限に待つ、利用者 cancel を timeout と誤判定 | mock HTTP body で timeout / OCE / 正常 copy を区別、外部通信なし |
| 自動実行の選択解除 | 設定に大文字小文字違いの重複 ID があり、OFF 後もサインイン時に実行 | 実 checklist 操作→保存→選択を通し、全重複を解除。無関係な ID と順序、繰返し OFF/ON の冪等性を保持。削除対象の解決や task 実行は禁止 |
| 更新スキップ解除 | 保存先の障害が生成 command へ未処理例外として伝播 | 正常保存と IOException 注入の両方で実 command を完了、画面・設定の tag を解除、空 tag の再実行は保存不要 |
| 移行保留 JSON | 型情報の統一で旧原本を読めない、null・日本語パス・プロパティ順・UTF-8 書式が変化 | 旧 serializer と生成 metadata のテキスト／UTF-8 同一性と旧 JSON の読込を確認。保護ストレージ・レジストリ・移行は起動しない |
| 静的補助 | locale キー欠落、更新 getter の通知漏れ、AutomationProperties.Name 欠落 | 17 locale XML と XAML/実装の対応を補助検査として記録 |
| runner | case の一件失敗や cancellation 伝播の回帰で成果物を失う | 各 case に独立した15秒の待機上限を設定し、全件を JSON へ出力、失敗時 exit 1。上限は元 task を停止しないため、実 OS 操作を含む runner へ転用しない |

## gogo 追加指摘の実装前の失敗条件

- 再 ON: 初回原値9→ON→外部値5→再 ON が途中失敗すると、9へ巻き戻して原本も失う。操作直前の5へ補償し、OFF用の9を保持する。初回失敗、補償失敗、混合HKLM/HKCUの新所有者取消、準備後キャンセルも確認する。
- UWP: 2値の片方だけの適用・復元・補償失敗、再起動後の混合状態、明確な外部変更、未設定原値。判断不能な原本を消さず、即時例外は確認可能な操作直前状態へ補償する。
- 起動コマンド: 引用符なしの空白入りパス、`.exe`で終わるディレクトリ、環境変数からの空白展開、正常な引用パス/空白なしパスと引数。曖昧な入力を欠損確定へ渡さない。
- MMAgent: 失敗終了、空出力、不正JSON、非object JSON、不正数値、同時照会。原因を共有照会境界で一度記録し、秘密値を診断へ出さず、状態不明と再試行を保つ。
- UI: toggle/choiceの汎用不明診断が言語変更で古いまま、具体的例外の誤置換、既知状態回復で結果まで消す、親子の実行/取消の同名、言語変更通知漏れ。

## 再現

`pwsh -NoProfile -File scripts/verify-review-fixes.ps1`

成果物は `local-release/review-fixes-verification.json`。これは保持する指定成果物です。新しい package、製品 version、配信設定は変更しません。temporary directory は作りません。logger はメモリ内providerだけを登録し、ファイル出力を停止します。

watchdog の異常系は `dotnet run --project scripts/verification/Lumin4ti.Verification.csproj --no-build -- C:\Users\IMT\dev\Lumin4ti C:\Users\IMT\dev\Lumin4ti\local-release\verification-watchdog.json --simulate-missing-download-cancellation` で再現します。mock body だけが cancellation を無視し、15秒後に取得ケースの失敗、残りケースの継続、JSON 出力、exit 1 を確認します。製品 timeout は変更しません。
