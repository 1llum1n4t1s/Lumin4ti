# AGENTS.md

This file provides guidance to Codex when working in this repository.

## 概要

Lumin4ti は Windows 10/11 向けのメンテナンス・最適化 GUI ツール。管理者権限で自己昇格して起動し、HKLM/HKCU レジストリ・DISM・powercfg・bcdedit・winget・regsvr32・Shell COM・WinRT を操作する。元は AegisOverhaul の巨大バッチ (`Maintenance.bat`) から機能を移植したもので、**バッチのコマンド丸投げをやめて可能な限り C# ネイティブに制御する**方針を継続すること。

## ビルド・テスト・実行

```bash
dotnet build Lumin4ti.slnx           # ビルド (0 warnings を維持する方針)
dotnet test Lumin4ti.slnx            # 全テスト (MSTest)
# 単一テストクラス/メソッド:
dotnet test Lumin4ti.slnx --filter "FullyQualifiedName~MaintenanceActionCatalogTests"
dotnet test Lumin4ti.slnx --filter "Name=既定値に戻せるトグルの既定値は適用値と異なる"
# 実行 (通常起動は UAC 昇格が入る):
./src/Lumin4ti.UI/bin/Debug/net10.0-windows10.0.20348.0/Lumin4ti.UI.exe
```

- TFM は `net10.0-windows10.0.20348.0` ([Directory.Build.props](Directory.Build.props))。UWP パッケージ列挙 (WinRT `PackageManager`) のため Windows SDK 付き。
- **`.github/` は存在せず CI は無い**。ローカルの `dotnet build` (0 warnings) と `dotnet test` が唯一の検証ゲートなので、変更後は必ず両方通す。
- **バージョン (`Directory.Build.props` の `<Version>`) は `/vava` 経由でのみ更新**。コード修正のついでに触らない。

## アーキテクチャ

3 プロジェクト構成 (Avalonia MVVM + CommunityToolkit.Mvvm + Microsoft.Extensions.DI + Velopack 自動更新)。

- **`Lumin4ti.Core`** — OS 操作ロジック。UI を一切参照しない (依存は一方向 UI→Core)。
- **`Lumin4ti.UI`** — Avalonia。`App.axaml.cs` の `ConfigureServices` で手動 DI (全 Singleton)。
- **`Lumin4ti.Tests`** — MSTest。純粋関数・パーサ・カタログ整合性を検証 (レジストリ/COM/管理者依存の実書き込みはテストしない)。

### メンテナンス項目の中核 (最重要)

すべての「機能」は `IMaintenanceItem` を軸にした 3 型 ([IMaintenanceAction.cs](src/Lumin4ti.Core/Interfaces/IMaintenanceAction.cs)):

- **`IMaintenanceAction`** — 「実行」ボタン型 (1 回実行)。`ExecuteAsync(IProgress<string>?, ct)` でライブ進捗を UI へ流せる。
- **`IMaintenanceToggle`** — ON/OFF トグル型。**ON = 最適化を適用 / OFF = Windows 既定に戻す** で統一 (例外は MMAgent 系トグルのみ ON = 機能有効)。`GetStateAsync`/`SetStateAsync`。
- **`IMaintenanceChoice`** — ドロップダウン選択型。ON/OFF に収まらない数値・段階設定に使う (例: `MmAgentOperationApiChoice` は「無効」と記録ファイル数を 1 つの操作で選ばせる)。`Options`/`GetSelectedValueAsync`/`SetSelectedValueAsync`。選択肢の表示名は翻訳不要なら `Label` をそのまま出し、翻訳が要るものだけ `LabelKey` を持たせる。`IsDefault` を付けた選択肢に UI が「(既定)」を添える。

`IMaintenanceItem.ParentId` に前提となる項目の Id を入れると、その項目は**親カードの中へ 1 段だけ入れ子表示**され、親トグルが OFF の間は操作不可になる (OS 側で連動して無効になる子設定に使う)。親は同じカテゴリに置くこと。

新機能を足すときは Actions 配下にクラスを作り、**[MaintenanceActionCatalog.cs](src/Lumin4ti.Core/Services/Windows/MaintenanceActionCatalog.cs) の `Items` に登録するだけ**で UI に現れる。カタログの並び順が画面の表示順。単純なレジストリ tweak は個別クラスを作らず汎用の `RegistryToggle` にスペックを渡す。

### 外部プロセス実行

`ICommandExecutor` → `ProcessCommandExecutor` が唯一の実装 (DI で単一登録)。DISM/regsvr32/powercfg/winget/bcdedit/MMAgent cmdlet など「OS 提供ツールが唯一の手段」のものだけ外部プロセスで実行し、レジストリ・COM・WinRT で代替可能なものは C# ネイティブで書く。

- **セキュリティ (回帰厳禁)**: bare exe 名を渡すと `CreateProcess` の検索順序でインストールディレクトリが `System32` より先に照合され、昇格プロセスがバイナリプランティング LPE を踏む。`ProcessCommandExecutor` は [SystemProcessResolver](src/Lumin4ti.Core/Services/SystemProcessResolver.cs) でフルパス解決 + `WorkingDirectory=System32` 固定してこれを封じている。呼び出し側は論理名でよいが、この解決を外さないこと。
- 子プロセスは [ProcessJobTracker](src/Lumin4ti.Core/Services/ProcessJobTracker.cs) の Job Object (KILL_ON_JOB_CLOSE) に紐付け、アプリ終了時に OS が孤児を kill する。`ct` キャンセル時はプロセスツリーごと kill。
- 出力は UTF-8 → OEM (CP932) の順で自動デコード。長時間コマンドの進捗は `\r`/`\n` 区切りで `IProgress<string>` 通知。
- サービスの停止・再開が要る操作は [WindowsServiceControl](src/Lumin4ti.Core/Services/Windows/WindowsServiceControl.cs) を通す。状態照会は SCM を直接叩き (`QueryState`)、停止・開始だけ `net.exe` に委ねる。`SuspendAsync` は**元から稼働していたサービスだけ**を止めて `ServiceSuspension` を返し、呼び出し側は失敗・キャンセル時も `finally` で `ResumeAsync()` を必ず実行する。キャンセル時に例外を投げず途中で打ち切るのは、既に止めたサービスの再開手段を呼び出し側が失わないため。

### 破壊的操作の復元性

「OFF で Windows 既定に戻す」を謳う以上、ハードコード既定値でなく**ユーザーの元の値**へ戻す。`RegistryToggle` は ON 適用前に [RegistryValueBackup](src/Lumin4ti.Core/Services/Windows/Actions/RegistryValueBackup.cs) で `%APPDATA%\Lumin4ti\backups\` にスナップショットし、OFF で復元 (UWP・Defender も同様のバックアップを持つ)。不可逆操作を足すときは同様のバックアップを検討する。

### 一時ファイル・キャッシュの削除 (グループ実行)

旧バッチのファイル削除は、対象を用途別にまとめた「グループ 1 つ = ボタン 1 つ」として実装している。ロジックは 3 ファイルに分かれ、**掃除対象を増やすときは [FileCleanupGroups.cs](src/Lumin4ti.Core/Services/Windows/Actions/FileCleanupGroups.cs) のパス表へ 1 行足すだけ**でよい (個別クラスを作らない)。

- [FileCleanupEngine](src/Lumin4ti.Core/Services/Windows/Actions/FileCleanupEngine.cs) — 削除の実体。`CleanupTarget` は `Contents` (既知のキャッシュ／ログフォルダの中身だけ) / `Files` (既知のキャッシュファイル名だけ) の 2 種。フォルダごとの削除とドライブ全体の再帰検索は扱わない。使用中ファイルは飛ばして続行し、削除数・解放バイト数・ブロック数を `CleanupOutcome` に集計する。
- [FileCleanupAction](src/Lumin4ti.Core/Services/Windows/Actions/FileCleanupAction.cs) — グループ 1 件分の `IMaintenanceAction`。サービス停止・再起動要否・Explorer 影響・再起動時削除予約をコンストラクタ引数で受ける。

削除対象は、名称と用途の両方から再生成可能と確認できるキャッシュ・ログ・一時領域だけに限定する。Python ランタイム、仮想環境、ローカルビルド成果物、WebStorage、閲覧・利用履歴、オフラインデータ、復旧資産、ドライバインストーラー、アプリ設定、汎用ホームディレクトリは対象にしない。ゴミ箱、`Windows.old`、Outlook OST/NST、ドライブ全体のファイル検索も扱わない。

**安全ガード (回帰厳禁)**: 実運用では他人の PC の実データを消すため、次を外さない。

- `TryResolve` が、環境変数の未解決 (`%ProgramData%` 未定義で `\LGHUB\cache` になる等)、相対パス、ドライブ直下、`%LOCALAPPDATA%` 等の基点フォルダを拒否する。基点フォルダは `Files` 指定 (`IconCache.db` / `FNTCACHE.DAT`) のときだけ許可する。
- ジャンクション・シンボリックリンクは辿らず、リンク自体も削除しない。キャッシュを別ドライブへ逃がしている利用者の配置設定と、リンク先の実体を保護するため。
- 認証情報・鍵・アプリ設定のフォルダ (`.gnupg` / `.aws` / `.config` / `.codex` 等) はどのグループにも入れない。再生成できないので掃除の巻き添えにしない。[FileCleanupTests](src/Lumin4ti.Tests/FileCleanupTests.cs) が回帰を検出する。
- Windows のイベントログ、Defender の検出履歴、GPU 設定、スタートアップ登録、ファイル関連付け、アプリのパッケージ登録、WinSxS の旧コンポーネントは、診断情報・利用者設定・ロールバック資産であってキャッシュではないため削除アクションを設けない。
- ETL トレースログは `%SystemRoot%\Logs`、`System32\LogFiles`、`Panther`、`%ProgramData%\Microsoft\Diagnosis\ETLLogs` の既知基点だけをリンク非追従で列挙し、`*.etl` のみ削除する。ドライブ全体を対象にする再帰パターン削除は復活させない。
- シェルが握って離さないファイル (アイコン・フォントキャッシュ) は `MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)` で再起動時削除に回す。Explorer を kill しないのは、失敗時に利用者がシェル無しで取り残されるのを避けるため。

### 同時実行と終了処理

[MaintenanceOperationCoordinator](src/Lumin4ti.UI/Services/MaintenanceOperationCoordinator.cs) が状態変更操作を **同時に 1 件だけ**に制限する (`TryBegin` が false なら UI は「実行中」表示に落とす)。終了要求では `RequestCancellation()` が全操作へキャンセルを通知し、`WaitForIdleAsync()` が各操作の補償・再検証を含む `finally` の完了を待ってからアプリを閉じる。長時間アクションを足すときは、この lease を跨いで生き残る後始末を作らないこと。

### 昇格とデバッグ起動

[Program.cs](src/Lumin4ti.UI/Program.cs) で `VelopackApp.Build().Run()` → 非管理者なら自己昇格 (ShellExecute + runas) → `SingleInstanceGuard`。**`Debugger.IsAttached` のときは昇格をスキップ**して非昇格のまま続行するため、デバッグ実行 (F5) では HKLM 系操作・`Get-MMAgent`・イベントログ全削除などが権限エラーになる (これは正常)。管理者系までデバッグするなら IDE 自体を管理者起動する。

### タスクバーアイコンと AUMID (回帰注意)

v1.0.11〜1.0.13 で 3 回連続修正した領域。現行方針は「**プロセスだけが AUMID を名乗り、ショートカットには何も書かない**」:

- 製品版 (`#if !DEBUG`) だけ起動直後に `WindowsElevationHelper.TrySetCurrentProcessAppUserModelId()` で Velopack の AUMID を設定する。Debug ビルドで名乗ると Windows がインストール済み製品の情報を参照して開発用 EXE のタスクバーアイコンが白紙になるため設定しない。
- ショートカット (.lnk) へ AUMID や明示アイコンを追記しない。埋め込みアイコンの解決は Windows に任せる。v1.0.12 が追記した override は起動時に [WindowsLegacyStartMenuShortcutMigrator.ClearInstalledShortcutOverrides()](src/Lumin4ti.UI/Services/WindowsLegacyStartMenuShortcutMigrator.cs) が Start メニュー・デスクトップ・タスクバーピン留めから除去する。
- .lnk のプロパティ操作が必要な場合は [WindowsShortcutPropertyStore](src/Lumin4ti.UI/Services/WindowsShortcutPropertyStore.cs) (IPropertyStore 直叩き) を使う。Velopack 1.2.0 の `ShellLink.SetAppUserModelId` は既存リンクへ Commit しないため使わない。

### ローカライズ

Komorebi/Lhamiel と同一方式。翻訳は [Resources/Locales/*.axaml](src/Lumin4ti.UI/Resources/Locales/) (`ResourceDictionary`) を 1 言語 1 ファイルで 17 言語持ち、XAML は `{DynamicResource Text.Xxx}`、C# は `App.Text("key", 日本語フォールバック, args)` で引く。`en_US.axaml` が全キーの英語マスターで、非 ja 言語はこれを `MergedDictionaries` に含めて上書きする。`ja_JP.axaml` はシェル文言のみ (Action Label/Description・カテゴリ Caption・ステータスはコード内日本語がフォールバックになる)。

- Core は翻訳キーだけ持つ (`IMaintenanceItem.LabelKey`/`DescriptionKey` = `Action.{Id}.Label`)。翻訳解決は UI 側。**UI 文字列を追加したら 17 言語すべてに同じ `x:Key` を足す** (キー集合は `en_US.axaml` と一致させる)。
- `App.SetLocale()` が辞書を差し替え、`App.LocaleChanged` を購読する VM プロパティが再評価される。既定言語は `App.DetectDefaultLocale()` が OS ロケールから判定、選択は settings.json の `Locale` に保存。
- アクションの実行結果ログ行 (`  - ...しました`) は技術的詳細なので日本語のまま。

## リリース・配信 (Cloudflare R2 + ローカル署名)

自動更新は **Cloudflare R2** 配信。クライアントは [UpdateService.cs](src/Lumin4ti.UI/Services/UpdateService.cs) で `SimpleWebSource(UpdateBaseUrl)` (= `https://lumin4ti.kagayoi.com`) の `releases.win.json` を見る (`UpdateBaseUrl`/`UpdateChannel` は [AppSettings.cs](src/Lumin4ti.Core/Models/AppSettings.cs) にハードコード)。GitHub Releases は使わない。

### 配布契約と変更権限

- 現行の配布契約は Velopack が生成する署名済みPerMachine `Lumin4ti-win.msi` と、Cloudflare R2 の `releases.win.json` による自動更新である。PerUser `Setup.exe` は公開しない。
- 不具合調査、全体レビュー、セキュリティレビュー、および「見つかったものを全部直してよい」という許可は、この現行契約内の修正に適用する。レビューで配布や昇格の設計リスクを発見しても、それを理由に対応スコープを配布方式の変更へ広げない。
- MSI / MSIX 等へのインストーラー形式変更、Velopack の置換、per-user / machine-wide のインストール範囲変更、NativeAOT 等のパッケージング方式変更、更新元・channel・署名方式の変更は、ユーザーが対象を個別に明示した場合だけ実装する。明示がない場合は現行契約を維持し、設計案と影響だけを報告する。
- `dotnet publish` によるローカル検証は通常の検証に含めてよい。`vpk pack`、署名、R2 upload、cache purge、配信確認は、リリースまたは `/vava` が明示された場合だけ実行する。

- **リリースはローカル署名リリース単独** (Windows のみ配信・CI リリース workflow なし)。SimplySign (Certum クラウド署名) は Desktop 接続 + スマホトークンが要り GitHub Actions から署名できないため。
- 実行は `pwsh scripts/release-local.ps1` (build + PerMachine MSI生成 + 署名 + R2 アップロード + キャッシュパージ + 配信確認 + 旧 nupkg 掃除を一括)。`-SkipUpload` で署名までの動作確認。**`/vava` の precheck (証明書確認) → bump → 自動実行** が [vava.config.json](vava.config.json) で配線済み。
- 旧`%LocalAppData%\Lumin4ti`版は、通常の自己昇格より前に[WindowsPerMachineMigration.cs](src/Lumin4ti.Core/Services/Windows/WindowsPerMachineMigration.cs)が固定URLのMSIを取得し、署名と発行元を検証してからPerMachine版へ移行する。設定・ログ・`%ProgramData%`の復元用バックアップは保持し、旧本体・Updater・キャッシュ・HKCUアンインストール登録・ユーザーショートカットだけを回収する。ユーザー書き込み可能な旧`Update.exe`は実行せず、`Update.exe`または`packages`だけが残った部分移行も次回の信頼済みPerMachine起動で再回収する。ショートカット削除後は`SHChangeNotify`でStartメニューの表示キャッシュを更新する。
- Velopack 1.2.0の生成MSIは`--instLocation PerMachine`でも`INSTALLFOLDER`が`TARGETDIR`直下になるため、[set-msi-program-files-location.ps1](scripts/set-msi-program-files-location.ps1)でDirectory表を`ProgramFiles64Folder\Lumin4ti`へ補正し、変更後のMSIを再署名してから検証・公開する。アプリ内移行も`VELOPACK_INSTALLDIR=<Program Files>\Lumin4ti`を明示する。補正前MSIで作られた既知の誤配置（ドライブ直下の`Lumin4ti`と`Program Files\ゆろち\Lumin4ti`）から起動した場合は、署名・MSI登録・`.msi-installed`を確認して固定MSIでProgram Filesへメジャーアップグレードし、旧プロセス終了後にその既知ルートだけを回収する。同一ProductCode/Versionで通常の`/i`が配置を変えない場合だけ`REINSTALL=ALL REINSTALLMODE=vamus`で全再配置・再キャッシュする。任意のカスタム配置は自動削除しない。
- 前提: SimplySign Desktop がログイン済み (`Cert:\CurrentUser\My` に `CN=Open Source Developer Yuichiro Shinozaki` が見える) / `<Version>` が `/vava` 済み / `C:\Users\IMT\dev\Secret\secrets.json` に `cloudflare.api_token`。
- **ランディングページ**は [web/](web/) の Cloudflare Worker (`lumin4ti-landing`)。`lumin4ti.kagayoi.com/*` に張った Worker Route が R2 カスタムドメインより優先され、`/` と `/index.html` だけ [web/index.html](web/index.html) を返し、それ以外 (更新ファイル) は R2 へ委譲する。ページ更新は `web/` で `pnpm dlx wrangler deploy` (トークンは secrets.json から env 注入・値は露出させない)。
- R2 バケット `lumin4ti-updates` (account `10901bfadbf1005164774a7350082985` / zone `kagayoi.com`)。`local-release/` は `.gitignore` 済み。

## コードレビュー時の注意点 (このコードベース特有)

- トグルの多重操作レース: `ToggleSwitch` の `IsEnabled` は `CanToggle` (= 状態既知 かつ 非実行中) にバインドすること。
- 状態表示の乖離を避ける: `GetStateAsync` はレジストリだけでなく実適用状態も見る (例: VBS トグルは bcdedit の `hypervisorlaunchtype` も照合)。部分適用を避けるため、失敗しやすいステップ (bcdedit 等) を先に実行してから残りを書く。
- 部分失敗を成功と偽らない: マルチステップ (powercfg 等) は重要ステップの失敗で `Fail` を返す。使用中ファイルのスキップのように「想定内の一部未処理」は `Partial` と結果行で伝える。
- 配布契約は [DistributionContractTests](src/Lumin4ti.Tests/DistributionContractTests.cs) が横断で固定している。`Lumin4ti.UI.csproj` / `scripts/release-local.ps1` / `scripts/set-msi-program-files-location.ps1` / `README.md` / `AppSettings.cs` を編集すると、意図せずここで落ちることがある。落ちたら文字列だけ直さず、配布方式を変えていないかを先に確認する。

## ドメイン移行（2026-07 開始・期限 2027/05/31）

屋号を **Kagayoi** に統一したため、配信ドメインを `nephilim.jp` から `kagayoi.com` へ移行中。方針の全体像はユーザーグローバルの `AGENTS.md` §屋号とドメイン を参照する。

- **旧ドメイン `nephilim.jp` はレジストラで廃止申請済みで 2027/05/31 に失効する**（延長しない）。それまでに出荷済みバイナリを新ドメインへ移行しきる。
- 旧ホストの Worker route / custom domain は**期限まで消さない**。消すと出荷済みアプリの自動更新が止まる。
- `nephilim.jp` の Redirect Rules は `/` だけを 301 する。`releases.*.json` / `*.nupkg` / `*-Setup.exe` は転送せず R2 が配信を続ける。
- 配信は `lumin4ti.kagayoi.com`（R2 `lumin4ti-updates`）。旧 `lumin4ti.nephilim.jp` は route に併記して残してある。
- アプリ名 `Lumin4ti` 自体は既存ユーザーが混乱するので改名しない。
