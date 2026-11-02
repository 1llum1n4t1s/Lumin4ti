# release-local.ps1 — ローカル署名付き Velopack リリース (Shisui テンプレートから横展開)
#
# SimplySign (Certum クラウド署名) は Desktop 接続 + スマホトークンが必要で
# GitHub Actions からは署名できないため、リリースは本スクリプトでローカル実行する。
#
# 前提:
#   - SimplySign Desktop が接続済み (証明書が CurrentUser\My に見えていること)
#   - Directory.Build.props の <Version> がリリースしたいバージョンになっていること (/vava 済み)
#   - D:\Dev\Secret\secrets.json に cloudflare.api_token があること
#
# 使い方:
#   pwsh scripts/release-local.ps1                # フルリリース (build + sign + upload + cleanup)
#   pwsh scripts/release-local.ps1 -SkipUpload    # ビルド + 署名のみ (アップロードしない動作確認用)

[CmdletBinding()]
param(
    [switch]$SkipUpload,
    [string[]]$Runtimes = @('win-x64')
)




# 検証済みの作業対象だけを標準APIで削除する。
function Remove-LocalWorkItem {
    param([string[]]$LiteralPath, [string]$AllowedRoot, [switch]$IgnoreMissing, [switch]$AllowLeafJunction)
    if ([string]::IsNullOrWhiteSpace($AllowedRoot) -or -not [IO.Path]::IsPathRooted($AllowedRoot)) { throw 'Cleanup root must be absolute.' }
    $boundary = [IO.Path]::GetFullPath($AllowedRoot).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    foreach ($target in $LiteralPath) {
        if ([string]::IsNullOrWhiteSpace($target) -or -not [IO.Path]::IsPathRooted($target)) { throw 'Cleanup target must be absolute.' }
        $resolved = [IO.Path]::GetFullPath($target)
        if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup target is outside the allowed root.' }
        $ancestor = $resolved
        while ($ancestor) {
            if (Test-Path -LiteralPath $ancestor) {
                $item = Get-Item -LiteralPath $ancestor -Force -ErrorAction Stop
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -and -not ($AllowLeafJunction -and $ancestor -eq $resolved -and $item.LinkType -eq 'Junction')) { throw 'Cleanup path contains a reparse point.' }
            }
            $ancestor = Split-Path $ancestor -Parent
        }
        if (-not (Test-Path -LiteralPath $resolved)) { if ($IgnoreMissing) { continue }; throw "Cleanup target is missing: $resolved" }
        $item = Get-Item -LiteralPath $resolved -Force -ErrorAction Stop
        $leafLink = [bool]($item.Attributes -band [IO.FileAttributes]::ReparsePoint)
        if ($item.PSIsContainer -and -not $leafLink) {
            if (@(Get-ChildItem -LiteralPath $resolved -Recurse -Force -ErrorAction Stop | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Cleanup directory contains a reparse point.' }
        }
        Remove-Item -LiteralPath $resolved -Recurse:($item.PSIsContainer -and -not $leafLink) -Force -ErrorAction Stop
        if (Test-Path -LiteralPath $resolved) { throw "Cleanup target remains: $resolved" }
    }
}

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# 実行せず、実際に配布するパッケージの版・署名と win-x64 構成を確認する。
function Assert-ReleasePackage {
    param([string]$PackagePath, [string]$ExpectedVersion)
    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $readText = {
            param([string]$Name)
            $entry = $archive.GetEntry($Name)
            if ($null -eq $entry -or $entry.Length -eq 0) { throw "パッケージに必須ファイルがありません: $Name" }
            $reader = [IO.StreamReader]::new($entry.Open())
            try { $reader.ReadToEnd() } finally { $reader.Dispose() }
        }
        [xml]$nuspec = & $readText 'Lumin4ti.nuspec'
        $metadata = $nuspec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        if ($null -eq $metadata -or $metadata.id -cne 'Lumin4ti' -or $metadata.version -cne $ExpectedVersion) {
            throw "パッケージの ID または版が一致しません: $PackagePath"
        }
        foreach ($name in @('Lumin4ti.UI.exe', 'Lumin4ti.UI.dll', 'Lumin4ti.UI.deps.json', 'Lumin4ti.UI.runtimeconfig.json',
                'Lumin4ti.RebootCleanup.exe', 'Lumin4ti.RebootCleanup.dll', 'Lumin4ti.RebootCleanup.deps.json', 'Lumin4ti.RebootCleanup.runtimeconfig.json',
                'Lumin4ti.Core.dll', 'coreclr.dll', 'hostfxr.dll', 'System.Private.CoreLib.dll')) {
            $entry = $archive.GetEntry("lib/app/$name")
            if ($null -eq $entry -or $entry.Length -eq 0) { throw "パッケージに必須ファイルがありません: $name" }
        }
        # 署名後の nupkg 内の実物を確認する。展開先は終端のリリース清掃まで保持する。
        $inspectionDir = Join-Path $WorkDir "payload-verification-$ExpectedVersion"
        [void][IO.Directory]::CreateDirectory($inspectionDir)
        foreach ($name in @('Lumin4ti.UI.exe', 'Lumin4ti.UI.dll', 'Lumin4ti.RebootCleanup.exe', 'Lumin4ti.RebootCleanup.dll', 'Lumin4ti.Core.dll')) {
            $destination = Join-Path $inspectionDir $name
            [IO.Compression.ZipFileExtensions]::ExtractToFile($archive.GetEntry("lib/app/$name"), $destination, $true)
            $signature = Get-AuthenticodeSignature -LiteralPath $destination
            $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($destination).FileVersion
            if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike "CN=$CertSubjectName*" -or
                    $fileVersion -cne "$ExpectedVersion.0") {
                throw "署名後のパッケージ内ファイルの版または署名が不一致です: $name ($fileVersion / $($signature.Status))"
            }
        }
        $deps = (& $readText 'lib/app/Lumin4ti.UI.deps.json') | ConvertFrom-Json
        if (-not ([string]$deps.runtimeTarget.name).EndsWith('/win-x64', [StringComparison]::Ordinal)) {
            throw "パッケージの runtimeTarget が win-x64 ではありません: $PackagePath"
        }
        $helperDeps = (& $readText 'lib/app/Lumin4ti.RebootCleanup.deps.json') | ConvertFrom-Json
        if ($helperDeps.runtimeTarget.name -cne $deps.runtimeTarget.name) {
            throw "helper と GUI の runtimeTarget が一致しません: $PackagePath"
        }
        $coreDependency = @($deps.libraries.PSObject.Properties.Name | Where-Object { $_ -clike 'Lumin4ti.Core/*' })
        $helperCoreDependency = @($helperDeps.libraries.PSObject.Properties.Name | Where-Object { $_ -clike 'Lumin4ti.Core/*' })
        if ($coreDependency.Count -ne 1 -or $helperCoreDependency.Count -ne 1 -or $helperCoreDependency[0] -cne $coreDependency[0]) {
            throw "helper と GUI の Core 依存が一致しません: $PackagePath"
        }
        $uiRuntime = ((& $readText 'lib/app/Lumin4ti.UI.runtimeconfig.json') | ConvertFrom-Json).runtimeOptions
        $helperRuntime = ((& $readText 'lib/app/Lumin4ti.RebootCleanup.runtimeconfig.json') | ConvertFrom-Json).runtimeOptions
        $uiFramework = @($uiRuntime.includedFrameworks | Where-Object { $_.name -ceq 'Microsoft.NETCore.App' })
        $helperFramework = @($helperRuntime.includedFrameworks | Where-Object { $_.name -ceq 'Microsoft.NETCore.App' })
        if ($uiFramework.Count -ne 1 -or $helperFramework.Count -ne 1 -or $uiFramework[0].version -cne $helperFramework[0].version) {
            throw "helper と GUI の同梱 .NET runtime が一致しません: $PackagePath"
        }
        foreach ($executable in @('Lumin4ti.UI.exe', 'Lumin4ti.RebootCleanup.exe')) {
            $apphost = [IO.MemoryStream]::new()
            $exeStream = $archive.GetEntry("lib/app/$executable").Open()
            $binary = $null
            try {
                $exeStream.CopyTo($apphost)
                $apphost.Position = 0
                $binary = [IO.BinaryReader]::new($apphost)
                if ($binary.ReadUInt16() -ne 0x5A4D) { throw 'apphost の DOS ヘッダーが不正です' }
                $apphost.Position = 0x3C
                $peOffset = $binary.ReadUInt32()
                $apphost.Position = $peOffset
                if ($binary.ReadUInt32() -ne 0x00004550 -or $binary.ReadUInt16() -ne 0x8664) {
                    throw "apphost の PE machine が x64 ではありません: $PackagePath"
                }
            }
            finally {
                if ($null -ne $binary) { $binary.Dispose() }
                $exeStream.Dispose()
                $apphost.Dispose()
            }
        }
    }
    finally { $archive.Dispose() }
}

function Assert-MsiVersion {
    param([string]$MsiPath, [string]$ExpectedVersion)
    $installer = $database = $view = $record = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.GetType().InvokeMember('OpenDatabase', [Reflection.BindingFlags]::InvokeMethod, $null, $installer, @($MsiPath, 0))
        $view = $database.GetType().InvokeMember('OpenView', [Reflection.BindingFlags]::InvokeMethod, $null, $database, @('SELECT `Value` FROM `Property` WHERE `Property` = ''ProductVersion'''))
        $view.GetType().InvokeMember('Execute', [Reflection.BindingFlags]::InvokeMethod, $null, $view, $null) | Out-Null
        $record = $view.GetType().InvokeMember('Fetch', [Reflection.BindingFlags]::InvokeMethod, $null, $view, $null)
        if ($null -eq $record) { throw "MSI に ProductVersion がありません: $MsiPath" }
        $productVersion = $record.GetType().InvokeMember('StringData', [Reflection.BindingFlags]::GetProperty, $null, $record, @(1))
        # Velopack MSI は 3 桁の製品版に末尾 .0 を付けるため、その表現だけも許容する。
        if ($productVersion -cne $ExpectedVersion -and $productVersion -cne "$ExpectedVersion.0") {
            throw "MSI の ProductVersion が一致しません: $productVersion (期待値: $ExpectedVersion)"
        }
    }
    finally {
        foreach ($com in @($record, $view, $database, $installer)) {
            if ($null -ne $com) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($com) }
        }
    }
}

# ---- 定数 ----
# リリースの再現性を保つため、vpk はリポジトリ内で明示的に固定する。
# 更新時は公式 NuGet の安定版を確認し、-SkipUpload で署名成果物を検証してから変更する。
$VpkVersion = '1.2.161'
Write-Host "vpk 固定バージョン: $VpkVersion"
$WranglerVersion = '4.156.1'        # サプライチェーン対策でバージョン固定
$Bucket = 'lumin4ti-updates'
$BaseUrl = 'https://lumin4ti.kagayoi.com'
$AccountId = '10901bfadbf1005164774a7350082985'
$KeepVersionCount = 2                # 全プロジェクト共通の保持ポリシー: 直近 2 バージョン
$SecretsPath = 'D:\Dev\Secret\secrets.json'
$CertSubjectName = 'Open Source Developer Yuichiro Shinozaki'
# /n (Subject 名) で選択: 証明書の年次更新で thumbprint が変わっても動く
$SignParams = "/n `"$CertSubjectName`" /fd SHA256 /td SHA256 /tr http://time.certum.pl"
$SignToolPath = Get-ChildItem -LiteralPath 'C:\Program Files (x86)\Windows Kits\10\bin' `
    -Filter 'signtool.exe' -File -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.DirectoryName -like '*\x64' } |
    Sort-Object { [version]$_.Directory.Parent.Name } -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $SignToolPath) { throw 'Windows SDK x64 signtool.exe が見つかりません' }

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

$resolvedRepo = [IO.Path]::GetFullPath($RepoRoot)
$resolvedWork = [IO.Path]::GetFullPath($WorkDir)
if ($resolvedWork -ne (Join-Path $resolvedRepo 'local-release')) { throw 'リリース出力先が不正です' }
foreach ($path in @($resolvedRepo, $resolvedWork)) {
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "リリース出力先に再解析ポイントがあります: $path"
    }
}
# 検証記録などの既存ファイルは保持し、今回再生成する出力だけを整理する。
foreach ($output in @($ArtifactsDir) + @($Runtimes | ForEach-Object { Join-Path $WorkDir "publish-$_" })) {
    $resolvedOutput = [IO.Path]::GetFullPath($output)
    if ((Split-Path -Parent $resolvedOutput) -ne $resolvedWork) { throw '整理対象がリリース出力範囲外です' }
    if (Test-Path -LiteralPath $resolvedOutput) {
        $entries = @((Get-Item -LiteralPath $resolvedOutput)) + @(Get-ChildItem -LiteralPath $resolvedOutput -Recurse -Force)
        if (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) {
            throw "整理対象に再解析ポイントがあります: $resolvedOutput"
        }
        Remove-LocalWorkItem -LiteralPath $resolvedOutput -AllowedRoot $resolvedWork
    }
}
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
            --self-contained true --no-restore -p:UseSharedCompilation=false -o $publishDir
    }

    if (-not (Test-Path (Join-Path $publishDir 'Lumin4ti.UI.exe'))) {
        throw "Lumin4ti.UI.exe が publish 出力にありません ($runtime)"
    }

    # --signParams は helper を含む全 PE (exe/dll) を署名する。helper の署名除外は行わない。
    Write-Host "== vpk pack + 署名: $runtime ==" -ForegroundColor Cyan
    # StartMenu は packAuthors 名のサブフォルダを作るため、ルート直下へ登録する。
    Invoke-Native "vpk pack ($runtime)" {
        vpk pack `
            --packId Lumin4ti `
            --packVersion $version `
            --packTitle 'Lumin4ti' `
            --packAuthors 'Kagayoi' `
            --mainExe Lumin4ti.UI.exe `
            --icon (Join-Path 'src' 'Lumin4ti.UI' 'icon' 'app.ico') `
            --packDir $publishDir `
            --outputDir $ArtifactsDir `
            --runtime $runtime `
            --channel $config.Channel `
            --shortcuts 'StartMenuRoot,Desktop' `
            --msi `
            --instLocation PerMachine `
            --signParams $SignParams
    }
}

# Velopack 1.2.0 の生成MSIは PerMachine 指定でも INSTALLFOLDER が TARGETDIR 直下になり、
# Windows Installer が C:\Lumin4ti と解決する。Directory表を公式の64-bit Program Files構造へ直し、
# 変更によって無効になったMSI本体の署名を上書きして再署名する。
foreach ($msi in Get-ChildItem $ArtifactsDir -Filter '*.msi' -File) {
    & (Join-Path $PSScriptRoot 'set-msi-program-files-location.ps1') -MsiPath $msi.FullName
    Invoke-Native "Program Files固定MSIの再署名 ($($msi.Name))" {
        & $SignToolPath sign /n $CertSubjectName /fd SHA256 /td SHA256 `
            /tr 'http://time.certum.pl' $msi.FullName
    }
}

# 署名検証 (MSIを含むインストーラが正しく署名されているかリリース前に確認)
Write-Host '== 署名検証 ==' -ForegroundColor Cyan
foreach ($signedArtifact in Get-ChildItem $ArtifactsDir -File | Where-Object { $_.Extension -in '.exe', '.msi' }) {
    $sig = Get-AuthenticodeSignature $signedArtifact.FullName
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notlike "CN=$CertSubjectName*") {
        throw "署名検証失敗: $($signedArtifact.Name) → $($sig.Status)"
    }
    Write-Host "  ✅ $($signedArtifact.Name): Valid ($($sig.SignerCertificate.Subject -replace ',.*$'))"
}

# ---- 2. R2 アップロード (asset → 取得検証 → manifest commit point) ----
$artifacts = @(Get-ChildItem $ArtifactsDir -File)
$legacySetupFiles = @($artifacts | Where-Object { $_.Name -like '*-Setup.exe' })
# PerUser Setup.exe は今後公開せず、MSIの伝播確認後にR2上の旧Setupも削除する。
$publishArtifacts = @($artifacts | Where-Object { $_.Name -notlike '*-Setup.exe' })
$msiFiles = @($publishArtifacts | Where-Object { $_.Extension -eq '.msi' })
if ($msiFiles.Count -ne 1 -or $msiFiles[0].Name -ne 'Lumin4ti-win.msi') {
    throw "固定URLへ公開するPerMachine MSIを一意に確認できません: $($msiFiles.Name -join ', ')"
}
$manifestFiles = @($publishArtifacts | Where-Object { $_.Name -like 'releases.*.json' } | Sort-Object Name)
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

Write-Host '== 公開前の版・構成・SHA256 検証 ==' -ForegroundColor Cyan
foreach ($msiFile in $msiFiles) {
    Assert-MsiVersion -MsiPath $msiFile.FullName -ExpectedVersion $version
}
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
        $fileName = [string]$asset.FileName
        if ([string]::IsNullOrWhiteSpace($fileName) -or -not $artifactByName.ContainsKey($fileName)) {
            throw "manifest がローカルに存在しない asset を参照しています: $($manifestFile.Name) → $fileName"
        }
        if ($asset.Version -cne $version -or $asset.PackageId -cne 'Lumin4ti') {
            throw "manifest asset の ID または版が一致しません: $fileName"
        }
        $assetFile = $artifactByName[$fileName]
        if ([long]$asset.Size -ne $assetFile.Length) { throw "manifest とローカル asset のサイズが一致しません: $fileName" }
        if ($asset.SHA256 -notmatch '^[A-Fa-f0-9]{64}$' -or $asset.SHA256 -ne (Get-FileHash -LiteralPath $assetFile.FullName -Algorithm SHA256).Hash) {
            throw "manifest とローカル asset の SHA256 が一致しません: $fileName"
        }
        Assert-ReleasePackage -PackagePath $assetFile.FullName -ExpectedVersion $version
        $referencedAssets[$fileName] = $assetFile
    }
}

if ($SkipUpload) {
    Write-Host "`n✅ -SkipUpload 指定のためここで終了。成果物: $ArtifactsDir" -ForegroundColor Green
    Get-ChildItem $ArtifactsDir | Format-Table Name, @{n='Size(MB)'; e={[math]::Round($_.Length/1MB,1)}}
    return
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

$preCommitFiles = @($publishArtifacts | Where-Object { $_.Name -notlike 'releases.*.json' } | Sort-Object Name)
if ($preCommitFiles.Count -eq 0) { throw 'manifest より先に公開する asset がありません' }

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
foreach ($msiFile in $msiFiles) {
    Wait-RemoteAsset -File $msiFile -BaseUrl $BaseUrl
}

# SimpleWebSource が読む releases.{channel}.json を最後に公開し、ここをリリースの commit point とする。
Write-Host '== manifest commit point 公開 ==' -ForegroundColor Cyan
foreach ($manifestFile in $manifestFiles) {
    Publish-R2Object $manifestFile
    $uploaded++
}
Write-Host "✅ R2 全アップロード完了: $uploaded ファイル (manifest は最後に公開)"

# ---- 2.5 Cloudflare エッジキャッシュのパージ ----
# 固定名ファイル (MSI / Portable.zip / RELEASES / releases.*.json / assets.*.json) は
# 毎リリースで中身が変わるのに URL が不変。パージしないと自動更新が旧版を掴む。
Write-Host '== Cloudflare キャッシュパージ ==' -ForegroundColor Cyan
$cfHeaders = @{ Authorization = "Bearer $($env:CLOUDFLARE_API_TOKEN)" }
$zoneName = ([uri]$BaseUrl).Host -replace '^[^.]+\.', ''   # <sub>.kagayoi.com → kagayoi.com (apex)
$zoneResp = Invoke-RestMethod -Uri "https://api.cloudflare.com/client/v4/zones?name=$zoneName" -Headers $cfHeaders -TimeoutSec 30
if (-not $zoneResp.success -or @($zoneResp.result).Count -eq 0) { throw "Cloudflare zone '$zoneName' の取得に失敗しました" }
$zoneId = $zoneResp.result[0].id
$purgeUrls = @()
$verificationClient = [Net.Http.HttpClient]::new()
$verificationClient.Timeout = [TimeSpan]::FromMinutes(3)
try {
    foreach ($fixedArtifact in @($publishArtifacts | Where-Object { $_.Name -notlike '*.nupkg' })) {
        $fixedUrl = "$BaseUrl/$([uri]::EscapeDataString($fixedArtifact.Name))"
        $remoteBytes = $verificationClient.GetByteArrayAsync("${fixedUrl}?_=$([Guid]::NewGuid().ToString('N'))").GetAwaiter().GetResult()
        $remoteHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($remoteBytes))
        $localHash = (Get-FileHash -LiteralPath $fixedArtifact.FullName -Algorithm SHA256).Hash
        if ($remoteHash -ne $localHash) { $purgeUrls += $fixedUrl }
        else { Write-Host "  ✅ 最新配信の SHA256 一致: $($fixedArtifact.Name)" }
    }
} finally { $verificationClient.Dispose() }
if ($purgeUrls.Count -gt 0) {
    $purgeBody = [PSCustomObject]@{ files = $purgeUrls } | ConvertTo-Json -Compress
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

# MSIが配信済みになった後で、再びPerUser版を導入できないよう旧Setup.exeをR2から除去する。
foreach ($legacySetup in $legacySetupFiles) {
    $encoded = [uri]::EscapeDataString($legacySetup.Name)
    try {
        Invoke-RestMethod -Method Delete -Uri "$api/objects/$encoded" -Headers $headers -TimeoutSec 30 | Out-Null
        Write-Host "  🗑️  旧PerUserインストーラ: $($legacySetup.Name)"
    }
    catch {
        $responseProperty = $_.Exception.PSObject.Properties['Response']
        $statusCode = if ($responseProperty -and $responseProperty.Value) {
            [int]$responseProperty.Value.StatusCode
        } else {
            $null
        }
        if ($statusCode -ne 404) { throw }
    }
}
if ($legacySetupFiles.Count -gt 0) {
    $legacySetupUrls = @($legacySetupFiles | ForEach-Object { "$BaseUrl/$($_.Name)" })
    $legacyPurgeBody = [PSCustomObject]@{ files = $legacySetupUrls } | ConvertTo-Json -Compress
    $legacyPurgeResp = Invoke-RestMethod -Method Post -Uri "https://api.cloudflare.com/client/v4/zones/$zoneId/purge_cache" `
        -Headers $cfHeaders -ContentType 'application/json' -Body $legacyPurgeBody -TimeoutSec 30
    if (-not $legacyPurgeResp.success) {
        throw "旧PerUserインストーラのキャッシュパージに失敗しました: $($legacyPurgeResp.errors | ConvertTo-Json -Compress)"
    }
}

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

# 全プロジェクト共通の保持ポリシー: manifest 参照 + 固定名 + 直近 $KeepVersionCount バージョン。
# ⚠️ 旧実装は「'*.nupkg' かつ last_modified が猶予日数より古い」を条件にしていたため、
#    猶予期間 (30 日) 内のリリースが重なると旧 nupkg が積み上がり続けた
#    (2026-07 実測: 1.0.3〜1.0.12 の 12 個 = 0.65GB が滞留)。直近 2 世代を残せば
#    配信直後の伝播中クライアントも 1 つ前を引けるので、猶予日数は不要。
#    固定ファイル名 (*.msi / Setup.exe / Portable.zip / RELEASES* / releases.*.json) は
#    バージョン文字列を含まないので対象外 = 安全。
$versionPattern = '(\d+\.\d+\.\d+)'
$toDelete = @()
if ($previousManifestReadSucceeded) {
    $allKeys = @($allObjects | ForEach-Object { [string]$_.key })
    $allVersions = @(
        $allKeys | ForEach-Object {
            $m = [regex]::Match($_, $versionPattern)
            if ($m.Success) { $m.Groups[1].Value }
        } | Sort-Object -Property { [version]$_ } -Unique
    )
    $keepVersions = @($allVersions | Select-Object -Last $KeepVersionCount)
    Write-Host "  保持バージョン: $($keepVersions -join ', ') (全 $($allVersions.Count) 世代)"

    $toDelete = @($allKeys | Where-Object {
        # manifest が参照するファイルは絶対保持 (消すと自動更新が壊れる)
        if ($keep.ContainsKey($_)) { return $false }
        # 固定ファイル名はバージョン文字列を含まない = 毎リリース上書きなので保持
        $m = [regex]::Match($_, $versionPattern)
        if (-not $m.Success) { return $false }
        return $keepVersions -notcontains $m.Groups[1].Value
    })
}
else {
    Write-Warning '旧 manifest の取得に失敗したため、fail-safe で cleanup をスキップします。'
}

if ($toDelete.Count -eq 0) {
    Write-Host '  ✅ 削除対象なし'
} else {
    $deleted = 0; $failed = 0
    # $toDelete は key 文字列の配列 (保持ポリシーの判定を key だけで行うため)
    foreach ($key in $toDelete) {
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
    Write-Host "  🧹 クリーンアップ: $deleted 削除 / $failed 失敗 (直近 $KeepVersionCount バージョンを保持)"
    if ($failed -gt 0 -and $deleted -eq 0) { throw '旧 nupkg の削除がすべて失敗しました。API token の権限を確認してください。' }
}

Write-Host "`n🎉 リリース完了: v$version → $BaseUrl" -ForegroundColor Green
