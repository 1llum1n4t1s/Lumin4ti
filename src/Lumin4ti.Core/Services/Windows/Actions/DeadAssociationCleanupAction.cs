using System.Security;
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
    private const string ApplicationsPath = @"SOFTWARE\Classes\Applications";
    private const string AppPathsPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

    private readonly record struct CleanupCounts(int Removed, int Skipped);

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
            var skipped = 0;

            // PATH は不変なのでここで 1 回だけ分割し、AppExists の呼び出しごとの再分割を避ける
            var pathDirs = (Environment.GetEnvironmentVariable("PATH")?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? [])
                .Select(d => d.Trim())
                .Where(d => d.Length > 0)
                .ToArray();
            var removalDecisionCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            var openWith = CleanOpenWithLists(lines, pathDirs, removalDecisionCache, ct);
            removed += openWith.Removed;
            skipped += openWith.Skipped;

            var machineApplications = CleanOrphanApplications(Registry.LocalMachine, ApplicationsPath, lines, ct);
            removed += machineApplications.Removed;
            skipped += machineApplications.Skipped;

            var userApplications = CleanOrphanApplications(Registry.CurrentUser, ApplicationsPath, lines, ct);
            removed += userApplications.Removed;
            skipped += userApplications.Skipped;

            lines.Add($"  - 合計削除: {removed} 件");
            if (skipped > 0)
            {
                lines.Add($"  - 未処理: {skipped} 件");
            }

            LoggerBootstrap.Log.Info($"{Id}: {removed} 件削除 / {skipped} 件未処理");
            return CreateResult(lines, skipped);
        }, ct);

    private static CleanupCounts CleanOpenWithLists(
        List<string> lines,
        string[] pathDirs,
        Dictionary<string, bool> removalDecisionCache,
        CancellationToken ct)
    {
        var removed = 0;
        var skipped = 0;
        using var fileExts = Registry.CurrentUser.OpenSubKey(FileExtsPath);
        if (fileExts is null)
        {
            return new CleanupCounts(0, 0);
        }

        foreach (var extName in fileExts.GetSubKeyNames())
        {
            ct.ThrowIfCancellationRequested();
            RegistryKey? owl;
            try
            {
                owl = fileExts.OpenSubKey($@"{extName}\OpenWithList", writable: true);
            }
            catch (Exception ex) when (IsRegistryAccessUncertain(ex))
            {
                lines.Add($@"  - スキップ (アクセス不能): {extName}\OpenWithList");
                skipped++;
                continue;
            }

            if (owl is null)
            {
                continue;
            }

            using (owl)
            {
                var dead = new List<string>();
                foreach (var slot in owl.GetValueNames())
                {
                    if (slot.Equals("MRUList", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var appName = owl.GetValue(slot)?.ToString();
                    if (!ShouldRemoveOpenWithEntry(appName, pathDirs, removalDecisionCache))
                    {
                        continue;
                    }

                    try
                    {
                        owl.DeleteValue(slot, throwOnMissingValue: false);
                        dead.Add(slot);
                        lines.Add($"  - OpenWithList削除: {extName} -> {appName}");
                        removed++;
                    }
                    catch (Exception ex) when (IsRegistryAccessUncertain(ex))
                    {
                        lines.Add($"  - スキップ (アクセス不能): {extName} -> {appName}");
                        skipped++;
                    }
                }

                if (dead.Count > 0)
                {
                    try
                    {
                        UpdateMruList(owl, dead);
                    }
                    catch (Exception ex) when (IsRegistryAccessUncertain(ex))
                    {
                        lines.Add($"  - スキップ (MRU更新不能): {extName}");
                        skipped++;
                    }
                }
            }
        }

        return new CleanupCounts(removed, skipped);
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

    private static CleanupCounts CleanOrphanApplications(
        RegistryKey root,
        string basePath,
        List<string> lines,
        CancellationToken ct)
    {
        var removed = 0;
        var skipped = 0;
        RegistryKey? baseKey;
        try
        {
            baseKey = root.OpenSubKey(basePath, writable: true);
        }
        catch (Exception ex) when (IsRegistryAccessUncertain(ex))
        {
            lines.Add($@"  - スキップ (アクセス不能): {root.Name}\{basePath}");
            return new CleanupCounts(0, 1);
        }

        if (baseKey is null)
        {
            return new CleanupCounts(0, 0);
        }

        using (baseKey)
        {
            foreach (var appName in baseKey.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                bool confirmedMissing;
                try
                {
                    using var appKey = baseKey.OpenSubKey(appName);
                    confirmedMissing = IsApplicationRegistrationConfirmedMissing(appKey);
                }
                catch (Exception ex) when (IsRegistryAccessUncertain(ex))
                {
                    lines.Add($"  - スキップ (アクセス不能): {appName}");
                    skipped++;
                    continue;
                }

                if (!confirmedMissing)
                {
                    continue;
                }

                try
                {
                    baseKey.DeleteSubKeyTree(appName, throwOnMissingSubKey: false);
                    lines.Add($"  - 孤立アプリ削除: {appName}");
                    removed++;
                }
                catch (Exception ex) when (IsRegistryAccessUncertain(ex))
                {
                    lines.Add($"  - スキップ (アクセス不能): {appName}");
                    skipped++;
                }
            }
        }

        return new CleanupCounts(removed, skipped);
    }

    private static bool IsApplicationRegistrationConfirmedMissing(RegistryKey? appKey)
    {
        if (appKey is null)
        {
            return false;
        }

        using var shellKey = appKey.OpenSubKey("shell");
        if (shellKey is null)
        {
            return false;
        }

        var candidates = new List<bool>();
        var verbs = shellKey.GetSubKeyNames();
        if (verbs.Length == 0)
        {
            return false;
        }

        foreach (var verb in verbs)
        {
            using var commandKey = shellKey.OpenSubKey($@"{verb}\command");
            if (commandKey is null)
            {
                return false;
            }

            var executable = StartupCommandParser.TryResolveExecutable(
                commandKey.GetValue(null)?.ToString() ?? string.Empty);
            candidates.Add(executable is not null && StartupCommandParser.IsConfirmedMissing(executable));
        }

        return AllCandidatesAreConfirmedMissing(candidates);
    }

    /// <summary>アプリ名を解決できる全候補で欠損を確定できた場合だけ削除する。</summary>
    private static bool ShouldRemoveOpenWithEntry(
        string? appName,
        string[] pathDirs,
        Dictionary<string, bool> removalDecisionCache)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return false;
        }

        return GetCachedRemovalDecision(appName, removalDecisionCache, Probe);

        bool Probe(string name)
        {
            return ShouldRemoveOpenWithEntryUncached(name, pathDirs);
        }
    }

    internal static bool GetCachedRemovalDecision(
        string appName,
        Dictionary<string, bool> cache,
        Func<string, bool> probe)
    {
        if (cache.TryGetValue(appName, out var exists))
        {
            return exists;
        }

        exists = probe(appName);
        cache[appName] = exists;
        return exists;
    }

    private static bool ShouldRemoveOpenWithEntryUncached(string appName, string[] pathDirs)
    {
        var candidates = new List<bool>();
        try
        {
            AddApplicationsCandidate(Registry.LocalMachine, appName, candidates);
            AddApplicationsCandidate(Registry.CurrentUser, appName, candidates);
            AddAppPathCandidate(Registry.CurrentUser, appName, candidates);

            RegistryView[] machineViews = Environment.Is64BitOperatingSystem
                ? [RegistryView.Registry64, RegistryView.Registry32]
                : [RegistryView.Default];
            foreach (var view in machineViews)
            {
                using var machineRoot = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                AddAppPathCandidate(machineRoot, appName, candidates);
            }

            foreach (var directory in GetExecutableSearchDirectories(pathDirs))
            {
                try
                {
                    // 標準検索先は実在・一時不明の反証にだけ使う。見つからないことだけでは、
                    // OpenWithList が元々指していた場所の欠損を確定できない。
                    if (!StartupCommandParser.IsConfirmedMissing(Path.Combine(directory, appName)))
                    {
                        candidates.Add(false);
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    candidates.Add(false);
                }
            }
        }
        catch (Exception ex) when (IsRegistryAccessUncertain(ex))
        {
            return false;
        }

        return AllCandidatesAreConfirmedMissing(candidates);
    }

    private static void AddApplicationsCandidate(RegistryKey root, string appName, List<bool> candidates)
    {
        using var appKey = root.OpenSubKey($@"{ApplicationsPath}\{appName}");
        if (appKey is null)
        {
            return;
        }

        using var commandKey = appKey.OpenSubKey(@"shell\open\command");
        var executable = StartupCommandParser.TryResolveExecutable(
            commandKey?.GetValue(null)?.ToString() ?? string.Empty);
        candidates.Add(executable is not null && StartupCommandParser.IsConfirmedMissing(executable));
    }

    private static void AddAppPathCandidate(RegistryKey root, string appName, List<bool> candidates)
    {
        using var appPathKey = root.OpenSubKey($@"{AppPathsPath}\{appName}");
        if (appPathKey is null)
        {
            return;
        }

        var value = appPathKey.GetValue(null)?.ToString();
        var executable = value is null
            ? null
            : StartupCommandParser.TryResolveExecutable(Environment.ExpandEnvironmentVariables(value));
        candidates.Add(executable is not null && StartupCommandParser.IsConfirmedMissing(executable));
    }

    private static IEnumerable<string> GetExecutableSearchDirectories(IEnumerable<string> pathDirs)
    {
        var knownDirectories = new[]
        {
            Environment.CurrentDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.SystemDirectory,
        };

        return knownDirectories
            .Concat(pathDirs)
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    internal static bool AllCandidatesAreConfirmedMissing(IEnumerable<bool> candidates)
    {
        var found = false;
        foreach (var confirmedMissing in candidates)
        {
            found = true;
            if (!confirmedMissing)
            {
                return false;
            }
        }

        return found;
    }

    internal static MaintenanceActionResult CreateResult(IEnumerable<string> lines, int skipped) =>
        skipped > 0
            ? MaintenanceActionResult.Partial(lines)
            : MaintenanceActionResult.Ok(lines);

    private static bool IsRegistryAccessUncertain(Exception exception) =>
        exception is UnauthorizedAccessException or SecurityException or IOException;
}
