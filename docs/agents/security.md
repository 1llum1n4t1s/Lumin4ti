# 安全に関わる規約

[AGENTS.md](../../AGENTS.md) から分割した詳細。

## 外部プロセス実行

`ICommandExecutor` → `ProcessCommandExecutor` が唯一の実装 (DI で単一登録)。DISM/regsvr32/powercfg/winget/bcdedit/MMAgent cmdlet など「OS 提供ツールが唯一の手段」のものだけ外部プロセスで実行し、レジストリ・COM・WinRT で代替可能なものは C# ネイティブで書く。winget を非昇格で起動する唯一の経路は、ユーザースコープ portable 更新（終了コード `0x8A15007D`）だけを `UnelevatedCommandExecutor` の厳格設定（`CreateForCurrentUserTree`）で再試行する経路とする。厳格設定は複製tokenに `TOKEN_ADJUST_DEFAULT|TOKEN_ADJUST_SESSIONID` を加えて起動し（無いと昇格側で Win32 5/1314 になる）、停止状態の子の実行パス・セッション・IL・非昇格・SID を再開前に検証する。QuickAccess と ExplorerLauncher の経路・権限は変えない。

- **セキュリティ (回帰厳禁)**: bare exe 名を渡すと `CreateProcess` の検索順序でインストールディレクトリが `System32` より先に照合され、昇格プロセスがバイナリプランティング LPE を踏む。`ProcessCommandExecutor` は [SystemProcessResolver](../../src/Lumin4ti.Core/Services/SystemProcessResolver.cs) でフルパス解決 + `WorkingDirectory=System32` 固定してこれを封じている。呼び出し側は論理名でよいが、この解決を外さないこと。
- 子プロセスは [ProcessJobTracker](../../src/Lumin4ti.Core/Services/ProcessJobTracker.cs) で子ごとに独立した Job Object (KILL_ON_JOB_CLOSE) へ登録する。生成を制御できる経路では停止状態で登録してから再開し、登録失敗時は実行させない。`ct` キャンセル時はプロセスツリーごと kill。Job の共有を再導入しないこと。理由と Explorer broker 経路の制約は [DESIGN.md の OS操作の境界](../design/security.md#os操作の境界) を参照する。
- 出力の復号とライブ進捗通知は `ProcessCommandExecutor` に集約し、呼び出し側で固定コードページを仮定しない。復号の順序は [DESIGN.md](../design/security.md#os操作の境界) を参照する。
- サービスの停止・再開が要る操作は [WindowsServiceControl](../../src/Lumin4ti.Core/Services/Windows/WindowsServiceControl.cs) を通す。停止を確認できなければ削除・設定変更を進めず、返された `ServiceSuspension` の `ResumeAsync()` を失敗・キャンセル時も `finally` で必ず実行する。依存サービス確認、標準入力 EOF、停止・開始の期限、復帰の再照合は [DESIGN.md](../design/security.md#os操作の境界) の共通補償フローを維持し、`net stop /y` を再導入しない。復帰失敗をキャンセルや成功で隠さず `Failed` として表示する。

## 破壊的操作の復元性

ON/OFF設定切り替えとDefenderの既定設定へのリセットは永続バックアップを使わない。既存の旧バックアップは参照・移行・削除しない。型付きの操作直前値はメモリにだけ保持し、失敗・キャンセル時の補償と再検証は [DESIGN.md の状態と永続化](../design/data-model.md#状態と永続化) に従う。HKLMの変更は他利用者にも影響し、利用者別の共有所有者管理は行わない。Path・クイックアクセスの並べ替えはデータ書き換えとして保護バックアップを維持する。変更時は `RegistryToggleTests` と `UwpBackgroundToggleTests` の該当検証、および操作フロー検証を通す。

## クリーンアップ (規則・走査・削除)

対象は根拠 (tier A/B/L) を持つ規則データと、利用者が明示した範囲に限る。保存パスワード・認証情報・鍵・アプリ設定は対象外で、Cookie・フォーム履歴は既定オフの明示選択に限る。リンク先を辿らず、同一ハンドルで削除し、所有権の奪取や ACL の変更をしない。規則・走査・削除・高度・スリム化の変更前に、回帰厳禁の安全ガードを定めた [cleanup-rules-and-deletion.md](cleanup-rules-and-deletion.md) を必ず読む。起動時再試行と旧サインイン時タスクの自己移行は [workflow.md](workflow.md) に従う。

## 同時実行と終了処理

状態変更は lease でマシン全体の 1 件に制限し、補償を含む `finally` より先に終了しない。変更前に [workflow.md](workflow.md#同時実行と終了処理) を必ず読む。

## 昇格とデバッグ起動

旧版移行は自己昇格より前、`SingleInstanceGuard` の取得は自己昇格後とする。デバッグ時の扱いを含め [workflow.md](workflow.md#昇格とデバッグ起動) に従う。

## 未接続 PnP デバイスの削除

削除対象は利用者が選択した未接続デバイスだけとし、削除直前にも再接続を確認する。実機のデバイスを消して検証しない。変更前に [disconnected-pnp-devices.md](disconnected-pnp-devices.md) を必ず読む。
