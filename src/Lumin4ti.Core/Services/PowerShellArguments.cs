using System.Text;

namespace Lumin4ti.Core.Services;

/// <summary>
/// Windows PowerShell (powershell.exe) へスクリプトを渡す引数の組み立てを 1 か所へ集約する。
/// </summary>
/// <remarks>
/// -EncodedCommand は UTF-16LE の Base64 を要求する。引用符・改行・$ を含むスクリプトを
/// コマンドライン上のエスケープに頼らず渡せるため、各アクションはこの形式だけを使う。
/// 呼び出し元ごとに同じ符号化を書き写すと、符号化方式やオプションの食い違いが起きやすい。
/// </remarks>
internal static class PowerShellArguments
{
    /// <summary>プロファイルを読まず、対話を待たずにスクリプトを実行する引数を返す。</summary>
    internal static string EncodedCommand(string script)
    {
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return $"-NoProfile -NonInteractive -EncodedCommand {encodedScript}";
    }
}
