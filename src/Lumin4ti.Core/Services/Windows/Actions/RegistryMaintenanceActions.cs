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

    public CommandCategory Category => CommandCategory.Repair;

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
/// 「設定 > ディスプレイ > グラフィック」でアプリごとに指定した GPU の設定を全件削除する。
/// 値名が実行ファイルのフルパス (デスクトップアプリ) か AUMID (ストアアプリ) で、
/// 値データが "GpuPreference=2;" のような設定文字列という構造。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GpuPreferenceResetAction : IMaintenanceAction
{
    internal const string UserGpuPreferencesKey = @"SOFTWARE\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>
    /// 同じキーに同居する、アプリ登録ではない OS 全体の設定 (自動 HDR、可変リフレッシュレート、
    /// ウィンドウ表示のゲームの最適化)。アプリ単位の指定ではないので削除対象から除く。
    /// </summary>
    internal const string GlobalSettingsValueName = "DirectXUserGlobalSettings";

    /// <summary>結果に個別列挙するアプリ数の上限 (超過分は件数だけ伝える)。</summary>
    private const int MaxListedEntries = 15;

    public string Id => "gpu-preference-reset";

    public string Label => "アプリごとのグラフィック設定 (GPU の指定) をすべて削除";

    public string Description =>
        "「設定 > システム > ディスプレイ > グラフィック」で個別に指定した、アプリごとの GPU 設定 (高パフォーマンス / 省電力) の登録をすべて削除し、" +
        "どのアプリも Windows の自動判定に戻します。アンインストール済みアプリの登録が残り続ける、意図しない GPU で起動する、といった場合の整理に使います。" +
        "デスクトップアプリとストアアプリの両方が対象です。全体設定 (自動 HDR、可変リフレッシュレート、ウィンドウ表示のゲームの最適化) は削除しません。" +
        "各アプリの次回起動から反映されます。";

    public CommandCategory Category => CommandCategory.Cleanup;

    public bool RequiresReboot => false;

    /// <summary>アプリ登録として削除してよい値か (OS 全体の設定だけを残す)。</summary>
    internal static bool IsAppRegistration(string valueName) =>
        !string.IsNullOrEmpty(valueName) &&
        !valueName.Equals(GlobalSettingsValueName, StringComparison.OrdinalIgnoreCase);

    public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default)
    {
        using var key = Registry.CurrentUser.OpenSubKey(UserGpuPreferencesKey, writable: true);
        if (key is null)
        {
            LoggerBootstrap.Log.Info($"{Id}: キーなし");
            return Task.FromResult(MaintenanceActionResult.Ok("  - アプリごとの GPU 設定は登録されていませんでした"));
        }

        var targets = key.GetValueNames().Where(IsAppRegistration).ToList();
        var removed = new List<string>();
        var failures = new List<string>();

        foreach (var name in targets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                key.DeleteValue(name, throwOnMissingValue: false);
                removed.Add(name);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                failures.Add(name);
            }
        }

        // 何を消したかは復元の手がかりになるので、表示を打ち切っても全件ログへ残す。
        foreach (var name in removed)
        {
            LoggerBootstrap.Log.Info($"{Id}: 削除 {name}");
        }

        if (removed.Count == 0 && failures.Count == 0)
        {
            LoggerBootstrap.Log.Info($"{Id}: 削除対象なし");
            return Task.FromResult(MaintenanceActionResult.Ok("  - アプリごとの GPU 設定は登録されていませんでした"));
        }

        var lines = new List<string> { $"  - {removed.Count} 件のアプリの GPU 設定を削除しました" };
        lines.AddRange(removed.Take(MaxListedEntries).Select(name => $"    {name}"));
        if (removed.Count > MaxListedEntries)
        {
            lines.Add($"    ほか {removed.Count - MaxListedEntries} 件 (全件はログに記録しました)");
        }

        if (failures.Count == 0)
        {
            LoggerBootstrap.Log.Info($"{Id}: {removed.Count} 件削除");
            return Task.FromResult(MaintenanceActionResult.Ok(lines));
        }

        LoggerBootstrap.Log.Error($"{Id}: {removed.Count} 件削除 / {failures.Count} 件失敗");
        lines.Add($"  - {failures.Count} 件は権限不足で削除できませんでした");
        return Task.FromResult(MaintenanceActionResult.Partial(lines));
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

    /// <summary>サービスの起動設定が壊れないよう、無効化されているときだけ戻す既定の起動種別 (需要開始)。</summary>
    private const int ServiceStartDemand = 3;

    /// <summary>最適化ツールが Game DVR 無効化のために書き込む起動種別 (無効)。</summary>
    private const int ServiceStartDisabled = 4;

    public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default)
    {
        var lines = new List<string>();

        DeleteValue(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", lines);
        DeleteValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", lines);
        RestoreServiceStart(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\BcastDVRUserService", lines);

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

    /// <summary>
    /// Start は Type / ImagePath と並ぶサービスキーの必須値のため、削除すると SCM がサービスを
    /// 正しく扱えなくなる恐れがある。無効化 (4) されているときだけ Windows 既定の需要開始 (3) へ
    /// 書き戻し、既に既定や他の値の場合は触らない。
    /// </summary>
    private static void RestoreServiceStart(RegistryKey root, string keyPath, List<string> lines)
    {
        using var key = root.OpenSubKey(keyPath, writable: true);
        if (key?.GetValue("Start") is int start && start == ServiceStartDisabled)
        {
            key.SetValue("Start", ServiceStartDemand, RegistryValueKind.DWord);
            lines.Add("  - BcastDVRUserService の起動設定を既定 (需要開始) に戻しました");
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
