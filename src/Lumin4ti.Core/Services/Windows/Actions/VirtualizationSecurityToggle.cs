using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// 仮想化ベースセキュリティ (VBS/HVCI) の無効化トグル。
/// ON 前に BCD とレジストリ 3 値を退避し、OFF ではユーザーの元状態へ正確に復元する。
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
    private readonly VirtualizationSecurityBackupStore _backupStore;

    public VirtualizationSecurityToggle(ICommandExecutor executor)
        : this(
            new WindowsVirtualizationSecurityPlatform(executor),
            new VirtualizationSecurityBackupStore(
                ProtectedBackupStorage.Default,
                "vbs-hvci.json"))
    {
    }

    internal VirtualizationSecurityToggle(
        IVirtualizationSecurityPlatform platform,
        VirtualizationSecurityBackupStore backupStore)
    {
        _platform = platform;
        _backupStore = backupStore;
    }

    public string Id => "vbs-hvci-off";

    public string Label => "VBS / HVCI を無効化 (メモリ節約 1〜2GB)";

    public string Description =>
        "仮想化ベースセキュリティ (VBS) とハイパーバイザー保護コード整合性 (HVCI) を無効化して、常時確保されている 1〜2GB 程度のメモリを解放します。" +
        "セキュリティ機能とのトレードオフであり、Hyper-V / WSL2 / Windows サンドボックス / Docker Desktop / Credential Guard を使う場合は OFF (既定) のままにしてください。";

    public CommandCategory Category => CommandCategory.Performance;

    public bool RequiresReboot => true;

    public async Task<bool?> GetStateAsync(CancellationToken ct = default)
    {
        try
        {
            var current = await CaptureAsync(ct);
            if (current.IsFullyApplied)
            {
                return true;
            }

            // 適用失敗を元状態へ補償した場合は、元状態が独自構成でも OFF と判定できる。
            var backup = _backupStore.Load();
            if (backup is not null && current.EquivalentTo(backup))
            {
                return false;
            }

            // 変更対象の一部だけが適用値なら部分適用。4 項目すべてを照合して判定する。
            return current.HasAnyAppliedComponent ? null : false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LoggerBootstrap.Log.Error($"{Id}: 状態取得に失敗しました", ex);
            return null;
        }
    }

    public async Task<MaintenanceActionResult> SetStateAsync(bool on, CancellationToken ct = default)
    {
        try
        {
            return on ? await ApplyAsync(ct) : await RestoreAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LoggerBootstrap.Log.Error($"{Id}: {(on ? "適用" : "復元")}の準備に失敗しました", ex);
            return MaintenanceActionResult.Fail(ex.Message);
        }
    }

    private async Task<MaintenanceActionResult> ApplyAsync(CancellationToken ct)
    {
        var before = await CaptureAsync(ct);
        if (_backupStore.Load() is null)
        {
            // 途中までの JSON を正本として読まないよう、一時ファイルを flush 後に rename する。
            _backupStore.SaveNew(before);
        }

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
            "  - ON 前の BCD・レジストリ状態をバックアップしました",
        ]);
    }

    private async Task<MaintenanceActionResult> RestoreAsync(CancellationToken ct)
    {
        var original = _backupStore.Load();
        if (original is null)
        {
            return MaintenanceActionResult.Fail(
                "元状態のバックアップがないため、安全に復元できません。ON を適用した環境と同じ設定フォルダーを使用してください。");
        }

        var before = await CaptureAsync(ct);
        var failure = await TransitionAsync(before, original, ct);
        if (failure is not null)
        {
            return await CreateFailureResultAsync("VBS / HVCI の元状態への復元", failure);
        }

        _backupStore.Delete();
        LoggerBootstrap.Log.Info($"{Id} → OFF (元状態に復元)");
        return MaintenanceActionResult.Ok(
        [
            "  - hypervisorlaunchtype の存在と値を元状態へ復元しました",
            "  - VBS / HVCI / Credential Guard の型と値を元状態へ復元しました",
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
                    // 安全上の補償は、利用者キャンセル後も最後まで実行する。
                    await WriteComponentAsync(component, before, CancellationToken.None);
                }
                catch (Exception rollbackEx)
                {
                    rollbackErrors.Add($"{component}: {rollbackEx.Message}");
                }
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
        false => "OFF (元状態)",
        null => "部分適用または判定不能",
    };

    private sealed record TransitionFailure(Exception Error, IReadOnlyList<string> RollbackErrors);
}
