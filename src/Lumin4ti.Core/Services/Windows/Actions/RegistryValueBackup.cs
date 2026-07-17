using System.Runtime.Versioning;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// トグルを ON にする直前のレジストリ実値を %ProgramData%\Lumin4ti\backups\registry\&lt;id&gt;.json に退避し、
/// OFF 時に「開発者が信じる既定値 (ハードコード)」ではなく「ユーザーの元の値」へ正確に復元する。
/// 正本は非管理者が変更できない保護ストレージだけを使用し、旧 AppData バックアップは読み込まない。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RegistryValueBackup(
    IRegistryBackupStorage storage,
    IRegistryValueAccessor registry)
{
    public static RegistryValueBackup Default { get; } = new(
        new ProtectedRegistryBackupStorage(ProtectedBackupStorage.Default),
        WindowsRegistryValueAccessor.Instance);

    private static string RelativePath(string id) => Path.Combine("registry", id + ".json");

    /// <summary>
    /// ON 適用前の各 spec の現在値を型付きで退避する。既存バックアップは真の元値を保つため
    /// 上書きしないが、現行 schema と spec に完全一致しない場合は適用を中止する。
    /// </summary>
    public void Save(string id, IReadOnlyList<RegistryToggleSpec> specs)
    {
        ValidateSpecs(specs);
        var relativePath = RelativePath(id);
        if (storage.FileExists(relativePath))
        {
            var existing = LoadRestorePlan(relativePath, specs);
            if (!existing.IsValid)
            {
                throw new InvalidDataException(
                    $"既存のレジストリ復元バックアップを安全に使用できません: {existing.FailureReason}");
            }

            return;
        }

        var entries = new List<RegistryValueBackupEntry>(specs.Count);
        foreach (var spec in specs)
        {
            var value = registry.Read(spec);
            value.Validate();
            entries.Add(RegistryValueBackupEntry.Create(spec, value));
        }

        var document = new RegistryValueBackupDocument
        {
            SchemaVersion = RegistryValueBackupDocument.CurrentSchemaVersion,
            Entries = entries,
        };
        storage.WriteNewAtomically(
            relativePath,
            stream => Lumin4tiJson.Serialize(stream, document));
    }

    /// <summary>
    /// 全エントリの schema・対応 spec・型付き値を検証して復元計画を確定してから書き戻す。
    /// 旧形式・破損・spec 不一致では一件も変更せず Invalid を返す。
    /// </summary>
    public RegistryBackupRestoreResult TryRestore(
        string id,
        IReadOnlyList<RegistryToggleSpec> specs,
        List<string> lines)
    {
        var relativePath = RelativePath(id);
        if (!storage.FileExists(relativePath))
        {
            return new(RegistryBackupRestoreStatus.Missing);
        }

        var loaded = LoadRestorePlan(relativePath, specs);
        if (!loaded.IsValid)
        {
            return new(RegistryBackupRestoreStatus.Invalid, loaded.FailureReason);
        }

        // LoadRestorePlan が全件を検証し、spec 順の不変な計画へした後にだけ書き始める。
        foreach (var operation in loaded.Plan!)
        {
            registry.Write(operation.Spec, operation.Value);
            lines.Add(operation.Value.Exists
                ? $"  - {operation.Spec.Name} を元の値 ({operation.Value.Kind}) に復元しました"
                : $"  - {operation.Spec.Name} を削除しました (元は未設定)");
        }

        try
        {
            storage.Delete(relativePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // 値の復元は完了している。正本を消せなかったことは隠さず、次回も同じ元値を保つ。
            lines.Add("  - 復元バックアップを削除できなかったため保持しました");
            LoggerBootstrap.Log.Error($"{id}: 復元済みバックアップを削除できませんでした", ex);
        }

        return new(RegistryBackupRestoreStatus.Restored);
    }

    private RegistryRestorePlanLoadResult LoadRestorePlan(
        string relativePath,
        IReadOnlyList<RegistryToggleSpec> specs)
    {
        try
        {
            ValidateSpecs(specs);
        }
        catch (InvalidDataException ex)
        {
            return RegistryRestorePlanLoadResult.Invalid(ex.Message);
        }

        RegistryValueBackupDocument? document;
        try
        {
            document = Lumin4tiJson.Deserialize<RegistryValueBackupDocument>(storage.ReadAllText(relativePath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or SecurityException)
        {
            return RegistryRestorePlanLoadResult.Invalid(ex.Message);
        }

        if (document?.SchemaVersion != RegistryValueBackupDocument.CurrentSchemaVersion)
        {
            return RegistryRestorePlanLoadResult.Invalid(
                $"未対応の schema version です ({document?.SchemaVersion?.ToString() ?? "未指定"})");
        }

        if (document.Entries is null)
        {
            return RegistryRestorePlanLoadResult.Invalid("entries がありません");
        }

        var byLocation = new Dictionary<RegistryLocation, RegistryValueSnapshot>(RegistryLocationComparer.Instance);
        try
        {
            foreach (var entry in document.Entries)
            {
                if (entry is null || entry.Hive is null || !Enum.IsDefined(entry.Hive.Value) ||
                    entry.KeyPath is null || entry.Name is null || entry.Value is null)
                {
                    return RegistryRestorePlanLoadResult.Invalid("保存先または値が欠落した entry があります");
                }

                entry.Value.Validate();
                var location = new RegistryLocation(entry.Hive.Value, entry.KeyPath, entry.Name);
                if (!byLocation.TryAdd(location, entry.Value))
                {
                    return RegistryRestorePlanLoadResult.Invalid(
                        $"重複した entry があります: {entry.KeyPath}\\{entry.Name}");
                }
            }
        }
        catch (InvalidDataException ex)
        {
            return RegistryRestorePlanLoadResult.Invalid(ex.Message);
        }

        if (byLocation.Count != specs.Count)
        {
            return RegistryRestorePlanLoadResult.Invalid(
                $"entry 数が現在の spec と一致しません (backup={byLocation.Count}, spec={specs.Count})");
        }

        var expected = new HashSet<RegistryLocation>(RegistryLocationComparer.Instance);
        var plan = new List<RegistryRestoreOperation>(specs.Count);
        foreach (var spec in specs)
        {
            var location = new RegistryLocation(spec.Hive, spec.KeyPath, spec.Name);
            if (!expected.Add(location))
            {
                return RegistryRestorePlanLoadResult.Invalid(
                    $"現在の spec が重複しています: {spec.KeyPath}\\{spec.Name}");
            }

            if (!byLocation.TryGetValue(location, out var value))
            {
                return RegistryRestorePlanLoadResult.Invalid(
                    $"現在の spec に対応する entry がありません: {spec.KeyPath}\\{spec.Name}");
            }

            plan.Add(new RegistryRestoreOperation(spec, value));
        }

        return RegistryRestorePlanLoadResult.Valid(plan);
    }

    private static void ValidateSpecs(IReadOnlyList<RegistryToggleSpec> specs)
    {
        var locations = new HashSet<RegistryLocation>(RegistryLocationComparer.Instance);
        foreach (var spec in specs)
        {
            if (!Enum.IsDefined(spec.Hive) || spec.KeyPath is null || spec.Name is null ||
                !Enum.IsDefined(spec.Kind) ||
                !locations.Add(new RegistryLocation(spec.Hive, spec.KeyPath, spec.Name)))
            {
                throw new InvalidDataException(
                    $"安全にバックアップできないレジストリ spec です: {spec.KeyPath}\\{spec.Name}");
            }
        }
    }

    private readonly record struct RegistryLocation(RegistryHive Hive, string KeyPath, string Name);

    private sealed class RegistryLocationComparer : IEqualityComparer<RegistryLocation>
    {
        public static RegistryLocationComparer Instance { get; } = new();

        public bool Equals(RegistryLocation x, RegistryLocation y) =>
            x.Hive == y.Hive &&
            string.Equals(x.KeyPath, y.KeyPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(RegistryLocation obj) => HashCode.Combine(
            obj.Hive,
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.KeyPath),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }

    private sealed record RegistryRestoreOperation(RegistryToggleSpec Spec, RegistryValueSnapshot Value);

    private sealed record RegistryRestorePlanLoadResult(
        bool IsValid,
        IReadOnlyList<RegistryRestoreOperation>? Plan,
        string? FailureReason)
    {
        public static RegistryRestorePlanLoadResult Valid(IReadOnlyList<RegistryRestoreOperation> plan) =>
            new(true, plan, null);

        public static RegistryRestorePlanLoadResult Invalid(string reason) =>
            new(false, null, reason);
    }
}

internal enum RegistryBackupRestoreStatus
{
    Missing,
    Restored,
    Invalid,
}

internal readonly record struct RegistryBackupRestoreResult(
    RegistryBackupRestoreStatus Status,
    string? FailureReason = null);

internal sealed record RegistryValueBackupDocument
{
    public const int CurrentSchemaVersion = 1;

    // nullable にして、SchemaVersion を持たない旧 Dictionary 形式を確実に拒否する。
    public int? SchemaVersion { get; init; }

    public List<RegistryValueBackupEntry>? Entries { get; init; }
}

internal sealed record RegistryValueBackupEntry
{
    public RegistryHive? Hive { get; init; }

    public string? KeyPath { get; init; }

    public string? Name { get; init; }

    public RegistryValueSnapshot? Value { get; init; }

    public static RegistryValueBackupEntry Create(RegistryToggleSpec spec, RegistryValueSnapshot value) => new()
    {
        Hive = spec.Hive,
        KeyPath = spec.KeyPath,
        Name = spec.Name,
        Value = value,
    };
}

internal interface IRegistryBackupStorage
{
    bool FileExists(string relativePath);

    string ReadAllText(string relativePath);

    void WriteNewAtomically(string relativePath, Action<Stream> write);

    void Delete(string relativePath);
}

internal sealed class ProtectedRegistryBackupStorage(ProtectedBackupStorage storage) : IRegistryBackupStorage
{
    public bool FileExists(string relativePath) => storage.FileExists(relativePath);

    public string ReadAllText(string relativePath) => storage.ReadAllText(relativePath);

    public void WriteNewAtomically(string relativePath, Action<Stream> write) =>
        storage.WriteNewAtomically(relativePath, write);

    public void Delete(string relativePath) => storage.Delete(relativePath);
}

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
