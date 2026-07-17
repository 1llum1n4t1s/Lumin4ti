# release-local.ps1 — ローカル署名付き Velopack リリース (Shisui テンプレートから横展開)
#
# SimplySign (Certum クラウド署名) は Desktop 接続 + スマホトークンが必要で
# GitHub Actions からは署名できないため、リリースは本スクリプトでローカル実行する。
#
# 前提:
#   - SimplySign Desktop が接続済み (証明書が CurrentUser\My に見えていること)
#   - Directory.Build.props の <Version> がリリースしたいバージョンになっていること (/vava 済み)
#   - C:\Users\IMT\dev\Secret\secrets.json に cloudflare.api_token があること
#
# 使い方:
#   pwsh scripts/release-local.ps1                # フルリリース (build + sign + upload + cleanup)
#   pwsh scripts/release-local.ps1 -SkipUpload    # ビルド + 署名のみ (アップロードしない動作確認用)

[CmdletBinding()]
param(
    [switch]$SkipUpload,
    [string[]]$Runtimes = @('win-x64')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---- 定数 ----
# リリースの再現性を保つため、vpk はリポジトリ内で明示的に固定する。
# 更新時は公式 NuGet の安定版を確認し、-SkipUpload で署名成果物を検証してから変更する。
$VpkVersion = '1.2.0'
Write-Host "vpk 固定バージョン: $VpkVersion"
$WranglerVersion = '4.92.0'         # サプライチェーン対策でバージョン固定
$Bucket = 'lumin4ti-updates'
$BaseUrl = 'https://lumin4ti.nephilim.jp'
$AccountId = '10901bfadbf1005164774a7350082985'
$NupkgRetentionGraceDays = 30
$SecretsPath = 'C:\Users\IMT\dev\Secret\secrets.json'
$CertSubjectName = 'Open Source Developer Yuichiro Shinozaki'
# /n (Subject 名) で選択: 証明書の年次更新で thumbprint が変わっても動く
$SignParams = "/n `"$CertSubjectName`" /fd SHA256 /td SHA256 /tr http://time.certum.pl"

# Lumin4ti は win-x64 のみ配信 (ARM64 配信なし)。
$RuntimeMatrix = @{
    'win-x64' = @{ Channel = 'win' }
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot
$WorkDir = Join-Path $RepoRoot 'local-release'
$ArtifactsDir = Join-Path $WorkDir 'artifacts'

function Invoke-Native {
    param([string]$Description, [scriptblock]$Block)
    & $Block
    if ($LASTEXITCODE -ne 0) { throw "$Description が失敗しました (exit $LASTEXITCODE)" }
}

function Publish-R2Object {
    param([System.IO.FileInfo]$File)

    Write-Host "  ↑ $($File.Name)"
    Invoke-Native "R2 put ($($File.Name))" {
        pnpm dlx "wrangler@$WranglerVersion" r2 object put "$Bucket/$($File.Name)" --file $File.FullName --remote
    }
}

function Wait-RemoteAsset {
    param(
        [System.IO.FileInfo]$File,
        [string]$BaseUrl,
        [int]$MaxAttempts = 18
    )

    $encodedName = [uri]::EscapeDataString($File.Name)
    $assetUrl = "$BaseUrl/$encodedName"
    $lastReason = '応答なし'

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            $requestUrl = "${assetUrl}?_=$([Guid]::NewGuid().ToString('N'))"
            $response = Invoke-WebRequest -Method Head -Uri $requestUrl `
                -Headers @{ 'Cache-Control' = 'no-cache' } -TimeoutSec 30
            $contentLengths = @($response.Headers['Content-Length'])
            $remoteLength = if ($contentLengths.Count -eq 1) { [long]$contentLengths[0] } else { -1 }
            if ([int]$response.StatusCode -eq 200 -and $remoteLength -eq $File.Length) {
                Write-Host "  ✅ $($File.Name): HTTP 200 / $remoteLength bytes (attempt $attempt)"
                return
            }

            $lastReason = "HTTP $([int]$response.StatusCode) / Content-Length=$remoteLength (期待値 $($File.Length))"
        }
        catch {
            $lastReason = $_.Exception.Message
        }

        if ($attempt -lt $MaxAttempts) {
            Write-Host "  ⚠️ $($File.Name) をまだ取得確認できません (attempt $attempt / $MaxAttempts)、5 秒待機..."
            Start-Sleep -Seconds 5
        }
    }

    throw "公開 asset を取得確認できないため manifest を公開しません: $($File.Name) — $lastReason"
}

# ---- 0. プリフライト ----
Write-Host '== プリフライト ==' -ForegroundColor Cyan

# Git Bash (MSYS) 経由で起動すると括弧入り環境変数が落ちて vswhere.exe 解決等が
# 壊れることがあるため補完する (非 AOT でも実害なしの保険)
if (-not ${env:ProgramFiles(x86)}) { ${env:ProgramFiles(x86)} = 'C:\Program Files (x86)' }
$vsInstallerDir = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ($env:PATH -notlike "*$vsInstallerDir*") { $env:PATH = "$env:PATH;$vsInstallerDir" }

# vpk (dotnet tool) は .NET 9 ランタイム要求だがローカルは 10 のみ → ロールフォワード
$env:DOTNET_ROLL_FORWARD = 'Major'

# XPath で取得 (member enumeration は Version を持たない PropertyGroup 混在時に StrictMode で throw する)
$versionNode = ([xml](Get-Content 'Directory.Build.props' -Raw)).SelectSingleNode('/Project/PropertyGroup/Version')
$version = if ($versionNode) { $versionNode.InnerText.Trim() } else { $null }
if (-not $version) { throw 'Directory.Build.props から <Version> を取得できませんでした' }
Write-Host "バージョン: $version"

# SimplySign 接続確認 (証明書が見えなければ署名できないので最初に落とす)
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -like "CN=$CertSubjectName*" -and $_.NotAfter -gt (Get-Date) }
if (-not $cert) {
    throw "署名証明書 (CN=$CertSubjectName) が見つかりません。SimplySign Desktop を起動してトークンでログインしてください。"
}
Write-Host "署名証明書: $($cert.Subject) (期限 $($cert.NotAfter.ToString('yyyy-MM-dd')))"

# vpk を固定バージョンで用意
$vpkInstalled = (dotnet tool list --global | Select-String -SimpleMatch 'vpk') -match [regex]::Escape($VpkVersion)
if (-not $vpkInstalled) {
    Write-Host "vpk $VpkVersion をインストールします..."
    dotnet tool uninstall --global vpk 2>$null | Out-Null
    Invoke-Native 'vpk のインストール' { dotnet tool install --global vpk --version $VpkVersion }
}

# Cloudflare トークン (アップロード時のみ必要)
if (-not $SkipUpload) {
    $secrets = Get-Content $SecretsPath -Raw | ConvertFrom-Json
    if (-not $secrets.cloudflare.api_token) { throw "secrets.json に cloudflare.api_token が見つかりません" }
    $env:CLOUDFLARE_API_TOKEN = $secrets.cloudflare.api_token
    $env:CLOUDFLARE_ACCOUNT_ID = $AccountId
}

if (Test-Path $WorkDir) { Remove-Item $WorkDir -Recurse -Force }
New-Item -ItemType Directory -Path $ArtifactsDir -Force | Out-Null

# ---- 1. ビルド + 署名付きパッケージング (RID ごと) ----
foreach ($runtime in $Runtimes) {
    $config = $RuntimeMatrix[$runtime]
    if (-not $config) { throw "未知の runtime: $runtime" }
    $publishDir = Join-Path $WorkDir "publish-$runtime"

    # restore と publish のパラメータを完全一致させる (self-contained を restore にも渡さないと
    # runtime packs が assets.json に入らず NETSDK1112 が出る)。
    Write-Host "== restore: $runtime ==" -ForegroundColor Cyan
    Invoke-Native "dotnet restore ($runtime)" {
        dotnet restore src/Lumin4ti.UI/Lumin4ti.UI.csproj -r $runtime --force-evaluate -p:SelfContained=true
    }

    Write-Host "== publish: $runtime ==" -ForegroundColor Cyan
    Invoke-Native "dotnet publish ($runtime)" {
        dotnet publish src/Lumin4ti.UI/Lumin4ti.UI.csproj -c Release -r $runtime `
            --self-contained true --no-restore -o $publishDir
    }

    if (-not (Test-Path (Join-Path $publishDir 'Lumin4ti.UI.exe'))) {
        throw "Lumin4ti.UI.exe が publish 出力にありません ($runtime)"
    }

    Write-Host "== vpk pack + 署名: $runtime ==" -ForegroundColor Cyan
    # StartMenu は packAuthors 名のサブフォルダを作るため、ルート直下へ登録する。
    Invoke-Native "vpk pack ($runtime)" {
        vpk pack `
            --packId Lumin4ti `
            --packVersion $version `
            --packTitle 'Lumin4ti' `
            --packAuthors 'ゆろち' `
            --mainExe Lumin4ti.UI.exe `
            --icon (Join-Path 'src' 'Lumin4ti.UI' 'icon' 'app.ico') `
            --packDir $publishDir `
            --outputDir $ArtifactsDir `
            --channel $config.Channel `
            --shortcuts 'StartMenuRoot,Desktop' `
            --signParams $SignParams
    }
}

# 署名検証 (Setup.exe が正しく署名されているかリリース前に確認)
Write-Host '== 署名検証 ==' -ForegroundColor Cyan
foreach ($exe in Get-ChildItem $ArtifactsDir -Filter '*.exe') {
    $sig = Get-AuthenticodeSignature $exe.FullName
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notlike "CN=$CertSubjectName*") {
        throw "署名検証失敗: $($exe.Name) → $($sig.Status)"
    }
    Write-Host "  ✅ $($exe.Name): Valid ($($sig.SignerCertificate.Subject -replace ',.*$'))"
}

if ($SkipUpload) {
    Write-Host "`n✅ -SkipUpload 指定のためここで終了。成果物: $ArtifactsDir" -ForegroundColor Green
    Get-ChildItem $ArtifactsDir | Format-Table Name, @{n='Size(MB)'; e={[math]::Round($_.Length/1MB,1)}}
    return
}

# ---- 2. R2 アップロード (asset → 取得検証 → manifest commit point) ----
$artifacts = @(Get-ChildItem $ArtifactsDir -File)
$manifestFiles = @($artifacts | Where-Object { $_.Name -like 'releases.*.json' } | Sort-Object Name)
if ($manifestFiles.Count -eq 0) { throw '公開 commit point となる releases.*.json が見つかりません' }

$expectedManifestNames = @($Runtimes | ForEach-Object {
    $channel = $RuntimeMatrix[$_].Channel
    "releases.$channel.json"
})
foreach ($expectedManifestName in $expectedManifestNames) {
    if ($expectedManifestName -notin $manifestFiles.Name) {
        throw "必要な manifest が artifacts にありません: $expectedManifestName"
    }
}

$unexpectedManifests = @($manifestFiles | Where-Object { $_.Name -notin $expectedManifestNames })
if ($unexpectedManifests.Count -gt 0) {
    throw "対象外 channel の manifest が artifacts にあります: $($unexpectedManifests.Name -join ', ')"
}

# manifest 更新直前の世代は、経路上のキャッシュや既に更新確認を済ませたクライアントが
# 参照している可能性があるため必ず保持する。取得に失敗した場合は cleanup 自体を止める。
$previousReleaseAssets = @{}
$previousManifestReadSucceeded = $true
Write-Host '== 旧 manifest 保持対象の取得 ==' -ForegroundColor Cyan
foreach ($manifestName in $expectedManifestNames) {
    $manifestUrl = "$BaseUrl/$manifestName"
    try {
        $response = Invoke-WebRequest -Uri "${manifestUrl}?_=$([Guid]::NewGuid().ToString('N'))" `
            -Headers @{ 'Cache-Control' = 'no-cache' } -TimeoutSec 30
        $raw = $response.Content
        if ($raw -is [byte[]]) { $raw = [System.Text.Encoding]::UTF8.GetString($raw) }
        $previousManifest = $raw | ConvertFrom-Json
        $assetsProperty = $previousManifest.PSObject.Properties['Assets']
        if (-not $assetsProperty -or @($assetsProperty.Value).Count -eq 0) {
            throw "Assets が空です: $manifestUrl"
        }
        foreach ($asset in @($assetsProperty.Value)) {
            if ($asset.FileName) { $previousReleaseAssets[[string]$asset.FileName] = $true }
        }
        Write-Host "  ✅ $manifestName : $(@($assetsProperty.Value).Count) asset"
    }
    catch {
        $responseProperty = $_.Exception.PSObject.Properties['Response']
        $responseValue = if ($responseProperty) { $responseProperty.Value } else { $null }
        $statusProperty = if ($responseValue) { $responseValue.PSObject.Properties['StatusCode'] } else { $null }
        $statusCode = if ($statusProperty) { [int]$statusProperty.Value } else { 0 }
        if ($statusCode -eq 404) {
            # 初回公開か一時的な欠落かを安全に区別できない。初回は削除対象も無いため、
            # どちらの場合も cleanup を止めるのが安全側。
            $previousManifestReadSucceeded = $false
            Write-Warning "旧 manifest が 404 のため、この実行では旧 nupkg を削除しません: $manifestName"
            continue
        }

        $previousManifestReadSucceeded = $false
        Write-Warning "旧 manifest を取得できないため、この実行では旧 nupkg を削除しません: $manifestName — $($_.Exception.Message)"
    }
}

$preCommitFiles = @($artifacts | Where-Object { $_.Name -notlike 'releases.*.json' } | Sort-Object Name)
if ($preCommitFiles.Count -eq 0) { throw 'manifest より先に公開する asset がありません' }

$artifactByName = @{}
foreach ($artifact in $artifacts) { $artifactByName[$artifact.Name] = $artifact }
$referencedAssets = @{}
foreach ($manifestFile in $manifestFiles) {
    $manifest = Get-Content $manifestFile.FullName -Raw | ConvertFrom-Json
    $assetsProperty = $manifest.PSObject.Properties['Assets']
    if (-not $assetsProperty -or @($assetsProperty.Value).Count -eq 0) {
        throw "manifest に asset がありません: $($manifestFile.Name)"
    }

    foreach ($asset in @($assetsProperty.Value)) {
        $fileNameProperty = $asset.PSObject.Properties['FileName']
        $fileName = if ($fileNameProperty) { [string]$fileNameProperty.Value } else { '' }
        if ([string]::IsNullOrWhiteSpace($fileName) -or -not $artifactByName.ContainsKey($fileName)) {
            throw "manifest がローカルに存在しない asset を参照しています: $($manifestFile.Name) → $fileName"
        }

        $assetFile = $artifactByName[$fileName]
        $sizeProperty = $asset.PSObject.Properties['Size']
        if ($sizeProperty -and [long]$sizeProperty.Value -ne $assetFile.Length) {
            throw "manifest とローカル asset のサイズが一致しません: $fileName"
        }

        $referencedAssets[$fileName] = $assetFile
    }
}

Write-Host '== R2 asset アップロード ==' -ForegroundColor Cyan
$uploaded = 0
foreach ($file in $preCommitFiles) {
    Publish-R2Object $file
    $uploaded++
}
Write-Host "✅ R2 asset アップロード完了: $uploaded ファイル"

# releases.*.json が参照する不変 asset を公開 URL から取得でき、サイズも一致するまで
# commit point は更新しない。途中失敗時は旧 manifest がそのまま有効なので更新経路を壊さない。
Write-Host '== 公開 asset 取得検証 ==' -ForegroundColor Cyan
foreach ($assetFile in @($referencedAssets.Values | Sort-Object Name)) {
    Wait-RemoteAsset -File $assetFile -BaseUrl $BaseUrl
}

# SimpleWebSource が読む releases.{channel}.json を最後に公開し、ここをリリースの commit point とする。
Write-Host '== manifest commit point 公開 ==' -ForegroundColor Cyan
foreach ($manifestFile in $manifestFiles) {
    Publish-R2Object $manifestFile
    $uploaded++
}
Write-Host "✅ R2 全アップロード完了: $uploaded ファイル (manifest は最後に公開)"

# ---- 2.5 Cloudflare エッジキャッシュのパージ ----
# 固定名ファイル (Setup.exe / Portable.zip / RELEASES / releases.*.json / assets.*.json) は
# 毎リリースで中身が変わるのに URL が不変。パージしないと自動更新が旧版を掴む。
Write-Host '== Cloudflare キャッシュパージ ==' -ForegroundColor Cyan
$cfHeaders = @{ Authorization = "Bearer $($env:CLOUDFLARE_API_TOKEN)" }
$zoneName = ([uri]$BaseUrl).Host -replace '^[^.]+\.', ''   # <sub>.nephilim.jp → nephilim.jp (apex)
$zoneResp = Invoke-RestMethod -Uri "https://api.cloudflare.com/client/v4/zones?name=$zoneName" -Headers $cfHeaders -TimeoutSec 30
if (-not $zoneResp.success -or @($zoneResp.result).Count -eq 0) { throw "Cloudflare zone '$zoneName' の取得に失敗しました" }
$zoneId = $zoneResp.result[0].id
$purgeUrls = @(Get-ChildItem $ArtifactsDir -File | Where-Object { $_.Name -notlike '*.nupkg' } | ForEach-Object { "$BaseUrl/$($_.Name)" })
if ($purgeUrls.Count -gt 0) {
    $purgeBody = "{`"files`":$(ConvertTo-Json -InputObject $purgeUrls -AsArray -Compress)}"
    $purgeResp = Invoke-RestMethod -Method Post -Uri "https://api.cloudflare.com/client/v4/zones/$zoneId/purge_cache" `
        -Headers $cfHeaders -ContentType 'application/json' -Body $purgeBody -TimeoutSec 30
    if (-not $purgeResp.success) { throw "Cloudflare キャッシュパージに失敗しました: $($purgeResp.errors | ConvertTo-Json -Compress)" }
    Write-Host "  ✅ パージ: $($purgeUrls.Count) URL"
} else {
    Write-Host '  パージ対象なし'
}

# ---- 3. 配信確認 (manifest 完全一致リトライ方式) ----
# 単純な HTTP 200 だと CDN/edge が古い manifest を返している間に cleanup が走り、旧 manifest を
# 取得したクライアントが直後に消える .nupkg を取りに行って 404 する race がある。
# ローカルアップロード済 manifest と完全一致するまでリトライしてから次へ進む。
Write-Host '== 配信確認 (manifest 伝播待機) ==' -ForegroundColor Cyan
foreach ($runtime in $Runtimes) {
    $channel = $RuntimeMatrix[$runtime].Channel
    $url = "$BaseUrl/releases.$channel.json"
    $localManifest = Get-Content (Join-Path $ArtifactsDir "releases.$channel.json") -Raw |
        ConvertFrom-Json | ConvertTo-Json -Depth 100 -Compress

    $maxAttempts = 18
    $matched = $false
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        $resp = Invoke-WebRequest -Uri "${url}?_=$([Guid]::NewGuid().ToString('N'))" `
            -Headers @{ 'Cache-Control' = 'no-cache' } -TimeoutSec 30
        # R2 は text 系でない Content-Type で返すことがあり、その場合 .Content は byte[] になる
        $raw = $resp.Content
        if ($raw -is [byte[]]) { $raw = [System.Text.Encoding]::UTF8.GetString($raw) }
        $remoteManifest = $raw | ConvertFrom-Json | ConvertTo-Json -Depth 100 -Compress
        if ($localManifest -eq $remoteManifest) {
            Write-Host "  ✅ $url がローカル manifest と一致 (attempt $attempt)"
            $matched = $true
            break
        }
        Write-Host "  ⚠️ remote manifest がまだ古い (attempt $attempt / $maxAttempts)、5 秒待機..."
        Start-Sleep -Seconds 5
    }
    if (-not $matched) {
        throw "remote manifest が $($maxAttempts * 5) 秒以内にローカルと一致しませんでした。race 回避のため cleanup を中止します: $url"
    }
}

# ---- 4. 旧バージョン nupkg のクリーンアップ ----
# 現世代と直前世代を必ず保持し、それ以前も grace period 内は残す。manifest 取得失敗や
# last_modified 不明の object は削除せず、更新確認済みクライアントの取得 race を避ける。
Write-Host '== 旧 nupkg クリーンアップ ==' -ForegroundColor Cyan
$keep = @{}
$manifests = Get-ChildItem $ArtifactsDir -Filter 'releases.*.json'
if (-not $manifests) { throw 'artifacts に releases.*.json が見つかりません' }
foreach ($m in $manifests) {
    foreach ($asset in (Get-Content $m.FullName -Raw | ConvertFrom-Json).Assets) {
        if ($asset.FileName) { $keep[$asset.FileName] = $true }
    }
}
foreach ($assetName in $previousReleaseAssets.Keys) { $keep[$assetName] = $true }
Write-Host "  保持対象 asset (現世代 + 直前世代): $($keep.Count) 件"

$api = "https://api.cloudflare.com/client/v4/accounts/$AccountId/r2/buckets/$Bucket"
$headers = @{ Authorization = "Bearer $($env:CLOUDFLARE_API_TOKEN)" }

$allObjects = [System.Collections.Generic.List[object]]::new()
$cursor = ''
while ($true) {
    $uri = "$api/objects?per_page=1000" + $(if ($cursor) { "&cursor=$cursor" })
    $resp = Invoke-RestMethod -Uri $uri -Headers $headers -TimeoutSec 30
    foreach ($obj in $resp.result) { $allObjects.Add($obj) }
    $info = $resp.PSObject.Properties['result_info']
    if (-not $info -or -not $info.Value) { break }
    $truncated = $info.Value.PSObject.Properties['is_truncated']
    if (-not $truncated -or -not $truncated.Value) { break }
    $cursorProp = $info.Value.PSObject.Properties['cursor']
    $cursor = if ($cursorProp) { $cursorProp.Value } else { '' }
    if (-not $cursor) { break }
}

$graceCutoff = [DateTimeOffset]::UtcNow.AddDays(-$NupkgRetentionGraceDays)
$toDelete = @()
if ($previousManifestReadSucceeded) {
    $toDelete = @($allObjects | Where-Object {
        $key = [string]$_.key
        if ($key -notlike '*.nupkg' -or $keep.ContainsKey($key)) { return $false }

        $lastModifiedProperty = $_.PSObject.Properties['last_modified']
        if (-not $lastModifiedProperty -or -not $lastModifiedProperty.Value) { return $false }
        try {
            $lastModified = [DateTimeOffset]$lastModifiedProperty.Value
        }
        catch {
            return $false
        }

        return $lastModified -lt $graceCutoff
    })
}
else {
    Write-Warning '旧 manifest の取得に失敗したため、fail-safe で cleanup をスキップします。'
}

if ($toDelete.Count -eq 0) {
    Write-Host '  ✅ 削除対象なし'
} else {
    $deleted = 0; $failed = 0
    foreach ($object in $toDelete) {
        $key = [string]$object.key
        $encoded = [uri]::EscapeDataString($key)
        try {
            Invoke-RestMethod -Method Delete -Uri "$api/objects/$encoded" -Headers $headers -TimeoutSec 30 | Out-Null
            Write-Host "  🗑️  $key"
            $deleted++
        } catch {
            Write-Warning "  削除失敗: $key — $($_.Exception.Message)"
            $failed++
        }
    }
    Write-Host "  🧹 クリーンアップ: $deleted 削除 / $failed 失敗 (保持猶予 $NupkgRetentionGraceDays 日)"
    if ($failed -gt 0 -and $deleted -eq 0) { throw '旧 nupkg の削除がすべて失敗しました。API token の権限を確認してください。' }
}

Write-Host "`n🎉 リリース完了: v$version → $BaseUrl" -ForegroundColor Green
