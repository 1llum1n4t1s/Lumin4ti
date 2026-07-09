using System.Runtime.Versioning;

namespace Lumin4ti.Core.Services;

/// <summary>
/// bare な実行ファイル名 (dism.exe 等) を確定的なフルパスへ解決する。
/// UseShellExecute=false で bare 名を起動すると CreateProcess の検索順序で
/// アプリ自身のインストールディレクトリが System32 より先に照合され、
/// 昇格プロセスがそこに植え付けられた偽バイナリを実行してしまう (バイナリプランティング LPE)。
/// 呼び出し側は論理名を渡し、ここで必ず System32 / 既知の正規パスへ解決してから起動する。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SystemProcessResolver
{
    public static string Resolve(string fileName)
    {
        // 既にディレクトリ区切りを含む = フルパス指定済み (MpCmdRun.exe 等) はそのまま
        if (fileName.Contains('\\') || fileName.Contains('/'))
        {
            return fileName;
        }

        var name = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".exe";

        // powershell.exe は System32\WindowsPowerShell\v1.0 配下
        if (name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
        {
            var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            return File.Exists(ps) ? ps : name;
        }

        // winget は App Execution Alias (WindowsApps 配下の正規リパースポイント) を指す
        if (name.Equals("winget.exe", StringComparison.OrdinalIgnoreCase))
        {
            var winget = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "winget.exe");
            return File.Exists(winget) ? winget : name;
        }

        // それ以外 (dism/powercfg/bcdedit/net/regsvr32/defrag/WSReset 等) は System32 直下
        var system32 = Path.Combine(Environment.SystemDirectory, name);
        return File.Exists(system32) ? system32 : name;
    }
}
