# 非昇格wingetの再試行

[security.md](security.md) から分割した詳細。

## 起動の条件

wingetの更新でユーザースコープのportableを昇格側から操作できない場合（終了コード`0x8A15007D`）に限り、厳格設定の`UnelevatedCommandExecutor`（`CreateForCurrentUserTree`）で非昇格のwingetを再試行する。昇格プロセスとExplorerの利用者SIDが一致することを必須とし、取得できない場合や不一致の場合は起動しない。Explorer brokerへは進まず、wingetは`SystemProcessResolver`の絶対パスで起動する。複製tokenの追加権限と再開前検証は厳格設定だけに適用し、Quick AccessとExplorer broker経由の既存経路の権限と手順は変えない。

## 起動方式と再開前の検証

Explorerのtokenを`TOKEN_ADJUST_DEFAULT`(0x80)と`TOKEN_ADJUST_SESSIONID`(0x100)を加えた権限で複製したprimary tokenを使い、`CreateProcessWithTokenW`（失敗時`CreateProcessAsUserW`）で起動する。昇格実機では、この2権限が無いとそれぞれWin32 5・1314で失敗し、加えると起動できた。MAXIMUM_ALLOWEDとExplorerを親プロセスにする方式でも起動できたが、権限が最小で済む前者を採用した。

停止状態で生成した子は、次をすべて満たす場合だけJobへ登録して再開し、1つでも満たさなければ終了して失敗とする。

- 実行パスが想定の`System32`の`powershell.exe`である。
- セッションがExplorerと同じである。
- 整合性レベルがmedium以下で、非AppContainerである。
- `TokenElevation`が0（非昇格）である。
- 利用者SIDが現プロセスと一致する。

起動失敗の文には、起動方式・Win32番号・呼出し元プロセスのJob所属を含める。

## 停止と残余リスク

起動した非昇格PowerShellは子ごとに独立したJob Object（`KILL_ON_JOB_CLOSE`）へ停止状態で登録する。厳格設定だけ、取消・タイムアウト・ルート（PowerShell）終了のいずれの後も、そのJobを`TerminateJobObject`で終了する（失敗はログへ記録する）。アプリが強制終了された場合はJobハンドルが閉じ、OSがJob内のプロセスを終了する。

実機ではWindowsApps配下のwingetがこのJobに所属せず、Jobで確実に終了できるのはPowerShellが起動した通常の子孫（pingで確認）までである。そのため取消・タイムアウト時は、ルートの生存中に親PIDからのプロセスツリー終了でwingetを止める（実機で確認済み）。スクリプトは、winget起動後の例外で抜ける場合に`finally`でSystem32の`taskkill.exe /PID <pid> /T /F`を実行して未終了のwingetをツリーごと止める（失敗しても元の結果は変えない）。ただし、PowerShellだけが外部から強制終了された場合と、Lumin4ti自体が強制終了された場合は、wingetが残り得る。

## 結果ファイルの扱い

再試行側が書く結果ファイルは非信頼入力として扱い、`SchemaVersion`・型・欠落・重複を検証できたものだけを結果の分類と表示に使う。パスやコマンドには使わない。
