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

        return Path.IsPathRooted(exe) ? exe : null;
    }
}
