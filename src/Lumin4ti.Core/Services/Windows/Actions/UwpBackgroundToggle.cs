using System.Runtime.Versioning;
using System.Text.Json;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// 全 UWP アプリのバックグラウンド実行を「常にオフ」に一括設定するトグル。
/// PowerShell (Get-AppxPackage) を使わず、WinRT の PackageManager でパッケージを列挙し、
/// BackgroundAccessApplications 配下へ Registry API で直接書き込む。
/// ON = 全アプリ「常にオフ」を適用、OFF = 個別設定を削除して既定 (電力最適化) に戻す。
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class UwpBackgroundToggle : IMaintenanceToggle
{
    private const string BasePath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications";

    public string Id => "uwp-background-off";

    public string Label => "UWP バックグラウンド実行を一括オフ (メモリ節約 200〜500MB)";

    public string Description =>
        "ストアアプリ (UWP) が画面を閉じた後もバックグラウンドで動き続ける動作を、全アプリまとめて「常にオフ」に設定します (設定 > プライバシー > バックグラウンドアプリの一括版)。" +
        "常駐が減ることで 200〜500MB 程度のメモリ節約が見込めます。通知やライブタイル更新が必要なアプリは、OFF に戻すか設定アプリで個別に「常にオン」へ変更してください。";

    public CommandCategory Category => CommandCategory.Performance;

    public bool RequiresReboot => false;

    public Task<bool?> GetStateAsync(CancellationToken ct = default)
    {
        using var baseKey = Registry.CurrentUser.OpenSubKey(BasePath);
        if (baseKey is null)
        {
            return Task.FromResult<bool?>(false);
        }

        // 1 つでも「常にオフ」(Disabled=1) のアプリがあれば適用済みとみなす
        foreach (var name in baseKey.GetSubKeyNames())
        {
            using var appKey = baseKey.OpenSubKey(name);
            if (appKey?.GetValue("Disabled") is int disabled && disabled == 1)
            {
                return Task.FromResult<bool?>(true);
            }
        }

        return Task.FromResult<bool?>(false);
    }

    private static string BackupPath =>
        Path.Combine(AppPaths.AppDataDirectory, "backups", "uwp-background.json");

    public Task<MaintenanceActionResult> SetStateAsync(bool on, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (!on)
            {
                return RestoreDefaults();
            }

            // ON 適用前に、個別設定 (「常にオン」等) を持つパッケージの状態を退避しておき、
            // OFF でユーザーの個別設定を正確に復元できるようにする。
            SaveSnapshot();

            var packageManager = new PackageManager();
            // Get-AppxPackage -PackageTypeFilter Main 相当: フレームワーク・リソースパッケージを除外
            var familyNames = packageManager.FindPackagesForUser(string.Empty)
                .Where(p => !p.IsFramework && !p.IsResourcePackage && !p.IsBundle)
                .Select(p => p.Id.FamilyName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            using var baseKey = Registry.CurrentUser.CreateSubKey(BasePath);
            var count = 0;
            foreach (var familyName in familyNames)
            {
                ct.ThrowIfCancellationRequested();
                using var appKey = baseKey.CreateSubKey(familyName);
                appKey.SetValue("Disabled", 1, RegistryValueKind.DWord);
                appKey.SetValue("DisabledByUser", 1, RegistryValueKind.DWord);
                count++;
            }

            LoggerBootstrap.Log.Info($"{Id}: {count} パッケージを常にオフに設定");
            return MaintenanceActionResult.Ok($"  - 「常にオフ」設定: {count} パッケージ");
        }, ct);

    /// <summary>ON 適用前に個別設定を持つパッケージの Disabled/DisabledByUser を退避する。</summary>
    private static void SaveSnapshot()
    {
        if (File.Exists(BackupPath))
        {
            return; // 既存バックアップ (真の元状態) を保つため上書きしない
        }

        var snapshot = new Dictionary<string, int?[]>();
        using (var baseKey = Registry.CurrentUser.OpenSubKey(BasePath))
        {
            if (baseKey is not null)
            {
                foreach (var name in baseKey.GetSubKeyNames())
                {
                    using var appKey = baseKey.OpenSubKey(name);
                    var disabled = appKey?.GetValue("Disabled") as int?;
                    var byUser = appKey?.GetValue("DisabledByUser") as int?;
                    if (disabled is not null || byUser is not null)
                    {
                        snapshot[name] = [disabled, byUser];
                    }
                }
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
        File.WriteAllText(BackupPath, JsonSerializer.Serialize(snapshot));
    }

    private MaintenanceActionResult RestoreDefaults()
    {
        // まず ON で書き込んだ全パッケージの Disabled/DisabledByUser を消す
        using (var baseKey = Registry.CurrentUser.OpenSubKey(BasePath, writable: true))
        {
            if (baseKey is null)
            {
                return MaintenanceActionResult.Ok("  - 個別設定はありませんでした (既定のまま)");
            }

            foreach (var name in baseKey.GetSubKeyNames())
            {
                using var appKey = baseKey.OpenSubKey(name, writable: true);
                appKey?.DeleteValue("Disabled", throwOnMissingValue: false);
                appKey?.DeleteValue("DisabledByUser", throwOnMissingValue: false);
            }
        }

        // 退避しておいた元の個別設定を復元する
        var restored = RestoreSnapshot();

        LoggerBootstrap.Log.Info($"{Id}: 既定に戻しました (個別設定 {restored} 件を復元)");
        return MaintenanceActionResult.Ok(restored == 0
            ? "  - 全パッケージの個別設定を削除して既定 (電力最適化) に戻しました"
            : $"  - 既定に戻し、元々あった個別設定 {restored} 件を復元しました");
    }

    /// <summary>退避した個別設定を書き戻す。復元した件数を返す。</summary>
    private static int RestoreSnapshot()
    {
        if (!File.Exists(BackupPath))
        {
            return 0;
        }

        Dictionary<string, int?[]>? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<Dictionary<string, int?[]>>(File.ReadAllText(BackupPath));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return 0;
        }

        var restored = 0;
        if (snapshot is not null)
        {
            using var baseKey = Registry.CurrentUser.CreateSubKey(BasePath);
            foreach (var (familyName, values) in snapshot)
            {
                using var appKey = baseKey.CreateSubKey(familyName);
                if (values.Length > 0 && values[0] is int disabled)
                {
                    appKey.SetValue("Disabled", disabled, RegistryValueKind.DWord);
                }

                if (values.Length > 1 && values[1] is int byUser)
                {
                    appKey.SetValue("DisabledByUser", byUser, RegistryValueKind.DWord);
                }

                restored++;
            }
        }

        try
        {
            File.Delete(BackupPath);
        }
        catch (IOException)
        {
            // 削除失敗は無視
        }

        return restored;
    }
}
