using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Windows.Management.Deployment;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>再登録の対象になるシステムアプリ 1 件分の状態。</summary>
/// <param name="FullName">PackageFullName (再登録のキー)。</param>
/// <param name="InstalledPath">インストール先。取得できない場合は null。</param>
internal sealed record SystemPackageState(string FullName, string? InstalledPath);

/// <summary>
/// 実体は残っているのに動作がおかしくなったシステムアプリを、パッケージの再登録で修復する。
/// アンインストールできないシステムアプリでも登録し直しは可能で、
/// 設定やデータを消さずに壊れた登録状態 (マニフェスト・拡張登録・アクティベーション情報) を作り直せる。
/// PowerShell (Add-AppxPackage -Register) を使わず WinRT の PackageManager で直接行う。
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class SystemPackageRepairAction : IMaintenanceAction
{
    private readonly string _familyName;
    private readonly Func<string, IReadOnlyList<SystemPackageState>> _findPackages;
    private readonly Func<string, CancellationToken, Task<string?>> _registerAsync;
    private readonly Func<string, bool> _isConfirmedMissing;

    public SystemPackageRepairAction(string id, string familyName, string label, string description)
        : this(id, familyName, label, description, FindPackagesForCurrentUser, RegisterPackageAsync,
            path => StartupCommandParser.IsConfirmedMissing(path))
    {
    }

    internal SystemPackageRepairAction(
        string id,
        string familyName,
        string label,
        string description,
        Func<string, IReadOnlyList<SystemPackageState>> findPackages,
        Func<string, CancellationToken, Task<string?>> registerAsync,
        Func<string, bool> isConfirmedMissing)
    {
        Id = id;
        _familyName = familyName;
        Label = label;
        Description = description;
        _findPackages = findPackages;
        _registerAsync = registerAsync;
        _isConfirmedMissing = isConfirmedMissing;
    }

    public string Id { get; }

    public string Label { get; }

    public string Description { get; }

    public CommandCategory Category => CommandCategory.Repair;

    public bool RequiresReboot => false;

    public bool IsLongRunning => true;

    public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default) =>
        ExecuteAsync(null, ct);

    public async Task<MaintenanceActionResult> ExecuteAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Report("対象のシステムアプリを確認しています...");

        IReadOnlyList<SystemPackageState> packages;
        try
        {
            packages = await Task.Run(() => _findPackages(_familyName), ct);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            LoggerBootstrap.Log.Error($"{Id}: パッケージの検索に失敗", ex);
            return MaintenanceActionResult.Fail("パッケージを検索できませんでした (管理者権限で実行してください)");
        }

        if (packages.Count == 0)
        {
            LoggerBootstrap.Log.Info($"{Id}: {_familyName} は登録されていません");
            return MaintenanceActionResult.Fail($"{_familyName} はこの Windows に登録されていません");
        }

        var lines = new List<string>();
        var failed = 0;
        foreach (var package in packages)
        {
            ct.ThrowIfCancellationRequested();

            // 実体が消えている場合は再登録できないため、安全に失敗として返す。
            if (package.InstalledPath is { Length: > 0 } path && _isConfirmedMissing(path))
            {
                failed++;
                lines.Add($"  - 再登録できません: {package.FullName} (インストール先 {path} が存在しません)");
                continue;
            }

            progress?.Report($"再登録しています: {package.FullName}");
            LoggerBootstrap.Log.Info($"{Id}: 再登録開始 {package.FullName}");

            string? error;
            try
            {
                error = await _registerAsync(package.FullName, ct);
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
            {
                error = ex.Message.Trim();
            }

            if (error is null)
            {
                lines.Add($"  - 再登録しました: {package.FullName}");
                LoggerBootstrap.Log.Info($"{Id}: 再登録完了 {package.FullName}");
            }
            else
            {
                failed++;
                lines.Add($"  - 失敗: {package.FullName} — {error}");
                LoggerBootstrap.Log.Error($"{Id}: 再登録に失敗 {package.FullName}: {error}");
            }
        }

        if (failed == packages.Count)
        {
            return MaintenanceActionResult.Fail(string.Join(Environment.NewLine, lines));
        }

        lines.Add("  - 常駐していたプロセスは終了しました (必要になった時点で Windows が起動し直します)");
        return failed > 0
            ? MaintenanceActionResult.Partial(lines)
            : MaintenanceActionResult.Ok(lines);
    }

    private static IReadOnlyList<SystemPackageState> FindPackagesForCurrentUser(string familyName)
    {
        var packageManager = new PackageManager();
        var states = new List<SystemPackageState>();
        foreach (var package in packageManager.FindPackagesForUser(string.Empty, familyName))
        {
            string? installedPath;
            try
            {
                installedPath = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
                    ? package.InstalledPath
                    : package.InstalledLocation.Path;
            }
            catch (Exception ex) when (ex is COMException or FileNotFoundException or InvalidOperationException)
            {
                installedPath = null;
            }

            states.Add(new SystemPackageState(package.Id.FullName, installedPath));
        }

        return states;
    }

    /// <summary>
    /// 現在ユーザーへパッケージを登録し直す。ForceApplicationShutdown で常駐プロセスを終了させてから
    /// 登録するため、ファイルロックで失敗せず、再起動後は作り直した登録で動き始める。
    /// </summary>
    private static async Task<string?> RegisterPackageAsync(string fullName, CancellationToken ct)
    {
        var packageManager = new PackageManager();
        var result = await packageManager
            .RegisterPackageByFullNameAsync(fullName, null, DeploymentOptions.ForceApplicationShutdown)
            .AsTask(ct);

        if (result.ExtendedErrorCode is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(result.ErrorText)
            ? result.ExtendedErrorCode.Message.Trim()
            : result.ErrorText.Trim();
    }
}
