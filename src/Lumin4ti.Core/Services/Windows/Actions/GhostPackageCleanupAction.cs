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
/// <param name="DisplayName">ログ表示用の名前。解決できない場合は FullName と同じ。</param>
/// <param name="InstalledPath">登録されたインストール先。取得できない場合は null。</param>
internal sealed record PackageRegistration(string FullName, string DisplayName, string? InstalledPath);

/// <summary>
/// パッケージ本体のフォルダが存在しないのに登録レコードだけが残った「ゴースト登録」を
/// WinRT の PackageManager で検出し、全ユーザーから登録解除する。
/// Windows のメジャーアップグレードで SystemApps のファイルだけが消えた場合に発生し、
/// 表示名を resources.pri から解決できないためスタートメニューに
/// 「ms-resource:ProductNameWindowsStore」のような生のリソースキーが並ぶ。
/// PowerShell (Get-AppxPackage / Remove-AppxPackage) を使わず WinRT で直接処理する。
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class GhostPackageCleanupAction : IMaintenanceAction
{
    private readonly Func<IReadOnlyList<PackageRegistration>> _enumeratePackages;
    private readonly Func<PackageRegistration, CancellationToken, Task<string?>> _removePackageAsync;
    private readonly Func<string, bool> _isConfirmedMissing;

    public GhostPackageCleanupAction()
        : this(EnumerateRegisteredPackages, RemovePackageAsync, path => StartupCommandParser.IsConfirmedMissing(path))
    {
    }

    internal GhostPackageCleanupAction(
        Func<IReadOnlyList<PackageRegistration>> enumeratePackages,
        Func<PackageRegistration, CancellationToken, Task<string?>> removePackageAsync,
        Func<string, bool> isConfirmedMissing)
    {
        _enumeratePackages = enumeratePackages;
        _removePackageAsync = removePackageAsync;
        _isConfirmedMissing = isConfirmedMissing;
    }

    public string Id => "remove-ghost-packages";

    public string Label => "スタートメニューの壊れたアプリ登録 (ゴースト) を削除";

    public string Description =>
        "インストール先フォルダがもう存在しないのに登録だけが残ったアプリ (ゴースト登録) を検出し、全ユーザーから登録解除します。" +
        "Windows のメジャーアップグレードでシステムアプリのファイルだけが消えたときに発生し、表示名を解決できないためスタートメニューに " +
        "「ms-resource:ProductNameWindowsStore」のような生の文字列で並び、クリックしても起動しない項目になります。" +
        "誤削除を避けるため、準備済みの固定ドライブ上でフォルダの不在が確実に確認できたものだけを対象とし、判定できないものはそのまま残します。";

    public CommandCategory Category => CommandCategory.Cleanup;

    public bool RequiresReboot => false;

    public bool IsLongRunning => true;

    public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default) =>
        ExecuteAsync(null, ct);

    public async Task<MaintenanceActionResult> ExecuteAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
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

        var ghosts = packages
            .Where(package => package.InstalledPath is { Length: > 0 } path && _isConfirmedMissing(path))
            .ToList();

        if (ghosts.Count == 0)
        {
            LoggerBootstrap.Log.Info($"{Id}: ゴースト登録なし (登録 {packages.Count} 件)");
            return MaintenanceActionResult.Ok($"  - ゴースト登録はありませんでした (登録 {packages.Count} 件を確認)");
        }

        var lines = new List<string>();
        var removed = 0;
        var failed = 0;
        foreach (var ghost in ghosts)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"登録解除しています: {ghost.DisplayName}");

            string? error;
            try
            {
                error = await _removePackageAsync(ghost, ct);
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
            {
                error = ex.Message;
            }

            if (error is null)
            {
                removed++;
                lines.Add($"  - 登録解除: {ghost.DisplayName} ({ghost.InstalledPath} が存在しません)");
                LoggerBootstrap.Log.Info($"{Id}: 登録解除 {ghost.FullName}");
            }
            else
            {
                failed++;
                lines.Add($"  - 失敗: {ghost.DisplayName} — {error}");
                LoggerBootstrap.Log.Error($"{Id}: 登録解除に失敗 {ghost.FullName}: {error}");
            }
        }

        if (failed > 0)
        {
            lines.Add("  - 残った項目は、サインアウトまたは再起動後にもう一度実行すると解除できることがあります");
            return MaintenanceActionResult.Partial(lines);
        }

        lines.Add($"  - {removed} 件のゴースト登録を削除しました");
        return MaintenanceActionResult.Ok(lines);
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
        // Optional / Xap 種別の取り残しも拾えるよう全種別を対象にする。
        foreach (var package in packageManager.FindPackagesWithPackageTypes(PackageTypes.All))
        {
            var fullName = package.Id.FullName;
            if (registrations.ContainsKey(fullName))
            {
                continue;
            }

            registrations[fullName] = new PackageRegistration(
                fullName,
                TryGetDisplayName(package) ?? fullName,
                TryGetInstalledPath(package));
        }

        return [.. registrations.Values];
    }

    private static string? TryGetDisplayName(Package package)
    {
        try
        {
            // ゴーストは resources.pri を開けず DisplayName が空/例外になるため、その場合は Name で代替する。
            var displayName = package.DisplayName;
            return string.IsNullOrWhiteSpace(displayName) ? package.Id.Name : displayName;
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

    private static async Task<string?> RemovePackageAsync(PackageRegistration package, CancellationToken ct)
    {
        var packageManager = new PackageManager();
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
