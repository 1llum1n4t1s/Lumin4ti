# リリース・配信 (Cloudflare R2 + Sigilune 署名)

[AGENTS.md](../../AGENTS.md) から分割した詳細。

更新・移行・配信の構造と理由は [DESIGN.md の更新と配布](../design/release-process.md#更新と配布) を参照する。GitHub Releases は使わない。

## 配布契約と変更権限

- 現行の配布契約は [DESIGN.md](../design/release-process.md#更新と配布) に記載する。`win-x64` のみを対象とし、restore / publish の RID と `vpk pack --runtime` を一致させる。`--channel win` は維持し、runtime と channel を混同しない。PerUser `Setup.exe` は公開しない。
- 不具合調査、全体レビュー、セキュリティレビュー、および「見つかったものを全部直してよい」という許可は、この現行契約内の修正に適用する。レビューで配布や昇格の設計リスクを発見しても、それを理由に対応スコープを配布方式の変更へ広げない。
- MSI / MSIX 等へのインストーラー形式変更、Velopack の置換、per-user / machine-wide のインストール範囲変更、NativeAOT 等のパッケージング方式変更、更新元・channel・署名方式の変更は、ユーザーが対象を個別に明示した場合だけ実装する。明示がない場合は現行契約を維持し、設計案と影響だけを報告する。
- `dotnet publish` によるローカル検証は通常の検証に含めてよい。`vpk pack`、署名、R2 upload、cache purge は、リリースまたは `/vava` が明示された場合だけ実行する。公開後の配信内容取得・ハッシュ照合・伝播待ちは行わず、公開前検証と API の受付成功を確認する。

- 通常は `.github/workflows/release.yml`（版タグ `v<x.y.z>` の push で自動起動・Environment `release`）が同じスクリプトを実行する。ローカル実行は `pwsh scripts/release-local.ps1` (build + PerMachine MSI生成 + 署名 + R2 アップロード + キャッシュパージ + 旧 nupkg 掃除を一括)。`-SkipUpload` では署名後の公開前検証まで行う。検証内容は [DESIGN.md のリリース成果物と公開順序](../design/release-process.md#リリース成果物と公開順序) を参照し、検証失敗時は公開へ進めない。
- [set-msi-program-files-location.ps1](../../scripts/set-msi-program-files-location.ps1) による Program Files 配置補正と、補正後 MSI の再署名（`sigilune sign --replace`）・署名検証を省略しない。
- 移行コードを変更するときは [DESIGN.md の旧配置からの移行](../design/release-process.md#旧配置からの移行) の信頼確認・回収範囲・期限を維持し、`WindowsPerMachineMigrationTests` と操作フロー検証を通す。旧 `Update.exe` を実行せず、設定・ログ・復元原本と任意のカスタム配置を回収対象へ広げない。
- リリース準備の清掃は今回再生成する `local-release/artifacts` と対象 RID の `publish-*` に限定し、検証記録を保持する。公開順序と受付成功の確認は [DESIGN.md](../design/release-process.md#リリース成果物と公開順序) に従う。
- 前提: 署名は Sigilune（`scripts/Sigilune.Common.ps1`、署名者 `CN=Open Source Developer Yuichiro Shinozaki`。SimplySign Desktop は不要）。CI は Environment `release` の `SIGILUNE_EMAIL` / `SIGILUNE_TOTP_URI` / `CLOUDFLARE_API_TOKEN`、ローカルは `sigilune setup` 済みで `D:\Dev\Secret\secrets.json` に `cloudflare.api_token` / `<Version>` が `/vava` 済み。
- 製品ページの配信は `../vps-web/deploy/deploy-lp.ps1` を使う。配信先は [DESIGN.md](../design/release-process.md#製品ページの配信先) を参照し、公開ホスト・更新ファイルの既存経路を維持する。
- R2 バケット `lumin4ti-updates` (account `10901bfadbf1005164774a7350082985` / zone `kagayoi.com`)。`local-release/` は `.gitignore` 済み。

## ローカルリリースの実行

タグ起動と `guard` の条件、再実行、ローカル実行のコマンドは [release-process.md](release-process.md) に従う。Sigilune のログインに失敗したら再試行せず中止し、署名を省略しない。
