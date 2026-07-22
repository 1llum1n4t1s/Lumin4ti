using System.ComponentModel;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Lumin4ti.Core.Services;

internal interface IUnelevatedCommandExecutor
{
    Task<UnelevatedCommandExecutionResult> RunPowerShellAsync(
        string script,
        CancellationToken ct = default);

    Task<UnelevatedCommandExecutionResult> RunPowerShellAsync(
        string script,
        TimeSpan timeout,
        CancellationToken ct = default);
}

internal sealed record UnelevatedCommandExecutionResult(
    bool Success,
    int ExitCode,
    string? ResultJson,
    string Error);

/// <summary>
/// 対話中 Explorer の検証済み medium token で、System32 の Windows PowerShell を起動する。
/// 昇格プロセスの環境は継承せず、Quick Access の Shell COM 操作を高整合性境界の外へ隔離する。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class UnelevatedCommandExecutor : IUnelevatedCommandExecutor
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Guid FolderIdProfile = new("5E6C858F-0E22-4760-9AFE-EA3317B67173");
    private static readonly Guid FolderIdRoamingAppData = new("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D");
    private static readonly Guid FolderIdLocalAppData = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
    private static readonly Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid IidDispatch = new("00020400-0000-0000-C000-000000000046");
    private static readonly TimeSpan ExplorerBrokerStartupTimeout = TimeSpan.FromSeconds(10);

    private const uint ProcessTerminate = 0x00000001;
    private const uint ProcessSetQuota = 0x00000100;
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const uint Synchronize = 0x00100000;
    private const uint ShellProcessAccessMask = ProcessQueryLimitedInformation | Synchronize;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenImpersonate = 0x0004;
    private const uint TokenQuery = 0x0008;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenIntegrityLevel = 25;
    private const int TokenIsAppContainer = 29;
    private const uint SecurityMandatoryMediumRid = 0x00002000;
    private const int ErrorInsufficientBuffer = 122;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const uint ProcessPollMilliseconds = 250;
    private const uint TerminationWaitMilliseconds = 5000;
    private const uint ExplorerBrokerPollMilliseconds = 50;
    private const uint ClassContextLocalServer = 0x00000004;
    private const uint CoInitApartmentThreaded = 0x00000002;
    private const uint CoInitDisableOle1Dde = 0x00000004;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const int ShellWindowDesktop = 8;
    private const int ShellWindowNeedDispatch = 1;
    private const int MaximumCommandLineCharacters = 1024;
    private const int MaximumEnvironmentCharacters = 32767;
    private const int MaximumResultBytes = 1024 * 1024;
    private const string MicrosoftWindowsCommonName = "Microsoft Windows";
    private const string ScriptEnvironmentVariable = "LUMIN4TI_SCRIPT_GZIP_B64";
    private const string ResultEnvironmentVariable = "LUMIN4TI_RESULT_PATH";

    private const string PowerShellBootstrap =
        "$b=[Convert]::FromBase64String($env:" + ScriptEnvironmentVariable + ");" +
        "$m=[IO.MemoryStream]::new($b);" +
        "$g=[IO.Compression.GZipStream]::new($m,[IO.Compression.CompressionMode]::Decompress);" +
        "$r=[IO.StreamReader]::new($g,[Text.Encoding]::Unicode);" +
        "&([ScriptBlock]::Create($r.ReadToEnd()))";

    private readonly TimeSpan _timeout;

    public UnelevatedCommandExecutor() : this(DefaultTimeout)
    {
    }

    internal UnelevatedCommandExecutor(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _timeout = timeout;
    }

    public async Task<UnelevatedCommandExecutionResult> RunPowerShellAsync(
        string script,
        CancellationToken ct = default)
        => await RunPowerShellAsync(script, _timeout, ct).ConfigureAwait(false);

    public async Task<UnelevatedCommandExecutionResult> RunPowerShellAsync(
        string script,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return new(false, -1, null, "実行する PowerShell script が空です");
        }

        if (timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
        {
            return new(false, -1, null, "timeout は正の値または Infinite である必要があります");
        }

        try
        {
            return await RunCoreAsync(script, timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, -1, null, ex.Message);
        }
    }

    private async Task<UnelevatedCommandExecutionResult> RunCoreAsync(
        string script,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var windowsDirectory = RequireSystemFolder(Environment.SpecialFolder.Windows, "Windows");
        var systemDirectory = RequireSystemFolder(Environment.SpecialFolder.System, "System32");
        var programData = RequireSystemFolder(Environment.SpecialFolder.CommonApplicationData, "ProgramData");
        var programFiles = RequireSystemFolder(Environment.SpecialFolder.ProgramFiles, "Program Files");
        var commonProgramFiles = RequireSystemFolder(
            Environment.SpecialFolder.CommonProgramFiles,
            "Common Program Files");
        var expectedExplorer = Path.Combine(windowsDirectory, "explorer.exe");
        var powerShellPath = Path.Combine(
            systemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        EnsureRegularExecutable(powerShellPath, "Windows PowerShell");

        var shellWindow = GetShellWindow();
        if (shellWindow == nint.Zero || GetWindowThreadProcessId(shellWindow, out var shellProcessId) == 0 ||
            shellProcessId == 0)
        {
            throw new InvalidOperationException("対話中 Explorer のプロセスを特定できませんでした");
        }

        if (!ProcessIdToSessionId(shellProcessId, out var shellSessionId) ||
            !ProcessIdToSessionId(GetCurrentProcessId(), out var currentSessionId) ||
            shellSessionId != currentSessionId)
        {
            throw new InvalidOperationException("対話中 Explorer と現在のセッションが一致しません");
        }

        using var shellProcess = OpenProcess(ShellProcessAccessMask, inheritHandle: false, shellProcessId);
        if (shellProcess.IsInvalid)
        {
            throw NewWin32Exception("対話中 Explorer を開けませんでした");
        }

        var actualExplorer = QueryProcessPath(shellProcess);
        if (!IsExpectedShellPath(actualExplorer, windowsDirectory))
        {
            throw new InvalidOperationException($"対話シェルの実体が explorer.exe ではありません: {actualExplorer}");
        }

        EnsureMicrosoftWindowsExecutable(expectedExplorer, "対話中 Explorer");

        const uint sourceTokenAccess = TokenQuery | TokenDuplicate | TokenImpersonate;
        if (!OpenProcessToken(shellProcess, sourceTokenAccess, out var shellToken))
        {
            throw NewWin32Exception("対話中 Explorer の token を開けませんでした");
        }

        using (shellToken)
        {
            EnsureAllowedToken(shellToken);
            using var primaryToken = DuplicatePrimaryToken(shellToken, "対話中 Explorer");
            EnsureAllowedToken(primaryToken);
            var folders = new ShellKnownFolders(
                GetKnownFolderPath(FolderIdProfile, primaryToken, "ユーザープロファイル"),
                GetKnownFolderPath(FolderIdRoamingAppData, primaryToken, "AppData"),
                GetKnownFolderPath(FolderIdLocalAppData, primaryToken, "LocalAppData"));
            var resultPath = BuildResultPath(
                folders.LocalAppData,
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

            try
            {
                var environment = CreateSafeEnvironmentValues(
                    windowsDirectory,
                    systemDirectory,
                    programData,
                    programFiles,
                    commonProgramFiles,
                    folders,
                    script,
                    resultPath);
                var environmentBlock = BuildEnvironmentBlock(environment);
                var commandLine = BuildPowerShellCommandLine(powerShellPath);

                EnsureShellStillCurrent(shellProcessId, shellProcess);
                ProcessWaitResult processResult;
                try
                {
                    processResult = await StartAndWaitAsync(
                        primaryToken,
                        powerShellPath,
                        commandLine,
                        environmentBlock,
                        systemDirectory,
                        timeout,
                        ct).ConfigureAwait(false);
                }
                catch (MediumProcessLaunchException tokenLaunchException)
                {
                    try
                    {
                        processResult = await StartWithExplorerBrokerAsync(
                            shellToken,
                            shellProcessId,
                            shellSessionId,
                            shellProcess,
                            powerShellPath,
                            systemDirectory,
                            folders.LocalAppData,
                            script,
                            resultPath,
                            timeout,
                            ct).ConfigureAwait(false);
                    }
                    catch (Exception brokerException) when (brokerException is not OperationCanceledException)
                    {
                        throw new InvalidOperationException(
                            $"{tokenLaunchException.Message}; " +
                            $"Explorer broker での medium process 起動にも失敗しました: {brokerException.Message}",
                            brokerException);
                    }
                }

                string? resultJson = null;
                string? resultReadError = null;
                try
                {
                    resultJson = WindowsIdentity.RunImpersonated(
                        primaryToken,
                        () => ReadResultFile(resultPath));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
                {
                    resultReadError = ex.Message;
                }

                if (processResult.TimedOut)
                {
                    return new(
                        false,
                        processResult.ExitCode,
                        resultJson,
                        $"非昇格操作が {timeout.TotalMinutes:0.#} 分でタイムアウトしました");
                }

                var success = processResult.ExitCode == 0 && resultJson is not null;
                var error = success
                    ? string.Empty
                    : resultReadError is not null
                        ? $"medium process の結果を読み取れませんでした: {resultReadError}"
                        : $"medium process が失敗しました (exit={processResult.ExitCode})";
                return new(success, processResult.ExitCode, resultJson, error);
            }
            finally
            {
                TryDeleteResultFile(primaryToken, resultPath);
            }
        }
    }

    private async Task<ProcessWaitResult> StartAndWaitAsync(
        SafeAccessTokenHandle primaryToken,
        string applicationPath,
        string commandLine,
        string environmentBlock,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var environmentBytes = Encoding.Unicode.GetBytes(environmentBlock);
        var environmentPointer = Marshal.AllocHGlobal(environmentBytes.Length);
        try
        {
            Marshal.Copy(environmentBytes, 0, environmentPointer, environmentBytes.Length);
            var processInformation = CreateMediumProcess(
                primaryToken,
                applicationPath,
                commandLine,
                environmentPointer,
                workingDirectory);

            using var processHandle = new SafeKernelHandle(processInformation.ProcessHandle, ownsHandle: true);
            using var threadHandle = new SafeKernelHandle(processInformation.ThreadHandle, ownsHandle: true);
            ProcessJobTracker.Track(processHandle.DangerousGetHandle());

            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout != Timeout.InfiniteTimeSpan)
            {
                executionCts.CancelAfter(timeout);
            }
            await Task.Run(
                () =>
                {
                    while (true)
                    {
                        var waitResult = WaitForSingleObject(processHandle, ProcessPollMilliseconds);
                        if (waitResult == WaitObject0)
                        {
                            return;
                        }

                        if (waitResult == WaitFailed)
                        {
                            throw NewWin32Exception("medium process の終了待機に失敗しました");
                        }

                        if (waitResult != WaitTimeout)
                        {
                            throw new InvalidOperationException(
                                $"medium process の待機結果が不正です: 0x{waitResult:X8}");
                        }

                        if (!executionCts.IsCancellationRequested)
                        {
                            continue;
                        }

                        // process creation API が返した full-access handle で停止する。
                        // 失敗しても既に終了した可能性を考慮して、有限時間だけ終了を再確認する。
                        _ = TerminateProcess(processHandle, 1);
                        var terminationWait = WaitForSingleObject(
                            processHandle,
                            TerminationWaitMilliseconds);
                        if (terminationWait != WaitObject0)
                        {
                            throw terminationWait == WaitFailed
                                ? NewWin32Exception("キャンセルした medium process の終了待機に失敗しました")
                                : new InvalidOperationException(
                                    "キャンセルした medium process を5秒以内に終了できませんでした");
                        }

                        return;
                    }
                },
                CancellationToken.None).ConfigureAwait(false);

            if (!GetExitCodeProcess(processHandle, out var exitCode))
            {
                throw NewWin32Exception("medium process の終了コードを取得できませんでした");
            }

            if (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            return new(unchecked((int)exitCode), executionCts.IsCancellationRequested);
        }
        finally
        {
            Marshal.FreeHGlobal(environmentPointer);
        }
    }

    private static async Task<ProcessWaitResult> StartWithExplorerBrokerAsync(
        SafeAccessTokenHandle shellToken,
        uint shellProcessId,
        uint shellSessionId,
        SafeKernelHandle shellProcess,
        string applicationPath,
        string workingDirectory,
        string localAppData,
        string script,
        string resultPath,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var nonce = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var brokerFiles = BuildExplorerBrokerPaths(localAppData, nonce);
        var launcherScript = BuildExplorerBrokerLauncherScript(
            script,
            resultPath,
            brokerFiles.ProcessIdPath);

        SafeKernelHandle? launchedProcess = null;
        try
        {
            WindowsIdentity.RunImpersonated(
                shellToken,
                () =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(brokerFiles.LauncherPath)!);
                    File.WriteAllText(brokerFiles.LauncherPath, launcherScript, StrictUtf8);
                });

            ct.ThrowIfCancellationRequested();
            EnsureShellStillCurrent(shellProcessId, shellProcess);
            ExecuteInExplorerProcess(
                shellProcessId,
                applicationPath,
                BuildExplorerBrokerArguments(brokerFiles.LauncherPath),
                workingDirectory);

            var startedAt = DateTime.UtcNow;
            var deadline = timeout == Timeout.InfiniteTimeSpan
                ? (DateTime?)null
                : startedAt + timeout;
            var startupDeadline = startedAt + ExplorerBrokerStartupTimeout;

            while (true)
            {
                launchedProcess ??= TryOpenExplorerBrokerProcess(
                    shellToken,
                    shellSessionId,
                    applicationPath,
                    brokerFiles.ProcessIdPath);

                if (UserFileExists(shellToken, resultPath))
                {
                    if (launchedProcess is null)
                    {
                        return new(0, false);
                    }

                    var finalWait = WaitForSingleObject(launchedProcess, TerminationWaitMilliseconds);
                    if (finalWait == WaitObject0)
                    {
                        return new(GetProcessExitCode(launchedProcess), false);
                    }

                    if (finalWait == WaitFailed)
                    {
                        throw NewWin32Exception("Explorer broker が起動した process の終了待機に失敗しました");
                    }

                    return new(0, false);
                }

                if (launchedProcess is not null)
                {
                    var waitResult = WaitForSingleObject(launchedProcess, 0);
                    if (waitResult == WaitObject0)
                    {
                        return new(GetProcessExitCode(launchedProcess), false);
                    }

                    if (waitResult == WaitFailed)
                    {
                        throw NewWin32Exception("Explorer broker が起動した process の待機に失敗しました");
                    }
                }

                if (deadline is { } timeoutAt && DateTime.UtcNow >= timeoutAt)
                {
                    TerminateBrokerProcess(launchedProcess);
                    return new(-1, true);
                }

                if (launchedProcess is null && DateTime.UtcNow >= startupDeadline)
                {
                    throw new InvalidOperationException(
                        "Explorer broker は Windows PowerShell の起動確認を返しませんでした");
                }

                if (ct.IsCancellationRequested)
                {
                    TerminateBrokerProcess(launchedProcess);
                    ct.ThrowIfCancellationRequested();
                }

                await Task.Delay((int)ExplorerBrokerPollMilliseconds, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            launchedProcess?.Dispose();
            TryDeleteResultFile(shellToken, brokerFiles.ProcessIdPath);
            TryDeleteResultFile(shellToken, brokerFiles.LauncherPath);
        }
    }

    private static SafeKernelHandle? TryOpenExplorerBrokerProcess(
        SafeAccessTokenHandle shellToken,
        uint shellSessionId,
        string expectedApplicationPath,
        string processIdPath)
    {
        var processIdText = WindowsIdentity.RunImpersonated(
            shellToken,
            () => File.Exists(processIdPath) ? File.ReadAllText(processIdPath, StrictUtf8) : null);
        if (processIdText is null)
        {
            return null;
        }

        if (processIdText.Length is 0 or > 16 ||
            !uint.TryParse(processIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) ||
            processId == 0)
        {
            // WriteAllText の作成直後を観測した場合は一時的に空または途中のため、次の poll で再読する。
            return null;
        }

        const uint jobCompatibleAccess =
            ProcessTerminate | ProcessSetQuota | ProcessQueryLimitedInformation | Synchronize;
        var process = OpenProcess(jobCompatibleAccess, inheritHandle: false, processId);
        if (process.IsInvalid)
        {
            process.Dispose();
            const uint minimumAccess = ProcessTerminate | ProcessQueryLimitedInformation | Synchronize;
            process = OpenProcess(minimumAccess, inheritHandle: false, processId);
        }

        if (process.IsInvalid)
        {
            process.Dispose();
            return null;
        }

        try
        {
            var actualApplicationPath = QueryProcessPath(process);
            if (!Path.GetFullPath(actualApplicationPath)
                    .Equals(Path.GetFullPath(expectedApplicationPath), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Explorer broker が想定外の実行ファイルを起動したため停止しました");
            }

            if (!ProcessIdToSessionId(processId, out var processSessionId) ||
                processSessionId != shellSessionId)
            {
                throw new InvalidOperationException(
                    "Explorer broker が対話セッション外へ process を起動したため停止しました");
            }

            if (!OpenProcessToken(process, TokenQuery, out var processToken))
            {
                throw NewWin32Exception("Explorer broker が起動した process の token を開けませんでした");
            }

            using (processToken)
            {
                EnsureAllowedToken(processToken);
                if (!TokensRepresentSameUser(shellToken, processToken))
                {
                    throw new InvalidOperationException(
                        "Explorer broker が別ユーザーの process を起動したため停止しました");
                }
            }

            ProcessJobTracker.Track(process.DangerousGetHandle());
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static bool UserFileExists(SafeAccessTokenHandle token, string path) =>
        WindowsIdentity.RunImpersonated(token, () => File.Exists(path));

    private static int GetProcessExitCode(SafeKernelHandle process)
    {
        if (!GetExitCodeProcess(process, out var exitCode))
        {
            throw NewWin32Exception("Explorer broker が起動した process の終了コードを取得できませんでした");
        }

        return unchecked((int)exitCode);
    }

    private static void TerminateBrokerProcess(SafeKernelHandle? process)
    {
        if (process is null || process.IsInvalid || process.IsClosed)
        {
            return;
        }

        _ = TerminateProcess(process, 1);
        _ = WaitForSingleObject(process, TerminationWaitMilliseconds);
    }

    private static void EnsureShellStillCurrent(
        uint expectedProcessId,
        SafeKernelHandle shellProcess)
    {
        // プロセスハンドルは SYNCHRONIZE 付きで開く必要がある。権限不足による
        // WAIT_FAILED を Explorer 終了と誤認せず、実際の Win32 エラーとして扱う。
        var processWaitResult = WaitForSingleObject(shellProcess, 0);
        if (processWaitResult == WaitFailed)
        {
            throw NewWin32Exception("対話中 Explorer の稼働状態を確認できませんでした");
        }

        var currentWindow = GetShellWindow();
        if (processWaitResult != WaitTimeout ||
            currentWindow == nint.Zero ||
            GetWindowThreadProcessId(currentWindow, out var currentProcessId) == 0 ||
            currentProcessId != expectedProcessId)
        {
            throw new InvalidOperationException("検証中に対話中 Explorer が変更または終了しました");
        }
    }

    private static void EnsureAllowedToken(SafeAccessTokenHandle token)
    {
        var integrityRid = GetTokenIntegrityRid(token);
        if (integrityRid > SecurityMandatoryMediumRid)
        {
            throw new InvalidOperationException(
                $"対話中 Explorer の整合性レベルが medium を超えています (RID=0x{integrityRid:X})");
        }

        var isAppContainer = GetTokenUInt32(token, TokenIsAppContainer);
        if (isAppContainer != 0)
        {
            throw new InvalidOperationException("対話中 Explorer の token が AppContainer のため使用できません");
        }
    }

    private static SafeAccessTokenHandle DuplicatePrimaryToken(
        SafeAccessTokenHandle token,
        string tokenDescription)
    {
        const uint duplicatedTokenAccess =
            TokenAssignPrimary | TokenDuplicate | TokenImpersonate | TokenQuery;
        if (!DuplicateTokenEx(
                token,
                duplicatedTokenAccess,
                nint.Zero,
                SecurityImpersonation,
                TokenPrimary,
                out var primaryToken))
        {
            throw NewWin32Exception($"{tokenDescription} の primary token を複製できませんでした");
        }

        return primaryToken;
    }

    private static bool TokensRepresentSameUser(
        SafeAccessTokenHandle left,
        SafeAccessTokenHandle right)
    {
        using var leftIdentity = new WindowsIdentity(left.DangerousGetHandle());
        using var rightIdentity = new WindowsIdentity(right.DangerousGetHandle());
        return leftIdentity.User is { } leftUser &&
               rightIdentity.User is { } rightUser &&
               leftUser.Equals(rightUser);
    }

    private static uint GetTokenIntegrityRid(SafeAccessTokenHandle token)
    {
        _ = GetTokenInformation(token, TokenIntegrityLevel, nint.Zero, 0, out var requiredBytes);
        if (requiredBytes == 0 || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
        {
            throw NewWin32Exception("token の整合性レベルサイズを取得できませんでした");
        }

        var buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, requiredBytes, out _))
            {
                throw NewWin32Exception("token の整合性レベルを取得できませんでした");
            }

            var sid = Marshal.ReadIntPtr(buffer);
            if (sid == nint.Zero || !IsValidSid(sid))
            {
                throw new InvalidOperationException("token の整合性 SID が不正です");
            }

            var countPointer = GetSidSubAuthorityCount(sid);
            if (countPointer == nint.Zero)
            {
                throw NewWin32Exception("token の整合性 SID を解析できませんでした");
            }

            var count = Marshal.ReadByte(countPointer);
            if (count == 0)
            {
                throw new InvalidOperationException("token の整合性 SID に sub-authority がありません");
            }

            var ridPointer = GetSidSubAuthority(sid, (uint)(count - 1));
            if (ridPointer == nint.Zero)
            {
                throw NewWin32Exception("token の整合性 RID を取得できませんでした");
            }

            return unchecked((uint)Marshal.ReadInt32(ridPointer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static uint GetTokenUInt32(SafeAccessTokenHandle token, int informationClass)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            if (!GetTokenInformation(token, informationClass, buffer, sizeof(uint), out var returnedBytes) ||
                returnedBytes != sizeof(uint))
            {
                throw NewWin32Exception("token 情報を取得できませんでした");
            }

            return unchecked((uint)Marshal.ReadInt32(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string QueryProcessPath(SafeKernelHandle process)
    {
        const int maximumPathCharacters = 32768;
        var builder = new StringBuilder(maximumPathCharacters);
        var size = (uint)builder.Capacity;
        if (!QueryFullProcessImageNameW(process, 0, builder, ref size) || size == 0)
        {
            throw NewWin32Exception("対話中 Explorer の実体パスを取得できませんでした");
        }

        return builder.ToString();
    }

    private static void EnsureMicrosoftWindowsExecutable(string path, string description)
    {
        EnsureRegularExecutable(path, description);

        if (!ExecutableTrustVerifier.TryVerify(path, MicrosoftWindowsCommonName, out var failureReason))
        {
            throw new InvalidOperationException($"{description} の Microsoft Windows 署名を検証できません: {failureReason}");
        }
    }

    private static void EnsureRegularExecutable(string path, string description)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"{description} の安全な実体を確認できませんでした: {path}");
        }
    }

    internal static bool IsExpectedShellPath(string? candidate, string? windowsDirectory)
    {
        if (string.IsNullOrWhiteSpace(candidate) ||
            string.IsNullOrWhiteSpace(windowsDirectory) ||
            !Path.IsPathFullyQualified(candidate) ||
            !Path.IsPathFullyQualified(windowsDirectory))
        {
            return false;
        }

        try
        {
            var expected = Path.GetFullPath(Path.Combine(windowsDirectory, "explorer.exe"));
            return Path.GetFullPath(candidate).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal static uint GetShellProcessAccessMask() => ShellProcessAccessMask;

    internal static string BuildResultPath(string localAppData, string nonce)
    {
        if (string.IsNullOrWhiteSpace(localAppData) ||
            !Path.IsPathFullyQualified(localAppData) ||
            nonce.Length != 32 ||
            !nonce.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("結果ファイルの基準パスまたは nonce が不正です");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localAppData));
        var resultDirectory = Path.GetFullPath(Path.Combine(root, "Lumin4ti", "command-results"));
        var resultPath = Path.GetFullPath(Path.Combine(resultDirectory, $"operation-{nonce}.json"));
        if (!IsPathInside(resultPath, root))
        {
            throw new ArgumentException("結果ファイルが LocalAppData の外を指しています");
        }

        return resultPath;
    }

    private static ExplorerBrokerPaths BuildExplorerBrokerPaths(string localAppData, string nonce)
    {
        if (string.IsNullOrWhiteSpace(localAppData) ||
            !Path.IsPathFullyQualified(localAppData) ||
            nonce.Length != 32 ||
            !nonce.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Explorer broker の基準パスまたは nonce が不正です");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localAppData));
        var launcherDirectory = Path.GetFullPath(Path.Combine(root, "Lumin4ti", "medium-launchers"));
        var launcherPath = Path.GetFullPath(
            Path.Combine(launcherDirectory, $"operation-{nonce}.ps1"));
        var processIdPath = Path.GetFullPath(
            Path.Combine(launcherDirectory, $"operation-{nonce}.pid"));
        if (!IsPathInside(launcherPath, root) || !IsPathInside(processIdPath, root))
        {
            throw new ArgumentException("Explorer broker の一時ファイルが LocalAppData の外を指しています");
        }

        return new(launcherPath, processIdPath);
    }

    internal static string BuildExplorerBrokerLauncherScript(
        string script,
        string resultPath,
        string processIdPath)
    {
        if (string.IsNullOrWhiteSpace(script) ||
            !Path.IsPathFullyQualified(resultPath) ||
            !Path.IsPathFullyQualified(processIdPath))
        {
            throw new ArgumentException("Explorer broker の script または出力パスが不正です");
        }

        var encodedScript = EncodeScriptForEnvironment(script);
        var escapedResultPath = EscapePowerShellSingleQuotedString(resultPath);
        var escapedProcessIdPath = EscapePowerShellSingleQuotedString(processIdPath);
        return
            "$ErrorActionPreference='Stop'\r\n" +
            $"[IO.File]::WriteAllText('{escapedProcessIdPath}',[string]$PID,[Text.UTF8Encoding]::new($false))\r\n" +
            $"$env:{ScriptEnvironmentVariable}='{encodedScript}'\r\n" +
            $"$env:{ResultEnvironmentVariable}='{escapedResultPath}'\r\n" +
            PowerShellBootstrap;
    }

    internal static string BuildExplorerBrokerArguments(string launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath) ||
            !Path.IsPathFullyQualified(launcherPath) ||
            launcherPath.Contains('"') ||
            launcherPath.Contains('\r') ||
            launcherPath.Contains('\n'))
        {
            throw new ArgumentException("Explorer broker の launcher path が不正です", nameof(launcherPath));
        }

        return
            $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass " +
            $"-WindowStyle Hidden -File \"{launcherPath}\"";
    }

    private static string EscapePowerShellSingleQuotedString(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    internal static string BuildPowerShellCommandLine(string powerShellPath)
    {
        if (string.IsNullOrWhiteSpace(powerShellPath) || !Path.IsPathFullyQualified(powerShellPath) ||
            powerShellPath.Contains('"'))
        {
            throw new ArgumentException("Windows PowerShell の絶対パスが不正です", nameof(powerShellPath));
        }

        var encodedBootstrap = Convert.ToBase64String(Encoding.Unicode.GetBytes(PowerShellBootstrap));
        var commandLine = $"\"{powerShellPath}\" -NoLogo -NoProfile -NonInteractive -EncodedCommand {encodedBootstrap}";
        if (commandLine.Length >= MaximumCommandLineCharacters)
        {
            throw new InvalidOperationException("CreateProcessWithTokenW の command line 上限を超えています");
        }

        return commandLine;
    }

    internal static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0)
        {
            throw new ArgumentException("環境ブロックが空です", nameof(values));
        }

        var entries = new List<string>(values.Count);
        foreach (var (name, value) in values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('=') || name.Contains('\0') || value.Contains('\0'))
            {
                throw new ArgumentException($"環境変数が不正です: {name}", nameof(values));
            }

            entries.Add($"{name}={value}");
        }

        var block = string.Join('\0', entries) + "\0\0";
        if (block.Length > MaximumEnvironmentCharacters)
        {
            throw new InvalidOperationException("medium process の環境ブロックが上限を超えています");
        }

        return block;
    }

    private static Dictionary<string, string> CreateSafeEnvironmentValues(
        string windowsDirectory,
        string systemDirectory,
        string programData,
        string programFiles,
        string commonProgramFiles,
        ShellKnownFolders folders,
        string script,
        string resultPath)
    {
        var systemDrive = Path.GetPathRoot(windowsDirectory)?.TrimEnd(Path.DirectorySeparatorChar)
            ?? throw new InvalidOperationException("Windows ドライブを特定できませんでした");
        var homeDrive = Path.GetPathRoot(folders.UserProfile)?.TrimEnd(Path.DirectorySeparatorChar)
            ?? throw new InvalidOperationException("ユーザープロファイルのドライブを特定できませんでした");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windowsDirectory,
            ["windir"] = windowsDirectory,
            ["SystemDrive"] = systemDrive,
            ["ComSpec"] = Path.Combine(systemDirectory, "cmd.exe"),
            ["TEMP"] = Path.Combine(folders.LocalAppData, "Temp"),
            ["TMP"] = Path.Combine(folders.LocalAppData, "Temp"),
            ["PATH"] = string.Join(
                Path.PathSeparator,
                systemDirectory,
                windowsDirectory,
                Path.Combine(systemDirectory, "Wbem"),
                Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0")),
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            ["PSModulePath"] = string.Join(
                Path.PathSeparator,
                Path.Combine(programFiles, "WindowsPowerShell", "Modules"),
                Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "Modules")),
            ["USERPROFILE"] = folders.UserProfile,
            ["HOMEDRIVE"] = homeDrive,
            ["HOMEPATH"] = Path.DirectorySeparatorChar + Path.GetRelativePath(
                Path.GetPathRoot(folders.UserProfile)!,
                folders.UserProfile),
            ["APPDATA"] = folders.RoamingAppData,
            ["LOCALAPPDATA"] = folders.LocalAppData,
            ["ProgramData"] = programData,
            ["ALLUSERSPROFILE"] = programData,
            ["ProgramFiles"] = programFiles,
            ["ProgramW6432"] = programFiles,
            ["CommonProgramFiles"] = commonProgramFiles,
            ["CommonProgramW6432"] = commonProgramFiles,
            ["OS"] = "Windows_NT",
            ["PROCESSOR_ARCHITECTURE"] = "AMD64",
            ["NUMBER_OF_PROCESSORS"] = Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture),
            ["DOTNET_EnableDiagnostics"] = "0",
            ["DOTNET_EnableDiagnostics_IPC"] = "0",
            ["DOTNET_EnableDiagnostics_Profiler"] = "0",
            ["DOTNET_EnableDiagnostics_Debugger"] = "0",
            ["CORECLR_ENABLE_PROFILING"] = "0",
            ["COR_ENABLE_PROFILING"] = "0",
            ["COMPlus_EnableDiagnostics"] = "0",
            [ScriptEnvironmentVariable] = EncodeScriptForEnvironment(script),
            [ResultEnvironmentVariable] = resultPath,
        };

        AddOptionalSystemFolder(values, "ProgramFiles(x86)", Environment.SpecialFolder.ProgramFilesX86);
        AddOptionalSystemFolder(values, "CommonProgramFiles(x86)", Environment.SpecialFolder.CommonProgramFilesX86);
        return values;
    }

    internal static string EncodeScriptForEnvironment(string script)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        var scriptBytes = Encoding.Unicode.GetBytes(script);
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(
                   compressed,
                   CompressionLevel.SmallestSize,
                   leaveOpen: true))
        {
            gzip.Write(scriptBytes);
        }

        return Convert.ToBase64String(compressed.ToArray());
    }

    private static string GetKnownFolderPath(
        Guid folderId,
        SafeAccessTokenHandle token,
        string description)
    {
        var pathPointer = nint.Zero;
        try
        {
            var result = SHGetKnownFolderPath(in folderId, 0, token, out pathPointer);
            if (result < 0 || pathPointer == nint.Zero)
            {
                Marshal.ThrowExceptionForHR(result);
            }

            var path = Marshal.PtrToStringUni(pathPointer);
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                throw new InvalidOperationException($"{description} の正規パスを特定できませんでした");
            }

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        finally
        {
            if (pathPointer != nint.Zero)
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }
    }

    private static string RequireSystemFolder(Environment.SpecialFolder folder, string description)
    {
        var path = Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException($"{description} の正規パスを特定できませんでした");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void AddOptionalSystemFolder(
        IDictionary<string, string> values,
        string variableName,
        Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
        {
            values[variableName] = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
    }

    private static string ReadResultFile(string path)
    {
        using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > MaximumResultBytes)
            {
                throw new InvalidDataException("medium process の結果が上限を超えています");
            }

            output.Write(buffer, 0, read);
        }

        if (output.Length == 0)
        {
            throw new InvalidDataException("medium process の結果が空です");
        }

        return StrictUtf8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private static void TryDeleteResultFile(SafeAccessTokenHandle token, string path)
    {
        try
        {
            WindowsIdentity.RunImpersonated(
                token,
                () =>
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ランダム名の一時結果だけなので、cleanup 失敗は次回実行へ影響しない。
        }
    }

    private static bool IsPathInside(string path, string directory) =>
        path.StartsWith(
            Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static Win32Exception NewWin32Exception(string message) =>
        CreateWin32Exception(Marshal.GetLastWin32Error(), message);

    private static Win32Exception CreateWin32Exception(int errorCode, string message) =>
        new(errorCode, $"{message} (Win32 {errorCode}: {new Win32Exception(errorCode).Message})");

    private static void ExecuteInExplorerProcess(
        uint expectedShellProcessId,
        string applicationPath,
        string arguments,
        string workingDirectory)
    {
        var initializeResult = CoInitializeEx(
            nint.Zero,
            CoInitApartmentThreaded | CoInitDisableOle1Dde);
        var uninitialize = initializeResult >= 0;
        if (initializeResult < 0 && initializeResult != RpcEChangedMode)
        {
            Marshal.ThrowExceptionForHR(initializeResult);
        }

        nint dispatchPointer = nint.Zero;
        object? shellWindows = null;
        object? desktopWindow = null;
        object? document = null;
        object? shellApplication = null;
        try
        {
            var classId = ClsidShellWindows;
            var interfaceId = IidDispatch;
            var createResult = CoCreateInstance(
                ref classId,
                nint.Zero,
                ClassContextLocalServer,
                ref interfaceId,
                out dispatchPointer);
            Marshal.ThrowExceptionForHR(createResult);

            shellWindows = Marshal.GetObjectForIUnknown(dispatchPointer);
            _ = Marshal.Release(dispatchPointer);
            dispatchPointer = nint.Zero;

            object location = null!;
            object root = null!;
            var desktopWindowHandle = 0;
            dynamic shellWindowsDispatch = shellWindows;
            desktopWindow = shellWindowsDispatch.FindWindowSW(
                ref location,
                ref root,
                ShellWindowDesktop,
                ref desktopWindowHandle,
                ShellWindowNeedDispatch);
            if (desktopWindow is null || desktopWindowHandle == 0 ||
                GetWindowThreadProcessId((nint)desktopWindowHandle, out var desktopProcessId) == 0 ||
                desktopProcessId != expectedShellProcessId)
            {
                throw new InvalidOperationException(
                    "Explorer broker のデスクトップ window を検証できませんでした");
            }

            dynamic desktopDispatch = desktopWindow;
            document = desktopDispatch.Document;
            if (document is null)
            {
                throw new InvalidOperationException("Explorer broker の document を取得できませんでした");
            }

            dynamic documentDispatch = document;
            shellApplication = documentDispatch.Application;
            if (shellApplication is null)
            {
                throw new InvalidOperationException("Explorer broker の Shell application を取得できませんでした");
            }

            dynamic applicationDispatch = shellApplication;
            applicationDispatch.ShellExecute(
                applicationPath,
                arguments,
                workingDirectory,
                "open",
                0);
        }
        finally
        {
            if (dispatchPointer != nint.Zero)
            {
                _ = Marshal.Release(dispatchPointer);
            }

            ReleaseComObject(shellApplication);
            if (!ReferenceEquals(document, shellApplication))
            {
                ReleaseComObject(document);
            }

            if (!ReferenceEquals(desktopWindow, document) &&
                !ReferenceEquals(desktopWindow, shellApplication))
            {
                ReleaseComObject(desktopWindow);
            }

            if (!ReferenceEquals(shellWindows, desktopWindow) &&
                !ReferenceEquals(shellWindows, document) &&
                !ReferenceEquals(shellWindows, shellApplication))
            {
                ReleaseComObject(shellWindows);
            }

            if (uninitialize)
            {
                CoUninitialize();
            }
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.ReleaseComObject(value);
        }
    }

    private static ProcessInformation CreateMediumProcess(
        SafeAccessTokenHandle primaryToken,
        string applicationPath,
        string commandLine,
        nint environment,
        string workingDirectory)
    {
        var creationFlags = CreateUnicodeEnvironment | CreateNoWindow;
        var startupInfo = new StartupInfo
        {
            Size = (uint)Marshal.SizeOf<StartupInfo>(),
        };
        var withTokenCommandLine = new StringBuilder(commandLine, commandLine.Length + 1);
        if (CreateProcessWithTokenW(
                primaryToken,
                0,
                applicationPath,
                withTokenCommandLine,
                creationFlags,
                environment,
                workingDirectory,
                ref startupInfo,
                out var processInformation))
        {
            return processInformation;
        }

        var withTokenError = Marshal.GetLastWin32Error();
        var fallbackStartupInfo = new StartupInfo
        {
            Size = (uint)Marshal.SizeOf<StartupInfo>(),
        };
        var asUserCommandLine = new StringBuilder(commandLine, commandLine.Length + 1);
        if (CreateProcessAsUserW(
                primaryToken,
                applicationPath,
                asUserCommandLine,
                nint.Zero,
                nint.Zero,
                inheritHandles: false,
                creationFlags,
                environment,
                workingDirectory,
                ref fallbackStartupInfo,
                out processInformation))
        {
            return processInformation;
        }

        var asUserError = Marshal.GetLastWin32Error();
        throw new MediumProcessLaunchException(
            asUserError,
            BuildProcessLaunchFailureMessage(withTokenError, asUserError));
    }

    internal static string BuildProcessLaunchFailureMessage(int withTokenError, int asUserError) =>
        "medium token で Windows PowerShell を起動できませんでした: " +
        $"CreateProcessWithTokenW=Win32 {withTokenError} ({new Win32Exception(withTokenError).Message}); " +
        $"CreateProcessAsUserW=Win32 {asUserError} ({new Win32Exception(asUserError).Message})";

    private sealed record ShellKnownFolders(
        string UserProfile,
        string RoamingAppData,
        string LocalAppData);

    private sealed record ExplorerBrokerPaths(
        string LauncherPath,
        string ProcessIdPath);

    private sealed class MediumProcessLaunchException(int errorCode, string message)
        : Win32Exception(
            errorCode,
            $"{message} (Win32 {errorCode}: {new Win32Exception(errorCode).Message})");

    private readonly record struct ProcessWaitResult(int ExitCode, bool TimedOut);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public nint Reserved2;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint ProcessHandle;
        public nint ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }

    private sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeKernelHandle() : base(ownsHandle: true)
        {
        }

        public SafeKernelHandle(nint handle, bool ownsHandle) : base(ownsHandle)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetShellWindow();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeKernelHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        SafeKernelHandle process,
        uint flags,
        StringBuilder executableName,
        ref uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentProcessId();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeKernelHandle process,
        uint desiredAccess,
        out SafeAccessTokenHandle token);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        SafeAccessTokenHandle existingToken,
        uint desiredAccess,
        nint tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out SafeAccessTokenHandle newToken);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token,
        int informationClass,
        nint information,
        uint informationLength,
        out uint returnLength);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidSid(nint sid);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern nint GetSidSubAuthorityCount(nint sid);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern nint GetSidSubAuthority(nint sid, uint subAuthorityIndex);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        in Guid folderId,
        uint flags,
        SafeAccessTokenHandle token,
        out nint path);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessWithTokenW(
        SafeAccessTokenHandle token,
        uint logonFlags,
        string applicationName,
        StringBuilder commandLine,
        uint creationFlags,
        nint environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(
        SafeAccessTokenHandle token,
        string applicationName,
        StringBuilder commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint coInitialize);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        ref Guid classId,
        nint outer,
        uint classContext,
        ref Guid interfaceId,
        out nint instance);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeKernelHandle handle, uint milliseconds);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeKernelHandle process, out uint exitCode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeKernelHandle process, uint exitCode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
