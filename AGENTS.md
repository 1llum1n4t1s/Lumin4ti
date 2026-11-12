# AGENTS.md

This file provides guidance to Codex when working in this repository.

## 概要

Lumin4ti は Windows 10/11 向けのメンテナンス・最適化 GUI ツール。管理者権限で自己昇格して起動し、HKLM/HKCU レジストリ・DISM・powercfg・bcdedit・winget・regsvr32・Shell COM・WinRT を操作する。元は AegisOverhaul の巨大バッチ (`Maintenance.bat`) から機能を移植したもので、**バッチのコマンド丸投げをやめて可能な限り C# ネイティブに制御する**方針を継続すること。

## ビルド・テスト・実行

```bash
dotnet build Lumin4ti.slnx           # ビルド (0 warnings を維持する方針)
dotnet test Lumin4ti.slnx            # 全テスト (MSTest)
# 単一テストクラス/メソッド:
dotnet test Lumin4ti.slnx --filter "FullyQualifiedName~RegistryToggleTests"
dotnet test Lumin4ti.slnx --filter "FullyQualifiedName~UwpBackgroundToggleTests"
# 実行 (通常起動は UAC 昇格が入る):
./src/Lumin4ti.UI/bin/Debug/net11.0-windows10.0.20348.0/Lumin4ti.UI.exe
```

- TFM は `net11.0-windows10.0.20348.0` ([Directory.Build.props](Directory.Build.props))。UWP パッケージ列挙 (WinRT `PackageManager`) のため Windows SDK 付き。
- `.github/workflows/dependency-validation.yml` は Dependabot PR と手動実行を対象に Windows 上で restore・Release build・test を行う。通常の変更後もローカルの `dotnet build` (0 warnings) と `dotnet test` を必ず両方通す。
- **バージョン (`Directory.Build.props` の `<Version>`) は `/vava` 経由でのみ更新**。コード修正のついでに触らない。
- 復元・サービス補償・終了時排他・状態表示などの操作フローを変更したら、`pwsh -NoProfile -File scripts/verify-review-fixes.ps1` も実行する。対象ケースと再現方法は [WORKFLOWS.md](scripts/verification/WORKFLOWS.md) を参照し、成果物 `local-release/review-fixes-verification.json` を保持する。検証の境界は [DESIGN.md](DESIGN.md#主要コンポーネント) に記載し、実 GUI の E2E 検証と区別する。

## アーキテクチャ

システム全体の構造と設計判断は [DESIGN.md](DESIGN.md) を正本とし、この節では実装時に守る規約を扱う。

- Core から UI を参照しない。Avalonia MVVM + CommunityToolkit.Mvvm を使い、`App.axaml.cs` の `ConfigureServices` で手動 DI (全 Singleton) を維持する。
- テストではレジストリ・SCM・COM・デバイスの実変更を行わない。ファイル削除の検証はテスト専用一時領域に限定する。各プロジェクトと操作フロー検証の責務は [DESIGN.md](DESIGN.md#主要コンポーネント) を参照する。

### メンテナンス項目の中核 (最重要)

すべての「機能」は `IMaintenanceItem` を軸にした 4 型 ([IMaintenanceAction.cs](src/Lumin4ti.Core/Interfaces/IMaintenanceAction.cs)):

- **`IMaintenanceAction`** — 「実行」ボタン型 (1 回実行)。`ExecuteAsync(IProgress<string>?, ct)` でライブ進捗を UI へ流せる。
- **`IMaintenanceBatchSetting`** — 対象一覧へ機能のON/OFFを繰り返し適用する一括設定型。同じカードの「一括OFF」「一括ON」から `ApplyAsync(bool enabled, ct)` を呼ぶ。`enabled = true` は対象機能を有効にする意味。現在状態の取得には依存せず、実行ごとに対象を再列挙する。UWPバックグラウンド実行が該当する。
- **`IMaintenanceToggle`** — ON/OFF トグル型。**ON = 最適化を適用 / OFF = 項目の解除値へ設定または明示上書きを削除**。永続バックアップと適用前の元値復元は行わない。MMAgent 系は ON = 機能有効。`GetStateAsync`/`SetStateAsync`。
- **`IMaintenanceChoice`** — ドロップダウン選択型。ON/OFF に収まらない数値・段階設定に使う (例: `MmAgentOperationApiChoice` は「無効」と記録ファイル数を 1 つの操作で選ばせる)。`Options`/`GetSelectedValueAsync`/`SetSelectedValueAsync`。選択肢の表示名は翻訳不要なら `Label` をそのまま出し、翻訳が要るものだけ `LabelKey` を持たせる。`IsDefault` を付けた選択肢に UI が「(既定)」を添える。

`IMaintenanceItem.ParentId` に前提となる項目の Id を入れると、その項目は**親カードの中へ 1 段だけ入れ子表示**され、親トグルが OFF または状態不明の間は操作不可になる (OS 側で連動して無効になる子設定に使う)。親の状態既知・ON/OFF の変化を子へ通知する。親は同じカテゴリに置くこと。

新機能を足すときは Actions 配下にクラスを作り、**[MaintenanceActionCatalog.cs](src/Lumin4ti.Core/Services/Windows/MaintenanceActionCatalog.cs) の `Items` に登録するだけ**で UI に現れる。カタログの並び順が画面の表示順。単純なレジストリ tweak は個別クラスを作らず汎用の `RegistryToggle` にスペックを渡す。

### 外部プロセス実行

`ICommandExecutor` → `ProcessCommandExecutor` が唯一の実装 (DI で単一登録)。DISM/regsvr32/powercfg/winget/bcdedit/MMAgent cmdlet など「OS 提供ツールが唯一の手段」のものだけ外部プロセスで実行し、レジストリ・COM・WinRT で代替可能なものは C# ネイティブで書く。

- **セキュリティ (回帰厳禁)**: bare exe 名を渡すと `CreateProcess` の検索順序でインストールディレクトリが `System32` より先に照合され、昇格プロセスがバイナリプランティング LPE を踏む。`ProcessCommandExecutor` は [SystemProcessResolver](src/Lumin4ti.Core/Services/SystemProcessResolver.cs) でフルパス解決 + `WorkingDirectory=System32` 固定してこれを封じている。呼び出し側は論理名でよいが、この解決を外さないこと。
- 子プロセスは [ProcessJobTracker](src/Lumin4ti.Core/Services/ProcessJobTracker.cs) で子ごとに独立した Job Object (KILL_ON_JOB_CLOSE) へ登録する。生成を制御できる経路では停止状態で登録してから再開し、登録失敗時は実行させない。`ct` キャンセル時はプロセスツリーごと kill。Job の共有を再導入しないこと。理由と Explorer broker 経路の制約は [DESIGN.md の OS操作の境界](DESIGN.md#os操作の境界) を参照する。
- 出力の復号とライブ進捗通知は `ProcessCommandExecutor` に集約し、呼び出し側で固定コードページを仮定しない。復号の順序は [DESIGN.md](DESIGN.md#os操作の境界) を参照する。
- サービスの停止・再開が要る操作は [WindowsServiceControl](src/Lumin4ti.Core/Services/Windows/WindowsServiceControl.cs) を通す。停止を確認できなければ削除・設定変更を進めず、返された `ServiceSuspension` の `ResumeAsync()` を失敗・キャンセル時も `finally` で必ず実行する。依存サービス確認、標準入力 EOF、停止・開始の期限、復帰の再照合は [DESIGN.md](DESIGN.md#os操作の境界) の共通補償フローを維持し、`net stop /y` を再導入しない。復帰失敗をキャンセルや成功で隠さず `Failed` として表示する。

### 破壊的操作の復元性

ON/OFF設定切り替えとDefenderの既定設定へのリセットは永続バックアップを使わない。既存の旧バックアップは参照・移行・削除しない。型付きの操作直前値はメモリにだけ保持し、失敗・キャンセル時の補償と再検証は [DESIGN.md の状態と永続化](DESIGN.md#状態と永続化) に従う。HKLMの変更は他利用者にも影響し、利用者別の共有所有者管理は行わない。Path・クイックアクセスの並べ替えはデータ書き換えとして保護バックアップを維持する。変更時は `RegistryToggleTests` と `UwpBackgroundToggleTests` の該当検証、および操作フロー検証を通す。

### ログの保存と移行

ログ保存先・旧ログ移行を変更したら `LegacyLogMigrationTests` を通し、[DESIGN.md の状態と永続化](DESIGN.md#状態と永続化) にある既知ログ限定・リンク非追従・失敗時保持の境界を維持する。

デバッグ画面・追加ログ出力・進捗オーバーレイを変更したら、`DebuggerIntegrationTests`、`LoggerProviderAttachmentTests`、`MaintenanceOperationCoordinatorTests` の該当検証を通す。デバッグ画面の初期化範囲とログ転送の境界は [DESIGN.md](DESIGN.md#主要コンポーネント) を維持し、実画面は [WORKFLOWS.md のプレビュー手順](scripts/verification/WORKFLOWS.md#再現) で背面操作禁止・中断後の待機・ファイルログとの併存を確認する。

### winget によるアプリ更新

[WingetUpgradeAction](src/Lumin4ti.Core/Services/Windows/Actions/UpdateActions.cs) の更新候補は、公式ソース、Package ID、表示名と更新チャネルの整合性を確認してから個別に実行する。`upgrade --all` へ置き換えず、確認不能な一覧では更新を中止し、確認不能な個別候補は除外して他の候補を続行すること。

個別の成功・失敗・除外、終了コードと出力要約を結果・進捗・ログへ残す。判定と診断を変更するときは [WingetOutputFilterTests](src/Lumin4ti.Tests/WingetOutputFilterTests.cs) の別チャネル拒否、個別結果、部分成功の検証を通す。データフローと集計結果の条件は [DESIGN.md のインストール済みアプリの更新](DESIGN.md#インストール済みアプリの更新) を参照する。

### 未接続 PnP デバイスの削除

[WindowsDisconnectedDeviceService](src/Lumin4ti.Core/Services/Windows/WindowsDisconnectedDeviceService.cs) は SetupAPI で接続中 ID とインストール済み ID の差を列挙し、ソフトウェア／仮想デバイスを含む未接続デバイスを UI へ返す。利用者が個別または全選択した項目だけを対象にし、削除直前にも再接続を確認してから `DiUninstallDevice` を呼ぶ。接続中デバイスと空の Instance ID は対象にしない。

専用 UI は [DeviceCleanupViewModel](src/Lumin4ti.UI/ViewModels/DeviceCleanupViewModel.cs) が個別／全選択、二段階確認、削除結果の集計を担当し、`MaintenanceOperationCoordinator` の排他制御へ参加する。検証は [DisconnectedDeviceTests](src/Lumin4ti.Tests/DisconnectedDeviceTests.cs) とモックサービスで行い、実機デバイスを削除して確認しない。

### 一時ファイル・キャッシュの削除 (グループ実行)

ファイル削除は「グループ 1 つ = ボタン 1 つ」とし、対象パスは [FileCleanupGroups.cs](src/Lumin4ti.Core/Services/Windows/Actions/FileCleanupGroups.cs) に集約する。掃除対象を増やすときはパス表へ追加し、個別クラスを作らない。

- [FileCleanupEngine](src/Lumin4ti.Core/Services/Windows/Actions/FileCleanupEngine.cs) — 削除の実体。`CleanupTarget` は `Contents` (既知のキャッシュ／ログフォルダの中身だけ) / `Files` (既知のキャッシュファイル名だけ) の 2 種。フォルダごとの削除とドライブ全体の再帰検索は扱わない。使用中ファイルは飛ばして続行し、削除数・解放バイト数・ブロック数を `CleanupOutcome` に集計する。削除不能の記録は `RecordBlocked` を通し、パス・理由・エラー番号の代表例を件数・長さの上限内で残す。
- [FileCleanupAction](src/Lumin4ti.Core/Services/Windows/Actions/FileCleanupAction.cs) — グループ 1 件分の `IMaintenanceAction`。サービス停止・再起動要否・Explorer 影響・再起動時削除予約をコンストラクタ引数で受ける。

削除対象は、名称と用途の両方から再生成可能と確認できるキャッシュ・ログ・一時領域だけに限定する。Python ランタイム、仮想環境、ローカルビルド成果物、WebStorage、閲覧・利用履歴、オフラインデータ、復旧資産、ドライバインストーラー、アプリ設定、汎用ホームディレクトリは対象にしない。ゴミ箱、`Windows.old`、Outlook OST/NST、ドライブ全体のファイル検索も扱わない。

**安全ガード (回帰厳禁)**: 実運用では他人の PC の実データを消すため、次を外さない。

- `TryResolve` が、環境変数の未解決 (`%ProgramData%` 未定義で `\LGHUB\cache` になる等)、相対パス、ドライブ直下、`%LOCALAPPDATA%` 等の基点フォルダを拒否する。基点フォルダは `Files` 指定 (`IconCache.db` / `FNTCACHE.DAT`) のときだけ許可する。
- ジャンクション・シンボリックリンクは、対象確認時点と削除直前のハンドル属性で検出した場合、リンク自体を保持する。列挙・属性変更・削除は再解析を拒否して開いた同じオブジェクトのハンドルで行い、途中のパス差替えでもリンク先へ作用しない。最終確認後の競合に対する保証範囲は [DESIGN.md の状態と永続化](DESIGN.md#状態と永続化) を参照する。
- 認証情報・鍵・アプリ設定のフォルダ (`.gnupg` / `.aws` / `.config` / `.codex` 等) はどのグループにも入れない。再生成できないので掃除の巻き添えにしない。[FileCleanupTests](src/Lumin4ti.Tests/FileCleanupTests.cs) が回帰を検出する。
- npm は `%LOCALAPPDATA%\npm-cache` / `%USERPROFILE%\.npm` の基点全体を対象にせず、再取得可能な `_cacache` と診断用の `_logs` だけを削除する。`_npx` は展開済みの実行環境なので削除対象へ戻さない。
- Windows のイベントログ、Defender の検出履歴、GPU 設定、スタートアップ登録、ファイル関連付け、アプリのパッケージ登録、WinSxS の旧コンポーネントはキャッシュではないため `FileCleanupGroups` では扱わない。リンク切れスタートアップと関連付け候補だけは専用アクションで扱い、`StartupCommandParser.IsConfirmedMissing` が準備済み固定ドライブ上の欠損を確定できた登録だけを削除する。UNC、リムーバブル、未準備ドライブ、再解析点配下、アクセス不能、解決不能なコマンドは保持する。
- ETL トレースログは `%SystemRoot%\Logs`、`System32\LogFiles`、`Panther`、`%ProgramData%\Microsoft\Diagnosis\ETLLogs` の既知基点だけをリンク非追従で列挙し、`*.etl` のみ削除する。ドライブ全体を対象にする再帰パターン削除は復活させない。
- 使用中キャッシュの起動時再試行を変更したら、`FileCleanupTests`、`RebootCacheCleanupTests`、`RebootCleanupTaskTests` と操作フロー検証を通す。[DESIGN.md の使用中キャッシュの起動時再試行](DESIGN.md#使用中キャッシュの起動時再試行) の対象・利用者・信頼確認・期限の境界を維持する。任意パスや SYSTEM 実行へ広げず、旧 MoveFileEx 予約を読み取り・変更・移行しない。Explorer を kill しない。
- サインイン時クリーンアップは画面と同じ `FileCleanupGroups` と利用者設定を使い、タスクスケジューラーから最高権限で実行する。タスクへ登録できるのは、署名・MSI マーカー・`Program Files\Lumin4ti\current` の配置と各パス要素の所有者／ACL／再解析ポイントを検証できた実行ファイルだけとする。登録用 XML は `%TEMP%` へ置かず、Administrators / SYSTEM だけが書ける保護ストレージで作成し、登録後に削除する。
- 固定名の既存タスクは Principal の利用者を照合し、別利用者・所有者不明の定義を上書き・削除しない。照会失敗時の新規登録に `/f` を付けない。起動時の修復条件は [DESIGN.md の起動と終了](DESIGN.md#起動と終了) を参照し、変更時は `ScheduledTempCleanupTests` の所有者・旧定義・照会失敗の検証を通す。

### 同時実行と終了処理

状態変更は [MaintenanceOperationCoordinator](src/Lumin4ti.UI/Services/MaintenanceOperationCoordinator.cs) と [MaintenanceOperationProcessLock](src/Lumin4ti.Core/Services/MaintenanceOperationProcessLock.cs) の lease を通し、マシン全体で同時に1件へ制限する。取得失敗時は実行せず、サインイン時クリーンアップは待たずにスキップする。終了処理では `RequestCancellation()` → `WaitForIdleAsync()` の順を維持し、補償・再検証を含む `finally` より先に閉じない。受付閉鎖と再読込の境界は [DESIGN.md の起動と終了](DESIGN.md#起動と終了) に従い、lease を跨いで生き残る後始末を作らない。

### 昇格とデバッグ起動

通常起動とサインイン時クリーンアップの分岐順序は [DESIGN.md の起動と終了](DESIGN.md#起動と終了) を維持する。旧版移行を自己昇格より後へ移さず、自己昇格前に `SingleInstanceGuard` を取得しない。**`Debugger.IsAttached` のときは昇格をスキップ**するため、F5 での HKLM 系操作・`Get-MMAgent` の権限エラーは正常である。管理者系までデバッグするなら IDE 自体を管理者起動する。

### タスクバーアイコンと AUMID (回帰注意)

「**プロセスだけが AUMID を名乗り、ショートカットには何も書かない**」方針を維持する:

- 製品版 (`#if !DEBUG`) だけ起動直後に `WindowsElevationHelper.TrySetCurrentProcessAppUserModelId()` で Velopack の AUMID を設定する。Debug ビルドで名乗ると Windows がインストール済み製品の情報を参照して開発用 EXE のタスクバーアイコンが白紙になるため設定しない。
- ショートカット (.lnk) へ AUMID や明示アイコンを追記しない。埋め込みアイコンの解決は Windows に任せる。v1.0.12 が追記した override は起動時に [WindowsLegacyStartMenuShortcutMigrator.ClearInstalledShortcutOverrides()](src/Lumin4ti.UI/Services/WindowsLegacyStartMenuShortcutMigrator.cs) が Start メニュー・デスクトップ・タスクバーピン留めから除去する。
- .lnk のプロパティ操作が必要な場合は [WindowsShortcutPropertyStore](src/Lumin4ti.UI/Services/WindowsShortcutPropertyStore.cs) (IPropertyStore 直叩き) を使い、既存リンクへの Commit を維持する。`ShellLink.SetAppUserModelId` へ置き換えない。

### ローカライズ

辞書の構造とフォールバックは [DESIGN.md のローカライズ](DESIGN.md#ローカライズ) を参照する。UI 文言を直書きせず、XAML は `{DynamicResource Text.Xxx}`、C# は `App.Text("key", 日本語フォールバック, args)` で解決する。

- Core は翻訳キーと日本語フォールバックを持ち、翻訳解決は UI 側で行う。`IMaintenanceItem.LabelKey` は `Action.{Id}.Label`、`DescriptionKey` は `Action.{Id}.Desc`。共通 UI 文字列を追加したら 17 言語すべてに同じ `x:Key` を足す。非 ja 辞書のキー集合は `en_US.axaml` と一致させ、ja の Action・カテゴリ・ステータスはコード内日本語フォールバックを維持する。
- `App.SetLocale()` が辞書を差し替え、`App.LocaleChanged` を購読する VM プロパティが再評価される。既定言語は `App.DetectDefaultLocale()` が OS ロケールから判定、選択は settings.json の `Locale` に保存。
- アクションの実行結果ログ行 (`  - ...しました`) は技術的詳細なので日本語のまま。

## リリース・配信 (Cloudflare R2 + ローカル署名)

更新・移行・配信の構造と理由は [DESIGN.md の更新と配布](DESIGN.md#更新と配布) を参照する。GitHub Releases は使わない。

### 配布契約と変更権限

- 現行の配布契約は [DESIGN.md](DESIGN.md#更新と配布) に記載する。`win-x64` のみを対象とし、restore / publish の RID と `vpk pack --runtime` を一致させる。`--channel win` は維持し、runtime と channel を混同しない。PerUser `Setup.exe` は公開しない。
- 不具合調査、全体レビュー、セキュリティレビュー、および「見つかったものを全部直してよい」という許可は、この現行契約内の修正に適用する。レビューで配布や昇格の設計リスクを発見しても、それを理由に対応スコープを配布方式の変更へ広げない。
- MSI / MSIX 等へのインストーラー形式変更、Velopack の置換、per-user / machine-wide のインストール範囲変更、NativeAOT 等のパッケージング方式変更、更新元・channel・署名方式の変更は、ユーザーが対象を個別に明示した場合だけ実装する。明示がない場合は現行契約を維持し、設計案と影響だけを報告する。
- `dotnet publish` によるローカル検証は通常の検証に含めてよい。`vpk pack`、署名、R2 upload、cache purge は、リリースまたは `/vava` が明示された場合だけ実行する。公開後の配信内容取得・ハッシュ照合・伝播待ちは行わず、公開前検証と API の受付成功を確認する。

- 実行は `pwsh scripts/release-local.ps1` (build + PerMachine MSI生成 + 署名 + R2 アップロード + キャッシュパージ + 旧 nupkg 掃除を一括)。`-SkipUpload` では署名後の公開前検証まで行う。検証内容は [DESIGN.md のリリース成果物と公開順序](DESIGN.md#リリース成果物と公開順序) を参照し、検証失敗時は公開へ進めない。
- [set-msi-program-files-location.ps1](scripts/set-msi-program-files-location.ps1) による Program Files 配置補正と、補正後 MSI の再署名・署名検証を省略しない。
- 移行コードを変更するときは [DESIGN.md の旧配置からの移行](DESIGN.md#旧配置からの移行) の信頼確認・回収範囲・期限を維持し、`WindowsPerMachineMigrationTests` と操作フロー検証を通す。旧 `Update.exe` を実行せず、設定・ログ・復元原本と任意のカスタム配置を回収対象へ広げない。
- リリース準備の清掃は今回再生成する `local-release/artifacts` と対象 RID の `publish-*` に限定し、検証記録を保持する。公開順序と受付成功の確認は [DESIGN.md](DESIGN.md#リリース成果物と公開順序) に従う。
- 前提: SimplySign Desktop がログイン済み (`Cert:\CurrentUser\My` に `CN=Open Source Developer Yuichiro Shinozaki` が見える) / `<Version>` が `/vava` 済み / `D:\Dev\Secret\secrets.json` に `cloudflare.api_token`。
- 製品ページの配信は `../vps-web/deploy/deploy-lp.ps1` を使う。配信先は [DESIGN.md](DESIGN.md#製品ページの配信先) を参照し、公開ホスト・更新ファイルの既存経路を維持する。
- R2 バケット `lumin4ti-updates` (account `10901bfadbf1005164774a7350082985` / zone `kagayoi.com`)。`local-release/` は `.gitignore` 済み。

### ローカルリリースの実行

リポジトリルートで事前確認を行い、失敗時は「署名証明書が見つからないよ。SimplySign Desktop を起動してトークンでログインしてから /vava を再実行してね」。公開依頼時に成功を確認してから実行する。

```powershell
pwsh -NoProfile -Command 'if (-not (Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -like "CN=Open Source Developer Yuichiro Shinozaki*" -and $_.NotAfter -gt (Get-Date) })) { exit 1 }'
if ($LASTEXITCODE -eq 0) { pwsh -NoProfile -File scripts/release-local.ps1 } else { throw '署名証明書が見つからないよ。SimplySign Desktop を起動してトークンでログインしてから /vava を再実行してね' }
```

## コードレビュー時の注意点 (このコードベース特有)

- トグルの多重操作レース: `ToggleSwitch` の `IsEnabled` は `CanToggle` (= 状態既知 かつ 非実行中 かつ 親がある場合は状態既知・ON) にバインドすること。操作はスイッチへ集約する。既定値は `IMaintenanceToggle.DefaultState` (`true`=ON、`false`=OFF、`null`=環境による)、選択型は `Options.IsDefault` から表示し、現在値や推奨値と混同させない。変更時は `CommandCategoryStateQueryTests` のOFF表示・状態不明・実行中・親子連動と既定値表示の検証を通す。表示の理由は [DESIGN.md のメンテナンス操作のモデル](DESIGN.md#メンテナンス操作のモデル) を参照する。
- 状態表示の乖離を避ける: `GetStateAsync` はレジストリだけでなく実適用状態も見る (例: VBS トグルは bcdedit の `hypervisorlaunchtype` も照合)。部分適用を避けるため、失敗しやすいステップ (bcdedit 等) を先に実行してから残りを書く。
- 状態取得の診断は `StateErrorText`、操作結果は `ResultText` へ分け、状態回復時に診断だけを消す。状態不明の技術的理由は `StateFailureReason`、翻訳可能なトグル固有診断は `StateDiagnostic` で渡し、正常取得時には解除する。失敗・部分成功・受付拒否の記録は `MaintenanceItemLog` を使い、項目名と原因を残す。診断の境界は [DESIGN.md の状態と永続化](DESIGN.md#状態と永続化) を参照し、変更時は操作フロー検証も通す。親子の操作部品はローカライズ済み項目名をアクセシビリティ名にする。デバイスの空表示は列挙成功時だけ出す。
- 部分失敗を成功と偽らない: マルチステップ (powercfg 等) は重要ステップの失敗で `Fail` を返す。使用中ファイルのスキップのように「想定内の一部未処理」は `Partial` と結果行で伝える。
- NTP と MMAgent の変更では [DESIGN.md の設定変更の範囲](DESIGN.md#設定変更の範囲) を維持する。同期モードの強制変更や `EnablePrefetcher` 全体の旧バックアップ復元を再導入しない。
- MMAgent の一括再読込・切替時は [DESIGN.md](DESIGN.md#設定変更の範囲) のキャッシュ更新と並行照会の集約を維持し、取得失敗や不正 JSON の診断を共有照会境界で記録する。外部コマンドの診断には `CommandFailureDiagnostic` を使い、秘密値・制御文字・出力量を制限する。
- 配布契約は [DistributionContractTests](src/Lumin4ti.Tests/DistributionContractTests.cs) が横断で固定している。`Lumin4ti.UI.csproj` / `scripts/release-local.ps1` / `scripts/set-msi-program-files-location.ps1` / `README.md` / `AppSettings.cs` を編集すると、意図せずここで落ちることがある。落ちたら文字列だけ直さず、配布方式を変えていないかを先に確認する。

## ドメイン移行（2026-07 開始・期限 2027/05/31）

屋号を **Kagayoi** に統一したため、配信ドメインを `nephilim.jp` から `kagayoi.com` へ移行中。方針の全体像はユーザーグローバルの `AGENTS.md` §屋号とドメイン を参照する。

- **旧ドメイン `nephilim.jp` はレジストラで廃止申請済みで 2027/05/31 に失効する**（延長しない）。それまでに出荷済みバイナリを新ドメインへ移行しきる。
- 旧ホストの Worker route / custom domain は**期限まで消さない**。消すと出荷済みアプリの自動更新が止まる。
- `nephilim.jp` の Redirect Rules は `/` だけを 301 する。`releases.*.json` / `*.nupkg` / `*-Setup.exe` は転送せず R2 が配信を続ける。
- 配信は `lumin4ti.kagayoi.com`（R2 `lumin4ti-updates`）。旧 `lumin4ti.nephilim.jp` は route に併記して残してある。
- アプリ名 `Lumin4ti` 自体は既存ユーザーが混乱するので改名しない。
