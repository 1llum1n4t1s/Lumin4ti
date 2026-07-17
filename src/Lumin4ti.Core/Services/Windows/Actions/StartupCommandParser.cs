using System.Security;
using System.Text.RegularExpressions;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// スタートアップ登録のコマンドライン文字列から、存在チェック対象の実行ファイルパスを解決する。
/// rundll32 等のラッパー経由や相対パスは誤削除リスクがあるため対象外 (null) とする。
/// </summary>
internal static partial class StartupCommandParser
{
    private static readonly string[] Wrappers =
    [
        "rundll32.exe", "cmd.exe", "wscript.exe", "cscript.exe",
        "powershell.exe", "pwsh.exe", "conhost.exe", "explorer.exe",
    ];

    [GeneratedRegex(@"^(.+?\.exe)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UnquotedExePattern();

    public static string? TryResolveExecutable(string command)
    {
        var value = command.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        string? exe = null;
        if (value[0] == '"')
        {
            var end = value.IndexOf('"', 1);
            if (end > 1)
            {
                exe = value[1..end];
            }
        }
        else if (UnquotedExePattern().Match(value) is { Success: true } match)
        {
            exe = match.Groups[1].Value;
        }

        if (exe is null)
        {
            return null;
        }

        exe = Environment.ExpandEnvironmentVariables(exe);

        if (Wrappers.Contains(Path.GetFileName(exe), StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.IsPathFullyQualified(exe) ? exe : null;
    }

    /// <summary>
    /// File.Exists=false を恒久的な欠損と判断してよいのは、準備済みの固定ドライブだけ。
    /// UNC、ネットワークドライブ、取り外し可能ドライブ、未準備 volume は一時不在の可能性があるため保持する。
    /// </summary>
    internal static bool CanTreatMissingAsBroken(
        string executable,
        Func<string, (DriveType DriveType, bool IsReady)>? probeDrive = null)
    {
        if (string.IsNullOrWhiteSpace(executable) ||
            !Path.IsPathFullyQualified(executable) ||
            executable.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        var root = Path.GetPathRoot(executable);
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            var status = probeDrive is null
                ? ProbeDrive(root)
                : probeDrive(root);
            return status.DriveType == DriveType.Fixed && status.IsReady;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 準備済み固定ドライブ上で、ファイルまたは親ディレクトリの不存在を OS が明示した場合だけ
    /// 欠損と判定する。アクセス拒否や一般的な I/O エラーは「存在不明」として保持する。
    /// </summary>
    internal static bool IsConfirmedMissing(
        string executable,
        Func<string, (DriveType DriveType, bool IsReady)>? probeDrive = null,
        Func<string, FileAttributes>? getAttributes = null)
    {
        if (!CanTreatMissingAsBroken(executable, probeDrive))
        {
            return false;
        }

        try
        {
            _ = (getAttributes ?? File.GetAttributes)(executable);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return HasOnlyConfirmedLocalAncestors(executable, getAttributes ?? File.GetAttributes);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 実行ファイルまでの既存祖先に junction / symlink / mount point が無いことを確認する。
    /// 途中の再解析点やアクセス不能箇所があれば、固定ドライブのルート判定を信頼しない。
    /// </summary>
    private static bool HasOnlyConfirmedLocalAncestors(
        string executable,
        Func<string, FileAttributes> getAttributes)
    {
        try
        {
            var fullPath = Path.GetFullPath(executable);
            var root = Path.GetPathRoot(fullPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            var current = root;
            if ((getAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            var relativeDirectory = Path.GetRelativePath(root, directory);
            foreach (var segment in relativeDirectory.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                try
                {
                    if ((getAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    {
                        return false;
                    }
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    // ここまでの既存祖先は通常ディレクトリで、以降は存在しない。
                    return true;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static (DriveType DriveType, bool IsReady) ProbeDrive(string root)
    {
        var drive = new DriveInfo(root);
        return (drive.DriveType, drive.IsReady);
    }
}
