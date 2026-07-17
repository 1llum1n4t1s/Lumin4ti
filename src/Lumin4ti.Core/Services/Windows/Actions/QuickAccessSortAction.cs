using System.Runtime.Versioning;
using System.Text.Json;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// エクスプローラーのクイックアクセスにピン留めしたフォルダを名前の昇順に並べ替える。
/// Shell.Application COM は検証済み Explorer の medium token で起動した PowerShell 内だけで操作する。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class QuickAccessSortAction : IMaintenanceAction
{
    private const int MaximumResultCharacters = 1024 * 1024;
    internal static TimeSpan TransactionTimeout { get; } = TimeSpan.FromMinutes(5);
    private readonly IUnelevatedCommandExecutor _executor;

    internal QuickAccessSortAction(IUnelevatedCommandExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public string Id => "quick-access-sort";

    public string Label => "クイックアクセスのピン留めをソート";

    public string Description => "クイックアクセスにピン留めしたフォルダを名前の昇順に並べ替えます (実行前にジャンプリストをバックアップします)。";

    public CommandCategory Category => CommandCategory.Organize;

    public bool RequiresReboot => false;

    public bool IsLongRunning => true;

    internal static string PowerShellScript =>
        """
        Set-StrictMode -Version 3.0
        $ErrorActionPreference = 'Stop'
        $quickAccessNamespace = 'shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}'
        $result = [ordered]@{
            SchemaVersion = 2
            Status = 'error'
            PinnedCount = 0
            SkipReason = $null
            BackupPath = $null
            BackupFailed = $false
            BackupVerified = $false
            MutationStarted = $false
            RecoveryAttempted = $false
            RestoredFromBackup = $false
            VerificationSucceeded = $false
            SuccessCount = 0
            FailedPaths = @()
            UnrecoveredPaths = @()
            Warnings = @()
            Error = $null
        }

        function Test-PinVerb([string]$name) {
            if ($null -eq $name) { return $false }
            return (($name.Contains('ピン留め') -and -not $name.Contains('外す')) -or $name.Contains('Pin to'))
        }

        function Test-UnpinVerb([string]$name) {
            if ($null -eq $name) { return $false }
            return (($name.Contains('ピン留め') -and $name.Contains('外す')) -or $name.Contains('Unpin from'))
        }

        function Find-QuickAccessVerb($verbs, [bool]$wantUnpin) {
            foreach ($verb in @($verbs)) {
                $name = [string]$verb.Name
                if (($wantUnpin -and (Test-UnpinVerb $name)) -or
                    (-not $wantUnpin -and (Test-PinVerb $name))) {
                    return $verb
                }
            }
            return $null
        }

        function Get-PinnedItems($shell) {
            $namespace = $shell.NameSpace($quickAccessNamespace)
            if ($null -eq $namespace) {
                throw 'クイックアクセス名前空間を取得できませんでした'
            }

            $items = [Collections.Generic.List[object]]::new()
            foreach ($item in @($namespace.Items())) {
                $unpinVerb = Find-QuickAccessVerb -Verbs ($item.Verbs()) -WantUnpin $true
                if ($null -ne $unpinVerb) {
                    $path = [string]$item.Path
                    if ([string]::IsNullOrWhiteSpace($path)) {
                        throw 'ピン留め項目のパスを取得できませんでした'
                    }
                    $name = [string]$item.Name
                    if ([string]::IsNullOrWhiteSpace($name)) {
                        $name = [IO.Path]::GetFileName($path.TrimEnd('\'))
                    }
                    if ([string]::IsNullOrWhiteSpace($name)) { $name = $path }
                    [void]$items.Add([pscustomobject]@{ Path = $path; Name = $name })
                }
            }
            return $items.ToArray()
        }

        function Get-PinnedPaths($shell) {
            return @(Get-PinnedItems $shell | ForEach-Object { [string]$_.Path })
        }

        function Limit-Text([string]$message, [int]$maximumLength) {
            if ($null -eq $message) { return '' }
            if ($message.Length -le $maximumLength) { return $message }
            return $message.Substring(0, $maximumLength)
        }

        function Add-Warning([string]$message) {
            if (@($result.Warnings).Count -lt 250) {
                $result.Warnings = @($result.Warnings) + (Limit-Text $message 4096)
            }
        }

        function Add-FailedPath([string]$path) {
            foreach ($existing in @($result.FailedPaths)) {
                if ([string]::Equals([string]$existing, $path, [StringComparison]::OrdinalIgnoreCase)) {
                    return
                }
            }
            if (@($result.FailedPaths).Count -lt 1000) {
                $result.FailedPaths = @($result.FailedPaths) + $path
            }
        }

        function Add-UnrecoveredPath([string]$path) {
            foreach ($existing in @($result.UnrecoveredPaths)) {
                if ([string]::Equals([string]$existing, $path, [StringComparison]::OrdinalIgnoreCase)) {
                    return
                }
            }
            if (@($result.UnrecoveredPaths).Count -lt 1000) {
                $result.UnrecoveredPaths = @($result.UnrecoveredPaths) + $path
            }
        }

        function Test-PathSequence($actual, $expected) {
            $actualPaths = @($actual)
            $expectedPaths = @($expected)
            if ($actualPaths.Count -ne $expectedPaths.Count) { return $false }

            for ($index = 0; $index -lt $expectedPaths.Count; $index++) {
                if (-not [string]::Equals(
                    [string]$actualPaths[$index],
                    [string]$expectedPaths[$index],
                    [StringComparison]::OrdinalIgnoreCase)) {
                    return $false
                }
            }
            return $true
        }

        function New-VerifiedBackup([string]$sourcePath, [string]$backupPath) {
            if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
                throw 'ジャンプリストが見つからないため、安全に並べ替えできません'
            }

            $sourceInfoBefore = Get-Item -LiteralPath $sourcePath -Force -ErrorAction Stop
            if ($sourceInfoBefore.Length -le 0) {
                throw 'ジャンプリストが空のため、安全に並べ替えできません'
            }
            $sourceHashBefore = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256 -ErrorAction Stop).Hash

            Copy-Item -LiteralPath $sourcePath -Destination $backupPath -Force -ErrorAction Stop

            $sourceInfoAfter = Get-Item -LiteralPath $sourcePath -Force -ErrorAction Stop
            $backupInfo = Get-Item -LiteralPath $backupPath -Force -ErrorAction Stop
            $sourceHashAfter = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256 -ErrorAction Stop).Hash
            $backupHash = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256 -ErrorAction Stop).Hash
            if ($sourceInfoBefore.Length -ne $sourceInfoAfter.Length -or
                $sourceInfoAfter.Length -ne $backupInfo.Length -or
                -not [string]::Equals($sourceHashBefore, $sourceHashAfter, [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals($sourceHashAfter, $backupHash, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'バックアップ作成中にジャンプリストが変化したか、コピー検証に失敗しました'
            }
            return $backupHash
        }

        function Restore-BackupFile(
            [string]$jumpList,
            [string]$backupPath,
            [string]$expectedHash) {
            if (-not (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
                throw '検証済みバックアップが見つかりません'
            }
            $backupHash = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256 -ErrorAction Stop).Hash
            if (-not [string]::Equals($backupHash, $expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'バックアップが作成後に変化したため復元を中止しました'
            }

            Copy-Item -LiteralPath $backupPath -Destination $jumpList -Force -ErrorAction Stop
            $restoredHash = (Get-FileHash -LiteralPath $jumpList -Algorithm SHA256 -ErrorAction Stop).Hash
            if (-not [string]::Equals($restoredHash, $expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'ジャンプリストの復元後検証に失敗しました'
            }
        }

        function Write-AtomicUtf8Json([string]$path, [string]$json) {
            $directory = [IO.Path]::GetDirectoryName($path)
            if ([string]::IsNullOrWhiteSpace($directory)) {
                throw 'transaction journal の保存先を特定できません'
            }

            [IO.Directory]::CreateDirectory($directory) | Out-Null
            $temporaryPath = $path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
            try {
                $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
                $stream = [IO.FileStream]::new(
                    $temporaryPath,
                    [IO.FileMode]::CreateNew,
                    [IO.FileAccess]::Write,
                    [IO.FileShare]::None,
                    4096,
                    [IO.FileOptions]::WriteThrough)
                try {
                    $stream.Write($bytes, 0, $bytes.Length)
                    $stream.Flush($true)
                }
                finally {
                    $stream.Dispose()
                }

                # 既存 journal は前回中断の復旧根拠かもしれないため上書きしない。
                # 同一ディレクトリ内の Move で新規 journal の公開だけを atomic に行う。
                if (Test-Path -LiteralPath $path) {
                    throw '既存の transaction journal があるため上書きしません'
                }
                [IO.File]::Move($temporaryPath, $path)
            }
            finally {
                if (Test-Path -LiteralPath $temporaryPath) {
                    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
                }
            }
        }

        function Read-PendingJournal(
            [string]$journalPath,
            [string]$expectedJumpList,
            [string]$expectedBackupPath) {
            if (-not (Test-Path -LiteralPath $journalPath -PathType Leaf)) {
                throw 'transaction journal がファイルではありません'
            }

            $journalInfo = Get-Item -LiteralPath $journalPath -Force -ErrorAction Stop
            if ($journalInfo.Length -le 0 -or $journalInfo.Length -gt 1048576) {
                throw 'transaction journal が空か上限サイズを超えています'
            }

            $journal = [IO.File]::ReadAllText($journalPath, [Text.Encoding]::UTF8) | ConvertFrom-Json -ErrorAction Stop
            if ($null -eq $journal -or [int]$journal.SchemaVersion -ne 1 -or
                -not [string]::Equals([string]$journal.State, 'pending', [StringComparison]::Ordinal) -or
                -not [string]::Equals([string]$journal.JumpListPath, $expectedJumpList, [StringComparison]::OrdinalIgnoreCase) -or
                -not [string]::Equals([string]$journal.BackupPath, $expectedBackupPath, [StringComparison]::OrdinalIgnoreCase) -or
                [string]$journal.BackupHash -notmatch '^[0-9A-Fa-f]{64}$') {
                throw 'transaction journal の schema・状態・パス・hash が不正です'
            }

            $originalPaths = @($journal.OriginalPaths)
            if ($originalPaths.Count -gt 250) {
                throw 'transaction journal の元のピン留め件数が上限を超えています'
            }
            foreach ($path in $originalPaths) {
                if ([string]::IsNullOrWhiteSpace([string]$path) -or ([string]$path).Length -gt 32767) {
                    throw 'transaction journal の元のピン留めパスが不正です'
                }
            }

            if (-not (Test-Path -LiteralPath $expectedBackupPath -PathType Leaf)) {
                throw 'transaction journal が参照する検証済みバックアップがありません'
            }
            $actualHash = (Get-FileHash -LiteralPath $expectedBackupPath -Algorithm SHA256 -ErrorAction Stop).Hash
            if (-not [string]::Equals($actualHash, [string]$journal.BackupHash, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'transaction journal 作成後にバックアップが変化しています'
            }

            return [pscustomobject]@{
                OriginalPaths = @($originalPaths | ForEach-Object { [string]$_ })
                BackupHash = [string]$journal.BackupHash
            }
        }

        function Save-PendingJournal(
            [string]$journalPath,
            [string]$jumpList,
            [string]$backupPath,
            [string]$backupHash,
            [string[]]$originalPaths) {
            $journal = [ordered]@{
                SchemaVersion = 1
                State = 'pending'
                JumpListPath = $jumpList
                BackupPath = $backupPath
                BackupHash = $backupHash
                OriginalPaths = @($originalPaths)
            }
            Write-AtomicUtf8Json $journalPath ($journal | ConvertTo-Json -Depth 4 -Compress)
            [void](Read-PendingJournal $journalPath $jumpList $backupPath)
        }

        function Remove-PendingJournal([string]$journalPath) {
            Remove-Item -LiteralPath $journalPath -Force -ErrorAction Stop
            if (Test-Path -LiteralPath $journalPath) {
                throw 'transaction journal を削除できませんでした'
            }
        }

        function Remove-AllPinned($shell) {
            for ($iteration = 0; $iteration -lt 250; $iteration++) {
                $remaining = @(Get-PinnedPaths $shell)
                if ($remaining.Count -eq 0) { return }

                $namespace = $shell.NameSpace($quickAccessNamespace)
                if ($null -eq $namespace) {
                    throw 'ピン留め解除中にクイックアクセス名前空間を取得できませんでした'
                }

                $unpinVerb = $null
                foreach ($item in @($namespace.Items())) {
                    $unpinVerb = Find-QuickAccessVerb -Verbs ($item.Verbs()) -WantUnpin $true
                    if ($null -ne $unpinVerb) { break }
                }
                if ($null -eq $unpinVerb) {
                    throw '残っているピン留め項目の解除操作を取得できませんでした'
                }

                [void]$unpinVerb.DoIt()
                Start-Sleep -Milliseconds 150
            }

            if (@(Get-PinnedPaths $shell).Count -ne 0) {
                throw 'ピン留め解除が安全上限の250回以内に完了しませんでした'
            }
        }

        function Add-PinnedPaths($shell, [string[]]$paths, [bool]$isRecovery) {
            foreach ($folderPath in @($paths)) {
                $pinnedSuccessfully = $false
                for ($attempt = 0; $attempt -lt 3; $attempt++) {
                    try {
                        $folder = $shell.NameSpace($folderPath)
                        if ($null -ne $folder -and $null -ne $folder.Self) {
                            $pinVerb = Find-QuickAccessVerb -Verbs ($folder.Self.Verbs()) -WantUnpin $false
                            if ($null -ne $pinVerb) {
                                [void]$pinVerb.DoIt()
                                $pinnedSuccessfully = $true
                                break
                            }
                        }
                    }
                    catch {
                        Add-Warning ('ピン留めに失敗 (' + $folderPath + '): ' + $_.Exception.Message)
                    }
                    Start-Sleep -Milliseconds 300
                }

                if (-not $pinnedSuccessfully) {
                    if ($isRecovery) {
                        Add-UnrecoveredPath $folderPath
                    }
                    else {
                        Add-FailedPath $folderPath
                    }
                    throw ('フォルダをピン留めできませんでした: ' + $folderPath)
                }
                Start-Sleep -Milliseconds 200
            }
        }

        function Restore-OriginalState(
            $shell,
            [string[]]$originalPaths,
            [string]$jumpList,
            [string]$backupPath,
            [string]$backupHash) {
            Restore-BackupFile $jumpList $backupPath $backupHash
            Start-Sleep -Milliseconds 1000

            $verificationShell = New-Object -ComObject Shell.Application
            $actual = @(Get-PinnedPaths $verificationShell)
            if (-not (Test-PathSequence $actual $originalPaths)) {
                Add-Warning 'バックアップの反映をShell verbで同期してから再検証します'
                Remove-AllPinned $shell
                Start-Sleep -Milliseconds 600
                Add-PinnedPaths $shell $originalPaths $true
                Start-Sleep -Milliseconds 600
                $actual = @(Get-PinnedPaths $shell)
                if (-not (Test-PathSequence $actual $originalPaths)) {
                    throw 'Shell verbによる元のピン留め状態の復元検証に失敗しました'
                }

                # Shell verbでジャンプリストが更新されたため、検証済みバックアップを最後に再適用する。
                Restore-BackupFile $jumpList $backupPath $backupHash
                Start-Sleep -Milliseconds 600
            }

            $finalShell = New-Object -ComObject Shell.Application
            $finalPaths = @(Get-PinnedPaths $finalShell)
            if (-not (Test-PathSequence $finalPaths $originalPaths)) {
                throw 'バックアップ復元後の実際のピン留め状態が元の並びと一致しません'
            }
            $finalHash = (Get-FileHash -LiteralPath $jumpList -Algorithm SHA256 -ErrorAction Stop).Hash
            if (-not [string]::Equals($finalHash, $backupHash, [StringComparison]::OrdinalIgnoreCase)) {
                throw '実状態検証中に復元済みジャンプリストが変化しました'
            }
        }

        function Invoke-QuickAccessSort {
            $shellLanguage = [Globalization.CultureInfo]::CurrentUICulture.TwoLetterISOLanguageName
            if ($shellLanguage -notin @('ja', 'en')) {
                throw ('Windows Shell の表示言語 ' + $shellLanguage +
                    ' ではピン留め操作を安全に識別できないため、変更しません')
            }

            $shell = New-Object -ComObject Shell.Application
            $jumpList = Join-Path $env:APPDATA 'Microsoft\Windows\Recent\AutomaticDestinations\f01b4d95cf55d32a.automaticDestinations-ms'
            $backupPath = $jumpList + '.lumin4tibak'
            $journalPath = $jumpList + '.lumin4tijournal.json'

            # watchdog / 利用者キャンセル / 異常終了で前回プロセスが失われても、
            # 変更前に確定した journal と SHA-256 検証済み backup から先に復元する。
            if (Test-Path -LiteralPath $journalPath) {
                $pending = $null
                try {
                    $pending = Read-PendingJournal $journalPath $jumpList $backupPath
                }
                catch {
                    throw ('前回中断された並べ替えの transaction journal を安全に検証できません。' +
                        'バックアップを保全したまま変更しません: ' + $_.Exception.Message)
                }

                $originalPaths = @($pending.OriginalPaths)
                $result.PinnedCount = $originalPaths.Count
                $result.BackupPath = $backupPath
                $result.BackupVerified = $true
                $result.MutationStarted = $true
                $result.RecoveryAttempted = $true
                try {
                    Restore-OriginalState $shell $originalPaths $jumpList $backupPath ([string]$pending.BackupHash)
                    $result.RestoredFromBackup = $true
                    $result.VerificationSucceeded = $true
                }
                catch {
                    foreach ($folderPath in $originalPaths) {
                        Add-UnrecoveredPath $folderPath
                    }
                    throw ('前回中断された並べ替えを検証済みバックアップから自動復元できませんでした: ' +
                        $_.Exception.Message)
                }

                Remove-PendingJournal $journalPath
                $result.SkipReason = 'recovered'
                return
            }

            $pinnedItems = @(Get-PinnedItems $shell)
            $pinned = @($pinnedItems | ForEach-Object { [string]$_.Path })
            $result.PinnedCount = $pinned.Count
            if ($pinned.Count -gt 250) {
                throw 'ピン留め件数が安全上限の250件を超えています'
            }
            if ($pinned.Count -le 1) {
                $result.SkipReason = 'single'
                return
            }

            $sorted = @($pinnedItems |
                Sort-Object @{ Expression = { [string]$_.Name }; Ascending = $true },
                            @{ Expression = { [string]$_.Path }; Ascending = $true } |
                ForEach-Object { [string]$_.Path })
            $alreadySorted = $true
            for ($index = 0; $index -lt $pinned.Count; $index++) {
                if (-not [string]::Equals($pinned[$index], $sorted[$index], [StringComparison]::Ordinal)) {
                    $alreadySorted = $false
                    break
                }
            }
            if ($alreadySorted) {
                $result.SkipReason = 'sorted'
                return
            }

            try {
                $backupHash = New-VerifiedBackup $jumpList $backupPath
                $result.BackupPath = $backupPath
                $result.BackupVerified = $true
            }
            catch {
                $result.BackupFailed = $true
                throw ('検証済みバックアップを作成できないため、ピン留め解除を開始しません: ' + $_.Exception.Message)
            }

            # backup と元の順序を永続化し、読み戻し検証に成功してからだけ変更する。
            Save-PendingJournal $journalPath $jumpList $backupPath $backupHash $pinned
            try {
                $result.MutationStarted = $true
                Remove-AllPinned $shell
                Start-Sleep -Milliseconds 600
                Add-PinnedPaths $shell $sorted $false
                Start-Sleep -Milliseconds 600

                $actual = @(Get-PinnedPaths $shell)
                if (-not (Test-PathSequence $actual $sorted)) {
                    foreach ($folderPath in $sorted) {
                        Add-FailedPath $folderPath
                    }
                    throw '並べ替え後の実際のピン留め順序が期待値と一致しません'
                }

                $result.SuccessCount = $pinned.Count
                $result.VerificationSucceeded = $true
                Remove-PendingJournal $journalPath
            }
            catch {
                $operationError = [string]$_.Exception.Message
                foreach ($folderPath in $sorted) {
                    Add-FailedPath $folderPath
                }
                $result.SuccessCount = 0
                $result.RecoveryAttempted = $true
                try {
                    Restore-OriginalState $shell $pinned $jumpList $backupPath $backupHash
                    $result.RestoredFromBackup = $true
                    $result.VerificationSucceeded = $true
                    try {
                        Remove-PendingJournal $journalPath
                    }
                    catch {
                        Add-Warning ('元状態は復元済みですが transaction journal を削除できません: ' + $_.Exception.Message)
                    }
                }
                catch {
                    $recoveryError = [string]$_.Exception.Message
                    foreach ($folderPath in $pinned) {
                        Add-UnrecoveredPath $folderPath
                    }
                    $result.RestoredFromBackup = $false
                    $result.VerificationSucceeded = $false
                    throw ('並べ替えに失敗し、元状態を自動復元できませんでした: ' +
                        $operationError + ' / 復元: ' + $recoveryError)
                }
                throw ('並べ替えに失敗しましたが、検証済みバックアップから元状態へ復元し、実状態を検証しました: ' +
                    $operationError)
            }
        }

        $exitCode = 0
        try {
            Invoke-QuickAccessSort
            $result.Status = 'ok'
        }
        catch {
            $result.Status = 'error'
            $result.Error = Limit-Text ([string]$_.Exception.Message) 4096
            $exitCode = 1
        }
        finally {
            try {
                if ([string]::IsNullOrWhiteSpace($env:LUMIN4TI_RESULT_PATH) -or
                    -not [IO.Path]::IsPathRooted($env:LUMIN4TI_RESULT_PATH)) {
                    throw '結果ファイルの絶対パスが不正です'
                }
                $resultDirectory = [IO.Path]::GetDirectoryName($env:LUMIN4TI_RESULT_PATH)
                [IO.Directory]::CreateDirectory($resultDirectory) | Out-Null
                $json = $result | ConvertTo-Json -Depth 5 -Compress
                [IO.File]::WriteAllText(
                    $env:LUMIN4TI_RESULT_PATH,
                    $json,
                    [Text.UTF8Encoding]::new($false))
            }
            catch {
                exit 2
            }
        }
        exit $exitCode
        """;

    public async Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // 変更前に medium process が永続 journal を確定するため、watchdog / キャンセルで
        // kill されても次回実行の先頭で検証済み backup から復旧できる。
        var execution = await _executor.RunPowerShellAsync(
            PowerShellScript,
            TransactionTimeout,
            ct).ConfigureAwait(false);
        if (!TryParseScriptResult(execution.ResultJson, out var result, out var parseFailure))
        {
            var reason = string.IsNullOrWhiteSpace(execution.Error)
                ? parseFailure
                : execution.Error;
            LoggerBootstrap.Log.Error($"{Id}: medium process の結果検証に失敗: {reason}");
            return MaintenanceActionResult.Fail($"クイックアクセスの並べ替えを安全に実行できませんでした: {Sanitize(reason)}");
        }

        var reachedSafeTerminal = !result.MutationStarted ||
            result.VerificationSucceeded &&
            (result.Status == "ok" || result.RestoredFromBackup);
        if (ct.IsCancellationRequested && reachedSafeTerminal)
        {
            ct.ThrowIfCancellationRequested();
        }

        if (!execution.Success || result.Status == "error")
        {
            var reason = result.Status == "error" && !string.IsNullOrWhiteSpace(result.Error)
                ? result.Error
                : execution.Error;
            LoggerBootstrap.Log.Error($"{Id}: medium process が失敗 (exit={execution.ExitCode}): {reason}");
            var recoveryStatus = result.MutationStarted
                ? result.RestoredFromBackup && result.VerificationSucceeded
                    ? " 元のピン留め状態は検証済みバックアップから自動復元し、実状態も確認済みです。"
                    : " 元のピン留め状態を自動復元できていません。バックアップを保全して手動確認してください。"
                : " ピン留め解除は開始していません。";
            return MaintenanceActionResult.Fail(
                $"クイックアクセスの並べ替えに失敗しました: {Sanitize(reason)}{recoveryStatus}");
        }

        var lines = new List<string>
        {
            $"  - 検出: ピン留め {result.PinnedCount} 件",
        };
        if (result.SkipReason == "single")
        {
            lines.Add("  - 対象が1件以下のためスキップ");
            return MaintenanceActionResult.Ok(lines);
        }

        if (result.SkipReason == "sorted")
        {
            lines.Add("  - 既に昇順のためスキップ");
            return MaintenanceActionResult.Ok(lines);
        }

        if (result.SkipReason == "recovered")
        {
            lines.Add("  - 前回中断された並べ替えを検証済みバックアップから復元し、実状態を確認しました");
            if (result.BackupPath is not null)
            {
                lines.Add($"  - 復元に使用したバックアップ: {Sanitize(result.BackupPath)}");
            }

            LoggerBootstrap.Log.Info($"{Id}: 前回中断された処理から元状態への復元と実状態検証に成功");
            return MaintenanceActionResult.Ok(lines);
        }

        lines.Add(
            $"  - 並び替えと実状態検証が完了: {result.SuccessCount} / 対象 {result.PinnedCount}");

        foreach (var warning in result.Warnings.Take(20))
        {
            lines.Add($"  - 警告: {Sanitize(warning)}");
        }

        if (result.BackupPath is not null)
        {
            lines.Add($"  - 検証済みバックアップを作成しました: {Sanitize(result.BackupPath)}");
        }

        LoggerBootstrap.Log.Info($"{Id}: 並び替えと実状態検証に成功 {result.SuccessCount} 件");
        return MaintenanceActionResult.Ok(lines);
    }

    // verb 名は OS 言語依存 (日本語 / 英語のみ対応。PowerShell 側と同じ判定をテストする)。
    internal static bool IsPinVerb(string? name) =>
        name is not null && ((name.Contains("ピン留め") && !name.Contains("外す")) || name.Contains("Pin to"));

    internal static bool IsUnpinVerb(string? name) =>
        name is not null && ((name.Contains("ピン留め") && name.Contains("外す")) || name.Contains("Unpin from"));

    internal static bool TryParseScriptResult(
        string? json,
        out QuickAccessScriptResult result,
        out string failureReason)
    {
        result = null!;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumResultCharacters)
        {
            failureReason = "medium process の結果が空または上限超過です";
            return false;
        }

        try
        {
            result = Lumin4tiJson.Deserialize<QuickAccessScriptResult>(json) ??
                throw new JsonException("結果 JSON が null です");
            if (result.SchemaVersion != 2 ||
                result.Status is not ("ok" or "error") ||
                result.PinnedCount is < 0 or > 10000 ||
                result.SuccessCount is < 0 or > 10000 ||
                result.FailedPaths is null ||
                result.UnrecoveredPaths is null ||
                result.Warnings is null ||
                result.FailedPaths.Length > 1000 ||
                result.UnrecoveredPaths.Length > 1000 ||
                result.Warnings.Length > 250 ||
                result.FailedPaths.Any(value => !IsBoundedText(value, 32767)) ||
                result.UnrecoveredPaths.Any(value => !IsBoundedText(value, 32767)) ||
                result.Warnings.Any(value => !IsBoundedText(value, 4096)) ||
                result.BackupPath is not null &&
                (!IsBoundedText(result.BackupPath, 32767) || !Path.IsPathFullyQualified(result.BackupPath)) ||
                result.Error is not null && !IsBoundedText(result.Error, 4096))
            {
                failureReason = "medium process の結果形式または値域が不正です";
                result = null!;
                return false;
            }

            if (result.BackupFailed &&
                (result.BackupVerified || result.BackupPath is not null || result.MutationStarted) ||
                result.BackupVerified != (result.BackupPath is not null) ||
                result.MutationStarted && !result.BackupVerified ||
                !result.MutationStarted &&
                (result.RecoveryAttempted || result.RestoredFromBackup || result.VerificationSucceeded) ||
                result.RestoredFromBackup &&
                (!result.RecoveryAttempted || !result.VerificationSucceeded))
            {
                failureReason = "バックアップ・変更・復元状態の整合性が不正です";
                result = null!;
                return false;
            }

            if (result.Status == "ok" && result.SkipReason is not null)
            {
                var isOrdinarySkip = result.SkipReason is "single" or "sorted";
                var isRecoveredInterruption = result.SkipReason == "recovered";
                var invalidOrdinarySkip = isOrdinarySkip &&
                    (result.BackupFailed ||
                     result.BackupVerified ||
                     result.MutationStarted ||
                     result.SuccessCount != 0 ||
                     result.FailedPaths.Length != 0 ||
                     result.UnrecoveredPaths.Length != 0);
                var invalidRecoveredInterruption = isRecoveredInterruption &&
                    (result.BackupFailed ||
                     !result.BackupVerified ||
                     !result.MutationStarted ||
                     !result.RecoveryAttempted ||
                     !result.RestoredFromBackup ||
                     !result.VerificationSucceeded ||
                     result.SuccessCount != 0 ||
                     result.FailedPaths.Length != 0 ||
                     result.UnrecoveredPaths.Length != 0);
                if ((!isOrdinarySkip && !isRecoveredInterruption) ||
                    invalidOrdinarySkip ||
                    invalidRecoveredInterruption)
                {
                    failureReason = "スキップ結果の整合性が不正です";
                    result = null!;
                    return false;
                }
            }
            else if (result.Status == "ok")
            {
                if (result.SkipReason is not null ||
                    result.BackupFailed ||
                    !result.BackupVerified ||
                    !result.MutationStarted ||
                    result.RecoveryAttempted ||
                    result.RestoredFromBackup ||
                    !result.VerificationSucceeded ||
                    result.SuccessCount != result.PinnedCount ||
                    result.FailedPaths.Length != 0 ||
                    result.UnrecoveredPaths.Length != 0)
                {
                    failureReason = "成功結果の検証・件数または復元状態が不正です";
                    result = null!;
                    return false;
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(result.Error))
                {
                    failureReason = "失敗結果にエラー理由がありません";
                    result = null!;
                    return false;
                }

                var invalidFailureState = result.SkipReason is not null ||
                    result.SuccessCount != 0 ||
                    !result.MutationStarted &&
                    (result.FailedPaths.Length != 0 ||
                     result.UnrecoveredPaths.Length != 0) ||
                    result.MutationStarted &&
                    (!result.RecoveryAttempted ||
                     result.RestoredFromBackup && result.UnrecoveredPaths.Length != 0 ||
                     !result.RestoredFromBackup &&
                     (result.VerificationSucceeded || result.UnrecoveredPaths.Length == 0));
                if (invalidFailureState)
                {
                    failureReason = "失敗結果の変更・復元状態が不正です";
                    result = null!;
                    return false;
                }
            }

            failureReason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            failureReason = $"medium process の結果 JSON を解析できません: {ex.Message}";
            result = null!;
            return false;
        }
    }

    private static bool IsBoundedText(string? value, int maximumLength) =>
        value is not null && value.Length <= maximumLength && !value.Contains('\0');

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "詳細なし";
        }

        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ').Trim();
        return sanitized.Length <= 4096 ? sanitized : sanitized[..4096] + "…";
    }
}

internal sealed class QuickAccessScriptResult
{
    public int SchemaVersion { get; set; }

    public string? Status { get; set; }

    public int PinnedCount { get; set; }

    public string? SkipReason { get; set; }

    public string? BackupPath { get; set; }

    public bool BackupFailed { get; set; }

    public bool BackupVerified { get; set; }

    public bool MutationStarted { get; set; }

    public bool RecoveryAttempted { get; set; }

    public bool RestoredFromBackup { get; set; }

    public bool VerificationSucceeded { get; set; }

    public int SuccessCount { get; set; }

    public string[] FailedPaths { get; set; } = [];

    public string[] UnrecoveredPaths { get; set; } = [];

    public string[] Warnings { get; set; } = [];

    public string? Error { get; set; }
}
