using System.Text.Json;
using Lumin4ti.Core.Interfaces;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows.Actions;

internal enum VirtualizationSecurityComponent
{
    HypervisorLaunchType,
    DeviceGuard,
    Hvci,
    Lsa,
}

internal sealed record BcdValueSnapshot
{
    public bool Exists { get; init; }

    public string? Value { get; init; }

    public static BcdValueSnapshot Missing() => new() { Exists = false };

    public static BcdValueSnapshot Present(string value) =>
        new() { Exists = true, Value = Normalize(value) };

    public bool IsApplied => Exists && Value?.Equals("Off", StringComparison.OrdinalIgnoreCase) == true;

    public bool EquivalentTo(BcdValueSnapshot other) =>
        Exists == other.Exists
        && (!Exists || string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase));

    public void Validate()
    {
        if (!Exists)
        {
            if (Value is not null)
            {
                throw new InvalidDataException("存在しない BCD 要素に値が設定されています。");
            }

            return;
        }

        _ = Normalize(Value ?? throw new InvalidDataException("BCD 要素の値がありません。"));
    }

    private static string Normalize(string value) => value.Trim() switch
    {
        var v when v.Equals("Auto", StringComparison.OrdinalIgnoreCase) => "Auto",
        var v when v.Equals("Off", StringComparison.OrdinalIgnoreCase) => "Off",
        _ => throw new InvalidDataException($"未対応の hypervisorlaunchtype 値です: {value}"),
    };
}

internal sealed record RegistryValueSnapshot
{
    public bool Exists { get; init; }

    public RegistryValueKind? Kind { get; init; }

    public int? DwordValue { get; init; }

    public long? QwordValue { get; init; }

    public string? StringValue { get; init; }

    public string[]? MultiStringValue { get; init; }

    public byte[]? BinaryValue { get; init; }

    public static RegistryValueSnapshot Missing() => new() { Exists = false };

    public static RegistryValueSnapshot Dword(int value) => new()
    {
        Exists = true,
        Kind = RegistryValueKind.DWord,
        DwordValue = value,
    };

    public static RegistryValueSnapshot FromRegistry(RegistryValueKind kind, object value) => kind switch
    {
        RegistryValueKind.DWord when value is int dword => Dword(dword),
        RegistryValueKind.QWord when value is long qword => new()
        {
            Exists = true,
            Kind = kind,
            QwordValue = qword,
        },
        RegistryValueKind.String or RegistryValueKind.ExpandString when value is string text => new()
        {
            Exists = true,
            Kind = kind,
            StringValue = text,
        },
        RegistryValueKind.MultiString when value is string[] strings => new()
        {
            Exists = true,
            Kind = kind,
            MultiStringValue = [.. strings],
        },
        RegistryValueKind.Binary or RegistryValueKind.None when value is byte[] bytes => new()
        {
            Exists = true,
            Kind = kind,
            BinaryValue = [.. bytes],
        },
        _ => throw new InvalidDataException($"レジストリ値 {kind} をバックアップできません。"),
    };

    public bool IsApplied =>
        Exists && Kind == RegistryValueKind.DWord && DwordValue == 0;

    public bool EquivalentTo(RegistryValueSnapshot other)
    {
        if (Exists != other.Exists)
        {
            return false;
        }

        if (!Exists)
        {
            return true;
        }

        return Kind == other.Kind
            && DwordValue == other.DwordValue
            && QwordValue == other.QwordValue
            && StringValue == other.StringValue
            && SequenceEqual(MultiStringValue, other.MultiStringValue)
            && SequenceEqual(BinaryValue, other.BinaryValue);
    }

    public object ToRegistryValue()
    {
        Validate();
        return Kind switch
        {
            RegistryValueKind.DWord => DwordValue!.Value,
            RegistryValueKind.QWord => QwordValue!.Value,
            RegistryValueKind.String or RegistryValueKind.ExpandString => StringValue!,
            RegistryValueKind.MultiString => MultiStringValue!,
            RegistryValueKind.Binary or RegistryValueKind.None => BinaryValue!,
            _ => throw new InvalidDataException($"レジストリ値 {Kind} を復元できません。"),
        };
    }

    public void Validate()
    {
        if (!Exists)
        {
            if (Kind is not null || HasAnyValue())
            {
                throw new InvalidDataException("存在しないレジストリ値に型または値が設定されています。");
            }

            return;
        }

        var valid = Kind switch
        {
            RegistryValueKind.DWord => DwordValue is not null && ValueCount() == 1,
            RegistryValueKind.QWord => QwordValue is not null && ValueCount() == 1,
            RegistryValueKind.String or RegistryValueKind.ExpandString => StringValue is not null && ValueCount() == 1,
            RegistryValueKind.MultiString => MultiStringValue is not null && ValueCount() == 1,
            RegistryValueKind.Binary or RegistryValueKind.None => BinaryValue is not null && ValueCount() == 1,
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidDataException($"レジストリ値バックアップの型と値が一致しません: {Kind}");
        }
    }

    private int ValueCount() =>
        (DwordValue is null ? 0 : 1)
        + (QwordValue is null ? 0 : 1)
        + (StringValue is null ? 0 : 1)
        + (MultiStringValue is null ? 0 : 1)
        + (BinaryValue is null ? 0 : 1);

    private bool HasAnyValue() => ValueCount() > 0;

    private static bool SequenceEqual<T>(T[]? left, T[]? right) =>
        left is null ? right is null : right is not null && left.SequenceEqual(right);
}

internal sealed record VirtualizationSecuritySnapshot
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required BcdValueSnapshot HypervisorLaunchType { get; init; }

    public required RegistryValueSnapshot DeviceGuard { get; init; }

    public required RegistryValueSnapshot Hvci { get; init; }

    public required RegistryValueSnapshot Lsa { get; init; }

    public bool IsFullyApplied =>
        HypervisorLaunchType.IsApplied && DeviceGuard.IsApplied && Hvci.IsApplied && Lsa.IsApplied;

    public bool HasAnyAppliedComponent =>
        HypervisorLaunchType.IsApplied || DeviceGuard.IsApplied || Hvci.IsApplied || Lsa.IsApplied;

    public static VirtualizationSecuritySnapshot Applied() => new()
    {
        HypervisorLaunchType = BcdValueSnapshot.Present("Off"),
        DeviceGuard = RegistryValueSnapshot.Dword(0),
        Hvci = RegistryValueSnapshot.Dword(0),
        Lsa = RegistryValueSnapshot.Dword(0),
    };

    public bool EquivalentTo(VirtualizationSecuritySnapshot other) =>
        HypervisorLaunchType.EquivalentTo(other.HypervisorLaunchType)
        && DeviceGuard.EquivalentTo(other.DeviceGuard)
        && Hvci.EquivalentTo(other.Hvci)
        && Lsa.EquivalentTo(other.Lsa);

    public RegistryValueSnapshot GetRegistryValue(VirtualizationSecurityComponent component) => component switch
    {
        VirtualizationSecurityComponent.DeviceGuard => DeviceGuard,
        VirtualizationSecurityComponent.Hvci => Hvci,
        VirtualizationSecurityComponent.Lsa => Lsa,
        _ => throw new ArgumentOutOfRangeException(nameof(component), component, null),
    };

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"未対応の VBS バックアップ schema です: {SchemaVersion}");
        }

        HypervisorLaunchType.Validate();
        DeviceGuard.Validate();
        Hvci.Validate();
        Lsa.Validate();
    }
}

internal interface IVirtualizationSecurityPlatform
{
    Task<BcdValueSnapshot> ReadHypervisorLaunchTypeAsync(CancellationToken ct);

    Task WriteHypervisorLaunchTypeAsync(BcdValueSnapshot value, CancellationToken ct);

    RegistryValueSnapshot ReadRegistryValue(VirtualizationSecurityComponent component);

    void WriteRegistryValue(VirtualizationSecurityComponent component, RegistryValueSnapshot value);
}

internal sealed class WindowsVirtualizationSecurityPlatform(ICommandExecutor executor) : IVirtualizationSecurityPlatform
{
    private const string DeviceGuardKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard";
    private const string HvciKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
    private const string LsaKey = @"SYSTEM\CurrentControlSet\Control\Lsa";

    public async Task<BcdValueSnapshot> ReadHypervisorLaunchTypeAsync(CancellationToken ct)
    {
        var result = await executor.RunAsync("bcdedit.exe", "/enum {current}", ct);
        if (!result.Success)
        {
            throw new InvalidOperationException($"bcdedit の読み取りに失敗しました (exit={result.ExitCode}): {result.StandardError}");
        }

        var line = result.StandardOutput.Split('\n')
            .FirstOrDefault(l => l.Contains("hypervisorlaunchtype", StringComparison.OrdinalIgnoreCase));
        if (line is null)
        {
            return BcdValueSnapshot.Missing();
        }

        var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2)
        {
            throw new InvalidDataException($"hypervisorlaunchtype を解析できません: {line.Trim()}");
        }

        return BcdValueSnapshot.Present(fields[^1]);
    }

    public async Task WriteHypervisorLaunchTypeAsync(BcdValueSnapshot value, CancellationToken ct)
    {
        value.Validate();
        var arguments = value.Exists
            ? $"/set hypervisorlaunchtype {value.Value!.ToLowerInvariant()}"
            : "/deletevalue hypervisorlaunchtype";
        var result = await executor.RunAsync("bcdedit.exe", arguments, ct);
        if (result.Success)
        {
            return;
        }

        // /deletevalue は要素が既に無い場合も非 0 になりうるため、目的状態なら冪等成功とする。
        var current = await ReadHypervisorLaunchTypeAsync(ct);
        if (!current.EquivalentTo(value))
        {
            throw new InvalidOperationException($"bcdedit の設定に失敗しました (exit={result.ExitCode}): {result.StandardError}");
        }
    }

    public RegistryValueSnapshot ReadRegistryValue(VirtualizationSecurityComponent component)
    {
        var (keyPath, valueName) = GetRegistryLocation(component);
        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        if (key is null || !key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
        {
            return RegistryValueSnapshot.Missing();
        }

        var kind = key.GetValueKind(valueName);
        var value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            ?? throw new InvalidDataException($@"{keyPath}\{valueName} の値を読み取れません。");
        return RegistryValueSnapshot.FromRegistry(kind, value);
    }

    public void WriteRegistryValue(VirtualizationSecurityComponent component, RegistryValueSnapshot value)
    {
        value.Validate();
        var (keyPath, valueName) = GetRegistryLocation(component);
        if (!value.Exists)
        {
            using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
            return;
        }

        using var writableKey = Registry.LocalMachine.CreateSubKey(keyPath);
        writableKey.SetValue(valueName, value.ToRegistryValue(), value.Kind!.Value);
    }

    private static (string KeyPath, string ValueName) GetRegistryLocation(VirtualizationSecurityComponent component) => component switch
    {
        VirtualizationSecurityComponent.DeviceGuard => (DeviceGuardKey, "EnableVirtualizationBasedSecurity"),
        VirtualizationSecurityComponent.Hvci => (HvciKey, "Enabled"),
        VirtualizationSecurityComponent.Lsa => (LsaKey, "LsaCfgFlags"),
        _ => throw new ArgumentOutOfRangeException(nameof(component), component, null),
    };
}

internal sealed class VirtualizationSecurityBackupStore
{
    private readonly ProtectedBackupStorage? _protectedStorage;
    private readonly string? _relativePath;

    public VirtualizationSecurityBackupStore(string backupPath)
    {
        BackupPath = backupPath;
    }

    public VirtualizationSecurityBackupStore(ProtectedBackupStorage protectedStorage, string relativePath)
    {
        _protectedStorage = protectedStorage;
        _relativePath = relativePath;
        BackupPath = protectedStorage.GetFullPath(relativePath);
    }

    public string BackupPath { get; }

    public VirtualizationSecuritySnapshot? Load()
    {
        if (!Exists())
        {
            return null;
        }

        try
        {
            var json = _protectedStorage is null
                ? File.ReadAllText(BackupPath)
                : _protectedStorage.ReadAllText(_relativePath!);
            var backup = Lumin4tiJson.Deserialize<VirtualizationSecuritySnapshot>(json)
                ?? throw new InvalidDataException("VBS バックアップが空です。");
            backup.Validate();
            return backup;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("VBS バックアップを解析できません。", ex);
        }
    }

    public void SaveNew(VirtualizationSecuritySnapshot snapshot)
    {
        snapshot.Validate();
        if (Exists())
        {
            throw new IOException("VBS バックアップは既に存在します。");
        }

        if (_protectedStorage is not null)
        {
            _protectedStorage.WriteNewAtomically(
                _relativePath!,
                stream => Lumin4tiJson.Serialize(stream, snapshot));
            return;
        }

        var directory = Path.GetDirectoryName(BackupPath)
            ?? throw new InvalidOperationException("VBS バックアップ先ディレクトリを特定できません。");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(BackupPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                Lumin4tiJson.Serialize(stream, snapshot);
                stream.Flush(flushToDisk: true);
            }

            // 同一ディレクトリ内の rename で、読者から途中までの JSON が見えないようにする。
            File.Move(tempPath, BackupPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public void Delete()
    {
        if (_protectedStorage is null)
        {
            File.Delete(BackupPath);
        }
        else
        {
            _protectedStorage.Delete(_relativePath!);
        }
    }

    private bool Exists() => _protectedStorage is null
        ? File.Exists(BackupPath)
        : _protectedStorage.FileExists(_relativePath!);
}
