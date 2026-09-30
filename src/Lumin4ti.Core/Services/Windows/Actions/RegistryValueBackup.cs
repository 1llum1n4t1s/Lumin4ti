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
    IRegistryValueAccessor registry,
    Func<string?>? currentUserScopeProvider = null)
{
    private readonly Func<string?> _currentUserScopeProvider = currentUserScopeProvider ?? CurrentUserScope;

    public static RegistryValueBackup Default { get; } = new(
        new ProtectedRegistryBackupStorage(ProtectedBackupStorage.Default),
        WindowsRegistryValueAccessor.Instance);

    /// <summary>
    /// 利用者原本の退避先。混合項目の共有原本は別途マシン共通で所有者を管理する。
    /// 分けないと、同じ PC の別利用者が同じ項目を操作したときに互いの元値を壊してしまう。
    /// </summary>
    private string RelativePath(string id, IReadOnlyList<RegistryToggleSpec> specs)
    {
        if (!specs.Any(spec => spec.Hive == RegistryHive.CurrentUser))
        {
            return LegacyRelativePath(id);
        }

        var currentUserScope = _currentUserScopeProvider();
        if (!IsValidUserScope(currentUserScope))
        {
            throw new InvalidOperationException(
                "現在の利用者 SID を取得できないため、レジストリ復元バックアップを安全に使用できません。");
        }

        return Path.Combine("registry", currentUserScope!, id + ".json");
    }

    /// <summary>利用者スコープを導入する前の退避先 (既存バックアップを読み落とさないために残す)。</summary>
    private static string LegacyRelativePath(string id) => Path.Combine("registry", id + ".json");

    internal static string? CurrentUserScope()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// ON 適用前の各 spec の現在値を型付きで退避する。既存バックアップは真の元値を保つため
    /// 上書きしないが、現行 schema と spec に完全一致しない場合は適用を中止する。
    /// </summary>
    public void Save(string id, IReadOnlyList<RegistryToggleSpec> specs)
    {
        ValidateSpecs(specs);
        if (IsMixed(specs))
        {
            SaveMixed(id, specs);
            return;
        }
        var relativePath = RelativePath(id, specs);

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

    /// <summary>今回の ON 準備で追加した原本・所有登録だけを取り消す。OFF の復元とは分離する。</summary>
    internal Action PrepareApply(string id, IReadOnlyList<RegistryToggleSpec> specs)
    {
        var paths = IsMixed(specs)
            ? new[] { LegacyRelativePath(id), RelativePath(id, specs) }
            : new[] { RelativePath(id, specs) };
        var before = paths.Select(path => storage.FileExists(path) ? storage.ReadAllText(path) : null).ToArray();
        Save(id, specs);
        var after = paths.Select(path => storage.FileExists(path) ? storage.ReadAllText(path) : null).ToArray();
        return () =>
        {
            // 他の原本を取り消さないよう、全件の同一性を検証してから変更する。
            for (var i = 0; i < paths.Length; i++)
            {
                var current = storage.FileExists(paths[i]) ? storage.ReadAllText(paths[i]) : null;
                if (!string.Equals(current, after[i], StringComparison.Ordinal))
                    throw new InvalidDataException("ON 準備後に復元原本が変化したため、原本と所有登録を保持しました。");
            }
            // 混合項目は共有所有登録を先に戻す。失敗した場合は利用者原本を保持する。
            for (var i = 0; i < paths.Length; i++)
            {
                if (string.Equals(before[i], after[i], StringComparison.Ordinal)) continue;
                if (before[i] is null) storage.Delete(paths[i]);
                else
                {
                    var original = System.Text.Encoding.UTF8.GetBytes(before[i]!);
                    storage.WriteAtomically(paths[i], stream => stream.Write(original));
                }
            }
        };
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
        if (IsMixed(specs))
        {
            return RestoreMixed(id, specs, lines);
        }

        string relativePath;
        try
        {
            relativePath = RelativePath(id, specs);
        }
        catch (InvalidOperationException ex)
        {
            return new(RegistryBackupRestoreStatus.Invalid, ex.Message);
        }

        if (!storage.FileExists(relativePath))
        {
            var legacyPath = LegacyRelativePath(id);
            if (relativePath == legacyPath || !storage.FileExists(legacyPath))
            {
                return new(RegistryBackupRestoreStatus.Missing);
            }

            return new(
                RegistryBackupRestoreStatus.Invalid,
                "利用者スコープ導入前の復元バックアップは所有者を証明できないため使用できません。");
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

    internal static bool IsValidUserScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return false;
        try { return new System.Security.Principal.SecurityIdentifier(scope).Value == scope; }
        catch (ArgumentException) { return false; }
    }

    private static bool IsMixed(IReadOnlyList<RegistryToggleSpec> specs) =>
        specs.Any(spec => spec.Hive == RegistryHive.CurrentUser) &&
        specs.Any(spec => spec.Hive != RegistryHive.CurrentUser);

    private void RejectLegacyMixed(string id)
    {
        // 他 SID の旧混合原本も検査する。適用済み HKLM を真の元値として再保存してはいけない。
        foreach (var path in storage.EnumerateUserBackupPaths(id))
        {
            var document = Lumin4tiJson.Deserialize<RegistryValueBackupDocument>(storage.ReadAllText(path));
            if (document?.SchemaVersion != RegistryValueBackupDocument.CurrentSchemaVersion ||
                document.Entries is null || document.Entries.Any(entry => entry?.Hive != RegistryHive.CurrentUser))
                throw new InvalidDataException("旧混合バックアップの共有元値を証明できないため、変更しません。");
        }
    }

    private RegistryValueBackupDocument Capture(IReadOnlyList<RegistryToggleSpec> specs) => new()
    {
        SchemaVersion = RegistryValueBackupDocument.CurrentSchemaVersion,
        Entries = specs.Select(spec =>
        {
            var value = registry.Read(spec);
            value.Validate();
            return RegistryValueBackupEntry.Create(spec, value);
        }).ToList(),
    };

    private RegistryValueBackupDocument LoadShared(string path, IReadOnlyList<RegistryToggleSpec> specs)
    {
        var plan = LoadRestorePlan(path, specs, shared: true);
        if (!plan.IsValid) throw new InvalidDataException(plan.FailureReason);
        return Lumin4tiJson.Deserialize<RegistryValueBackupDocument>(storage.ReadAllText(path))!;
    }

    private void SaveMixed(string id, IReadOnlyList<RegistryToggleSpec> specs)
    {
        var userPath = RelativePath(id, specs); // SID 取得不能では共有退避にも触れない。
        var scope = Path.GetFileName(Path.GetDirectoryName(userPath));
        RejectLegacyMixed(id);
        var userSpecs = specs.Where(spec => spec.Hive == RegistryHive.CurrentUser).ToArray();
        var machineSpecs = specs.Where(spec => spec.Hive != RegistryHive.CurrentUser).ToArray();
        if (storage.FileExists(userPath) && !LoadRestorePlan(userPath, userSpecs).IsValid)
            throw new InvalidDataException("利用者バックアップが現在の仕様と一致しません。");
        var machinePath = LegacyRelativePath(id);
        var originalSharedJson = storage.FileExists(machinePath) ? storage.ReadAllText(machinePath) : null;
        var shared = originalSharedJson is not null ? LoadShared(machinePath, machineSpecs) : null;
        if (shared?.OwnerScopes!.Contains(scope!, StringComparer.Ordinal) == true && !storage.FileExists(userPath))
            throw new InvalidDataException("適用中利用者の原本が欠落しているため再取得できません。");
        // 所有解除の置換後にエラーとなると、呼出元の補償で共有値だけ適用値へ戻り得る。
        // 利用者原本が残る場合は共有実値が原本と型付きで一致することを確かめてから再取得する。
        // 復元成功後の削除失敗なら一致するため、UI が OFF を表示した状態からも ON で回復できる。
        if (shared is not null && shared.OwnerScopes!.Count == 0 &&
            storage.EnumerateUserBackupPaths(id).Count != 0)
        {
            var restored = LoadRestorePlan(machinePath, machineSpecs, shared: true);
            if (!restored.IsValid || restored.Plan!.Any(operation =>
                    !registry.Read(operation.Spec).EquivalentTo(operation.Value)))
                throw new InvalidDataException("共有設定の復元完了を確認できません。OFF を再実行してください。");
        }
        // 他 SID の利用者原本は読み戻さず保持する。共有原値の一致を確認できたときだけ再取得する。
        if (shared is null || shared.OwnerScopes!.Count == 0)
            shared = Capture(machineSpecs) with { SchemaVersion = RegistryValueBackupDocument.SharedSchemaVersion, OwnerScopes = [] };
        if (!shared.OwnerScopes!.Contains(scope!, StringComparer.Ordinal))
            shared = shared with { OwnerScopes = [..shared.OwnerScopes!, scope!] };
        var newUserBackup = storage.FileExists(userPath) ? null : Capture(userSpecs);
        var newUserJson = newUserBackup is null ? null : Lumin4tiJson.Serialize(newUserBackup);
        var attemptedUserCreation = false;
        // 所有登録より先に利用者原本を確定する。置換後の検証失敗も同じ取消境界で扱う。
        try
        {
            if (newUserBackup is not null)
            {
                attemptedUserCreation = true;
                storage.WriteNewAtomically(userPath, stream => Lumin4tiJson.Serialize(stream, newUserBackup));
            }
            storage.WriteAtomically(machinePath, stream => Lumin4tiJson.Serialize(stream, shared));
        }
        catch (Exception saveError) when (saveError is not OperationCanceledException)
        {
            if (attemptedUserCreation)
            {
                try
                {
                    // Save の完了前なので製品レジストリは未変更。共有正本が更新前と完全一致するときだけ
                    // 今回新規作成した利用者原本を取り消し、次の ON で準備をやり直せるようにする。
                    // 原子的置換後のエラーでは所有情報が変わるため、利用者原本を削除しない。
                    var unchanged = originalSharedJson is null
                        ? !storage.FileExists(machinePath)
                        : storage.FileExists(machinePath) && string.Equals(
                            storage.ReadAllText(machinePath), originalSharedJson, StringComparison.Ordinal);
                    if (unchanged)
                    {
                        if (storage.FileExists(userPath))
                        {
                            // 新規作成の置換後に例外となった場合も、今回の退避内容と一致する原本だけ取り消す。
                            if (string.Equals(storage.ReadAllText(userPath), newUserJson, StringComparison.Ordinal))
                                storage.Delete(userPath);
                            else LoggerBootstrap.Log.Error($"{id}: 新規利用者原本の同一性を確認できないため保持しました", saveError);
                        }
                    }
                    else LoggerBootstrap.Log.Error($"{id}: 共有原本の更新完了を判定できないため利用者原本を保持しました", saveError);
                }
                catch (Exception cleanupError) when (cleanupError is not OperationCanceledException)
                {
                    LoggerBootstrap.Log.Error($"{id}: 適用準備の取消に失敗したため利用者原本を保持しました", cleanupError);
                }
            }
            throw;
        }
    }

    private RegistryBackupRestoreResult RestoreMixed(string id, IReadOnlyList<RegistryToggleSpec> specs, List<string> lines)
    {
        string userPath;
        RegistryValueBackupDocument shared;
        bool ownsShared;
        RegistryRestorePlanLoadResult? userPlan;
        List<RegistryRestoreOperation> operations;
        string machinePath;
        string? scope;
        try
        {
            ValidateSpecs(specs);
            userPath = RelativePath(id, specs);
            scope = Path.GetFileName(Path.GetDirectoryName(userPath));
            RejectLegacyMixed(id);
            machinePath = LegacyRelativePath(id);
            var userSpecs = specs.Where(spec => spec.Hive == RegistryHive.CurrentUser).ToArray();
            var machineSpecs = specs.Where(spec => spec.Hive != RegistryHive.CurrentUser).ToArray();
            if (!storage.FileExists(machinePath))
            {
                if (storage.FileExists(userPath)) throw new InvalidDataException("共有バックアップが欠落しています。");
                return new(RegistryBackupRestoreStatus.Missing);
            }
            shared = LoadShared(machinePath, machineSpecs);
            ownsShared = shared.OwnerScopes!.Contains(scope!, StringComparer.Ordinal);
            userPlan = storage.FileExists(userPath) ? LoadRestorePlan(userPath, userSpecs) : null;
            if (userPlan is not null && !userPlan.IsValid) throw new InvalidDataException(userPlan.FailureReason);
            if (ownsShared && userPlan is null) throw new InvalidDataException("適用中利用者のバックアップが欠落しています。");
            // 全原本を検証した後に書く。別利用者の適用中は HKLM を保持する。
            var machinePlan = LoadRestorePlan(machinePath, machineSpecs, shared: true);
            if (!machinePlan.IsValid) throw new InvalidDataException(machinePlan.FailureReason);
            operations = new List<RegistryRestoreOperation>();
            if (userPlan is not null) operations.AddRange(userPlan.Plan!);
            // 所有解除の置換後に保存エラーとなり呼出元が補償した場合も、利用者原本が残れば再復元する。
            // 別の所有者がいるときは共有設定へ触れない。
            if ((ownsShared && shared.OwnerScopes!.Count == 1) ||
                (shared.OwnerScopes!.Count == 0 && userPlan is not null))
                operations.AddRange(machinePlan.Plan!);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or JsonException)
        {
            return new(RegistryBackupRestoreStatus.Invalid, ex.Message);
        }
        foreach (var operation in operations)
        {
            registry.Write(operation.Spec, operation.Value);
            lines.Add($"  - {operation.Spec.Name} を元の状態へ復元しました");
        }
        if (ownsShared)
        {
            var released = shared with { OwnerScopes = shared.OwnerScopes!.Where(owner => owner != scope).ToList() };
            // この境界では全値の復元が完了している。所有更新の成否が不明なまま
            // レジストリだけ ON へ戻すと、所有登録を失った ON が残ってしまう。
            try
            {
                storage.WriteAtomically(machinePath, stream => Lumin4tiJson.Serialize(stream, released));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                try
                {
                    if (!string.Equals(storage.ReadAllText(machinePath), Lumin4tiJson.Serialize(released), StringComparison.Ordinal))
                        return new(RegistryBackupRestoreStatus.Incomplete, ex.Message);
                    LoggerBootstrap.Log.Error($"{id}: 所有更新の例外後に更新済み原本を照合し、復元完了を確認しました", ex);
                }
                catch (Exception verifyError) when (verifyError is not OperationCanceledException)
                {
                    LoggerBootstrap.Log.Error($"{id}: 設定復元は完了しましたが、所有更新を確認できないため原本を保持しました", verifyError);
                    return new(RegistryBackupRestoreStatus.Incomplete, ex.Message);
                }
            }
        }
        if (userPlan is not null)
        {
            try { storage.Delete(userPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                lines.Add("  - 復元済み利用者バックアップを削除できなかったため保持しました");
                LoggerBootstrap.Log.Error($"{id}: 利用者バックアップの削除失敗", ex);
            }
        }
        if (shared.OwnerScopes!.Count > (ownsShared ? 1 : 0))
            lines.Add("  - 別の利用者が適用中の共有設定と元値は保持しました");
        // owner ゼロの共有 journal は retry の完了印として残す。再実行で既定値を上書きしない。
        return new(RegistryBackupRestoreStatus.Restored);
    }

    private RegistryRestorePlanLoadResult LoadRestorePlan(
        string relativePath,
        IReadOnlyList<RegistryToggleSpec> specs,
        bool shared = false)
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

        if (document?.SchemaVersion != (shared ? RegistryValueBackupDocument.SharedSchemaVersion : RegistryValueBackupDocument.CurrentSchemaVersion))
        {
            return RegistryRestorePlanLoadResult.Invalid(
                $"未対応の schema version です ({document?.SchemaVersion?.ToString() ?? "未指定"})");
        }

        if (shared && (document.OwnerScopes is null ||
            document.OwnerScopes.Any(owner => !IsValidUserScope(owner)) ||
            document.OwnerScopes.Distinct(StringComparer.Ordinal).Count() != document.OwnerScopes.Count))
            return RegistryRestorePlanLoadResult.Invalid("共有バックアップの所有者情報が不正です");

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
    Incomplete,
}

internal readonly record struct RegistryBackupRestoreResult(
    RegistryBackupRestoreStatus Status,
    string? FailureReason = null);

internal sealed record RegistryValueBackupDocument
{
    public const int CurrentSchemaVersion = 1;

    public const int SharedSchemaVersion = 2;

    // nullable にして、SchemaVersion を持たない旧 Dictionary 形式を確実に拒否する。
    public int? SchemaVersion { get; init; }

    public List<RegistryValueBackupEntry>? Entries { get; init; }

    public List<string>? OwnerScopes { get; init; }
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

    void WriteAtomically(string relativePath, Action<Stream> write) =>
        throw new InvalidOperationException("共有バックアップの原子的更新が未対応です。");

    IReadOnlyList<string> EnumerateUserBackupPaths(string id) =>
        throw new InvalidOperationException("旧混合バックアップの検査が未対応です。");
}

internal sealed class ProtectedRegistryBackupStorage(ProtectedBackupStorage storage) : IRegistryBackupStorage
{
    public bool FileExists(string relativePath) => storage.FileExists(relativePath);

    public string ReadAllText(string relativePath) => storage.ReadAllText(relativePath);

    public void WriteNewAtomically(string relativePath, Action<Stream> write) =>
        storage.WriteNewAtomically(relativePath, write);

    public void Delete(string relativePath) => storage.Delete(relativePath);

    public void WriteAtomically(string relativePath, Action<Stream> write) => storage.WriteAtomically(relativePath, write);

    public IReadOnlyList<string> EnumerateUserBackupPaths(string id)
    {
        // 親を ACL / 再解析ポイント検証した後、直下の SID フォルダだけ調べる。
        _ = storage.FileExists(Path.Combine("registry", ".scope-probe"));
        var root = storage.GetFullPath("registry");
        var paths = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var scope = Path.GetFileName(directory);
            if (!RegistryValueBackup.IsValidUserScope(scope)) continue;
            var path = Path.Combine("registry", scope, id + ".json");
            if (storage.FileExists(path)) paths.Add(path);
        }
        return paths;
    }
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
