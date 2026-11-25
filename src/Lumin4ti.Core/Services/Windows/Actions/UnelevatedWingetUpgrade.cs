using System.Runtime.Versioning;
using System.Text.Json;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>非昇格 winget 再試行の結果。<see cref="Completed"/> が false のときは winget の結果を得られていない。</summary>
internal sealed record UnelevatedWingetRetryResult(
    bool Completed,
    CommandExecutionResult? Result,
    string Error);

internal delegate Task<UnelevatedWingetRetryResult> UnelevatedWingetRetry(
    string packageId,
    CancellationToken ct);

/// <summary>
/// 昇格側から更新できないユーザースコープ portable を、同一利用者の非昇格 winget で 1 回だけ更新する。
/// 起動する実行ファイルは <see cref="SystemProcessResolver"/> が確認した winget のフルパスだけとし、
/// 戻り値の JSON は非信頼入力として検証し、検証後に結果の分類と表示だけに使う（パスやコマンドには使わない）。
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
internal static class UnelevatedWingetUpgrade
{
    internal const int SchemaVersion = 1;
    internal const int MaximumStreamCharacters = 8192;
    private const string ResultSchemaVersionName = "SchemaVersion";
    private static readonly TimeSpan Timeout = TimeSpan.FromHours(1);
    private static readonly string[] ResultPropertyNames =
        [ResultSchemaVersionName, "ExitCode", "StandardOutput", "StandardError"];

    internal static Task<UnelevatedWingetRetryResult> RunAsync(string packageId, CancellationToken ct) =>
        RunAsync(
            packageId,
            UnelevatedCommandExecutor.CreateForCurrentUserTree(Timeout),
            () => SystemProcessResolver.Resolve("winget"),
            ct);

    internal static async Task<UnelevatedWingetRetryResult> RunAsync(
        string packageId,
        IUnelevatedCommandExecutor executor,
        Func<string> resolveWinget,
        CancellationToken ct)
    {
        if (!WingetUpgradeAction.IsSafePackageId(packageId))
        {
            return Failure("Package ID が安全な形式ではありません");
        }

        string wingetPath;
        try
        {
            wingetPath = resolveWinget();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return Failure($"winget の実体を確認できませんでした: {ex.Message}");
        }

        if (!Path.IsPathFullyQualified(wingetPath) ||
            !string.Equals(Path.GetFileName(wingetPath), "winget.exe", StringComparison.OrdinalIgnoreCase))
        {
            return Failure("winget の絶対パスを確認できませんでした");
        }

        var script = BuildScript(wingetPath, WingetUpgradeAction.BuildUpgradeArguments(packageId));
        var run = await executor.RunPowerShellAsync(script, Timeout, ct).ConfigureAwait(false);
        if (!run.Success || run.ResultJson is null)
        {
            return Failure(run.Error.Length > 0 ? run.Error : $"非昇格 PowerShell が失敗しました (exit={run.ExitCode})");
        }

        return TryParseResult(run.ResultJson, wingetPath, out var result, out var error)
            ? new(true, result, string.Empty)
            : Failure(error);
    }

    // スクリプト本文は ASCII だけにする。finally は、例外などで winget が未終了のまま抜ける場合だけ、
    // System32 の taskkill でツリーごと止める。失敗しても元の終了コード (exit 0 / 1) は変えない。
    internal static string BuildScript(string wingetPath, string arguments) =>
        $$"""
        $ErrorActionPreference='Stop'
        $p=$null
        try {
          $resultPath=$env:LUMIN4TI_RESULT_PATH
          [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resultPath))
          $psi=New-Object System.Diagnostics.ProcessStartInfo
          $psi.FileName={{Quote(wingetPath)}}
          $psi.Arguments={{Quote(arguments)}}
          $psi.UseShellExecute=$false
          $psi.CreateNoWindow=$true
          $psi.RedirectStandardInput=$true
          $psi.RedirectStandardOutput=$true
          $psi.RedirectStandardError=$true
          $psi.StandardOutputEncoding=[Text.Encoding]::UTF8
          $psi.StandardErrorEncoding=[Text.Encoding]::UTF8
          $p=[Diagnostics.Process]::Start($psi)
          $p.StandardInput.Close()
          $o=$p.StandardOutput.ReadToEndAsync()
          $e=$p.StandardError.ReadToEndAsync()
          $p.WaitForExit()
          $out=[string]$o.Result
          $err=[string]$e.Result
          if ($out.Length -gt {{MaximumStreamCharacters}}) { $out=$out.Substring($out.Length-{{MaximumStreamCharacters}}) }
          if ($err.Length -gt {{MaximumStreamCharacters}}) { $err=$err.Substring($err.Length-{{MaximumStreamCharacters}}) }
          $result=[ordered]@{ SchemaVersion={{SchemaVersion}}; ExitCode=[int]$p.ExitCode; StandardOutput=$out; StandardError=$err }
          $json=ConvertTo-Json -InputObject $result -Compress
          [IO.File]::WriteAllText($resultPath,$json,(New-Object Text.UTF8Encoding($false)))
          exit 0
        } catch {
          exit 1
        } finally {
          try {
            if ($null -ne $p -and -not $p.HasExited) {
              $tk=[IO.Path]::Combine([Environment]::SystemDirectory,'taskkill.exe')
              & $tk /PID $p.Id /T /F *> $null
            }
          } catch { }
        }
        """;

    internal static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>結果 JSON を検証して <see cref="CommandExecutionResult"/> へ変換する。内容はパスやコマンドに使わない。</summary>
    internal static bool TryParseResult(
        string json,
        string commandPath,
        out CommandExecutionResult result,
        out string error)
    {
        result = null!;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "再試行結果が JSON オブジェクトではありません";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!ResultPropertyNames.Contains(property.Name, StringComparer.Ordinal) ||
                    !seen.Add(property.Name))
                {
                    error = $"再試行結果に想定外または重複したプロパティがあります: {property.Name}";
                    return false;
                }
            }

            if (seen.Count != ResultPropertyNames.Length)
            {
                error = "再試行結果に必須プロパティが不足しています";
                return false;
            }

            if (!TryGetInt32(root.GetProperty(ResultSchemaVersionName), out var version) || version != SchemaVersion)
            {
                error = "再試行結果の版が一致しません";
                return false;
            }

            var standardOutput = root.GetProperty("StandardOutput");
            var standardError = root.GetProperty("StandardError");
            if (!TryGetInt32(root.GetProperty("ExitCode"), out var exitCode) ||
                standardOutput.ValueKind != JsonValueKind.String ||
                standardError.ValueKind != JsonValueKind.String)
            {
                error = "再試行結果のプロパティの型が不正です";
                return false;
            }

            var output = standardOutput.GetString()!;
            var errorOutput = standardError.GetString()!;
            if (output.Length > MaximumStreamCharacters || errorOutput.Length > MaximumStreamCharacters)
            {
                error = "再試行結果の出力が上限を超えています";
                return false;
            }

            result = new CommandExecutionResult(
                exitCode == 0,
                commandPath,
                exitCode,
                output,
                errorOutput);
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"再試行結果を解析できませんでした: {ex.Message}";
            return false;
        }
    }

    private static bool TryGetInt32(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }

    private static UnelevatedWingetRetryResult Failure(string error) => new(false, null, error);
}
