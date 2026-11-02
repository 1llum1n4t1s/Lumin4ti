using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// 仮想化ベースセキュリティ (VBS/HVCI) の無効化トグル。
/// OFF では BCD とレジストリ 3 値の明示設定を解除し、Windows の既定値とポリシーへ委ねる。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VirtualizationSecurityToggle : IMaintenanceToggle
{
    private static readonly VirtualizationSecurityComponent[] Components =
    [
        VirtualizationSecurityComponent.HypervisorLaunchType,
        VirtualizationSecurityComponent.DeviceGuard,
        VirtualizationSecurityComponent.Hvci,
        VirtualizationSecurityComponent.Lsa,
    ];

    private readonly IVirtualizationSecurityPlatform _platform;

    public VirtualizationSecurityToggle(ICommandExecutor executor)
        : this(new WindowsVirtualizationSecurityPlatform(executor))
    {
    }

    internal VirtualizationSecurityToggle(IVirtualizationSecurityPlatform platform)
    {
        _platform = platform;
    }

    public string Id => "vbs-hvci-off";

    public string Label => "VBS / HVCI を無効化 (メモリ節約 1〜2GB)";

    public string Description =>
        "仮想化ベースセキュリティ (VBS) とハイパーバイザー保護コード整合性 (HVCI) を無効化して、常時確保されている 1〜2GB 程度のメモリを解放します。" +
        "セキュリティ機能とのトレードオフであり、Hyper-V / WSL2 / Windows サンドボックス / Docker Desktop / Credential Guard を使う場合は OFF (既定) のままにしてください。" +
        "OFF は BCD とレジストリ3値の明示設定を削除します。元の値は保存・復元せず、実効状態は Windows と組織ポリシーに依存します。";

    public CommandCategory Category => CommandCategory.Performance;

    public bool RequiresReboot => true;

    public bool SupportsExplicitOff => true;

    public MaintenanceStateDiagnostic? StateDiagnostic { get; private set; }
    public string? StateFailureReason { get; private set; }

    public async Task<bool?> GetStateAsync(CancellationToken ct = default)
    {
        StateDiagnostic = null;
        StateFailureReason = null;
        try
        {
            var current = await CaptureAsync(ct);
            return current.IsFullyApplied;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StateFailureReason = $"{ex.GetType().Name}: {ex.Message}";
            MaintenanceItemLog.StateReadFailed(Id, Label, StateFailureReason);
            return null;
        }
    }

    public async Task<MaintenanceActionResult> SetStateAsync(bool on, CancellationToken ct = default)
    {
        try
        {
            return on ? await ApplyAsync(ct) : await ResetAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LoggerBootstrap.Log.Error($"{Id}: {(on ? "適用" : "設定解除")}の準備に失敗しました", ex);
            return MaintenanceActionResult.Fail(ex.Message);
        }
    }

    private async Task<MaintenanceActionResult> ApplyAsync(CancellationToken ct)
    {
        var before = await CaptureAsync(ct);
        var failure = await TransitionAsync(before, VirtualizationSecuritySnapshot.Applied(), ct);
        if (failure is not null)
        {
            return await CreateFailureResultAsync("VBS / HVCI の無効化", failure);
        }

        LoggerBootstrap.Log.Info($"{Id} → ON");
        return MaintenanceActionResult.Ok(
        [
            "  - hypervisorlaunchtype を off に設定しました",
            "  - VBS / HVCI / Credential Guard のレジストリ値を無効 (0) に設定しました",
        ]);
    }

    private async Task<MaintenanceActionResult> ResetAsync(CancellationToken ct)
    {
        var before = await CaptureAsync(ct);
        var failure = await TransitionAsync(before, VirtualizationSecuritySnapshot.Default(), ct);
        if (failure is not null)
        {
            return await CreateFailureResultAsync("VBS / HVCI の明示設定の解除", failure);
        }

        LoggerBootstrap.Log.Info($"{Id} → OFF (明示設定を解除)");
        return MaintenanceActionResult.Ok(
        [
            "  - hypervisorlaunchtype と VBS / HVCI / Credential Guard の明示設定を解除しました",
            "  - セキュリティ機能の有効・無効は Windows の既定値とポリシーに従います",
        ]);
    }

    private async Task<TransitionFailure?> TransitionAsync(
        VirtualizationSecuritySnapshot before,
        VirtualizationSecuritySnapshot desired,
        CancellationToken ct)
    {
        var attempted = new Stack<VirtualizationSecurityComponent>();
        try
        {
            foreach (var component in Components)
            {
                if (ComponentEquals(before, desired, component))
                {
                    continue;
                }

                // 書き込みが「変更後に例外」になる場合も戻せるよう、試行前に補償対象へ積む。
                attempted.Push(component);
                await WriteComponentAsync(component, desired, ct);
            }

            var actual = await CaptureAsync(ct);
            if (!actual.EquivalentTo(desired))
            {
                throw new InvalidOperationException("変更後の実状態が目標状態と一致しません。");
            }

            return null;
        }
        catch (Exception ex)
        {
            var rollbackErrors = new List<string>();
            while (attempted.TryPop(out var component))
            {
                try
                {
                    var (isBefore, isDesired) = await ReadRollbackStateAsync(component, before, desired);
                    if (isBefore)
                    {
                        continue;
                    }

                    if (!isDesired)
                    {
                        rollbackErrors.Add($"{component}: 外部で変更された値を保持しました。");
                        continue;
                    }

                    // 安全上の補償は、利用者キャンセル後も最後まで実行する。
                    await WriteComponentAsync(component, before, CancellationToken.None);
                }
                catch (Exception rollbackEx)
                {
                    rollbackErrors.Add($"{component}: {rollbackEx.Message}");
                }
            }

            try
            {
                var restored = await CaptureAsync(CancellationToken.None);
                if (!restored.EquivalentTo(before))
                {
                    rollbackErrors.Add("補償後の実状態が開始前の状態と一致しません。");
                }
            }
            catch (Exception verificationEx)
            {
                rollbackErrors.Add($"補償後の実状態を確認できません: {verificationEx.Message}");
            }

            LoggerBootstrap.Log.Error($"{Id}: 変更失敗。補償エラー={rollbackErrors.Count}", ex);
            return new TransitionFailure(ex, rollbackErrors);
        }
    }

    private async Task<MaintenanceActionResult> CreateFailureResultAsync(
        string operation,
        TransitionFailure failure)
    {
        var finalState = await GetStateAsync(CancellationToken.None);
        var detail = $"{operation}に失敗しました: {failure.Error.Message}";
        detail += failure.RollbackErrors.Count == 0
            ? "\n  - 変更済み項目は開始前の状態へ補償しました"
            : $"\n  - 補償にも失敗しました: {string.Join(" / ", failure.RollbackErrors)}";
        detail += $"\n  - 最終状態: {DescribeState(finalState)}";
        return MaintenanceActionResult.Fail(detail);
    }

    private async Task<VirtualizationSecuritySnapshot> CaptureAsync(CancellationToken ct) => new()
    {
        HypervisorLaunchType = await _platform.ReadHypervisorLaunchTypeAsync(ct),
        DeviceGuard = _platform.ReadRegistryValue(VirtualizationSecurityComponent.DeviceGuard),
        Hvci = _platform.ReadRegistryValue(VirtualizationSecurityComponent.Hvci),
        Lsa = _platform.ReadRegistryValue(VirtualizationSecurityComponent.Lsa),
    };

    private async Task WriteComponentAsync(
        VirtualizationSecurityComponent component,
        VirtualizationSecuritySnapshot snapshot,
        CancellationToken ct)
    {
        if (component == VirtualizationSecurityComponent.HypervisorLaunchType)
        {
            await _platform.WriteHypervisorLaunchTypeAsync(snapshot.HypervisorLaunchType, ct);
            return;
        }

        ct.ThrowIfCancellationRequested();
        _platform.WriteRegistryValue(component, snapshot.GetRegistryValue(component));
    }

    private async Task<(bool IsBefore, bool IsDesired)> ReadRollbackStateAsync(
        VirtualizationSecurityComponent component,
        VirtualizationSecuritySnapshot before,
        VirtualizationSecuritySnapshot desired)
    {
        if (component == VirtualizationSecurityComponent.HypervisorLaunchType)
        {
            var current = await _platform.ReadHypervisorLaunchTypeAsync(CancellationToken.None);
            return (current.EquivalentTo(before.HypervisorLaunchType), current.EquivalentTo(desired.HypervisorLaunchType));
        }

        var registryValue = _platform.ReadRegistryValue(component);
        return (registryValue.EquivalentTo(before.GetRegistryValue(component)),
            registryValue.EquivalentTo(desired.GetRegistryValue(component)));
    }

    private static bool ComponentEquals(
        VirtualizationSecuritySnapshot left,
        VirtualizationSecuritySnapshot right,
        VirtualizationSecurityComponent component) =>
        component == VirtualizationSecurityComponent.HypervisorLaunchType
            ? left.HypervisorLaunchType.EquivalentTo(right.HypervisorLaunchType)
            : left.GetRegistryValue(component).EquivalentTo(right.GetRegistryValue(component));

    private static string DescribeState(bool? state) => state switch
    {
        true => "ON (全項目適用済み)",
        false => "OFF (全項目適用には該当しません)",
        null => "判定不能",
    };

    private sealed record TransitionFailure(Exception Error, IReadOnlyList<string> RollbackErrors);
}
