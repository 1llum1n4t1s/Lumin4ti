using System.Runtime.Versioning;
using Lumin4ti.Core.Services.Windows;
using Velopack.Locators;
using Velopack.Windows;

namespace Lumin4ti.UI.Services;

/// <summary>
/// 旧パッケージで作成された著者名サブフォルダ内のスタートメニューショートカットを、
/// 現行のスタートメニュー直下へ一度だけ移行する。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsLegacyStartMenuShortcutMigrator
{
    private const string LegacyFolderName = "ゆろち";
    private const string ShortcutFileName = "Lumin4ti.lnk";
    private const string ExecutableFileName = "Lumin4ti.UI.exe";

    /// <summary>
    /// 現在のユーザーのスタートメニューで旧ショートカットの移行を試みる。
    /// Velopack の高速フックを失敗させないよう、ファイルシステム上の競合は無視する。
    /// </summary>
    internal static void MigrateForCurrentUser()
    {
        try
        {
            if (!VelopackLocator.IsCurrentSet)
            {
                return;
            }

            var programsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            _ = TryMigrate(
                programsDirectory,
                VelopackLocator.Current.RootAppDir,
                ReadShortcut);
            WindowsShellChangeNotifier.RefreshStartMenu(programsDirectory);
        }
        catch (Exception)
        {
            // 更新処理を止めず、通常起動時の再試行へ委ねる。
        }
    }

    /// <summary>
    /// 指定したスタートメニュー Programs ディレクトリで旧ショートカットを移行する。
    /// 既に直下の自アプリショートカットがある場合は、旧リンクだけを除去して重複を解消する。
    /// </summary>
    internal static bool TryMigrate(string programsDirectory, string? rootAppDirectory) =>
        TryMigrate(programsDirectory, rootAppDirectory, ReadShortcut);

    internal static bool TryMigrate(
        string programsDirectory,
        string? rootAppDirectory,
        Func<string, ShortcutDetails?> readShortcut)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(programsDirectory)
                || string.IsNullOrWhiteSpace(rootAppDirectory))
            {
                return false;
            }

            var normalizedProgramsDirectory = Path.GetFullPath(programsDirectory);
            var normalizedRootAppDirectory = Path.GetFullPath(rootAppDirectory);
            var legacyDirectory = Path.Combine(normalizedProgramsDirectory, LegacyFolderName);
            var legacyShortcutPath = Path.Combine(legacyDirectory, ShortcutFileName);
            var rootShortcutPath = Path.Combine(normalizedProgramsDirectory, ShortcutFileName);

            if (!IsPathInside(legacyDirectory, normalizedProgramsDirectory)
                || !File.Exists(legacyShortcutPath)
                || IsReparsePoint(legacyDirectory)
                || IsReparsePoint(legacyShortcutPath)
                || !IsLumin4tiShortcut(readShortcut(legacyShortcutPath), normalizedRootAppDirectory))
            {
                return false;
            }

            if (File.Exists(rootShortcutPath))
            {
                if (IsReparsePoint(rootShortcutPath)
                    || !IsLumin4tiShortcut(readShortcut(rootShortcutPath), normalizedRootAppDirectory))
                {
                    return false;
                }

                File.Delete(legacyShortcutPath);
            }
            else
            {
                File.Move(legacyShortcutPath, rootShortcutPath, overwrite: false);
            }

            TryDeleteIfEmpty(legacyDirectory);
            return true;
        }
        catch (Exception)
        {
            // 高速更新フックでは例外を Velopack へ伝播させない。
            return false;
        }
    }

    private static ShortcutDetails? ReadShortcut(string shortcutPath)
    {
        using var shortcut = new ShellLink(shortcutPath);
        return new ShortcutDetails(shortcut.Target, shortcut.WorkingDirectory);
    }

    private static bool IsLumin4tiShortcut(ShortcutDetails? shortcut, string rootAppDirectory)
    {
        if (shortcut is null
            || !string.Equals(
                Path.GetFileName(shortcut.TargetPath),
                ExecutableFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsPathInside(shortcut.TargetPath, rootAppDirectory)
            || IsPathInside(shortcut.WorkingDirectory, rootAppDirectory);
    }

    private static bool IsPathInside(string? candidatePath, string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        var normalizedCandidatePath = Path.GetFullPath(candidatePath);
        var normalizedRootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var rootPrefix = normalizedRootDirectory + Path.DirectorySeparatorChar;

        return normalizedCandidatePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void TryDeleteIfEmpty(string directoryPath)
    {
        try
        {
            if (!IsReparsePoint(directoryPath))
            {
                // recursive にはせず、他アプリのショートカットや利用者のファイルは必ず残す。
                Directory.Delete(directoryPath);
            }
        }
        catch (Exception)
        {
            // 空でない、または一時的に使用中ならそのまま残す。
        }
    }

    internal sealed record ShortcutDetails(string? TargetPath, string? WorkingDirectory);
}
