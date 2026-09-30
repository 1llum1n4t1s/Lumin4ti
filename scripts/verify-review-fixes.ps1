param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $PSScriptRoot 'verification/Lumin4ti.Verification.csproj'
$reportPath = Join-Path $repoRoot 'local-release/review-fixes-verification.json'
$arguments = @('run', '--project', $projectPath, '--configuration', 'Debug')
if ($NoBuild) { $arguments += '--no-build' }
$arguments += @('--', $repoRoot, $reportPath)
& dotnet @arguments
exit $LASTEXITCODE
