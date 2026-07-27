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
    private readonly Func<PackageRegistration, bool, CancellationToken, Task<string?>> _removePackageAsync;
    private readonly Func<string, bool> _isConfirmedMissing;
    private readonly Func<IReadOnlyList<string>?> _readStartMenuAppIds;
    private readonly Action _refreshStartMenu;

    public GhostPackageCleanupAction()
        : this(
            EnumerateRegisteredPackages,
            RemovePackageAsync,
            path => StartupCommandParser.IsConfirmedMissing(path),
            StartMenuAppListReader.TryReadAppUserModelIds,
            StartMenuAppListReader.RefreshStartMenu)
    {
    }

    internal GhostPackageCleanupAction(
        Func<IReadOnlyList<PackageRegistration>> enumeratePackages,
        Func<PackageRegistration, bool, CancellationToken, Task<string?>> removePackageAsync,
        Func<string, bool> isConfirmedMissing,
        Func<IReadOnlyList<string>?> readStartMenuAppIds,
        Action refreshStartMenu)
    {
        _enumeratePackages = enumeratePackages;
        _removePackageAsync = removePackageAsync;
        _isConfirmedMissing = isConfirmedMissing;
        _readStartMenuAppIds = readStartMenuAppIds;
        _refreshStartMenu = refreshStartMenu;
    }

    public string Id => "remove-ghost-packages";

    public string Label => "スタートメニューの壊れたアプリ登録 (ゴースト) を削除";

    public string Description =>
        "スタートメニューに並んでいるのに実体フォルダが存在しないアプリ登録 (ゴースト) を検出し、全ユーザーから登録解除してスタートの一覧を再構築します。" +
        "Windows のメジャーアップグレードでシステムアプリのファイルだけが消えたときに発生し、表示名を解決できないため " +
        "「ms-resource:ProductNameWindowsStore」のような生の文字列で並び、クリックしても起動しない項目になります。" +
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

        // 1 回目は通常の登録解除、残ったものはプロビジョニング解除を伴う 2 回目で再試行する。
        foreach (var deprovision in (bool[])[false, true])
        {
            pending = await RemoveAsync(pending, deprovision, lines, progress, ct);
            if (pending.Count == 0)
            {
                break;
            }
        }

        var removedCount = ghosts.Count - pending.Count;
        if (removedCount > 0)
        {
            progress?.Report("スタートメニューの一覧を再構築しています...");
            _refreshStartMenu();
        }

        foreach (var survivor in pending)
        {
            lines.Add($"  - 残存: {survivor.DisplayName} ({survivor.FullName})");
        }

        if (pending.Count > 0)
        {
            lines.Add("  - 残った項目は Windows がシステムアプリとして保護しています。オプション機能を入れ直してから削除するか、サインアウト後に再実行してください");
            LoggerBootstrap.Log.Error($"{Id}: {removedCount} 件解除 / {pending.Count} 件残存");
            return MaintenanceActionResult.Partial(lines);
        }

        lines.Add($"  - {removedCount} 件のゴースト登録を削除しました (スタートメニューを再構築済み)");
        LoggerBootstrap.Log.Info($"{Id}: {removedCount} 件解除");
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
        bool deprovision,
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
                var error = await _removePackageAsync(target, deprovision, ct);
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
        bool deprovision,
        CancellationToken ct)
    {
        var packageManager = new PackageManager();

        if (deprovision && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            // システムアプリは全ユーザー向けプロビジョニングが残っていると登録解除が巻き戻される。
            var deprovisionResult = await packageManager
                .DeprovisionPackageForAllUsersAsync(package.FamilyName)
                .AsTask(ct);
            if (deprovisionResult.ExtendedErrorCode is not null)
            {
                LoggerBootstrap.Log.Info(
                    $"remove-ghost-packages: プロビジョニング解除は不可 {package.FamilyName}: {deprovisionResult.ExtendedErrorCode.Message}");
            }
        }

        var result = await packageManager
            .RemovePackageAsync(package.FullName, RemovalOptions.RemoveForAllUsers)
            .AsTask(ct);

        if (result.ExtendedErrorCode is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(result.ErrorText)
            ? result.ExtendedErrorCode.Message
            : result.ErrorText;
    }
}
