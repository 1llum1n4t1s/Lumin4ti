using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// レジストリ値 1 件の切替仕様。
/// ON で <paramref name="AppliedValue"/> を書き込み、OFF で <paramref name="DefaultValue"/> に戻す
/// (DefaultValue が null なら値を削除して Windows 既定に戻す)。
/// </summary>
public sealed record RegistryToggleSpec(
    RegistryHive Hive,
    string KeyPath,
    string Name,
    RegistryValueKind Kind,
    object AppliedValue,
    object? DefaultValue = null);

/// <summary>
/// レジストリ値の書き込み/削除だけで ON/OFF が完結する tweak の汎用トグル。
/// 状態判定は「全 spec の現在値が AppliedValue と一致 = ON」。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryToggle(
    string id,
    string label,
    string description,
    CommandCategory category,
    IReadOnlyList<RegistryToggleSpec> specs,
    bool requiresReboot = false,
    bool affectsExplorer = false) : IMaintenanceToggle
{
    public string Id => id;

    public string Label => label;

    public string Description => description;

    public CommandCategory Category => category;

    public bool RequiresReboot => requiresReboot;

    public bool AffectsExplorer => affectsExplorer;

    internal IReadOnlyList<RegistryToggleSpec> Specs => specs;

    public Task<bool?> GetStateAsync(CancellationToken ct = default)
    {
        foreach (var spec in specs)
        {
            using var root = RegistryKey.OpenBaseKey(spec.Hive, RegistryView.Default);
            using var key = root.OpenSubKey(spec.KeyPath);
            if (!ValueEqualsApplied(key?.GetValue(spec.Name), spec))
            {
                return Task.FromResult<bool?>(false);
            }
        }

        return Task.FromResult<bool?>(true);
    }

    public Task<MaintenanceActionResult> SetStateAsync(bool on, CancellationToken ct = default)
    {
        var lines = new List<string>();

        if (on)
        {
            // ON 適用前の実値を退避し、OFF でユーザーの元の値へ正確に戻せるようにする
            RegistryValueBackup.Save(Id, specs);

            foreach (var spec in specs)
            {
                using var root = RegistryKey.OpenBaseKey(spec.Hive, RegistryView.Default);
                using var key = root.CreateSubKey(spec.KeyPath);
                key.SetValue(spec.Name, NormalizeForWrite(spec.AppliedValue, spec.Kind), spec.Kind);
                lines.Add($"  - {spec.Name} = {spec.AppliedValue}");
            }

            LoggerBootstrap.Log.Info($"{Id} → ON");
            return Task.FromResult(MaintenanceActionResult.Ok(lines));
        }

        // OFF: まず退避した元値への復元を試み、無ければ既定値/削除にフォールバックする
        if (RegistryValueBackup.TryRestore(Id, specs, lines))
        {
            LoggerBootstrap.Log.Info($"{Id} → OFF (元値に復元)");
            return Task.FromResult(MaintenanceActionResult.Ok(lines));
        }

        foreach (var spec in specs)
        {
            using var root = RegistryKey.OpenBaseKey(spec.Hive, RegistryView.Default);
            if (spec.DefaultValue is null)
            {
                using var key = root.OpenSubKey(spec.KeyPath, writable: true);
                key?.DeleteValue(spec.Name, throwOnMissingValue: false);
                lines.Add($"  - {spec.Name} を削除しました (Windows 既定に戻す)");
            }
            else
            {
                using var key = root.CreateSubKey(spec.KeyPath);
                key.SetValue(spec.Name, NormalizeForWrite(spec.DefaultValue, spec.Kind), spec.Kind);
                lines.Add($"  - {spec.Name} = {spec.DefaultValue} (既定値)");
            }
        }

        LoggerBootstrap.Log.Info($"{Id} → OFF");
        return Task.FromResult(MaintenanceActionResult.Ok(lines));
    }

    private static object NormalizeForWrite(object value, RegistryValueKind kind) =>
        // 0xFFFFFFFF 等 int 範囲外の DWORD は unchecked で int に落として書く
        kind == RegistryValueKind.DWord ? unchecked((int)Convert.ToUInt32(value)) : value;

    private static bool ValueEqualsApplied(object? current, RegistryToggleSpec spec)
    {
        if (current is null)
        {
            return false;
        }

        if (spec.Kind == RegistryValueKind.DWord)
        {
            return current is int i && unchecked((uint)i) == Convert.ToUInt32(spec.AppliedValue);
        }

        return string.Equals(current.ToString(), spec.AppliedValue.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
