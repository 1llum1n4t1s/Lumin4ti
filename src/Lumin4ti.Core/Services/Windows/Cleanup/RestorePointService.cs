using System.Globalization;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>復元ポイント作成の結果区分。</summary>
public enum RestorePointOutcome
{
    /// <summary>今回の呼び出しで新しい復元ポイントが作られた。</summary>
    Created,

    /// <summary>作成は省略され、24 時間以内の既存の復元ポイントを利用する。</summary>
    ReusedExisting,

    /// <summary>システムの保護が無効なため作成していない。</summary>
    ProtectionDisabled,

    /// <summary>作成できず、利用できる既存の復元ポイントも無い (呼び出し側が続行可否を利用者へ聞く)。</summary>
    Failed,

    /// <summary>キャンセルされた。復元ポイントの有無は保証しない。</summary>
    Cancelled,
}

/// <summary>復元ポイント 1 件の最小情報。</summary>
public sealed record RestorePointInfo(long SequenceNumber, DateTimeOffset CreatedAtUtc, string Description);

/// <summary>復元ポイント作成の結果。Created / ReusedExisting のときだけ成功として扱える。</summary>
public sealed record RestorePointResult(RestorePointOutcome Outcome, string Message, RestorePointInfo? Point)
{
    public bool IsUsable => Outcome is RestorePointOutcome.Created or RestorePointOutcome.ReusedExisting;
}

/// <summary>システムの保護を有効化した結果。</summary>
public sealed record RestorePointProtectionResult(bool Enabled, string Message);

/// <summary>
/// WMI SystemRestore.CreateRestorePoint を既存の PowerShell 実行境界 (ICommandExecutor) 経由で呼ぶ。
/// SRSetRestorePoint は COM 既定セキュリティの変更が要るため使わない。
/// 作成頻度のレジストリ (SystemRestorePointCreationFrequency) は変更しない。
/// システムの保護の有効化は <see cref="EnableProtectionAsync"/> に分け、利用者確認後にだけ呼ぶ。
/// </summary>
public sealed class RestorePointService
{
    internal static readonly TimeSpan ReuseWindow = TimeSpan.FromHours(24);

    private const string Marker = "L4T_";
    private const int MaxDescriptionLength = 64;
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CreateTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan EnableTimeout = TimeSpan.FromMinutes(5);

    private readonly ICommandExecutor _executor;
    private readonly TimeProvider _time;

    public RestorePointService(ICommandExecutor executor, TimeProvider? time = null)
    {
        _executor = executor;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// 復元ポイントを作成する。前後の SequenceNumber が増えなければ作成は省略されたとみなし、
    /// 24 時間以内の既存点があれば利用、無ければ失敗とする。
    /// </summary>
    public async Task<RestorePointResult> CreateAsync(string description, CancellationToken ct = default)
    {
        var safeDescription = SanitizeDescription(description);
        if (safeDescription.Length == 0)
        {
            return Fail(CleanupMessage.Of("RestorePointEmptyDescription"));
        }

        try
        {
            var before = await QueryAsync(ct).ConfigureAwait(false);
            if (before.Error is not null)
            {
                return Fail(CleanupMessage.Of("RestorePointStateUnknown", before.Error));
            }

            if (!before.ProtectionEnabled)
            {
                return new(RestorePointOutcome.ProtectionDisabled, CleanupMessage.Of("RestorePointProtectionDisabled"), null);
            }

            var create = await _executor.RunAsync(
                "powershell.exe",
                PowerShellArguments.EncodedCommand(BuildCreateScript(safeDescription)),
                ct,
                timeout: CreateTimeout).ConfigureAwait(false);
            var returnValue = ParseReturnValue(create);
            if (returnValue is null)
            {
                return Fail(CleanupMessage.Of("RestorePointCreateUninterpretable", create.ExitCode));
            }

            if (returnValue != 0)
            {
                return Fail(CleanupMessage.Of("RestorePointCreateFailed", returnValue));
            }

            var after = await QueryAsync(ct).ConfigureAwait(false);
            if (after.Error is not null)
            {
                return Fail(CleanupMessage.Of("RestorePointVerifyFailed", after.Error));
            }

            return Decide(before, after, _time.GetUtcNow());
        }
        catch (OperationCanceledException)
        {
            return new(RestorePointOutcome.Cancelled, CleanupMessage.Of("RestorePointCancelled"), null);
        }
    }

    /// <summary>
    /// システムの保護 (システムドライブ) を有効化する。利用者の確認後にだけ呼ぶこと。
    /// 有効化後に状態を再照合し、確認できなければ失敗とする。
    /// </summary>
    public async Task<RestorePointProtectionResult> EnableProtectionAsync(CancellationToken ct = default)
    {
        try
        {
            var enable = await _executor.RunAsync(
                "powershell.exe",
                PowerShellArguments.EncodedCommand(BuildEnableScript()),
                ct,
                timeout: EnableTimeout).ConfigureAwait(false);
            if (!enable.Success || enable.ExitCode != 0)
            {
                return new(false, CleanupMessage.Of("RestorePointEnableFailed", enable.ExitCode));
            }

            var state = await QueryAsync(ct).ConfigureAwait(false);
            if (state.Error is not null)
            {
                return new(false, CleanupMessage.Of("RestorePointEnableVerifyFailed", state.Error));
            }

            return state.ProtectionEnabled
                ? new(true, CleanupMessage.Of("RestorePointEnabled"))
                : new(false, CleanupMessage.Of("RestorePointEnableNotEffective"));
        }
        catch (OperationCanceledException)
        {
            return new(false, CleanupMessage.Of("RestorePointEnableCancelled"));
        }
    }

    internal static RestorePointResult Decide(RestorePointSnapshot before, RestorePointSnapshot after, DateTimeOffset nowUtc)
    {
        var beforeMax = before.Points.Count == 0 ? 0 : before.Points.Max(p => p.SequenceNumber);
        var created = after.Points
            .Where(p => p.SequenceNumber > beforeMax)
            .OrderByDescending(p => p.SequenceNumber)
            .FirstOrDefault();
        if (created is not null)
        {
            return new(RestorePointOutcome.Created, CleanupMessage.Of("RestorePointCreated"), created);
        }

        var recent = after.Points
            .Where(p => p.CreatedAtUtc <= nowUtc.AddMinutes(5) && nowUtc - p.CreatedAtUtc <= ReuseWindow)
            .OrderByDescending(p => p.CreatedAtUtc)
            .FirstOrDefault();
        if (recent is not null)
        {
            var local = recent.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return new(
                RestorePointOutcome.ReusedExisting,
                CleanupMessage.Of("RestorePointReused", local),
                recent);
        }

        return Fail(CleanupMessage.Of("RestorePointNoneAvailable"));
    }

    internal static RestorePointSnapshot ParseSnapshot(CommandExecutionResult result)
    {
        if (!result.Success || result.ExitCode != 0)
        {
            return RestorePointSnapshot.Invalid(CleanupMessage.Of("RestorePointExitCode", result.ExitCode));
        }

        bool? enabled = null;
        var ended = false;
        var points = new List<RestorePointInfo>();
        foreach (var raw in result.StandardOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(Marker, StringComparison.Ordinal))
            {
                continue;
            }

            if (line == "L4T_END")
            {
                ended = true;
            }
            else if (line.StartsWith("L4T_PROTECTION=", StringComparison.Ordinal))
            {
                var value = line["L4T_PROTECTION=".Length..];
                if (enabled is not null || value is not ("Enabled" or "Disabled"))
                {
                    return RestorePointSnapshot.Invalid(CleanupMessage.Of("RestorePointProtectionOutputInvalid"));
                }

                enabled = value == "Enabled";
            }
            else if (line.StartsWith("L4T_POINT=", StringComparison.Ordinal))
            {
                if (!TryParsePoint(line["L4T_POINT=".Length..], out var point))
                {
                    return RestorePointSnapshot.Invalid(CleanupMessage.Of("RestorePointOutputInvalid"));
                }

                points.Add(point);
            }
            else
            {
                return RestorePointSnapshot.Invalid(CleanupMessage.Of("RestorePointOutputUnknownLine"));
            }
        }

        return enabled is null || !ended
            ? RestorePointSnapshot.Invalid(CleanupMessage.Of("RestorePointOutputIncomplete"))
            : new RestorePointSnapshot(enabled.Value, points, null);
    }

    internal static bool TryParsePoint(string text, out RestorePointInfo point)
    {
        point = null!;
        var parts = text.Split('|', 3);
        if (parts.Length != 3 ||
            !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) ||
            !DateTimeOffset.TryParse(
                parts[1],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var createdAt))
        {
            return false;
        }

        point = new RestorePointInfo(sequence, createdAt, parts[2]);
        return true;
    }

    internal static int? ParseReturnValue(CommandExecutionResult result)
    {
        if (!result.Success || result.ExitCode != 0)
        {
            return null;
        }

        int? value = null;
        foreach (var raw in result.StandardOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("L4T_RETURN=", StringComparison.Ordinal))
            {
                continue;
            }

            if (value is not null ||
                !int.TryParse(line["L4T_RETURN=".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return null;
            }

            value = parsed;
        }

        return value;
    }

    internal static string SanitizeDescription(string description)
    {
        var chars = description
            .Where(c => !char.IsControl(c) && c != '|' && c != '\'' && c != '"' && c != '`' && c != '$')
            .ToArray();
        var text = new string(chars).Trim();
        return text.Length > MaxDescriptionLength ? text[..MaxDescriptionLength] : text;
    }

    private async Task<RestorePointSnapshot> QueryAsync(CancellationToken ct)
    {
        var result = await _executor.RunAsync(
            "powershell.exe",
            PowerShellArguments.EncodedCommand(QueryScript),
            ct,
            timeout: QueryTimeout).ConfigureAwait(false);
        return ParseSnapshot(result);
    }

    private static RestorePointResult Fail(string message) => new(RestorePointOutcome.Failed, message, null);

    // 保護状態はポリシー (DisableSR) と RPSessionInterval で判定する。頻度の値は読まない・書かない。
    private const string QueryScript = """
        $ErrorActionPreference = 'Stop'
        $enabled = $true
        $policy = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore' -Name DisableSR -ErrorAction SilentlyContinue
        if ($policy -and $policy.DisableSR -eq 1) { $enabled = $false }
        $sr = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore' -ErrorAction SilentlyContinue
        if (-not $sr -or $sr.DisableSR -eq 1 -or $sr.RPSessionInterval -ne 1) { $enabled = $false }
        if ($enabled) { Write-Output 'L4T_PROTECTION=Enabled' } else { Write-Output 'L4T_PROTECTION=Disabled' }
        if ($enabled) {
            foreach ($p in @(Get-CimInstance -Namespace 'root/default' -ClassName SystemRestore)) {
                $t = [Management.ManagementDateTimeConverter]::ToDateTime($p.CreationTime).ToUniversalTime().ToString('o')
                $d = ([string]$p.Description) -replace '[\r\n|]', ' '
                Write-Output ('L4T_POINT={0}|{1}|{2}' -f $p.SequenceNumber, $t, $d)
            }
        }
        Write-Output 'L4T_END'
        """;

    private static string BuildCreateScript(string description) => $$"""
        $ErrorActionPreference = 'Stop'
        $r = Invoke-CimMethod -Namespace 'root/default' -ClassName SystemRestore -MethodName CreateRestorePoint -Arguments @{ Description = '{{description}}'; RestorePointType = [uint32]12; EventType = [uint32]100 }
        Write-Output ('L4T_RETURN={0}' -f [int]$r.ReturnValue)
        """;

    private static string BuildEnableScript() => """
        $ErrorActionPreference = 'Stop'
        Enable-ComputerRestore -Drive ($env:SystemDrive + '\')
        """;
}

/// <summary>復元ポイント一覧と保護状態のスナップショット。Error があれば解釈不能を表す。</summary>
internal sealed record RestorePointSnapshot(bool ProtectionEnabled, IReadOnlyList<RestorePointInfo> Points, string? Error)
{
    internal static RestorePointSnapshot Invalid(string error) => new(false, [], error);
}
