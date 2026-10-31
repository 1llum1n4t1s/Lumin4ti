using System.Globalization;

namespace Lumin4ti.Core.Services;

/// <summary>旧ログだけを移し、他の利用者データは残す。</summary>
internal static class LegacyLogMigration
{
    internal static void RemoveEmptyParent(string sourceDirectory)
    {
        try
        {
            if (Directory.Exists(sourceDirectory)) return;
            var parent = Path.GetDirectoryName(Path.GetFullPath(sourceDirectory));
            if (parent is not null && Directory.Exists(parent) && HasNoReparsePoints(parent))
                Directory.Delete(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 空でなければ利用者データを保持する。
        }
    }

    internal static void Migrate(string sourceDirectory, string destinationDirectory)
    {
        try
        {
            if (!Directory.Exists(sourceDirectory) || !HasNoReparsePoints(sourceDirectory) ||
                !HasNoReparsePoints(destinationDirectory))
            {
                return;
            }

            Directory.CreateDirectory(destinationDirectory);
            if (!HasNoReparsePoints(destinationDirectory)) return;

            foreach (var source in Directory.EnumerateFiles(sourceDirectory))
            {
                if (!IsKnownLog(Path.GetFileName(source))) continue;
                try
                {
                    if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) continue;
                    // 読書きを排他し、実行中の旧ロガーと他の移行処理を避ける。
                    using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Delete);
                    var destination = Path.Combine(destinationDirectory, $"legacy-{Guid.NewGuid():N}-{Path.GetFileName(source)}");
                    File.Move(source, destination);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // 使用中・競合・別ボリューム等の失敗時は元を残し、次回再試行する。
                }
            }

            if (HasNoReparsePoints(sourceDirectory) && !Directory.EnumerateFileSystemEntries(sourceDirectory).Any())
                Directory.Delete(sourceDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // ログ移行の失敗で起動を妨げない。
        }
    }

    private static bool HasNoReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        }
        return true;
    }

    private static bool IsKnownLog(string name)
    {
        if (name.Equals("msi-per-machine.log", StringComparison.OrdinalIgnoreCase)) return true;
        const string prefix = "Lumin4ti_";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) return false;
        var stem = name[prefix.Length..^4];
        var parts = stem.Split('_');
        return parts.Length is 1 or 2 &&
            DateTime.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) &&
            (parts.Length == 1 || int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }
}
