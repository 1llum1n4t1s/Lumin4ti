using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// トグルを ON にする直前のレジストリ実値を %APPDATA%\Lumin4ti\backups\registry\&lt;id&gt;.json に退避し、
/// OFF 時に「開発者が信じる既定値 (ハードコード)」ではなく「ユーザーの元の値」へ正確に復元する。
/// これにより ON→OFF 往復でカスタム値が失われる非対称性を解消する。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RegistryValueBackup
{
    private static string BackupPath(string id) =>
        Path.Combine(AppPaths.AppDataDirectory, "backups", "registry", id + ".json");

    public static bool Exists(string id) => File.Exists(BackupPath(id));

    /// <summary>ON 適用前の各 spec の現在値を退避する (既にバックアップがあれば真の元値を保つため上書きしない)。</summary>
    public static void Save(string id, IReadOnlyList<RegistryToggleSpec> specs)
    {
        if (Exists(id))
        {
            return;
        }

        var snapshot = new Dictionary<string, string?>();
        foreach (var spec in specs)
        {
            using var root = RegistryKey.OpenBaseKey(spec.Hive, RegistryView.Default);
            using var key = root.OpenSubKey(spec.KeyPath);
            // 値が無ければ null (= 元は未設定) として記録
            snapshot[SpecKey(spec)] = key?.GetValue(spec.Name)?.ToString();
        }

        var path = BackupPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot));
    }

    /// <summary>
    /// 退避した元値へ復元する。全 spec の値が揃っていれば復元してバックアップを削除し true を返す。
    /// バックアップが無い / 壊れている / spec と対応しない場合は false (呼び出し側は既定値へフォールバック)。
    /// </summary>
    public static bool TryRestore(string id, IReadOnlyList<RegistryToggleSpec> specs, List<string> lines)
    {
        var path = BackupPath(id);
        if (!File.Exists(path))
        {
            return false;
        }

        Dictionary<string, string?>? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return false;
        }

        if (snapshot is null || specs.Any(s => !snapshot.ContainsKey(SpecKey(s))))
        {
            return false;
        }

        foreach (var spec in specs)
        {
            var saved = snapshot[SpecKey(spec)];
            using var root = RegistryKey.OpenBaseKey(spec.Hive, RegistryView.Default);

            if (saved is null)
            {
                using var key = root.OpenSubKey(spec.KeyPath, writable: true);
                key?.DeleteValue(spec.Name, throwOnMissingValue: false);
                lines.Add($"  - {spec.Name} を削除しました (元は未設定)");
            }
            else
            {
                using var key = root.CreateSubKey(spec.KeyPath);
                object value = spec.Kind == RegistryValueKind.DWord ? int.Parse(saved) : saved;
                key.SetValue(spec.Name, value, spec.Kind);
                lines.Add($"  - {spec.Name} = {saved} (元の値に復元)");
            }
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 削除に失敗しても復元自体は完了しているので無視
        }

        return true;
    }

    private static string SpecKey(RegistryToggleSpec spec) => $"{spec.Hive}|{spec.KeyPath}|{spec.Name}";
}
