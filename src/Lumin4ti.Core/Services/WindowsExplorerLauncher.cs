using System.Runtime.Versioning;
using System.Text;

namespace Lumin4ti.Core.Services;

/// <summary>
/// 検証済みの対話中 Explorer の medium token で Shell.Application.ShellExecute を呼び、
/// 高整合性プロセスから HKCU の関連付けやシェル拡張を解釈しないようにする。
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsExplorerLauncher
{
    public static Task OpenAsync(string target, CancellationToken ct = default) =>
        OpenAsync(target, new UnelevatedCommandExecutor(TimeSpan.FromSeconds(30)), ct);

    internal static async Task OpenAsync(
        string target,
        IUnelevatedCommandExecutor executor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(executor);
        var normalizedTarget = NormalizeTarget(target);
        var result = await executor.RunPowerShellAsync(
            BuildOpenScript(normalizedTarget),
            ct).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"対話中ユーザーの権限で対象を開けませんでした: {result.Error}");
        }
    }

    internal static string NormalizeTarget(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (target.Length > 32767 || target.Contains('\0'))
        {
            throw new ArgumentException("開く対象が長すぎるか、不正な文字を含んでいます。", nameof(target));
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
            uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return uri.AbsoluteUri;
        }

        if (!Path.IsPathFullyQualified(target))
        {
            throw new ArgumentException(
                "開く対象は完全パスまたは HTTPS URL に限ります。",
                nameof(target));
        }

        return Path.GetFullPath(target);
    }

    internal static string BuildOpenScript(string normalizedTarget)
    {
        var encodedTarget = Convert.ToBase64String(Encoding.Unicode.GetBytes(normalizedTarget));
        const string template = """
            $ErrorActionPreference = 'Stop'
            $shell = $null
            try {
                $target = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('__TARGET_B64__'))
                $shell = New-Object -ComObject Shell.Application
                $shell.ShellExecute($target, '', '', 'open', 1)

                if ([string]::IsNullOrWhiteSpace($env:LUMIN4TI_RESULT_PATH) -or
                    -not [IO.Path]::IsPathRooted($env:LUMIN4TI_RESULT_PATH)) {
                    throw '結果ファイルのパスが不正です'
                }
                $resultDirectory = [IO.Path]::GetDirectoryName($env:LUMIN4TI_RESULT_PATH)
                [IO.Directory]::CreateDirectory($resultDirectory) | Out-Null
                [IO.File]::WriteAllText(
                    $env:LUMIN4TI_RESULT_PATH,
                    '{"Success":true}',
                    [Text.UTF8Encoding]::new($false))
            }
            catch {
                exit 1
            }
            finally {
                if ($null -ne $shell) {
                    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
                }
            }
            """;

        return template.Replace("__TARGET_B64__", encodedTarget, StringComparison.Ordinal);
    }
}
