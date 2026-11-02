using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows.Actions;

internal readonly record struct UwpBackgroundValues(int? Disabled, int? DisabledByUser)
{
    public static UwpBackgroundValues Enabled => new(0, 0);
    public static UwpBackgroundValues DisabledValues => new(1, 1);

    public bool IsCombinationOf(UwpBackgroundValues before, UwpBackgroundValues after) =>
        (Disabled == before.Disabled || Disabled == after.Disabled) &&
        (DisabledByUser == before.DisabledByUser || DisabledByUser == after.DisabledByUser);
}

internal interface IUwpBackgroundSettingsStore
{
    IReadOnlyDictionary<string, UwpBackgroundValues> ReadMany(
        IReadOnlyList<string> familyNames,
        CancellationToken ct);

    void WriteMany(
        IReadOnlyList<KeyValuePair<string, UwpBackgroundValues>> values,
        CancellationToken ct);
}

[SupportedOSPlatform("windows")]
internal sealed class RegistryUwpBackgroundSettingsStore : IUwpBackgroundSettingsStore
{
    private const string BasePath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications";

    public IReadOnlyDictionary<string, UwpBackgroundValues> ReadMany(
        IReadOnlyList<string> familyNames,
        CancellationToken ct)
    {
        var values = new Dictionary<string, UwpBackgroundValues>(
            familyNames.Count,
            StringComparer.OrdinalIgnoreCase);
        using var baseKey = Registry.CurrentUser.OpenSubKey(BasePath);
        foreach (var familyName in familyNames)
        {
            ct.ThrowIfCancellationRequested();
            using var appKey = baseKey?.OpenSubKey(familyName);
            values[familyName] = appKey is null
                ? default
                : new UwpBackgroundValues(
                    ReadDword(appKey, "Disabled"),
                    ReadDword(appKey, "DisabledByUser"));
        }

        return values;
    }

    public void WriteMany(
        IReadOnlyList<KeyValuePair<string, UwpBackgroundValues>> values,
        CancellationToken ct)
    {
        if (values.Count == 0)
        {
            return;
        }

        using var baseKey = Registry.CurrentUser.CreateSubKey(BasePath);
        foreach (var (familyName, setting) in values)
        {
            ct.ThrowIfCancellationRequested();
            using var appKey = baseKey.CreateSubKey(familyName);
            WriteDword(appKey, "Disabled", setting.Disabled);
            WriteDword(appKey, "DisabledByUser", setting.DisabledByUser);
        }
    }

    private static int? ReadDword(RegistryKey key, string name)
    {
        var value = key.GetValue(name);
        return value switch
        {
            null => null,
            int number => number,
            _ => throw new InvalidDataException($"{name} が DWORD ではありません。"),
        };
    }

    private static void WriteDword(RegistryKey key, string name, int? value)
    {
        if (value is int number)
        {
            key.SetValue(name, number, RegistryValueKind.DWord);
        }
        else
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
    }
}
