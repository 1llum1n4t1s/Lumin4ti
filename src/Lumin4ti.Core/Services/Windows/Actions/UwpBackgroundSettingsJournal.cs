using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows.Actions;

internal readonly record struct UwpBackgroundValues(int? Disabled, int? DisabledByUser)
{
    public static UwpBackgroundValues Applied => new(AppliedValue, AppliedValue);

    private const int AppliedValue = 1;
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

internal enum UwpBackgroundJournalLoadStatus
{
    Missing,
    Valid,
    Invalid,
}

internal sealed record UwpBackgroundJournalLoadResult(
    UwpBackgroundJournalLoadStatus Status,
    UwpBackgroundJournal? Journal = null,
    string? Error = null);

internal sealed record UwpBackgroundJournal(int? SchemaVersion, List<UwpBackgroundJournalEntry>? Entries)
{
    public const int CurrentSchemaVersion = 1;

    public static UwpBackgroundJournal Create(IEnumerable<UwpBackgroundJournalEntry> entries) =>
        new(
            CurrentSchemaVersion,
            entries.OrderBy(entry => entry.FamilyName, StringComparer.OrdinalIgnoreCase).ToList());
}

internal sealed record UwpBackgroundJournalEntry(
    string? FamilyName,
    UwpBackgroundJournalValues? Before,
    UwpBackgroundJournalValues? Applied)
{
    public static UwpBackgroundJournalEntry Create(
        string familyName,
        UwpBackgroundValues before,
        UwpBackgroundValues applied) =>
        new(familyName, UwpBackgroundJournalValues.Create(before), UwpBackgroundJournalValues.Create(applied));

    public UwpBackgroundValues GetBefore() => Before!.ToValues();

    public UwpBackgroundValues GetApplied() => Applied!.ToValues();
}

internal sealed record UwpBackgroundJournalValues(
    UwpBackgroundJournalDword? Disabled,
    UwpBackgroundJournalDword? DisabledByUser)
{
    public static UwpBackgroundJournalValues Create(UwpBackgroundValues values) =>
        new(UwpBackgroundJournalDword.Create(values.Disabled), UwpBackgroundJournalDword.Create(values.DisabledByUser));

    public UwpBackgroundValues ToValues() => new(Disabled!.ToValue(), DisabledByUser!.ToValue());
}

internal sealed record UwpBackgroundJournalDword(bool? Exists, int? Value)
{
    public static UwpBackgroundJournalDword Create(int? value) => new(value is not null, value);

    public int? ToValue() => Exists == true ? Value : null;
}

internal static class UwpBackgroundJournalStore
{
    private const int AppliedValue = 1;
    public static UwpBackgroundJournalLoadResult Load(string path)
    {
        if (!File.Exists(path))
        {
            return new(UwpBackgroundJournalLoadStatus.Missing);
        }

        try
        {
            var journal = Lumin4tiJson.Deserialize<UwpBackgroundJournal>(File.ReadAllText(path));
            var validationError = Validate(journal);
            return validationError is null
                ? new(UwpBackgroundJournalLoadStatus.Valid, journal)
                : new(UwpBackgroundJournalLoadStatus.Invalid, Error: validationError);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(UwpBackgroundJournalLoadStatus.Invalid, Error: ex.Message);
        }
    }

    public static void SaveAtomic(string path, UwpBackgroundJournal journal)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("journal の保存先ディレクトリがありません。", nameof(path));
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                Lumin4tiJson.Serialize(stream, journal);
                stream.Flush(flushToDisk: true);
            }

            // 一時ファイルを同じディレクトリに作ることで、置換を単一ファイルシステム操作にする。
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 置換前に失敗した一時ファイルの掃除だけなので、元の例外を優先する。
            }
        }
    }

    public static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? Validate(UwpBackgroundJournal? journal)
    {
        if (journal?.SchemaVersion != UwpBackgroundJournal.CurrentSchemaVersion)
        {
            return "schemaVersion が未対応です。";
        }

        if (journal.Entries is null)
        {
            return "entries がありません。";
        }

        var familyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in journal.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.FamilyName) || !familyNames.Add(entry.FamilyName))
            {
                return "familyName が空か重複しています。";
            }

            if (!IsValid(entry.Before, requirePresent: false) || !IsValid(entry.Applied, requirePresent: true))
            {
                return $"{entry.FamilyName} の before/applied が不正です。";
            }
        }

        return null;
    }

    private static bool IsValid(UwpBackgroundJournalValues? values, bool requirePresent)
    {
        if (values?.Disabled is null || values.DisabledByUser is null)
        {
            return false;
        }

        return IsValid(values.Disabled, requirePresent) && IsValid(values.DisabledByUser, requirePresent);
    }

    private static bool IsValid(UwpBackgroundJournalDword value, bool requirePresent)
    {
        if (value.Exists is null || value.Exists == true && value.Value is null)
        {
            return false;
        }

        if (value.Exists == false && value.Value is not null)
        {
            return false;
        }

        return !requirePresent || value.Exists == true && value.Value == AppliedValue;
    }
}
