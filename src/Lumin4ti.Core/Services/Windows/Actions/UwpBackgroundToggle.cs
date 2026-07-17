using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Windows.Management.Deployment;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// 全 UWP アプリのバックグラウンド実行を「常にオフ」に一括設定するトグル。
/// PowerShell (Get-AppxPackage) を使わず、WinRT の PackageManager でパッケージを列挙し、
/// BackgroundAccessApplications 配下へ Registry API で直接書き込む。
/// ON = 全アプリ「常にオフ」を適用、OFF = Lumin4ti が適用した値だけを元の個別設定へ戻す。
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class UwpBackgroundToggle : IMaintenanceToggle
{
    private readonly Func<IReadOnlyList<string>> _getTargetFamilyNames;
    private readonly IUwpBackgroundSettingsStore _settings;
    private readonly string _journalPath;

    public UwpBackgroundToggle()
        : this(GetInstalledPackageFamilyNames, new RegistryUwpBackgroundSettingsStore(), DefaultJournalPath)
    {
    }

    internal UwpBackgroundToggle(
        Func<IReadOnlyList<string>> getTargetFamilyNames,
        IUwpBackgroundSettingsStore settings,
        string journalPath)
    {
        _getTargetFamilyNames = getTargetFamilyNames;
        _settings = settings;
        _journalPath = journalPath;
    }

    public string Id => "uwp-background-off";

    public string Label => "UWP バックグラウンド実行を一括オフ (メモリ節約 200〜500MB)";

    public string Description =>
        "ストアアプリ (UWP) が画面を閉じた後もバックグラウンドで動き続ける動作を、全アプリまとめて「常にオフ」に設定します (設定 > プライバシー > バックグラウンドアプリの一括版)。" +
        "常駐が減ることで 200〜500MB 程度のメモリ節約が見込めます。通知やライブタイル更新が必要なアプリは、OFF に戻すか設定アプリで個別に「常にオン」へ変更してください。";

    public CommandCategory Category => CommandCategory.Performance;

    public bool RequiresReboot => false;

    private static string DefaultJournalPath =>
        Path.Combine(AppPaths.AppDataDirectory, "backups", "uwp-background.json");

    public Task<bool?> GetStateAsync(CancellationToken ct = default) =>
        Task.Run<bool?>(() =>
        {
            var familyNames = _getTargetFamilyNames();
            if (familyNames.Count == 0)
            {
                return false;
            }

            var currentValues = _settings.ReadMany(familyNames, ct);
            foreach (var familyName in familyNames)
            {
                ct.ThrowIfCancellationRequested();
                if (currentValues[familyName] != UwpBackgroundValues.Applied)
                {
                    return false;
                }
            }

            return true;
        }, ct);

    public Task<MaintenanceActionResult> SetStateAsync(bool on, CancellationToken ct = default) =>
        Task.Run(() => on ? Apply(ct) : Restore(ct), ct);

    private MaintenanceActionResult Apply(CancellationToken ct)
    {
        var load = UwpBackgroundJournalStore.Load(_journalPath);
        if (load.Status == UwpBackgroundJournalLoadStatus.Invalid)
        {
            LoggerBootstrap.Log.Error($"{Id}: 復元 journal を読み取れないため適用を中止: {load.Error}");
            return MaintenanceActionResult.Fail(
                "復元情報が破損しているか未対応の形式です。既存の個別設定を保護するため、変更しませんでした。");
        }

        var entries = (load.Journal?.Entries ?? [])
            .ToDictionary(entry => entry.FamilyName!, StringComparer.OrdinalIgnoreCase);
        var pending = new List<string>();

        var targetFamilyNames = _getTargetFamilyNames();
        var currentValues = _settings.ReadMany(targetFamilyNames, ct);
        foreach (var familyName in targetFamilyNames)
        {
            ct.ThrowIfCancellationRequested();
            var current = currentValues[familyName];
            if (current == UwpBackgroundValues.Applied)
            {
                continue;
            }

            // 外部変更で ownership を失っていた場合も、今回の明示的な ON を新しい before として記録する。
            entries[familyName] = UwpBackgroundJournalEntry.Create(familyName, current, UwpBackgroundValues.Applied);
            pending.Add(familyName);
        }

        if (pending.Count == 0)
        {
            return MaintenanceActionResult.Ok("  - 対象パッケージはすべて「常にオフ」設定済みでした");
        }

        var journal = UwpBackgroundJournal.Create(entries.Values);
        try
        {
            // レジストリより先に journal を確定し、途中失敗でも適用済みの値を安全に戻せるようにする。
            UwpBackgroundJournalStore.SaveAtomic(_journalPath, journal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggerBootstrap.Log.Error($"{Id}: 復元 journal の保存に失敗", ex);
            return MaintenanceActionResult.Fail(
                "復元情報を安全に保存できなかったため、個別設定は変更しませんでした。");
        }

        _settings.WriteMany(
            pending.Select(familyName =>
                    new KeyValuePair<string, UwpBackgroundValues>(familyName, UwpBackgroundValues.Applied))
                .ToArray(),
            ct);

        LoggerBootstrap.Log.Info($"{Id}: {pending.Count} パッケージを常にオフに設定");
        return MaintenanceActionResult.Ok($"  - 「常にオフ」設定: {pending.Count} パッケージ");
    }

    private MaintenanceActionResult Restore(CancellationToken ct)
    {
        var load = UwpBackgroundJournalStore.Load(_journalPath);
        if (load.Status == UwpBackgroundJournalLoadStatus.Missing)
        {
            return MaintenanceActionResult.Ok(
                "  - Lumin4ti が記録した変更はありませんでした (現在の個別設定は保持しました)");
        }

        if (load.Status == UwpBackgroundJournalLoadStatus.Invalid)
        {
            LoggerBootstrap.Log.Error($"{Id}: 復元 journal を読み取れないため復元を中止: {load.Error}");
            return MaintenanceActionResult.Fail(
                "復元情報が破損しているか未対応の形式です。現在の個別設定を保護するため、変更しませんでした。");
        }

        var restored = 0;
        var conflicts = 0;
        var journalEntries = load.Journal!.Entries!;
        var currentValues = _settings.ReadMany(
            journalEntries.Select(entry => entry.FamilyName!).ToArray(),
            ct);
        var pending = new List<KeyValuePair<string, UwpBackgroundValues>>();
        foreach (var entry in journalEntries)
        {
            ct.ThrowIfCancellationRequested();
            var current = currentValues[entry.FamilyName!];
            if (current != entry.GetApplied())
            {
                // ON 後のユーザー変更、新規パッケージの設定、既に戻した値には触れない。
                conflicts++;
                continue;
            }

            pending.Add(new KeyValuePair<string, UwpBackgroundValues>(entry.FamilyName!, entry.GetBefore()));
            restored++;
        }

        _settings.WriteMany(pending, ct);

        var journalDeleted = UwpBackgroundJournalStore.TryDelete(_journalPath);
        LoggerBootstrap.Log.Info($"{Id}: 元の個別設定 {restored} 件を復元 / 外部変更 {conflicts} 件を保持");

        var lines = new List<string>
        {
            $"  - Lumin4ti が適用した設定 {restored} 件を元に戻しました",
        };
        if (conflicts > 0)
        {
            lines.Add($"  - 適用後に変更された設定 {conflicts} 件は現在値を保持しました");
        }

        if (!journalDeleted)
        {
            lines.Add("  - 復元情報の削除に失敗しましたが、再実行しても現在値は上書きしません");
        }

        return MaintenanceActionResult.Ok(lines);
    }

    private static IReadOnlyList<string> GetInstalledPackageFamilyNames()
    {
        var packageManager = new PackageManager();
        // Get-AppxPackage -PackageTypeFilter Main 相当: フレームワーク・リソースパッケージを除外
        return packageManager.FindPackagesForUser(string.Empty)
            .Where(package => !package.IsFramework && !package.IsResourcePackage && !package.IsBundle)
            .Select(package => package.Id.FamilyName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
