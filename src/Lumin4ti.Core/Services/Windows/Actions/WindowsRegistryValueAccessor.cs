using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows.Actions;

internal interface IRegistryValueAccessor
{
    RegistryValueSnapshot Read(RegistryToggleSpec spec);

    void Write(RegistryToggleSpec spec, RegistryValueSnapshot value);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsRegistryValueAccessor : IRegistryValueAccessor
{
    private static readonly object MissingValue = new();

    public static WindowsRegistryValueAccessor Instance { get; } = new();

    public RegistryValueSnapshot Read(RegistryToggleSpec spec)
    {
        using var root = RegistryKey.OpenBaseKey(spec.Hive, RegistryView.Default);
        using var key = root.OpenSubKey(spec.KeyPath);
        if (key is null)
        {
            return RegistryValueSnapshot.Missing();
        }

        var value = key.GetValue(
            spec.Name,
            MissingValue,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (ReferenceEquals(value, MissingValue))
        {
            return RegistryValueSnapshot.Missing();
        }

        return RegistryValueSnapshot.FromRegistry(key.GetValueKind(spec.Name), value!);
    }

    public void Write(RegistryToggleSpec spec, RegistryValueSnapshot value)
    {
        value.Validate();
        using var root = RegistryKey.OpenBaseKey(spec.Hive, RegistryView.Default);
        if (!value.Exists)
        {
            using var key = root.OpenSubKey(spec.KeyPath, writable: true);
            key?.DeleteValue(spec.Name, throwOnMissingValue: false);
            return;
        }

        using var writableKey = root.CreateSubKey(spec.KeyPath);
        writableKey.SetValue(spec.Name, value.ToRegistryValue(), value.Kind!.Value);
    }
}
