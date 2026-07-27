using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Windows.Management.Deployment;
// 自プロジェクトの Lumin4ti.Core.Services.Windows と衝突するため WinRT の型は別名で参照する。
using Package = Windows.ApplicationModel.Package;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>アプリ登録 DB (StateRepository) に載っているパッケージ 1 件分の登録情報。</summary>
/// <param name="FullName">PackageFullName (登録解除のキー)。</param>
/// <param name="FamilyName">PackageFamilyName (AUMID の前半・プロビジョニング解除のキー)。</param>
/// <param name="DisplayName">ログ表示用の名前。解決できない場合はパッケージ名。</param>
/// <param name="InstalledPath">登録されたインストール先。取得できない場合は null。</param>
internal sealed record PackageRegistration(
    string FullName,
    string FamilyName,
    string DisplayName,
    string? InstalledPath);

/// <summary>登録解除の狙い方。</summary>
internal enum PackageRemovalMode
{
    /// <summary>全ユーザーの登録を解除する (通常のアンインストール相当)。</summary>
    AllUsers,

    /// <summary>
    /// プロビジョニング解除を試したうえで、現在ユーザーの登録だけを解除する。
    /// マシン側の登録が消えた移行残骸は全ユーザー解除が空振り (成功応答でも未削除) になるため必要。
    /// </summary>
    CurrentUser,
}

/// <summary>
/// スタートメニューに並んでいるのに実体フォルダが存在しないパッケージ登録 (ゴースト) を
/// WinRT の PackageManager で登録解除し、スタートメニューの一覧を再構築する。
/// Windows のメジャーアップグレードで SystemApps のファイルだけが消えた場合に発生し、
/// 表示名を resources.pri から解決できないため「ms-resource:ProductNameWindowsStore」の
/// ような生のリソースキーがスタートに並ぶ。
///
/// 対象は「shell:AppsFolder に出ている = 利用者に見えている項目」に限定する。
/// LKG / SxS などの更新残骸もフォルダは消えているが、スタートには出ず OS の内部管理下にあるため触らない。
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class GhostPackageCleanupAction : IMaintenanceAction
{
    private readonly Func<IReadOnlyList<PackageRegistration>> _enumeratePackages;
    private readonly Func<PackageRegistration, PackageRemovalMode, CancellationToken, Task<string?>> _removePackageAsync;
    private readonly Func<string, bool> _isConfirmedMissing;
    private readonly Func<IReadOnlyList<string>?> _readStartMenuAppIds;
    private readonly Action _refreshStartMenu;
    private readonly Func<string, IProgress<string>?, CancellationToken, Task<string?>>? _repairSystemAppAsync;
    private readonly Func<string, bool> _canRepairSystemApp;

    public GhostPackageCleanupAction(ICommandExecutor executor)
        : this(
            EnumerateRegisteredPackages,
            RemovePackageAsync,
            path => StartupCommandParser.IsConfirmedMissing(path),
            StartMenuAppListReader.TryReadAppUserModelIds,
            StartMenuAppListReader.RefreshStartMenu,
            (familyName, progress, ct) =>
                SystemAppCapabilityRepair.CanRepair(familyName)
                    ? SystemAppCapabilityRepair.TryRepairAsync(familyName, executor, progress, ct)
                    : Task.FromResult<string?>("この項目に対応するオプション機能が分かりません"),
            SystemAppCapabilityRepair.CanRepair)
    {
    }

    internal GhostPackageCleanupAction(
        Func<IReadOnlyList<PackageRegistration>> enumeratePackages,
        Func<PackageRegistration, PackageRemovalMode, CancellationToken, Task<string?>> removePackageAsync,
        Func<string, bool> isConfirmedMissing,
        Func<IReadOnlyList<string>?> readStartMenuAppIds,
        Action refreshStartMenu,
        Func<string, IProgress<string>?, CancellationToken, Task<string?>>? repairSystemAppAsync = null,
        Func<string, bool>? canRepairSystemApp = null)
    {
        _enumeratePackages = enumeratePackages;
        _removePackageAsync = removePackageAsync;
        _isConfirmedMissing = isConfirmedMissing;
        _readStartMenuAppIds = readStartMenuAppIds;
        _refreshStartMenu = refreshStartMenu;
        _repairSystemAppAsync = repairSystemAppAsync;
        _canRepairSystemApp = canRepairSystemApp ?? (_ => false);
    }

    public string Id => "remove-ghost-packages";

    public string Label => "スタートメニューの壊れたアプリ項目 (ゴースト) を修復";

    public string Description =>
        "スタートメニューに並んでいるのに実体フォルダが存在しないアプリ (ゴースト登録) を検出して修復します。" +
        "Windows のメジャーアップグレードでシステムアプリのファイルだけが消えたときに発生し、表示名を解決できないため " +
        "「ms-resource:ProductNameWindowsStore」のような生の文字列で並び、クリックしても起動しない項目になります。" +
        "基本は全ユーザーからの登録解除で消しますが、Windows が保護していて消せないシステムアプリは、本来あるべき状態に戻すため本体 (オプション機能) を入れ直します。" +
        "誤削除を避けるため、スタートに実際に表示されている項目だけを対象とし、準備済みの固定ドライブ上でフォルダの不在が確認できないものは残します。";

    public CommandCategory Category => CommandCategory.Cleanup;

    public bool RequiresReboot => false;

    public bool IsLongRunning => true;

    public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default) =>
        ExecuteAsync(null, ct);

    public async Task<MaintenanceActionResult> ExecuteAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Report("スタートメニューのアプリ一覧を確認しています...");

        var startAppIds = _readStartMenuAppIds();
        if (startAppIds is null)
        {
            LoggerBootstrap.Log.Error($"{Id}: スタートメニューの一覧を読み取れないため中止");
            return MaintenanceActionResult.Fail(
                "スタートメニューのアプリ一覧を読み取れませんでした。対象を判定できないため、何も変更していません。");
        }

        var startFamilyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var appId in startAppIds)
        {
            if (StartMenuAppListReader.TryGetPackageFamilyName(appId) is { } familyName)
            {
                startFamilyNames.Add(familyName);
            }
        }

        progress?.Report("インストール済みパッケージの登録を確認しています...");
        IReadOnlyList<PackageRegistration> packages;
        try
        {
            packages = await Task.Run(_enumeratePackages, ct);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            LoggerBootstrap.Log.Error($"{Id}: パッケージ登録の列挙に失敗", ex);
            return MaintenanceActionResult.Fail("パッケージ登録を列挙できませんでした (管理者権限で実行してください)");
        }

        var ghosts = packages.Where(package => IsGhost(package, startFamilyNames)).ToList();
        if (ghosts.Count == 0)
        {
            LoggerBootstrap.Log.Info($"{Id}: 対象なし (登録 {packages.Count} 件 / スタート表示 {startAppIds.Count} 件)");
            return MaintenanceActionResult.Ok(
                $"  - スタートメニューに壊れた項目はありませんでした (表示 {startAppIds.Count} 件を確認)");
        }

        var lines = new List<string>();
        var pending = ghosts;

        // 1 回目は全ユーザーからの解除、残ったものはプロビジョニング解除 + 現在ユーザーからの解除で再試行する。
        // 移行残骸ではマシン側の登録が既に無く、全ユーザー解除が「成功」を返しても
        // ユーザー側の登録が残るため、2 回目は明示的にユーザー登録だけを狙う。
        foreach (var mode in (PackageRemovalMode[])[PackageRemovalMode.AllUsers, PackageRemovalMode.CurrentUser])
        {
            pending = await RemoveAsync(pending, mode, lines, progress, ct);
            if (pending.Count == 0)
            {
                break;
            }
        }

        var removedCount = ghosts.Count - pending.Count;

        // Windows が保護していて消せない = 本来そこに在るべきシステムアプリなので、
        // 削除ではなく本体 (オプション機能) の入れ直しで正しい状態へ戻す。
        var unresolved = await RepairAsync(pending, lines, progress, ct);
        var repairedCount = pending.Count - unresolved.Count;

        // 解除できたシステムアプリも「本来入っているべき」ので、残骸を消したうえで本体を入れ直す。
        var restoredCount = await RestoreRemovedSystemAppsAsync(
            ghosts.Where(ghost => !pending.Contains(ghost) && _canRepairSystemApp(ghost.FamilyName)).ToList(),
            lines,
            progress,
            ct);
        repairedCount += restoredCount;

        if (removedCount > 0 || repairedCount > 0)
        {
            progress?.Report("スタートメニューの一覧を再構築しています...");
            _refreshStartMenu();
        }

        if (unresolved.Count > 0)
        {
            lines.Add("  - 残った項目は Windows がシステムアプリとして保護しています。サインアウト後に再実行するか、設定のオプション機能から本体を入れ直してください");
            LoggerBootstrap.Log.Error(
                $"{Id}: {removedCount} 件解除 / {repairedCount} 件再インストール / {unresolved.Count} 件残存");
            return MaintenanceActionResult.Partial(lines);
        }

        if (removedCount > 0)
        {
            lines.Add($"  - {removedCount} 件のゴースト登録を削除しました (スタートメニューを再構築済み)");
        }

        LoggerBootstrap.Log.Info($"{Id}: {removedCount} 件解除 / {repairedCount} 件再インストール");
        return MaintenanceActionResult.Ok(lines);
    }

    private bool IsGhost(PackageRegistration package, HashSet<string> startFamilyNames) =>
        startFamilyNames.Contains(package.FamilyName) &&
        package.InstalledPath is { Length: > 0 } path &&
        _isConfirmedMissing(path);

    /// <summary>
    /// 対象を登録解除し、登録が実際に消えたかを再列挙で検証する。戻り値は残存したパッケージ。
    /// DeploymentResult が成功を返しても StateRepository に残るケースがあるため、自己申告を信用しない。
    /// </summary>
    private async Task<List<PackageRegistration>> RemoveAsync(
        IReadOnlyList<PackageRegistration> targets,
        PackageRemovalMode mode,
        List<string> lines,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"登録解除しています: {target.DisplayName}");

            try
            {
                var error = await _removePackageAsync(target, mode, ct);
                if (error is not null)
                {
                    LoggerBootstrap.Log.Error($"{Id}: 登録解除に失敗 {target.FullName}: {error}");
                }
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
            {
                LoggerBootstrap.Log.Error($"{Id}: 登録解除で例外 {target.FullName}", ex);
            }
        }

        var remaining = new HashSet<string>(
            (await Task.Run(_enumeratePackages, ct)).Select(package => package.FullName),
            StringComparer.OrdinalIgnoreCase);

        var survivors = new List<PackageRegistration>();
        foreach (var target in targets)
        {
            if (remaining.Contains(target.FullName))
            {
                survivors.Add(target);
            }
            else
            {
                lines.Add($"  - 登録解除: {target.DisplayName} ({target.InstalledPath} が存在しません)");
                LoggerBootstrap.Log.Info($"{Id}: 登録解除 {target.FullName}");
            }
        }

        return survivors;
    }

    /// <summary>
    /// 登録解除できなかった項目の本体を入れ直して修復する。戻り値は修復もできなかった項目。
    /// 入れ直し後は実体フォルダが復活し、スタートの表示名も正しく解決されるようになる。
    /// </summary>
    private async Task<List<PackageRegistration>> RepairAsync(
        IReadOnlyList<PackageRegistration> targets,
        List<string> lines,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var unresolved = new List<PackageRegistration>();
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();

            if (_repairSystemAppAsync is null)
            {
                unresolved.Add(target);
                lines.Add($"  - 残存: {target.DisplayName} ({target.FullName})");
                continue;
            }

            progress?.Report($"本体を入れ直しています: {target.DisplayName}");
            string? error;
            try
            {
                error = await _repairSystemAppAsync(target.FamilyName, progress, ct);
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
            {
                error = ex.Message;
            }

            if (error is null && !await IsStillGhostAsync(target, ct))
            {
                lines.Add($"  - 再インストール: {target.DisplayName} (削除できないシステムアプリのため本体を復元しました)");
                LoggerBootstrap.Log.Info($"{Id}: 本体を再インストール {target.FamilyName}");
                continue;
            }

            unresolved.Add(target);
            // オプション機能が「インストール済み」のまま実体だけ欠けていると DISM は何もせず成功を返すため、
            // 追加コマンドの成功では判定せず、実体が戻ったかどうかで判定する。
            lines.Add(error is null
                ? $"  - 残存: {target.DisplayName} — 本体を追加しても実体が復元されませんでした (設定のオプション機能で一度削除してから追加し直してください)"
                : $"  - 残存: {target.DisplayName} — {error}");
        }

        return unresolved;
    }

    /// <summary>
    /// 解除できたシステムアプリの本体を入れ直す。ここでの失敗はスタートメニューの表示問題を
    /// 悪化させない (残骸は既に消えている) ので、成功件数だけ返して部分失敗にはしない。
    /// </summary>
    private async Task<int> RestoreRemovedSystemAppsAsync(
        IReadOnlyList<PackageRegistration> targets,
        List<string> lines,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (_repairSystemAppAsync is null)
        {
            return 0;
        }

        var restored = 0;
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"システムアプリの本体を入れ直しています: {target.DisplayName}");

            string? error;
            try
            {
                error = await _repairSystemAppAsync(target.FamilyName, progress, ct);
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
            {
                error = ex.Message;
            }

            if (error is null)
            {
                restored++;
                lines.Add($"  - 再インストール: {target.DisplayName} (システムアプリなので本体を入れ直しました)");
                LoggerBootstrap.Log.Info($"{Id}: 本体を再インストール {target.FamilyName}");
            }
            else
            {
                lines.Add($"  - 本体の入れ直しは見送りました: {target.DisplayName} — {error}");
                LoggerBootstrap.Log.Info($"{Id}: 本体の再インストールに失敗 {target.FamilyName}: {error}");
            }
        }

        return restored;
    }

    /// <summary>修復後に実体が戻ったかを再列挙で確認する (登録自体が消えていれば解消扱い)。</summary>
    private async Task<bool> IsStillGhostAsync(PackageRegistration target, CancellationToken ct)
    {
        var packages = await Task.Run(_enumeratePackages, ct);
        return packages.Any(package =>
            package.FamilyName.Equals(target.FamilyName, StringComparison.OrdinalIgnoreCase) &&
            package.InstalledPath is { Length: > 0 } path &&
            _isConfirmedMissing(path));
    }

    /// <summary>
    /// 全ユーザー分の登録済みパッケージを列挙する (管理者権限が必要)。
    /// ゴーストは InstalledLocation (StorageFolder) の取得が実フォルダを開くため失敗するので、
    /// 登録レコード上の文字列パスを返す InstalledPath を使う。
    /// </summary>
    private static IReadOnlyList<PackageRegistration> EnumerateRegisteredPackages()
    {
        var packageManager = new PackageManager();
        var registrations = new Dictionary<string, PackageRegistration>(StringComparer.OrdinalIgnoreCase);

        // 既定の FindPackages() は Main/Framework/Resource/Bundle だけなので、
        // Optional / Xap 種別の取り残しも拾えるよう全種別を対象にする (19041 以降で利用可)。
        var packages = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
            ? packageManager.FindPackagesWithPackageTypes(PackageTypes.All)
            : packageManager.FindPackages();

        foreach (var package in packages)
        {
            var fullName = package.Id.FullName;
            if (registrations.ContainsKey(fullName))
            {
                continue;
            }

            registrations[fullName] = new PackageRegistration(
                fullName,
                package.Id.FamilyName,
                TryGetDisplayName(package) ?? fullName,
                TryGetInstalledPath(package));
        }

        return [.. registrations.Values];
    }

    private static string? TryGetDisplayName(Package package)
    {
        try
        {
            // ゴーストは resources.pri を開けず、DisplayName が空か
            // "@{Family?ms-resource://...}" の未解決な間接文字列になるためパッケージ名で代替する。
            var displayName = package.DisplayName;
            return string.IsNullOrWhiteSpace(displayName) || displayName.StartsWith('@')
                ? package.Id.Name
                : displayName;
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or InvalidOperationException)
        {
            try
            {
                return package.Id.Name;
            }
            catch (COMException)
            {
                return null;
            }
        }
    }

    private static string? TryGetInstalledPath(Package package)
    {
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                return package.InstalledPath;
            }

            // 19041 未満には文字列パスを返す API が無い。StorageFolder 経由はゴーストで失敗するので不明扱いになる。
            return package.InstalledLocation.Path;
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or InvalidOperationException)
        {
            // パスすら取得できない登録は不在を確認できないため、対象から外す。
            return null;
        }
    }

    private static async Task<string?> RemovePackageAsync(
        PackageRegistration package,
        PackageRemovalMode mode,
        CancellationToken ct)
    {
        var packageManager = new PackageManager();

        if (mode == PackageRemovalMode.CurrentUser && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            // システムアプリは全ユーザー向けプロビジョニングが残っていると登録解除が巻き戻される。
            try
            {
                var deprovisionResult = await packageManager
                    .DeprovisionPackageForAllUsersAsync(package.FamilyName)
                    .AsTask(ct);
                if (deprovisionResult.ExtendedErrorCode is not null)
                {
                    LoggerBootstrap.Log.Info(
                        $"remove-ghost-packages: プロビジョニング解除は不可 {package.FamilyName}: {deprovisionResult.ExtendedErrorCode.Message}");
                }
            }
            catch (COMException ex)
            {
                // プロビジョニングされていない移行残骸では ERROR_NOT_FOUND になる。解除本体は続行する。
                LoggerBootstrap.Log.Info(
                    $"remove-ghost-packages: プロビジョニング解除は対象外 {package.FamilyName}: {ex.Message.Trim()}");
            }
        }

        // OS のメジャーアップグレードでマシン側の登録が消え、ユーザー側の登録だけが残った残骸では、
        // RemoveForAllUsers が ERROR_NOT_FOUND (0x80070490) になるか、成功を返しても何も消えない。
        // どちらの場合も現在ユーザーの登録を直接狙えば消えるので、2 巡目は最初からそちらを使う。
        if (mode == PackageRemovalMode.CurrentUser)
        {
            var currentUserOnlyError = await TryRemoveAsync(packageManager, package.FullName, RemovalOptions.None, ct);
            LoggerBootstrap.Log.Info(
                $"remove-ghost-packages: 現在ユーザーからの解除 {package.FullName}: {currentUserOnlyError ?? "エラーなし"}");
            return currentUserOnlyError;
        }

        var allUsersError = await TryRemoveAsync(packageManager, package.FullName, RemovalOptions.RemoveForAllUsers, ct);
        if (allUsersError is null)
        {
            return null;
        }

        LoggerBootstrap.Log.Info(
            $"remove-ghost-packages: 全ユーザー解除に失敗したため現在ユーザーで再試行 {package.FullName}: {allUsersError}");

        var currentUserError = await TryRemoveAsync(packageManager, package.FullName, RemovalOptions.None, ct);
        return currentUserError is null
            ? null
            : $"{allUsersError} (現在ユーザーからの解除も失敗: {currentUserError})";
    }

    /// <summary>登録解除を 1 回試す。COM 例外もエラー文字列に変換して呼び出し側の分岐を単純にする。</summary>
    private static async Task<string?> TryRemoveAsync(
        PackageManager packageManager,
        string fullName,
        RemovalOptions options,
        CancellationToken ct)
    {
        try
        {
            var result = await packageManager.RemovePackageAsync(fullName, options).AsTask(ct);
            if (result.ExtendedErrorCode is null)
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(result.ErrorText)
                ? result.ExtendedErrorCode.Message.Trim()
                : result.ErrorText.Trim();
        }
        catch (COMException ex)
        {
            return ex.Message.Trim();
        }
    }
}
