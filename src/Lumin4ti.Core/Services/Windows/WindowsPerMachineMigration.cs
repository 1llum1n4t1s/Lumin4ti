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
    private const string UpdateExeName = "Update.exe";
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

        var legacyRoot = GetLegacyRootIfCurrentProcessIsPerUser(
            processPath,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (legacyRoot is null)
        {
            if (IsCurrentProcessPerMachine(processPath)
                && (args.Contains(CompletionArgument, StringComparer.Ordinal) || File.Exists(PendingFilePath)))
            {
                await CompletePendingMigrationAsync(ct);
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

    internal static bool IsPerMachineProcessPath(string processPath, string programFilesDirectory)
    {
        if (string.IsNullOrWhiteSpace(processPath) || string.IsNullOrWhiteSpace(programFilesDirectory))
        {
            return false;
        }

        var normalizedProcessPath = Path.GetFullPath(processPath);
        var normalizedProgramFiles = Path.GetFullPath(programFilesDirectory);
        if (!IsPathInside(normalizedProcessPath, normalizedProgramFiles))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(normalizedProcessPath);
        while (directory is not null && IsPathInside(directory, normalizedProgramFiles))
        {
            if (File.Exists(Path.Combine(directory, ".msi-installed")))
            {
                return true;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return false;
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

        var updaterPath = Path.Combine(legacyRoot, UpdateExeName);
        if (File.Exists(updaterPath))
        {
            try
            {
                var startInfo = new ProcessStartInfo(updaterPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                startInfo.ArgumentList.Add("--silent");
                startInfo.ArgumentList.Add("--rootDir");
                startInfo.ArgumentList.Add(legacyRoot);
                startInfo.ArgumentList.Add("uninstall");
                using var updater = Process.Start(startInfo);
                updater?.WaitForExit(30_000);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // 下の限定削除へフォールバックする。
            }
        }

        for (var attempt = 0; attempt < 10 && Directory.Exists(legacyRoot); attempt++)
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

    private static async Task CompletePendingMigrationAsync(CancellationToken ct)
    {
        var pending = ReadPendingMigration();
        if (pending is null)
        {
            return;
        }

        await WaitForLegacyProcessExitAsync(pending.ParentProcessId, ct);
        var expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppId);
        var cleaned = TryCleanupLegacyArtifacts(
            pending.LegacyRoot,
            expectedRoot,
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

        if (cleaned)
        {
            DeleteIfExists(PendingFilePath);
            RemoveCleanupRunOnce();
        }
        else if (!string.IsNullOrWhiteSpace(pending.InstalledExecutable))
        {
            RegisterCleanupRunOnce(pending.InstalledExecutable);
            ShowInformation("旧インストール先の一部が使用中だったため、次回サインイン時にもう一度回収します。");
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
        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        startInfo.ArgumentList.Add("/i");
        startInfo.ArgumentList.Add(msiPath);
        startInfo.ArgumentList.Add("/passive");
        startInfo.ArgumentList.Add("/norestart");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("msiexecを起動できませんでした");
        process.WaitForExit();
        return process.ExitCode;
    }

    private static string? FindTrustedPerMachineExecutable()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRegistryInstallLocations(candidates, RegistryView.Registry64);
        AddRegistryInstallLocations(candidates, RegistryView.Registry32);

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        candidates.Add(Path.Combine(programFiles, AppId, StableExeName));
        candidates.Add(Path.Combine(programFiles, "ゆろち", AppId, StableExeName));

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

    private static bool IsCurrentProcessPerMachine(string processPath) => IsPerMachineProcessPath(
        processPath,
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    private static void TryDeleteTreeWithoutFollowingReparsePoints(string directoryPath)
    {
        try
        {
            if (!Directory.Exists(directoryPath))
            {
                return;
            }

            var attributes = File.GetAttributes(directoryPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(directoryPath, recursive: false);
                return;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(directoryPath))
            {
                var entryAttributes = File.GetAttributes(entry);
                if ((entryAttributes & FileAttributes.Directory) != 0)
                {
                    if ((entryAttributes & FileAttributes.ReparsePoint) != 0)
                    {
                        Directory.Delete(entry, recursive: false);
                    }
                    else
                    {
                        TryDeleteTreeWithoutFollowingReparsePoints(entry);
                    }
                }
                else
                {
                    File.SetAttributes(entry, FileAttributes.Normal);
                    File.Delete(entry);
                }
            }

            Directory.Delete(directoryPath, recursive: false);
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            // 呼び出し元が残存確認し、次回起動の再試行へ回す。
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
