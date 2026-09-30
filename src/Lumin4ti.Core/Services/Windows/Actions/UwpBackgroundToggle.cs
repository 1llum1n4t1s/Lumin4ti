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
    private readonly JournalAccess _journal;

    public UwpBackgroundToggle()
        : this(GetInstalledPackageFamilyNames, new RegistryUwpBackgroundSettingsStore(), CreateProtectedJournalAccess())
    {
    }

    internal UwpBackgroundToggle(
        Func<IReadOnlyList<string>> getTargetFamilyNames,
        IUwpBackgroundSettingsStore settings,
        string journalPath,
        Func<string?>? currentUserScopeProvider = null)
        : this(
            getTargetFamilyNames,
            settings,
            CreateFileJournalAccess(journalPath, currentUserScopeProvider ?? RegistryValueBackup.CurrentUserScope))
    {
    }

    internal UwpBackgroundToggle(
        Func<IReadOnlyList<string>> getTargetFamilyNames,
        IUwpBackgroundSettingsStore settings,
        Func<UwpBackgroundJournalLoadResult> loadJournal,
        Action<UwpBackgroundJournal> saveJournal,
        Func<bool> clearJournal)
        : this(getTargetFamilyNames, settings, new JournalAccess(loadJournal, saveJournal, clearJournal))
    {
    }

    private UwpBackgroundToggle(
        Func<IReadOnlyList<string>> getTargetFamilyNames,
        IUwpBackgroundSettingsStore settings,
        JournalAccess journal)
    {
        _getTargetFamilyNames = getTargetFamilyNames;
        _settings = settings;
        _journal = journal;
    }

    public string Id => "uwp-background-off";

    public string Label => "UWP バックグラウンド実行を一括オフ / 復元 (メモリ節約 200〜500MB)";

    public string Description =>
        "ON にすると、ストアアプリ (UWP) のバックグラウンド実行を全アプリまとめて「常にオフ」にし、変更前の個別設定を保存します。" +
        "OFF にすると、Lumin4ti が変更した項目だけを保存済みの個別設定へ戻します (適用後にユーザーが変更した項目は上書きしません)。" +
        "ON/OFF が適用と復元の対になるため、実行ボタンではなくトグルとして表示しています。常駐が減ることで 200〜500MB 程度のメモリ節約が見込めます。";

    public CommandCategory Category => CommandCategory.Performance;

    public bool RequiresReboot => false;

    private static string DefaultJournalPath =>
        Path.Combine(AppPaths.AppDataDirectory, "backups", "uwp-background.json");

    private static string RequireScope(Func<string?> provider)
    {
        var scope = provider();
        if (!RegistryValueBackup.IsValidUserScope(scope))
            throw new InvalidOperationException("現在の利用者 SID を取得できません。");
        return scope!;
    }

    private static JournalAccess CreateFileJournalAccess(string path, Func<string?> scopeProvider) => new(
        () => UwpBackgroundJournalStore.LoadScoped(() => File.Exists(path), () => File.ReadAllText(path), () => false, scopeProvider()),
        journal => UwpBackgroundJournalStore.SaveAtomic(path, journal with { UserScope = RequireScope(scopeProvider) }),
        () => UwpBackgroundJournalStore.TryClear(journal =>
            UwpBackgroundJournalStore.SaveAtomic(path, journal with { UserScope = RequireScope(scopeProvider) })));

    private static JournalAccess CreateProtectedJournalAccess()
    {
        var storage = ProtectedBackupStorage.Default;
        string ScopedPath() => Path.Combine("uwp", RequireScope(RegistryValueBackup.CurrentUserScope), "uwp-background.json");
        return new JournalAccess(
            () => UwpBackgroundJournalStore.LoadScoped(
                () => storage.FileExists(ScopedPath()),
                () => storage.ReadAllText(ScopedPath()),
                () => storage.FileExists("uwp-background.json") || File.Exists(DefaultJournalPath),
                RegistryValueBackup.CurrentUserScope()),
            journal => UwpBackgroundJournalStore.SaveAtomic(storage, ScopedPath(),
                journal with { UserScope = RequireScope(RegistryValueBackup.CurrentUserScope) }),
            () => UwpBackgroundJournalStore.TryClear(journal =>
                UwpBackgroundJournalStore.SaveAtomic(storage, ScopedPath(),
                    journal with { UserScope = RequireScope(RegistryValueBackup.CurrentUserScope) })));
    }

    public Task<bool?> GetStateAsync(CancellationToken ct = default) =>
        Task.Run<bool?>(() =>
        {
            var load = _journal.Load();
            if (load.Status == UwpBackgroundJournalLoadStatus.Invalid)
            {
                LoggerBootstrap.Log.Error(
                    "UWP バックグラウンド設定の復元 journal を検証できないため、状態を判定できませんでした");
                return null;
            }

            var ownedEntries = load.Journal?.Entries ?? [];
            if (ownedEntries.Count > 0)
            {
                var ownedFamilyNames = ownedEntries
                    .Select(entry => entry.FamilyName!)
                    .ToArray();
                var ownedValues = _settings.ReadMany(ownedFamilyNames, ct);
                var hasApplied = false;
                var hasAmbiguousPartial = false;
                foreach (var entry in ownedEntries)
                {
                    ct.ThrowIfCancellationRequested();
                    var current = ownedValues[entry.FamilyName!];
                    hasApplied |= current == entry.GetApplied();
                    hasAmbiguousPartial |= entry.IsAmbiguousPartial(current);
                }

                // 復元可能な別項目があれば OFF を選べる。曖昧な項目だけなら状態不明として保護する。
                if (hasApplied) return true;
                if (hasAmbiguousPartial)
                {
                    LoggerBootstrap.Log.Error($"{Id}: ペア値の途中更新と外部変更を区別できないため、復元情報を保持して状態を不明にしました");
                    return null;
                }
                return false;
            }

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
        var load = _journal.Load();
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
            if (entries.TryGetValue(familyName, out var existing) && existing.IsAmbiguousPartial(current))
            {
                return MaintenanceActionResult.Fail(
                    $"{familyName} のペア値が途中更新または外部変更の状態です。元の復元情報と現在値を保持し、適用を中止しました。");
            }
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
            _journal.Save(journal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LoggerBootstrap.Log.Error($"{Id}: 復元 journal の保存に失敗", ex);
            return MaintenanceActionResult.Fail(
                "復元情報を安全に保存できなかったため、個別設定は変更しませんでした。");
        }

        // ここから先は複数パッケージの設定を順次書き換えるクリティカル区間。途中で打ち切ると
        // 「キャンセルしたのに一部だけ常にオフ」という部分適用が残る。開始前の最後の境界で
        // キャンセルを反映し、開始後は CancellationToken.None で完了まで継続する。
        ct.ThrowIfCancellationRequested();
        var writeFailure = WriteWithCompensation(
            pending.Select(familyName =>
                    new KeyValuePair<string, UwpBackgroundValues>(familyName, UwpBackgroundValues.Applied))
                .ToArray(),
            currentValues,
            "適用");
        if (writeFailure is not null) return writeFailure;

        LoggerBootstrap.Log.Info($"{Id}: {pending.Count} パッケージを常にオフに設定");
        return MaintenanceActionResult.Ok($"  - 「常にオフ」設定: {pending.Count} パッケージ");
    }

    private MaintenanceActionResult Restore(CancellationToken ct)
    {
        var load = _journal.Load();
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
        var retained = new List<UwpBackgroundJournalEntry>();
        var journalEntries = load.Journal!.Entries!;
        var currentValues = _settings.ReadMany(
            journalEntries.Select(entry => entry.FamilyName!).ToArray(),
            ct);
        var pending = new List<KeyValuePair<string, UwpBackgroundValues>>();
        foreach (var entry in journalEntries)
        {
            ct.ThrowIfCancellationRequested();
            var current = currentValues[entry.FamilyName!];
            if (entry.IsAmbiguousPartial(current))
            {
                // 強制終了後の途中書込みと利用者の変更を識別できないため、どちらも上書きしない。
                retained.Add(entry);
                LoggerBootstrap.Log.Error($"{Id}: {entry.FamilyName} のペア値を安全に復元できません。before={entry.GetBefore()}, current={current}, applied={entry.GetApplied()}");
                continue;
            }
            if (current != entry.GetApplied())
            {
                // ON 後のユーザー変更、新規パッケージの設定、既に戻した値には触れない。
                conflicts++;
                continue;
            }

            pending.Add(new KeyValuePair<string, UwpBackgroundValues>(entry.FamilyName!, entry.GetBefore()));
            restored++;
        }

        // 復元も適用と同じクリティカル区間にする。途中で打ち切ると一部だけ元へ戻った状態になり、
        // どこまで戻したかを利用者が判別できない。
        ct.ThrowIfCancellationRequested();
        var writeFailure = WriteWithCompensation(pending, currentValues, "復元");
        if (writeFailure is not null) return writeFailure;

        if (retained.Count > 0)
        {
            try
            {
                _journal.Save(UwpBackgroundJournal.Create(retained));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                LoggerBootstrap.Log.Error($"{Id}: 未解決の復元情報の保存に失敗。復元情報は保持", ex);
                return MaintenanceActionResult.Fail(
                    $"個別設定 {restored} 件を復元しましたが、未解決の復元情報を保存できませんでした。復元情報は保持しています。\n  - {ex.Message}");
            }

            LoggerBootstrap.Log.Error($"{Id}: ペア値の途中更新または外部変更 {retained.Count} 件の復元情報を保持");
            return MaintenanceActionResult.Fail(
                $"個別設定 {restored} 件を復元し、外部変更 {conflicts} 件を保持しました。" +
                $"ペア値の途中更新と外部変更を区別できない {retained.Count} 件は現在値と元の復元情報を保持しています。");
        }

        var journalDeleted = _journal.Clear();
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

    private MaintenanceActionResult? WriteWithCompensation(
        IReadOnlyList<KeyValuePair<string, UwpBackgroundValues>> pending,
        IReadOnlyDictionary<string, UwpBackgroundValues> beforeValues,
        string operation)
    {
        foreach (var (familyName, target) in pending)
        {
            var before = beforeValues[familyName];
            var writeStarted = false;
            try
            {
                // ジャーナル確定後に変わった設定を、古いスナップショットで上書きしない。
                var current = _settings.ReadMany([familyName], CancellationToken.None)[familyName];
                if (current != before)
                    return MaintenanceActionResult.Fail(
                        $"{familyName} の設定が{operation}開始前に変更されました。現在値と復元情報を保持して中止しました。");

                writeStarted = true;
                _settings.WriteMany([new(familyName, target)], CancellationToken.None);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                LoggerBootstrap.Log.Error($"{Id}: {familyName} の{operation}に失敗", error);
                if (!writeStarted)
                    return MaintenanceActionResult.Fail(
                        $"{familyName} の{operation}前に現在値を確認できませんでした。設定の変更を中止し、復元情報を保持しています。\n  - {error.Message}");
                try
                {
                    var current = _settings.ReadMany([familyName], CancellationToken.None)[familyName];
                    if (!current.IsCombinationOf(before, target))
                        return MaintenanceActionResult.Fail(
                            $"{familyName} の{operation}に失敗し、外部変更を検出したため補償を中止しました。現在値と復元情報を保持しています。\n  - {error.Message}");

                    if (current != before)
                        _settings.WriteMany([new(familyName, before)], CancellationToken.None);
                    if (_settings.ReadMany([familyName], CancellationToken.None)[familyName] != before)
                        throw new InvalidOperationException("開始前のペア値への復帰を確認できませんでした。");

                    return MaintenanceActionResult.Fail(
                        $"{familyName} の{operation}に失敗したため、この項目を開始前の値へ補償しました。復元情報は保持しています。\n  - {error.Message}");
                }
                catch (Exception compensationError) when (compensationError is not OperationCanceledException)
                {
                    LoggerBootstrap.Log.Error($"{Id}: {familyName} の補償にも失敗。復元情報は保持", compensationError);
                    return MaintenanceActionResult.Fail(
                        $"{familyName} の{operation}と補償に失敗しました。部分更新の可能性があるため復元情報は保持しています。\n" +
                        $"  - {error.Message}\n  - 補償失敗: {compensationError.Message}");
                }
            }
        }
        return null;
    }

    private sealed record JournalAccess(
        Func<UwpBackgroundJournalLoadResult> Load,
        Action<UwpBackgroundJournal> Save,
        Func<bool> Clear);

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
