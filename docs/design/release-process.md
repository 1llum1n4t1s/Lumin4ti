# 更新と配布

[DESIGN.md](../../DESIGN.md) から分割した詳細。

## 更新と配布

更新元は`https://lumin4ti.kagayoi.com`、channelは`win`へ固定され、`settings.json`から変更できない。インストール版だけがVelopackの`releases.win.json`を参照し、開発実行では更新機構を無効として扱う。`UpdateService`は`UpdateManager`の生成と現在バージョンの取得を担当し、`VersionViewModel`が操作コーディネーターのlease内で`VelopackUpdateDialog.Avalonia`へ確認・ダウンロード・適用UIを委譲する。終了時のキャンセルもこのleaseのトークンで伝える。

配布物はVelopackが生成する署名済みPerMachine `Lumin4ti-win.msi`である。対象は`win-x64`のみで、restore、self-contained publish、`vpk pack --runtime`へ同じRIDを渡す。publishだけでなく梱包時も明示することで、実行ファイルと配布パッケージの対象環境を一致させる。更新channelの`win`はRIDとは別の識別子であり、manifest名を維持する。ARM64向けパッケージは配信しない。

## リリース成果物と公開順序

リリーススクリプト（通常は版タグ`v<x.y.z>`のpushで起動する`release.yml`がGitHub Actionsで実行し、ローカルでも同じ手順で動く）がrestore、self-contained publish、Velopack梱包と署名、MSI配置補正と再署名、公開前検証、Cloudflare R2 uploadを行う。`set-msi-program-files-location.ps1`がMSIのDirectory表を`ProgramFiles64Folder\Lumin4ti`へ補正し、固定配置を保証する。補正で元の署名が無効になるため、公開前に`sigilune sign --replace`で再署名し、発行元を検証する。署名はSigiluneがCertumのクラウド鍵をsigntool `/dlib`で使って行い、SimplySign Desktopを必要としないため、CIで完結できる。署名者のCNは従来と同じ証明書であり、アプリの実行時検証（`ExpectedPublisher`）は変わらない。

準備時に再生成するのは`local-release/artifacts`と対象RIDの`publish-*`だけで、同じ親ディレクトリの検証記録は保持する。清掃対象がその直下にあることと、再解析ポイントを含まないことを確認してから削除する。

公開前には固定MSIと対象channelのmanifestを確認し、MSIの`ProductVersion`が対象版（末尾`.0`も許容）と一致することを検証する。manifestの各assetはローカル実体の存在、Package ID、版、サイズ、SHA256を照合する。参照するnupkgは実行せずに読み、nuspecのIDと版、必須アプリファイル、depsの`win-x64`指定、GUIと起動時再試行ヘルパー両方のapphostのx64 PEヘッダーを検証する。両者のruntimeTarget、Core依存と同梱.NETランタイムの版も照合し、ヘルパーを含むPEファイルを署名対象とし、署名後のnupkgから主要なPEを作業領域へ取り出して署名と版を確認する。これにより、署名が有効でも版や構成の異なる成果物を公開することを防ぐ。`-SkipUpload`もこの検証を終えてから終了する。

公開はassetのアップロード受付成功を確認してから、`releases.win.json`を最後に更新する。これを公開の確定点とすることで、途中失敗時も旧manifestの更新経路を維持する。固定名の配布物は全URLのキャッシュをパージし、書込みとパージAPIの受付成功で公開を判定する。公開後の配信内容取得・サイズやハッシュの再照合・CDN伝播待ちは行わない。

旧assetの清掃では、現manifestと更新直前manifestの参照先、直近2バージョン、バージョン文字列を含まない固定名を保持する。旧manifestを取得できない場合は清掃を省略する。清掃の一部失敗は公開済みのリリースを巻き戻さないが、完了表示は全工程成功と区別して警告する。PerUser `Setup.exe`は新規公開せず、MSIのアップロードと固定URLのパージの受付成功後に旧Setupを回収する。配布契約テストが製品参照、リリーススクリプト、配置補正、利用者向け導線、更新設定の整合性を固定する。

## 旧配置からの移行

旧 PerUser 版から信頼済み PerMachine 版への移行と既知の誤配置の修復は、署名と発行元を確認した MSI だけで行い、設定・ログ・保護バックアップを保持する。取得期限、再配置、回収範囲は [section-01.md](section-01.md) にまとめる。

## 製品ページと更新ファイルの経路

Cloudflare Workerは`/`、`/index.html`と`/common.css`を`LP_CONTENT`サービスbinding経由で配信し、更新manifest、nupkg、MSIなどのパスを加工せず既存のR2配信へ委譲する。これによりWebページとVelopack配信が同じホスト名を共有する。

## 製品ページの配信先

製品ページの配信HTMLは `../vps-web/lp/lumin4ti/`（編集元は `../vps-web/tools/lp/templates/`）。Cloudflareの `vps-web-lp` サービスがStatic Assetsとして提供する。
Cloudflare側の中継設定は `../vps-web/deploy/lp-gateways/lumin4ti/` に置く。
公開URLと既存のR2更新経路を維持し、配信は `../vps-web/deploy/deploy-lp.ps1` へ統一する。
