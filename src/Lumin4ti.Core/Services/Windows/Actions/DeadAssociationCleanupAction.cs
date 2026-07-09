using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// 「プログラムから開く」候補 (FileExts\*\OpenWithList) と Applications 登録から、
/// 実行ファイルが存在しなくなったアプリを Registry API で削除する。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DeadAssociationCleanupAction : IMaintenanceAction
{
    private const string FileExtsPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";

    public string Id => "remove-dead-associations";

    public string Label => "関連付け候補から存在しないアプリを削除";

    public string Description => "「プログラムから開く」の候補リストと Applications 登録から、実行ファイルが存在しなくなったアプリを削除します。";

    public CommandCategory Category => CommandCategory.Cleanup;

    public bool RequiresReboot => false;

    public bool AffectsExplorer => true;

    public bool IsLongRunning => false;

    public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default) =>
        // レジストリ全走査 + File.Exists 多数でボタン押下中に UI スレッドを塞がないようオフロードする
        Task.Run(() =>
        {
            var lines = new List<string>();
            var removed = 0;

            // PATH は不変なのでここで 1 回だけ分割し、AppExists の呼び出しごとの再分割を避ける
            var pathDirs = (Environment.GetEnvironmentVariable("PATH")?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [])
                .Select(d => d.Trim())
                .Where(d => d.Length > 0)
                .ToArray();

            removed += CleanOpenWithLists(lines, pathDirs, ct);
            removed += CleanOrphanApplications(Registry.LocalMachine, @"SOFTWARE\Classes\Applications", lines, ct);
            removed += CleanOrphanApplications(Registry.CurrentUser, @"Software\Classes\Applications", lines, ct);

            lines.Add($"  - 合計削除: {removed} 件");
            LoggerBootstrap.Log.Info($"{Id}: {removed} 件削除");
            return MaintenanceActionResult.Ok(lines);
        }, ct);

    private static int CleanOpenWithLists(List<string> lines, string[] pathDirs, CancellationToken ct)
    {
        var removed = 0;
        using var fileExts = Registry.CurrentUser.OpenSubKey(FileExtsPath);
        if (fileExts is null)
        {
            return 0;
        }

        foreach (var extName in fileExts.GetSubKeyNames())
        {
            ct.ThrowIfCancellationRequested();
            using var owl = fileExts.OpenSubKey($@"{extName}\OpenWithList", writable: true);
            if (owl is null)
            {
                continue;
            }

            var dead = new List<string>();
            foreach (var slot in owl.GetValueNames())
            {
                if (slot.Equals("MRUList", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var appName = owl.GetValue(slot)?.ToString();
                if (AppExists(appName, pathDirs))
                {
                    continue;
                }

                owl.DeleteValue(slot, throwOnMissingValue: false);
                dead.Add(slot);
                lines.Add($"  - OpenWithList削除: {extName} -> {appName}");
                removed++;
            }

            if (dead.Count > 0)
            {
                UpdateMruList(owl, dead);
            }
        }

        return removed;
    }

    /// <summary>削除したスロット文字を MRUList から取り除く (a,b,c… の 1 文字がスロット名)。</summary>
    private static void UpdateMruList(RegistryKey owl, List<string> deadSlots)
    {
        if (owl.GetValue("MRUList")?.ToString() is not { } mru)
        {
            return;
        }

        var updated = new string(mru.Where(c => !deadSlots.Contains(c.ToString(), StringComparer.OrdinalIgnoreCase)).ToArray());
        if (updated.Length > 0)
        {
            owl.SetValue("MRUList", updated);
        }
        else
        {
            owl.DeleteValue("MRUList", throwOnMissingValue: false);
        }
    }

    private static int CleanOrphanApplications(RegistryKey root, string basePath, List<string> lines, CancellationToken ct)
    {
        var removed = 0;
        using var baseKey = root.OpenSubKey(basePath, writable: true);
        if (baseKey is null)
        {
            return 0;
        }

        foreach (var appName in baseKey.GetSubKeyNames())
        {
            ct.ThrowIfCancellationRequested();
            using var commandKey = baseKey.OpenSubKey($@"{appName}\shell\open\command");
            var exe = StartupCommandParser.TryResolveExecutable(commandKey?.GetValue(null)?.ToString() ?? string.Empty);
            if (exe is null || File.Exists(exe))
            {
                continue;
            }

            try
            {
                baseKey.DeleteSubKeyTree(appName, throwOnMissingSubKey: false);
                lines.Add($"  - 孤立アプリ削除: {appName} (exe不在: {exe})");
                removed++;
            }
            catch (UnauthorizedAccessException)
            {
                lines.Add($"  - スキップ (アクセス拒否): {appName}");
            }
        }

        return removed;
    }

    /// <summary>アプリ名が Applications 登録 / App Paths / PATH 上の実在する実行ファイルを指すか。</summary>
    private static bool AppExists(string? appName, string[] pathDirs)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return false;
        }

        foreach (var (root, basePath) in new[]
                 {
                     (Registry.LocalMachine, @"SOFTWARE\Classes\Applications"),
                     (Registry.CurrentUser, @"Software\Classes\Applications"),
                 })
        {
            using var commandKey = root.OpenSubKey($@"{basePath}\{appName}\shell\open\command");
            var exe = StartupCommandParser.TryResolveExecutable(commandKey?.GetValue(null)?.ToString() ?? string.Empty);
            if (exe is not null && File.Exists(exe))
            {
                return true;
            }
        }

        foreach (var appPathsBase in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
                     @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths",
                 })
        {
            using var appPathKey = Registry.LocalMachine.OpenSubKey($@"{appPathsBase}\{appName}");
            if (appPathKey?.GetValue(null)?.ToString() is { } value)
            {
                var exe = Environment.ExpandEnvironmentVariables(value).Trim('"');
                if (File.Exists(exe))
                {
                    return true;
                }
            }
        }

        // PATH 上の実行ファイル (Get-Command 相当。pathDirs は呼び出し元で 1 回だけ計算済み)
        return pathDirs.Any(dir => File.Exists(Path.Combine(dir, appName)));
    }
}
