using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// システムトレイの通知アイコンキャッシュをリセットする。
/// アンインストール済みアプリの亡霊アイコン掃除の定番手順。Registry API で直接削除する。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIconResetAction : IMaintenanceAction
{
    private const string TrayNotifyKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\TrayNotify";

    public string Id => "tray-icon-reset";

    public string Label => "システムトレイのアイコンキャッシュをリセット";

    public string Description =>
        "タスクバー右下の通知領域 (システムトレイ) のアイコンキャッシュを削除します。" +
        "アンインストールしたはずのアプリのアイコンが「隠れているインジケーター」に残り続ける現象を解消できます。トレイの表示/非表示のカスタマイズ設定は一度リセットされます。";

    public CommandCategory Category => CommandCategory.Cleanup;

    public bool RequiresReboot => false;

    public bool AffectsExplorer => true;

    public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default)
    {
        using var key = Registry.CurrentUser.OpenSubKey(TrayNotifyKey, writable: true);
        if (key is null)
        {
            return Task.FromResult(MaintenanceActionResult.Ok("  - キャッシュはありませんでした"));
        }

        key.DeleteValue("IconStreams", throwOnMissingValue: false);
        key.DeleteValue("PastIconsStream", throwOnMissingValue: false);

        LoggerBootstrap.Log.Info($"{Id}: 完了");
        return Task.FromResult(MaintenanceActionResult.Ok("  - IconStreams / PastIconsStream を削除しました"));
    }
}

/// <summary>
/// Game Bar / Game DVR 関連の無効化設定を削除して Windows 既定に戻す。
/// 過去の最適化ツール等が書き込んだ「録画無効化」を解除し、Game Bar を正常動作に戻す。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GameDvrResetAction : IMaintenanceAction
{
    public string Id => "gamedvr-reset";

    public string Label => "Game Bar / Game DVR の設定を既定に戻す";

    public string Description =>
        "過去の最適化ツールやレジストリ調整で書き込まれた Game Bar / Game DVR (ゲーム録画) の無効化設定を削除し、Windows 既定の動作に戻します。" +
        "「Win+G が反応しない」「ゲームのキャプチャができない」といった症状の解消に有効です。";

    public CommandCategory Category => CommandCategory.System;

    public bool RequiresReboot => false;

    public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default)
    {
        var lines = new List<string>();

        DeleteValue(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", lines);
        DeleteValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", lines);
        DeleteValue(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\BcastDVRUserService", "Start", lines);

        if (lines.Count == 0)
        {
            lines.Add("  - 無効化設定はありませんでした (既に既定の状態です)");
        }

        LoggerBootstrap.Log.Info($"{Id}: 完了");
        return Task.FromResult(MaintenanceActionResult.Ok(lines));
    }

    private static void DeleteValue(RegistryKey root, string keyPath, string name, List<string> lines)
    {
        using var key = root.OpenSubKey(keyPath, writable: true);
        if (key?.GetValue(name) is not null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
            lines.Add($"  - {name} を削除しました");
        }
    }
}

/// <summary>
/// NVIDIA GPU の MSI 割り込み上限 (MessageNumberLimit) を削除して割り込み分散を既定に戻す。
/// PowerShell + CIM を使わず、PCI デバイスのレジストリを C# で直接列挙する。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NvidiaMsiFixAction : IMaintenanceAction
{
    private const string PciEnumKey = @"SYSTEM\CurrentControlSet\Enum\PCI";

    public string Id => "nvidia-msi-fix";

    public string Label => "NVIDIA GPU の割り込み分散設定を最適化";

    public string Description =>
        "過去の最適化ツールが NVIDIA GPU に書き込んだ MSI 割り込みの上限値 (MessageNumberLimit) を削除し、割り込み処理を既定の分散動作に戻します。" +
        "この値が残っていると GPU 割り込みが CPU コア 0 に集中し、ゲーム中のカクつきの原因になることがあります。NVIDIA GPU が無い PC では何も変更しません。";

    public CommandCategory Category => CommandCategory.Performance;

    public bool RequiresReboot => true;

    public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default)
    {
        var removed = 0;
        var devices = 0;

        using var pciKey = Registry.LocalMachine.OpenSubKey(PciEnumKey);
        if (pciKey is not null)
        {
            // NVIDIA のベンダー ID は VEN_10DE
            foreach (var deviceName in pciKey.GetSubKeyNames().Where(n => n.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)))
            {
                ct.ThrowIfCancellationRequested();
                using var deviceKey = pciKey.OpenSubKey(deviceName);
                foreach (var instanceName in deviceKey?.GetSubKeyNames() ?? [])
                {
                    devices++;
                    var msiPath = $@"{PciEnumKey}\{deviceName}\{instanceName}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                    using var msiKey = Registry.LocalMachine.OpenSubKey(msiPath, writable: true);
                    if (msiKey?.GetValue("MessageNumberLimit") is not null)
                    {
                        msiKey.DeleteValue("MessageNumberLimit", throwOnMissingValue: false);
                        removed++;
                    }
                }
            }
        }

        LoggerBootstrap.Log.Info($"{Id}: NVIDIA デバイス {devices} 件中 {removed} 件から削除");
        return Task.FromResult(MaintenanceActionResult.Ok(devices == 0
            ? "  - NVIDIA GPU は見つかりませんでした"
            : $"  - NVIDIA デバイス {devices} 件中 {removed} 件から MessageNumberLimit を削除しました"));
    }
}

/// <summary>
/// NVMe ネイティブドライバの強制有効化 (FeatureManagement Override) を解除して初期状態に戻す。
/// BSOD (PAGE_FAULT_IN_NONPAGED_AREA) 対策の復旧手順。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NvmeDriverRevertAction : IMaintenanceAction
{
    private const string OverridesKey = @"SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides";
    private static readonly string[] OverrideValues = ["735209102", "1853569164", "156965516"];

    public string Id => "nvme-driver-revert";

    public string Label => "NVMe ネイティブドライバの強制有効化を解除";

    public string Description =>
        "Windows の実験的な NVMe ネイティブドライバを強制有効化する FeatureManagement の上書き設定を削除し、ストレージドライバを OS 既定の判定に戻します。" +
        "この上書きが残っていると環境によってはブルースクリーン (PAGE_FAULT_IN_NONPAGED_AREA) の原因になります。上書きしていない PC では何も変更しません。";

    public CommandCategory Category => CommandCategory.System;

    public bool RequiresReboot => true;

    public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default)
    {
        using var key = Registry.LocalMachine.OpenSubKey(OverridesKey, writable: true);
        if (key is null)
        {
            return Task.FromResult(MaintenanceActionResult.Ok("  - 上書き設定はありませんでした (既定のままです)"));
        }

        var removed = 0;
        foreach (var name in OverrideValues)
        {
            if (key.GetValue(name) is not null)
            {
                key.DeleteValue(name, throwOnMissingValue: false);
                removed++;
            }
        }

        LoggerBootstrap.Log.Info($"{Id}: {removed} 件削除");
        return Task.FromResult(MaintenanceActionResult.Ok(removed == 0
            ? "  - 上書き設定はありませんでした (既定のままです)"
            : $"  - {removed} 件の上書き設定を削除しました"));
    }
}
