# AGENTS.md

This file provides guidance to Codex (ChatGPT) and other coding agents working in this repository.

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
- **バージョン (`Directory.Build.props` の `<Version>`) は `/vava` 経由でのみ更新**。コード修正のついでに触らない。

## アーキテクチャ

3 プロジェクト構成 (Avalonia MVVM + CommunityToolkit.Mvvm + Microsoft.Extensions.DI + Velopack 自動更新)。

- **`Lumin4ti.Core`** — OS 操作ロジック。UI を一切参照しない (依存は一方向 UI→Core)。
- **`Lumin4ti.UI`** — Avalonia。`App.axaml.cs` の `ConfigureServices` で手動 DI (全 Singleton)。
- **`Lumin4ti.Tests`** — MSTest。純粋関数・パーサ・カタログ整合性を検証 (レジストリ/COM/管理者依存の実書き込みはテストしない)。

### メンテナンス項目の中核 (最重要)

すべての「機能」は `IMaintenanceItem` を軸にした 2 型 ([IMaintenanceAction.cs](src/Lumin4ti.Core/Interfaces/IMaintenanceAction.cs)):

- **`IMaintenanceAction`** — 「実行」ボタン型 (1 回実行)。`ExecuteAsync(IProgress<string>?, ct)` でライブ進捗を UI へ流せる。
- **`IMaintenanceToggle`** — ON/OFF トグル型。**ON = 最適化を適用 / OFF = Windows 既定に戻す** で統一 (例外は MMAgent 系トグルのみ ON = 機能有効)。`GetStateAsync`/`SetStateAsync`。

新機能を足すときは Actions 配下にクラスを作り、**[MaintenanceActionCatalog.cs](src/Lumin4ti.Core/Services/Windows/MaintenanceActionCatalog.cs) の `Items` に登録するだけ**で UI に現れる。カタログの並び順が画面の表示順。単純なレジストリ tweak は個別クラスを作らず汎用の `RegistryToggle` にスペックを渡す。

### 外部プロセス実行

`ICommandExecutor` → `ProcessCommandExecutor` が唯一の実装 (DI で単一登録)。DISM/regsvr32/powercfg/winget/bcdedit/MMAgent cmdlet など「OS 提供ツールが唯一の手段」のものだけ外部プロセスで実行し、レジストリ・COM・WinRT で代替可能なものは C# ネイティブで書く。

- **セキュリティ (回帰厳禁)**: bare exe 名を渡すと `CreateProcess` の検索順序でインストールディレクトリが `System32` より先に照合され、昇格プロセスがバイナリプランティング LPE を踏む。`ProcessCommandExecutor` は [SystemProcessResolver](src/Lumin4ti.Core/Services/SystemProcessResolver.cs) でフルパス解決 + `WorkingDirectory=System32` 固定してこれを封じている。呼び出し側は論理名でよいが、この解決を外さないこと。
- 子プロセスは [ProcessJobTracker](src/Lumin4ti.Core/Services/ProcessJobTracker.cs) の Job Object (KILL_ON_JOB_CLOSE) に紐付け、アプリ終了時に OS が孤児を kill する。`ct` キャンセル時はプロセスツリーごと kill。
- 出力は UTF-8 → OEM (CP932) の順で自動デコード。長時間コマンドの進捗は `\r`/`\n` 区切りで `IProgress<string>` 通知。

### 破壊的操作の復元性

「OFF で Windows 既定に戻す」を謳う以上、ハードコード既定値でなく**ユーザーの元の値**へ戻す。`RegistryToggle` は ON 適用前に [RegistryValueBackup](src/Lumin4ti.Core/Services/Windows/Actions/RegistryValueBackup.cs) で `%APPDATA%\Lumin4ti\backups\` にスナップショットし、OFF で復元 (UWP・Defender も同様のバックアップを持つ)。不可逆操作を足すときは同様のバックアップを検討する。

### 昇格とデバッグ起動

[Program.cs](src/Lumin4ti.UI/Program.cs) で `VelopackApp.Build().Run()` → 非管理者なら自己昇格 (ShellExecute + runas) → `SingleInstanceGuard`。**`Debugger.IsAttached` のときは昇格をスキップ**して非昇格のまま続行するため、デバッグ実行 (F5) では HKLM 系操作・`Get-MMAgent`・イベントログ全削除などが権限エラーになる (これは正常)。管理者系までデバッグするなら IDE 自体を管理者起動する。

### ローカライズ

Komorebi/Lhamiel と同一方式。翻訳は [Resources/Locales/*.axaml](src/Lumin4ti.UI/Resources/Locales/) (`ResourceDictionary`) を 1 言語 1 ファイルで 17 言語持ち、XAML は `{DynamicResource Text.Xxx}`、C# は `App.Text("key", 日本語フォールバック, args)` で引く。`en_US.axaml` が全キーの英語マスターで、非 ja 言語はこれを `MergedDictionaries` に含めて上書きする。`ja_JP.axaml` はシェル文言のみ (Action Label/Description・カテゴリ Caption・ステータスはコード内日本語がフォールバックになる)。

- Core は翻訳キーだけ持つ (`IMaintenanceItem.LabelKey`/`DescriptionKey` = `Action.{Id}.Label`)。翻訳解決は UI 側。**UI 文字列を追加したら 17 言語すべてに同じ `x:Key` を足す** (キー集合は `en_US.axaml` と一致させる)。
- `App.SetLocale()` が辞書を差し替え、`App.LocaleChanged` を購読する VM プロパティが再評価される。既定言語は `App.DetectDefaultLocale()` が OS ロケールから判定、選択は settings.json の `Locale` に保存。
- アクションの実行結果ログ行 (`  - ...しました`) は技術的詳細なので日本語のまま。

## リリース・配信 (Cloudflare R2 + ローカル署名)

自動更新は **Cloudflare R2** 配信。クライアントは [UpdateService.cs](src/Lumin4ti.UI/Services/UpdateService.cs) で `SimpleWebSource(UpdateBaseUrl)` (= `https://lumin4ti.nephilim.jp`) の `releases.win.json` を見る (`UpdateBaseUrl`/`UpdateChannel` は [AppSettings.cs](src/Lumin4ti.Core/Models/AppSettings.cs) にハードコード)。GitHub Releases は使わない。

### 配布契約と変更権限

- 現行の配布契約は Velopack が生成する `Lumin4ti-win-Setup.exe` と、Cloudflare R2 の `releases.win.json` による自動更新である。
- 不具合調査、全体レビュー、セキュリティレビュー、および「見つかったものを全部直してよい」という許可は、この現行契約内の修正に適用する。レビューで配布や昇格の設計リスクを発見しても、それを理由に対応スコープを配布方式の変更へ広げない。
- MSI / MSIX 等へのインストーラー形式変更、Velopack の置換、per-user / machine-wide のインストール範囲変更、NativeAOT 等のパッケージング方式変更、更新元・channel・署名方式の変更は、ユーザーが対象を個別に明示した場合だけ実装する。明示がない場合は現行契約を維持し、設計案と影響だけを報告する。
- `dotnet publish` によるローカル検証は通常の検証に含めてよい。`vpk pack`、署名、R2 upload、cache purge、配信確認は、リリースまたは `/vava` が明示された場合だけ実行する。

- **リリースはローカル署名リリース単独** (Windows のみ配信・CI リリース workflow なし)。SimplySign (Certum クラウド署名) は Desktop 接続 + スマホトークンが要り GitHub Actions から署名できないため。
- 実行は `pwsh scripts/release-local.ps1` (build + 署名 + R2 アップロード + キャッシュパージ + 配信確認 + 旧 nupkg 掃除を一括)。`-SkipUpload` で署名までの動作確認。**`/vava` の precheck (証明書確認) → bump → 自動実行** が [vava.config.json](vava.config.json) で配線済み。
- 前提: SimplySign Desktop がログイン済み (`Cert:\CurrentUser\My` に `CN=Open Source Developer Yuichiro Shinozaki` が見える) / `<Version>` が `/vava` 済み / `C:\Users\IMT\dev\Secret\secrets.json` に `cloudflare.api_token`。
- **ランディングページ**は [web/](web/) の Cloudflare Worker (`lumin4ti-landing`)。`lumin4ti.nephilim.jp/*` に張った Worker Route が R2 カスタムドメインより優先され、`/` と `/index.html` だけ [web/index.html](web/index.html) を返し、それ以外 (更新ファイル) は R2 へ委譲する。ページ更新は `web/` で `pnpm dlx wrangler deploy` (トークンは secrets.json から env 注入・値は露出させない)。
- R2 バケット `lumin4ti-updates` (account `10901bfadbf1005164774a7350082985` / zone `nephilim.jp`)。`local-release/` は `.gitignore` 済み。

## コードレビュー時の注意点 (このコードベース特有)

- トグルの多重操作レース: `ToggleSwitch` の `IsEnabled` は `CanToggle` (= 状態既知 かつ 非実行中) にバインドすること。
- 状態表示の乖離を避ける: `GetStateAsync` はレジストリだけでなく実適用状態も見る (例: VBS トグルは bcdedit の `hypervisorlaunchtype` も照合)。部分適用を避けるため、失敗しやすいステップ (bcdedit 等) を先に実行してから残りを書く。
- 部分失敗を成功と偽らない: マルチステップ (powercfg 等) は重要ステップの失敗で `Fail` を返す。
