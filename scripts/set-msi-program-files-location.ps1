# Velopack 1.2.0 の PerMachine MSI が INSTALLFOLDER を TARGETDIR 直下へ置く場合でも、
# 64-bit Program Files\Lumin4ti へ解決される Directory 表へ正規化する。
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$MsiPath,

    [string]$InstallFolderName = 'Lumin4ti'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($InstallFolderName -notmatch '^[A-Za-z0-9._ -]+$') {
    throw "インストールフォルダー名に使用できない文字が含まれています: $InstallFolderName"
}

$resolvedMsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
$installer = $null
$database = $null

function Invoke-MsiSql {
    param(
        [Parameter(Mandatory)]
        [object]$Database,

        [Parameter(Mandatory)]
        [string]$Sql,

        [switch]$Fetch
    )

    $view = $null
    try {
        $view = $Database.GetType().InvokeMember(
            'OpenView',
            [Reflection.BindingFlags]::InvokeMethod,
            $null,
            $Database,
            @($Sql))
        $view.GetType().InvokeMember(
            'Execute',
            [Reflection.BindingFlags]::InvokeMethod,
            $null,
            $view,
            $null) | Out-Null
        if ($Fetch) {
            return $view.GetType().InvokeMember(
                'Fetch',
                [Reflection.BindingFlags]::InvokeMethod,
                $null,
                $view,
                $null)
        }
    }
    finally {
        if ($null -ne $view) {
            try {
                $view.GetType().InvokeMember(
                    'Close',
                    [Reflection.BindingFlags]::InvokeMethod,
                    $null,
                    $view,
                    $null) | Out-Null
            }
            finally {
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
            }
        }
    }
}

try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.GetType().InvokeMember(
        'OpenDatabase',
        [Reflection.BindingFlags]::InvokeMethod,
        $null,
        $installer,
        @($resolvedMsiPath, 1)) # msiOpenDatabaseModeTransact

    $programFilesRow = Invoke-MsiSql -Database $database -Fetch -Sql @'
SELECT `Directory` FROM `Directory` WHERE `Directory` = 'ProgramFiles64Folder'
'@
    if ($null -eq $programFilesRow) {
        Invoke-MsiSql -Database $database -Sql @'
INSERT INTO `Directory` (`Directory`, `Directory_Parent`, `DefaultDir`) VALUES ('ProgramFiles64Folder', 'TARGETDIR', '.')
'@ | Out-Null
    }
    else {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($programFilesRow)
    }

    Invoke-MsiSql -Database $database -Sql "UPDATE ``Directory`` SET ``Directory_Parent`` = 'ProgramFiles64Folder', ``DefaultDir`` = '$InstallFolderName' WHERE ``Directory`` = 'INSTALLFOLDER'" | Out-Null
    $database.GetType().InvokeMember(
        'Commit',
        [Reflection.BindingFlags]::InvokeMethod,
        $null,
        $database,
        $null) | Out-Null
}
finally {
    if ($null -ne $database) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    }
    if ($null -ne $installer) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
    }
}

# 書き込み後は読み取り専用で再オープンし、リリース工程中に構造を機械検証する。
$installer = $null
$database = $null
$installFolderRow = $null
$programFilesRow = $null
try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.GetType().InvokeMember(
        'OpenDatabase',
        [Reflection.BindingFlags]::InvokeMethod,
        $null,
        $installer,
        @($resolvedMsiPath, 0)) # msiOpenDatabaseModeReadOnly

    $installFolderRow = Invoke-MsiSql -Database $database -Fetch -Sql @'
SELECT `Directory_Parent`, `DefaultDir` FROM `Directory` WHERE `Directory` = 'INSTALLFOLDER'
'@
    $programFilesRow = Invoke-MsiSql -Database $database -Fetch -Sql @'
SELECT `Directory_Parent`, `DefaultDir` FROM `Directory` WHERE `Directory` = 'ProgramFiles64Folder'
'@

    if ($null -eq $installFolderRow -or
        $installFolderRow.StringData(1) -ne 'ProgramFiles64Folder' -or
        $installFolderRow.StringData(2) -ne $InstallFolderName -or
        $null -eq $programFilesRow -or
        $programFilesRow.StringData(1) -ne 'TARGETDIR' -or
        $programFilesRow.StringData(2) -ne '.') {
        throw 'MSIのProgram Filesインストール構造を検証できませんでした'
    }

    [pscustomobject]@{
        MsiPath = $resolvedMsiPath
        InstallDirectory = "ProgramFiles64Folder\$InstallFolderName"
    }
}
finally {
    if ($null -ne $installFolderRow) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installFolderRow)
    }
    if ($null -ne $programFilesRow) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($programFilesRow)
    }
    if ($null -ne $database) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    }
    if ($null -ne $installer) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
    }
}
