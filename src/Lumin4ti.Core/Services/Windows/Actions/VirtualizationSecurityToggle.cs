using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// 仮想化ベースセキュリティ (VBS/HVCI) の無効化トグル。
/// レジストリ 3 値は C# で直接書き、hypervisor 起動設定のみ bcdedit (外部プロセス) を使う。
/// ON = 無効化を適用、OFF = レジストリ値を削除し bcdedit を auto に戻して Windows 既定へ。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VirtualizationSecurityToggle(ICommandExecutor executor) : IMaintenanceToggle
{
    private const string DeviceGuardKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard";
    private const string HvciKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
    private const string LsaKey = @"SYSTEM\CurrentControlSet\Control\Lsa";

    public string Id => "vbs-hvci-off";

    public string Label => "VBS / HVCI を無効化 (メモリ節約 1〜2GB)";

    public string Description =>
        "仮想化ベースセキュリティ (VBS) とハイパーバイザー保護コード整合性 (HVCI) を無効化して、常時確保されている 1〜2GB 程度のメモリを解放します。" +
        "セキュリティ機能とのトレードオフであり、Hyper-V / WSL2 / Windows サンドボックス / Docker Desktop / Credential Guard を使う場合は OFF (既定) のままにしてください。";

    public CommandCategory Category => CommandCategory.Performance;

    public bool RequiresReboot => true;

    public async Task<bool?> GetStateAsync(CancellationToken ct = default)
    {
        bool registryDisabled;
        using (var key = Registry.LocalMachine.OpenSubKey(DeviceGuardKey))
        {
            registryDisabled = key?.GetValue("EnableVirtualizationBasedSecurity") is int value && value == 0;
        }

        // レジストリだけでなく bcdedit の hypervisorlaunchtype も確認し、両者が食い違う
        // (=前回の適用が途中で失敗した部分適用状態) 場合は「状態不明 (null)」を返す。
        var hypervisorOff = await GetHypervisorOffAsync(ct);
        if (hypervisorOff is null)
        {
            return null; // bcdedit を読めない = 判定不能
        }

        if (registryDisabled == hypervisorOff.Value)
        {
            return registryDisabled; // 両者一致: true=無効化適用済み / false=既定
        }

        return null; // 不一致 = 部分適用状態
    }

    public async Task<MaintenanceActionResult> SetStateAsync(bool on, CancellationToken ct = default)
    {
        var lines = new List<string>();

        // hypervisor 起動種別 (bcdedit) を先に変更する。bcdedit は失敗しやすい (BitLocker 等) ため、
        // 先に実行して失敗ならレジストリに一切触れず中断することで「レジストリだけ変わった部分適用状態」を防ぐ。
        var bcd = await executor.RunAsync("bcdedit.exe", $"/set hypervisorlaunchtype {(on ? "off" : "auto")}", ct);
        if (!bcd.Success)
        {
            LoggerBootstrap.Log.Error($"{Id}: bcdedit 失敗 (exit={bcd.ExitCode}): {bcd.StandardError}");
            return MaintenanceActionResult.Fail(
                $"bcdedit の設定に失敗しました (exit={bcd.ExitCode})。レジストリは変更していません: {bcd.StandardError}");
        }

        lines.Add($"  - hypervisorlaunchtype を {(on ? "off" : "auto")} に設定しました");

        if (on)
        {
            SetDword(DeviceGuardKey, "EnableVirtualizationBasedSecurity", 0);
            SetDword(HvciKey, "Enabled", 0);
            SetDword(LsaKey, "LsaCfgFlags", 0);
            lines.Add("  - VBS / HVCI / Credential Guard のレジストリ値を無効 (0) に設定しました");
        }
        else
        {
            DeleteValue(DeviceGuardKey, "EnableVirtualizationBasedSecurity");
            DeleteValue(HvciKey, "Enabled");
            DeleteValue(LsaKey, "LsaCfgFlags");
            lines.Add("  - レジストリ値を削除して Windows 既定に戻しました");
        }

        LoggerBootstrap.Log.Info($"{Id} → {(on ? "ON" : "OFF")}");
        return MaintenanceActionResult.Ok(lines);
    }

    /// <summary>bcdedit /enum {current} から hypervisorlaunchtype が Off か判定する。読めなければ null。</summary>
    private async Task<bool?> GetHypervisorOffAsync(CancellationToken ct)
    {
        var bcd = await executor.RunAsync("bcdedit.exe", "/enum {current}", ct);
        if (!bcd.Success)
        {
            return null;
        }

        // "hypervisorlaunchtype" と "Off"/"Auto" は bcdedit のロケール非依存な固定トークン。
        var line = bcd.StandardOutput.Split('\n')
            .FirstOrDefault(l => l.Contains("hypervisorlaunchtype", StringComparison.OrdinalIgnoreCase));

        // 行が無い = 既定 (Auto) 扱い = Off ではない
        return line is not null && line.Contains("Off", StringComparison.OrdinalIgnoreCase);
    }

    private static void SetDword(string keyPath, string name, int value)
    {
        using var key = Registry.LocalMachine.CreateSubKey(keyPath);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    private static void DeleteValue(string keyPath, string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
