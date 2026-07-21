using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows;

/// <summary>旧PerUser Velopack版を、署名済みPerMachine MSI版へ安全に移行する。</summary>
[SupportedOSPlatform("windows")]
public static class WindowsPerMachineMigration
{
    private const string AppId = "Lumin4ti";
    private const string StableExeName = "Lumin4ti.exe";
    private const string MsiFileName = "Lumin4ti-win.msi";
    private const string ExpectedPublisher = "Open Source Developer Yuichiro Shinozaki";
    private const string CompletionArgument = "--complete-per-machine-migration";
    private const string PendingFileName = "per-machine-migration.pending.json";
    private const string RunOnceValueName = "Lumin4tiPerMachineMigrationCleanup";
    private const long MaximumMsiSizeBytes = 250_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// 通常起動を継続する場合は null、移行処理後に現プロセスを終了する場合は終了コードを返す。
    /// </summary>
    public static async Task<int?> HandleStartupAsync(string[] args, CancellationToken ct = default)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return null;
        }

        var localAppDataDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var expectedLegacyRoot = Path.Combine(localAppDataDirectory, AppId);
        var legacyRoot = GetLegacyRootIfCurrentProcessIsPerUser(
            processPath,
            localAppDataDirectory);
        if (legacyRoot is null)
        {
            var cleanupRequired = args.Contains(CompletionArgument, StringComparer.Ordinal) ||
                                  File.Exists(PendingFilePath) ||
                                  HasLegacyInstallationArtifacts(expectedLegacyRoot);
            if (cleanupRequired
                && IsCurrentProcessPerMachine(processPath))
            {
                await CompletePendingMigrationAsync(expectedLegacyRoot, ct);
            }

            return null;
        }

        // MSI版が既に入っている場合は再インストールせず、旧版の回収だけを引き継ぐ。
        var installedExe = FindTrustedPerMachineExecutable();
        if (installedExe is not null)
        {
            WritePendingMigration(legacyRoot, Environment.ProcessId, installedExe);
            StartInstalledApplication(installedExe);
            return 0;
        }

        if (!ConfirmMigration())
        {
            // ユーザー書き込み可能な旧版を管理者として起動し続けない。
            return 1;
        }

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            AppId,
            "per-machine-migration",
            Guid.NewGuid().ToString("N"));
        var msiPath = Path.Combine(temporaryDirectory, MsiFileName);
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            await DownloadMsiAsync(msiPath, ct);
            if (!ExecutableTrustVerifier.TryVerify(
                    msiPath,
                    ExpectedPublisher,
                    AuthenticodeRevocationMode.Online,
                    out _))
            {
                ShowError("ダウンロードしたMSIの署名または発行元を確認できませんでした。移行を中止します。");
                return 1;
            }

            var exitCode = InstallMsi(msiPath);
            if (exitCode is not 0 and not 3010)
            {
                ShowError($"PerMachine MSIのインストールに失敗しました (終了コード: {exitCode})。");
                return 1;
            }

            installedExe = FindTrustedPerMachineExecutable();
            WritePendingMigration(legacyRoot, Environment.ProcessId, installedExe);
            if (installedExe is not null)
            {
                StartInstalledApplication(installedExe);
            }
            else
            {
                ShowInformation(
                    "MSIのインストールは完了しました。スタートメニューからLumin4tiを起動すると、旧版の残存ファイルを回収します。");
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 1;
        }
        catch (Exception ex) when (ex is
            HttpRequestException or
            IOException or
            InvalidOperationException or
            UnauthorizedAccessException or
            Win32Exception)
        {
            ShowError($"PerMachine版への移行に失敗しました。\n\n{ex.Message}");
            return 1;
        }
        finally
        {
            TryDeleteTreeWithoutFollowingReparsePoints(temporaryDirectory);
        }
    }

    internal static string? GetLegacyRootIfCurrentProcessIsPerUser(
        string processPath,
        string localAppDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(processPath) || string.IsNullOrWhiteSpace(localAppDataDirectory))
        {
            return null;
        }

        var expectedRoot = Path.GetFullPath(Path.Combine(localAppDataDirectory, AppId));
        var normalizedProcessPath = Path.GetFullPath(processPath);
        return IsPathInside(normalizedProcessPath, expectedRoot) ? expectedRoot : null;
    }

    internal static bool IsMsiProcessPath(string processPath, string installedExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(processPath) || string.IsNullOrWhiteSpace(installedExecutablePath))
        {
            return false;
        }

        var normalizedProcessPath = Path.GetFullPath(processPath);
        var normalizedInstalledExecutable = Path.GetFullPath(installedExecutablePath);
        var installRoot = Path.GetDirectoryName(normalizedInstalledExecutable);
        if (installRoot is null ||
            !Path.GetFileName(normalizedInstalledExecutable).Equals(StableExeName, StringComparison.OrdinalIgnoreCase) ||
            !IsPathInside(normalizedProcessPath, installRoot))
        {
            return false;
        }

        return File.Exists(Path.Combine(installRoot, ".msi-installed"));
    }

    internal static bool HasLegacyInstallationArtifacts(string legacyRoot)
    {
        if (string.IsNullOrWhiteSpace(legacyRoot))
        {
            return false;
        }

        return Directory.Exists(Path.Combine(legacyRoot, "current")) ||
               File.Exists(Path.Combine(legacyRoot, "Lumin4ti.UI.exe")) ||
               File.Exists(Path.Combine(legacyRoot, StableExeName));
    }

    internal static bool TryCleanupLegacyArtifacts(
        string legacyRoot,
        string expectedLegacyRoot,
        string userProgramsDirectory,
        string userDesktopDirectory)
    {
        if (!Path.GetFullPath(legacyRoot).Equals(
                Path.GetFullPath(expectedLegacyRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var attempt = 0; attempt < 20 && Directory.Exists(legacyRoot); attempt++)
        {
            TryDeleteTreeWithoutFollowingReparsePoints(legacyRoot);
            if (Directory.Exists(legacyRoot))
            {
                Thread.Sleep(300);
            }
        }

        DeleteIfExists(Path.Combine(userProgramsDirectory, "Lumin4ti.lnk"));
        DeleteIfExists(Path.Combine(userProgramsDirectory, "ゆろち", "Lumin4ti.lnk"));
        TryDeleteEmptyDirectory(Path.Combine(userProgramsDirectory, "ゆろち"));
        DeleteIfExists(Path.Combine(userDesktopDirectory, "Lumin4ti.lnk"));
        TryRemoveLegacyUninstallEntry(expectedLegacyRoot);

        return !Directory.Exists(legacyRoot);
    }

    private static async Task CompletePendingMigrationAsync(
        string expectedLegacyRoot,
        CancellationToken ct)
    {
        var pending = ReadPendingMigration();
        if (pending is null)
        {
            if (!HasLegacyInstallationArtifacts(expectedLegacyRoot))
            {
                return;
            }

            pending = new PendingMigration(
                expectedLegacyRoot,
                0,
                FindTrustedPerMachineExecutable());
        }

        await WaitForLegacyProcessExitAsync(pending.ParentProcessId, ct);
        var cleaned = TryCleanupLegacyArtifacts(
            pending.LegacyRoot,
            expectedLegacyRoot,
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

        if (cleaned)
        {
            DeleteIfExists(PendingFilePath);
            RemoveCleanupRunOnce();
        }
        else
        {
            var installedExecutable = pending.InstalledExecutable ?? FindTrustedPerMachineExecutable();
            if (!string.IsNullOrWhiteSpace(installedExecutable))
            {
                WritePendingMigration(pending.LegacyRoot, 0, installedExecutable);
                ShowInformation("旧インストール先の一部が使用中だったため、次回起動時にもう一度回収します。");
            }
        }
    }

    private static async Task DownloadMsiAsync(string destinationPath, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppId, "PerMachineMigration"));
        var uri = new Uri($"{AppSettings.DefaultUpdateBaseUrl}/{MsiFileName}");
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumMsiSizeBytes)
        {
            throw new InvalidDataException("MSIのサイズが上限を超えています");
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        var buffer = new byte[81_920];
        long totalBytes = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }

            totalBytes += read;
            if (totalBytes > MaximumMsiSizeBytes)
            {
                throw new InvalidDataException("MSIのサイズが上限を超えています");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static int InstallMsi(string msiPath)
    {
        var startInfo = CreateMsiInstallStartInfo(
            msiPath,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("msiexecを起動できませんでした");
        process.WaitForExit();
        return process.ExitCode;
    }

    internal static ProcessStartInfo CreateMsiInstallStartInfo(
        string msiPath,
        string programFilesDirectory)
    {
        if (string.IsNullOrWhiteSpace(msiPath) || !Path.IsPathFullyQualified(msiPath))
        {
            throw new ArgumentException("MSIの絶対パスが必要です。", nameof(msiPath));
        }

        if (string.IsNullOrWhiteSpace(programFilesDirectory) ||
            !Path.IsPathFullyQualified(programFilesDirectory))
        {
            throw new InvalidOperationException("Program Filesの場所を取得できませんでした。");
        }

        var normalizedProgramFiles = Path.GetFullPath(programFilesDirectory);
        var programFilesRoot = Path.GetPathRoot(normalizedProgramFiles);
        if (string.IsNullOrWhiteSpace(programFilesRoot) ||
            normalizedProgramFiles.TrimEnd(Path.DirectorySeparatorChar)
                .Equals(programFilesRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Program Filesの場所がドライブ直下を示しています。");
        }

        var installDirectory = Path.Combine(normalizedProgramFiles, AppId);
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        startInfo.ArgumentList.Add("/i");
        startInfo.ArgumentList.Add(msiPath);
        // /passive ではMSIのUI側既定フォルダー解決に依存せず、保護された配置先を明示する。
        startInfo.ArgumentList.Add($"VELOPACK_INSTALLDIR={installDirectory}");
        startInfo.ArgumentList.Add("/passive");
        startInfo.ArgumentList.Add("/norestart");
        return startInfo;
    }

    private static string? FindTrustedPerMachineExecutable()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRegistryInstallLocations(candidates, RegistryView.Registry64);
        AddRegistryInstallLocations(candidates, RegistryView.Registry32);

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        candidates.Add(Path.Combine(programFiles, AppId, StableExeName));

        return candidates.FirstOrDefault(path =>
            File.Exists(path)
            && File.Exists(Path.Combine(Path.GetDirectoryName(path)!, ".msi-installed"))
            && ExecutableTrustVerifier.TryVerify(path, ExpectedPublisher, out _));
    }

    private static void AddRegistryInstallLocations(HashSet<string> candidates, RegistryView view)
    {
        using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var uninstall = localMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
        if (uninstall is null)
        {
            return;
        }

        foreach (var name in uninstall.GetSubKeyNames())
        {
            using var product = uninstall.OpenSubKey(name);
            if (!string.Equals(product?.GetValue("DisplayName") as string, AppId, StringComparison.Ordinal))
            {
                continue;
            }

            if (product?.GetValue("InstallLocation") is string installLocation
                && !string.IsNullOrWhiteSpace(installLocation))
            {
                candidates.Add(Path.Combine(installLocation, StableExeName));
            }
        }
    }

    private static void WritePendingMigration(
        string legacyRoot,
        int parentProcessId,
        string? installedExecutable)
    {
        Directory.CreateDirectory(AppPaths.AppDataDirectory);
        var pending = new PendingMigration(legacyRoot, parentProcessId, installedExecutable);
        File.WriteAllText(PendingFilePath, JsonSerializer.Serialize(pending, JsonOptions));
        if (installedExecutable is not null)
        {
            RegisterCleanupRunOnce(installedExecutable);
        }
    }

    private static PendingMigration? ReadPendingMigration()
    {
        try
        {
            return File.Exists(PendingFilePath)
                ? JsonSerializer.Deserialize<PendingMigration>(
                    File.ReadAllText(PendingFilePath),
                    JsonOptions)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task WaitForLegacyProcessExitAsync(int processId, CancellationToken ct)
    {
        if (processId <= 0 || processId == Environment.ProcessId)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
        catch (Exception ex) when (ex is
            ArgumentException or
            InvalidOperationException or
            TimeoutException)
        {
            // 既に終了済み、または次回起動で再試行できる一時的な待機失敗。
        }
    }

    private static void StartInstalledApplication(string executablePath)
    {
        var startInfo = new ProcessStartInfo(executablePath) { UseShellExecute = true };
        startInfo.ArgumentList.Add(CompletionArgument);
        Process.Start(startInfo);
    }

    private static void RegisterCleanupRunOnce(string executablePath)
    {
        using var runOnce = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce");
        runOnce?.SetValue(
            RunOnceValueName,
            $"\"{executablePath}\" {CompletionArgument}",
            RegistryValueKind.String);
    }

    private static void RemoveCleanupRunOnce()
    {
        using var runOnce = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
            writable: true);
        runOnce?.DeleteValue(RunOnceValueName, throwOnMissingValue: false);
    }

    private static void TryRemoveLegacyUninstallEntry(string expectedLegacyRoot)
    {
        const string uninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        using var uninstall = Registry.CurrentUser.OpenSubKey(uninstallPath, writable: true);
        using var product = uninstall?.OpenSubKey(AppId);
        var installLocation = product?.GetValue("InstallLocation") as string;
        product?.Close();
        if (installLocation is not null
            && Path.GetFullPath(installLocation).Equals(
                Path.GetFullPath(expectedLegacyRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            uninstall?.DeleteSubKeyTree(AppId, throwOnMissingSubKey: false);
        }
    }

    private static bool IsPathInside(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative != ".."
               && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               && !Path.IsPathRooted(relative);
    }

    private static bool IsCurrentProcessPerMachine(string processPath)
    {
        var installedExecutable = FindTrustedPerMachineExecutable();
        return installedExecutable is not null
               && IsMsiProcessPath(processPath, installedExecutable)
               && ExecutableTrustVerifier.TryVerify(processPath, ExpectedPublisher, out _);
    }

    internal static void TryDeleteTreeWithoutFollowingReparsePoints(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        FileAttributes directoryAttributes;
        try
        {
            directoryAttributes = File.GetAttributes(directoryPath);
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            return;
        }

        if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
        {
            TryDeleteEmptyDirectory(directoryPath);
            return;
        }

        string[] entries;
        try
        {
            // 遅延列挙のハンドルを残したまま削除しないよう、先にスナップショット化する。
            entries = Directory.GetFileSystemEntries(directoryPath);
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            TryDeleteFileSystemEntryWithoutFollowingReparsePoints(entry);
        }

        TryDeleteEmptyDirectory(directoryPath);
    }

    private static void TryDeleteFileSystemEntryWithoutFollowingReparsePoints(string entry)
    {
        try
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(entry, recursive: false);
                }
                else
                {
                    TryDeleteTreeWithoutFollowingReparsePoints(entry);
                }

                return;
            }

            File.SetAttributes(entry, FileAttributes.Normal);
            File.Delete(entry);
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            // この項目だけを残し、兄弟項目の回収と次回起動での再試行を続ける。
        }
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            DirectoryNotFoundException)
        {
        }
    }

    private static bool ConfirmMigration() => MessageBox(
        "Lumin4tiを安全なProgram Files版へ移行します。\n\n" +
        "設定・ログ・復元用バックアップは保持し、インストール完了後に旧LocalAppData版と古いショートカットを削除します。続行しますか？",
        0x00000001u | 0x00000040u) == 1;

    private static void ShowInformation(string message) =>
        _ = MessageBox(message, 0x00000000u | 0x00000040u);

    private static void ShowError(string message) =>
        _ = MessageBox(message, 0x00000000u | 0x00000010u);

    private static int MessageBox(string message, uint type) =>
        MessageBoxW(IntPtr.Zero, message, "Lumin4ti セキュリティ移行", type | 0x00010000u);

    private static string PendingFilePath =>
        Path.Combine(AppPaths.AppDataDirectory, PendingFileName);

    private sealed record PendingMigration(
        string LegacyRoot,
        int ParentProcessId,
        string? InstalledExecutable);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr windowHandle, string text, string caption, uint type);
}
