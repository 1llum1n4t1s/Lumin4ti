using System.Text;
using System.Text.RegularExpressions;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services;

/// <summary>外部コマンドの診断を、秘密値・制御文字・記録量を制限して表示する。</summary>
internal static partial class CommandFailureDiagnostic
{
    // 正規表現はビルド時生成へ揃える (StartupCommandParser と同じ方式)。
    // 実行時の解釈・キャッシュ参照を省き、同じパターンを winget 診断の整形からも共有する。
    // パターンとオプションは従来の Regex.Replace 呼出しと同一で、置換結果は変えない。

    /// <summary>ANSI のエスケープシーケンス (色指定・カーソル制御など)。</summary>
    [GeneratedRegex("\\x1B\\[[0-?]*[ -/]*[@-~]")]
    internal static partial Regex AnsiEscapeSequence();

    /// <summary>連続する空白文字。1 個の空白へ畳むために使う。</summary>
    [GeneratedRegex(@"\s+")]
    internal static partial Regex WhitespaceRun();

    [GeneratedRegex(@"(?i)\bBearer\s+[^\s;,]+")]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"(?i)(https?://)[^\s/@:]+:[^\s/@]+@")]
    private static partial Regex UrlCredential();

    [GeneratedRegex(@"(?i)((?:password|passwd|pwd|token|secret|api[_-]?key|authorization)\s*[=:]\s*)(?:""[^""]*""|'[^']*'|[^\s;&]+)")]
    private static partial Regex SecretAssignment();

    internal static string Format(CommandExecutionResult result)
    {
        var diagnostic = new StringBuilder($"exit={result.ExitCode}/0x{unchecked((uint)result.ExitCode):X8}");
        foreach (var (label, output) in new[] { ("stdout", result.StandardOutput), ("stderr", result.StandardError) })
        {
            var summary = Sanitize(output);
            if (summary.Length > 0)
            {
                diagnostic.Append("; ").Append(label).Append('=').Append(summary);
            }
        }
        return diagnostic.ToString();
    }

    internal static string Sanitize(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return string.Empty;
        // 巨大な出力全体へ正規表現をかけず、末尾の診断だけを処理する。
        var text = output.Length > 8192 ? output[^8192..] : output;
        text = AnsiEscapeSequence().Replace(text, string.Empty);
        text = BearerToken().Replace(text, "Bearer [redacted]");
        text = UrlCredential().Replace(text, "$1[redacted]@");
        text = SecretAssignment().Replace(text, "$1[redacted]");
        var clean = new string(text.Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) ==
            System.Globalization.UnicodeCategory.Format ? ' ' : c).ToArray());
        clean = WhitespaceRun().Replace(clean, " ").Trim();
        return clean.Length <= 600 ? clean : clean[..600] + "…";
    }
}
