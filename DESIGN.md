# Lumin4ti 設計

この文書は、現在のコード・設定・テストから確認できるシステム構造と設計判断の正本である。開発時の必須コマンド、変更制約、リリース手順は [AGENTS.md](AGENTS.md) を参照する。

## 目的と対象範囲

Lumin4ti は Windows 10 / 11（64-bit）向けのメンテナンス・最適化GUIである。利用者がコマンドを直接入力せずに、Windows Update、セキュリティ、キャッシュ整理、修復、性能設定、システム設定、並べ替えをカテゴリ別に実行できるようにする。

アプリは管理者権限で動作し、レジストリ、DISM、powercfg、bcdedit、winget、Shell COM、WinRTなどを操作する。Windows APIやレジストリで直接表現できる処理はC#で実装し、OS提供ツールが正式な実行手段である場合だけ外部プロセスを使う。

## 主要コンポーネント

| コンポーネント | 責務 | 主な境界 |
| --- | --- | --- |
| `Lumin4ti.Core` | メンテナンス項目、Windows操作、設定・ログ・バックアップ、外部プロセス管理 | UIを参照しない |
| `Lumin4ti.UI` | Avalonia画面、MVVM、ローカライズ、DI、操作状態、更新UI | Coreの公開契約を呼び出す |
| `Lumin4ti.Tests` | 純粋ロジック、安全ガード、カタログ、配布契約の回帰検証 | 実レジストリ・実削除・管理者依存の書き込みは行わない |
| `scripts/` | Windows向けpublish、Velopack梱包、MSI補正、署名、R2公開 | リリース時だけ実行する |
| `../vps-web/deploy/lp-gateways/lumin4ti/` | Cloudflare内の製品ページへ中継するWorker | ページと共通CSS以外は既存のR2配信へ委譲する |

依存方向は `Lumin4ti.UI` → `Lumin4ti.Core` の一方向である。OS操作をUI層へ持ち込まず、CoreはAvalonia型を受け取らない。

## 起動と終了

通常起動は次の順序で進む。

1. ログを初期化する。スケジュール済み一時ファイル整理の引数なら、UI・昇格・多重起動ガードを経由せず実行して終了する。
2. Velopackのinstall/update hookを処理し、旧ショートカットと旧PerUser版から署名済みPerMachine版への移行を試みる。
3. デバッガ未接続の非管理者プロセスを`runas`で再起動する。昇格後に単一起動mutexを取得する。
4. Avaloniaを起動し、`App.ConfigureServices`が設定、コマンド実行、カタログ、操作コーディネーター、ViewModelをSingletonとして構成する。
5. `SettingsService`から表示言語と利用者設定を読み、`MainWindowViewModel`へカテゴリ別ViewModelを組み立てる。

終了要求時は`MaintenanceOperationCoordinator`が進行中操作へキャンセルを通知し、補償処理と状態再確認を含む`finally`の完了を待つ。設定保存キューをflushし、DIコンテナとログを閉じてからプロセスを終了する。

## メンテナンス操作のモデル

`MaintenanceActionCatalog`が画面へ公開する操作の正本であり、各項目は次のいずれかを実装する。

- `IMaintenanceAction`: 一度だけ実行するボタン操作。進捗行と`MaintenanceActionResult`を返す。
- `IMaintenanceToggle`: 現在状態を取得し、ONで適用、OFFで復元する。
- `IMaintenanceChoice`: 複数の値から選び、適用後の実値を再取得する。

`ParentId`を持つ項目は親カード内へ1段だけ配置され、親が無効な間は操作できない。カタログの並び順が画面の表示順になる。

操作データは次の順に流れる。

1. `MainWindowViewModel`がカテゴリごとの`CommandCategoryViewModel`を公開する。
2. ViewModelが`MaintenanceOperationCoordinator`からプロセス間で排他的なleaseを取得する。サインイン時クリーンアップも同じ共有ロックを取得し、GUI操作と重なった場合は実行しない。
3. CoreのAction / Toggle / ChoiceがWindows API、レジストリ、または`ICommandExecutor`を通してOSを操作する。
4. 進捗と`Success` / `Partial` / `Failure` / `Canceled`をUIへ返す。
5. トグルと選択項目は実状態を再取得し、必要な項目だけExplorerを再起動する。

GUI とサインイン時クリーンアップを含め、状態変更操作はマシン全体で同時に1件だけ実行する。これにより、複数のレジストリ変更、サービス停止、外部コマンド、Explorer再起動が競合しない。

### 未接続デバイス管理

未接続 PnP デバイスは複数項目の選択・確認・逐次削除を伴うため、`MaintenanceActionCatalog` の単一項目モデルではなく専用フローで扱う。`DeviceCleanupViewModel` が一覧、選択、二段階確認、進捗と結果集計を担当し、OS 操作はCoreの `IDisconnectedDeviceService` 境界へ委譲する。

`WindowsDisconnectedDeviceService` は SetupAPI から接続中 ID 集合とインストール済みデバイスを列挙し、その差をソフトウェア／仮想デバイスも含めてUIへ返す。削除時は利用者が選択した対象が再接続されていないか再列挙し、未接続のままならNewDevの `DiUninstallDevice` を呼ぶ。結果は削除済み、既に不在、再接続、失敗と再起動要否に正規化され、ViewModelが再列挙後の一覧と集計表示へ反映する。

## OS操作の境界

`ProcessCommandExecutor`は外部コマンド名を`SystemProcessResolver`で信頼済みの完全パスへ解決し、作業ディレクトリをSystem32へ固定する。プロセスはJob Objectへ登録し、キャンセルまたはアプリ終了時にプロセスツリーを終了する。標準出力はUTF-8を優先し、失敗時はWindowsのOEMコードページで復号する。

利用者セッションで行うShell操作は`UnelevatedCommandExecutor`へ分離する。サービス操作は`WindowsServiceControl`に集約し、元から実行中だったサービスだけを停止・再開する。

ファイル整理は既知のキャッシュ、ログ、一時領域だけを対象にする。未解決環境変数、相対パス、ドライブ直下、保護対象の基点を拒否し、ジャンクションとシンボリックリンクを辿らない。使用中ファイルは処理を継続し、許可された対象だけ`MoveFileEx`で再起動時削除を予約する。

スタートアップ登録とファイル関連付け候補はキャッシュ整理と混ぜず、専用アクションでレジストリを整理する。`StartupCommandParser` が完全パスへ解決でき、準備済み固定ドライブ上で再解析点を通らない実行ファイルの欠損を確認できた候補だけを削除する。複数の関連付け候補の一つでも存在または判定不能なら保持し、レジストリアクセス不能は `Partial` として可視化する。

## 状態と永続化

- `%APPDATA%\Lumin4ti\settings.json`: 言語、自動更新確認、除外パス、スケジュール整理項目などの利用者設定。
- `%APPDATA%\Lumin4ti\logs\`: 起動、操作、例外、移行の診断ログ。
- `%ProgramData%\Lumin4ti\backups\`: HKLMやBCDなど特権状態を戻すためのバックアップ。AdministratorsとSYSTEMだけが変更できるACLを検証する。

設定保存は一時ファイルからの置換で行い、保存要求を直列化する。既存設定の読込に失敗した場合は既定値でUIを起動するが、壊れた原本を既定値で上書きしない。

復元可能なトグルは適用直前の実値を保存し、OFFでその値へ戻す。保護バックアップのACL、所有者、パス、再解析ポイントを検証できない場合は、危険な要素を隔離して当該操作をfail closedにする。

## ローカライズ

UI文字列は`Resources/Locales/*.axaml`の17辞書で管理する。`en_US.axaml`が完全なキー集合で、各非日本語辞書は英語をmergeして上書きする。Coreは翻訳キーと日本語フォールバックだけを持ち、UIが`DynamicResource`または`App.Text`で現在言語を解決する。

## 更新と配布

更新元は`https://lumin4ti.kagayoi.com`、channelは`win`へ固定され、`settings.json`から変更できない。インストール版だけがVelopackの`releases.win.json`を参照し、開発実行では更新機構を無効として扱う。`UpdateService`は`UpdateManager`の生成と現在バージョンの取得を担当し、`VersionViewModel`が操作コーディネーターのlease内で`VelopackUpdateDialog.Avalonia`へ確認・ダウンロード・適用UIを委譲する。終了時のキャンセルもこのleaseのトークンで伝える。

配布物はVelopackが生成する署名済みPerMachine MSIである。ローカルのリリーススクリプトがrestore、self-contained publish、Velopack梱包、MSI配置補正、SimplySign署名、Cloudflare R2 upload、配信検証を順に行う。署名に対話的なSimplySign Desktopを使うため、リリースはCIではなくローカルで完結する。

Cloudflare Workerは`/`、`/index.html`と`/common.css`を`LP_CONTENT`サービスbinding経由で配信し、更新manifest、nupkg、MSIなどのパスを加工せず既存のR2配信へ委譲する。これによりWebページとVelopack配信が同じホスト名を共有する。

## 重要な不変条件

- CoreはUIを参照せず、Windows操作の正本をCoreへ置く。
- 状態変更は1件ずつ実行し、終了時はキャンセル後の補償完了を待つ。
- 外部コマンドは信頼済み完全パスとSystem32作業ディレクトリで起動する。
- ON/OFFで戻せると表示する項目は、適用前の実値を保存して復元する。
- 削除対象は再生成可能な既知領域へ限定し、リンクと未解決パスを拒否する。
- スタートアップ登録と関連付け候補は、準備済み固定ドライブ上で実行ファイルの欠損を確定できた場合だけ削除する。
- 複数段階の操作は重要ステップの失敗を成功扱いせず、想定内の未処理は`Partial`として可視化する。
- UIは変更後の状態を推測せず、OSから再取得した値を表示する。
- 未接続デバイスはソフトウェア／仮想デバイスも表示し、利用者の選択と二段階確認を経て、削除直前にも接続状態を確認する。
- 配布形式、インストール範囲、更新元、channel、署名方式は配布契約テストとリリーススクリプトで一致させる。

## 採用済み設計判断

| 判断 | 理由 | トレードオフ |
| --- | --- | --- |
| 巨大バッチではなくC#とWindows APIへ移植する | 入力、結果、キャンセル、復元、安全境界を型として検証できる | OS固有APIの実装とテスト用抽象化が増える |
| `asInvoker` manifestから自己昇格する | Velopack hookと旧版移行を昇格前に安全な順序で処理できる | 起動時にプロセスを再生成する必要がある |
| カタログ駆動でAction / Toggle / Choiceを共通表示する | 機能追加時のUI分岐を抑え、状態・結果処理を統一できる | 特殊機能も共通契約へ適合させる必要がある |
| 手動DIをSingletonで構成する | デスクトップアプリの単一Window・共有操作状態を単純に保てる | componentごとの短いlifetimeは使わない |
| 更新元とchannelをコードで固定する | 設定改ざんによる第三者ホストへの誘導を防ぐ | 利用者が任意mirrorへ切り替えることはできない |
| PerMachine MSIとローカル署名を採用する | 管理者ツールをユーザー書込み可能領域から実行せず、対話署名を維持できる | リリースは署名環境のあるWindows端末へ依存する |

## 製品ページの配信先

製品ページの配信HTMLは `../vps-web/lp/lumin4ti/`（編集元は `../vps-web/tools/lp/templates/`）。Cloudflareの `vps-web-lp` サービスがStatic Assetsとして提供する。
Cloudflare側の中継設定は `../vps-web/deploy/lp-gateways/lumin4ti/` に置く。
公開URLと既存のR2・ライセンス通信を維持し、配信は `vps-web/deploy/deploy-lp.ps1` へ統一する。
