using System.Text;
using System.Text.RegularExpressions;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services;

/// <summary>外部コマンドの診断を、秘密値・制御文字・記録量を制限して表示する。</summary>
internal static class CommandFailureDiagnostic
{
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
        text = Regex.Replace(text, "\\x1B\\[[0-?]*[ -/]*[@-~]", string.Empty);
        text = Regex.Replace(text, @"(?i)\bBearer\s+[^\s;,]+", "Bearer [redacted]");
        text = Regex.Replace(text, @"(?i)(https?://)[^\s/@:]+:[^\s/@]+@", "$1[redacted]@");
        text = Regex.Replace(text,
            @"(?i)((?:password|passwd|pwd|token|secret|api[_-]?key|authorization)\s*[=:]\s*)(?:""[^""]*""|'[^']*'|[^\s;&]+)",
            "$1[redacted]");
        var clean = new string(text.Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) ==
            System.Globalization.UnicodeCategory.Format ? ' ' : c).ToArray());
        clean = Regex.Replace(clean, @"\s+", " ").Trim();
        return clean.Length <= 600 ? clean : clean[..600] + "…";
    }
}
