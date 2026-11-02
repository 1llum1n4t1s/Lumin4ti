using System.Security;
using System.Security.Principal;
using Lumin4ti.Core.Services.Windows.Actions;
using Microsoft.Win32;
using Lumin4ti.Core.Interfaces;

namespace Lumin4ti.Core.Services.Windows;

internal sealed record RebootCleanupProfile(string ProfileRoot, string WindowsRoot)
{
    internal string LocalAppData => Path.Combine(ProfileRoot, "AppData", "Local");
    internal string RoamingAppData => Path.Combine(ProfileRoot, "AppData", "Roaming");
}

internal interface IRebootCleanupEnvironment
{
    string CurrentSid { get; }
    string HelperPath { get; }
    DateTimeOffset UtcNow { get; }
    bool IsBootAccount { get; }
    bool IsTrustedLauncher { get; }
    bool IsTrustedHelper { get; }
    RebootCleanupProfile GetProfile(string sid);
    void ValidateEnqueueProfile(RebootCleanupProfile profile, IReadOnlyList<CleanupTarget> selected);
    ISettingsService LoadSettings(RebootCleanupProfile profile);
}

internal sealed class RebootCleanupEnvironment : IRebootCleanupEnvironment
{
    public string CurrentSid { get { using var identity = WindowsIdentity.GetCurrent(); return identity.User?.Value ?? throw new SecurityException("利用者 SID を確認できません。"); } }
    public string HelperPath => Path.Combine(AppContext.BaseDirectory, ScheduledTaskExecutableTrust.RebootCleanupExecutableName);
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public bool IsBootAccount
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User is { } sid && !sid.IsWellKnown(WellKnownSidType.LocalSystemSid) &&
                   new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
    public bool IsTrustedLauncher
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == false &&
                   new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) &&
                   ScheduledTaskExecutableTrust.IsTrustedInstalledExecutable(Environment.ProcessPath ?? string.Empty);
        }
    }
    public bool IsTrustedHelper => ScheduledTaskExecutableTrust.IsTrustedRebootCleanupExecutable(HelperPath);

    public RebootCleanupProfile GetProfile(string sid)
    {
        RebootCleanupTargets.ValidateSid(sid);
        if (sid != CurrentSid) throw new SecurityException("現在の利用者以外のプロファイルは処理しません。");
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid);
        var raw = key?.GetValue("ProfileImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemDrive = Path.GetPathRoot(windows)?.TrimEnd('\\') ?? throw new IOException("Windows のドライブを確認できません。");
        var root = raw?.Replace("%SystemDrive%", systemDrive, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(root) || root.Contains('%') || !Path.IsPathFullyQualified(root))
            throw new IOException("SID に対応する標準プロファイルを確認できません。");
        var profile = new RebootCleanupProfile(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), Path.GetFullPath(windows));
        ValidateProfile(profile);
        ValidateRootIdentity(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), profile.LocalAppData);
        ValidateRootIdentity(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), profile.RoamingAppData);
        return profile;
    }

    public void ValidateEnqueueProfile(RebootCleanupProfile profile, IReadOnlyList<CleanupTarget> selected)
    {
        if (!Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
                .Equals(profile.LocalAppData, StringComparison.OrdinalIgnoreCase))
            throw new IOException("LocalAppData が標準プロファイルと異なるため起動時の削除を予約できません。");
        ValidateProfile(profile);
        ValidateRootIdentity(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), profile.LocalAppData);
        ValidateRootIdentity(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), profile.RoamingAppData);
        foreach (var target in selected)
        {
            var expected = RebootCleanupTargets.Resolve(profile, RebootCleanupTargets.GetIds([target])).Single();
            if (!FileCleanupEngine.TryResolve(target.RawPath, true, out var actual, out _) ||
                !actual.Equals(expected.RawPath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("今回の対象と起動時に再構成する対象が一致しないため予約しません。");
            if (target.RawPath.Contains("%LOCALAPPDATA%", StringComparison.Ordinal))
                ValidateRootIdentity(Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%"), profile.LocalAppData);
            if (target.RawPath.Contains("%SystemRoot%", StringComparison.Ordinal))
                ValidateRootIdentity(Environment.ExpandEnvironmentVariables("%SystemRoot%"), profile.WindowsRoot);
        }
    }

    public ISettingsService LoadSettings(RebootCleanupProfile profile)
    {
        using var roaming = NativeCleanupEntry.Open(profile.RoamingAppData, true);
        if (!roaming.MatchesGlobalDosPath(profile.RoamingAppData)) throw new IOException("Roaming の実体を確認できません。");
        var directory = Path.Combine(profile.RoamingAppData, "Lumin4ti");
        var path = Path.Combine(directory, "settings.json");
        using var file = roaming.OpenDescendant(path, false, readData: true);
        return SettingsService.CreateReadOnlySnapshot(file.ReadText(1024 * 1024), path);
    }

    internal static void ValidateRootIdentity(string actual, string expected)
    {
        using var entry = NativeCleanupEntry.Open(actual, true);
        if (!entry.MatchesGlobalDosPath(expected)) throw new IOException("今回の基点と起動時の基点の実体が一致しません。");
    }

    private static void ValidateProfile(RebootCleanupProfile profile)
    {
        // local DOS alias と SYSTEM の global DOS alias が同じ文字列でも別実体になり得る。
        foreach (var path in new[] { profile.ProfileRoot, profile.LocalAppData, profile.RoamingAppData, profile.WindowsRoot })
        {
            using var entry = NativeCleanupEntry.Open(path, true);
            if (!entry.MatchesGlobalDosPath(path)) throw new IOException("利用者と SYSTEM のパス実体が一致しません。");
        }
    }
}

internal static class RebootCleanupTargets
{
    internal static readonly string[] Ids = ["user-icon-db", "user-explorer", "user-font", "service-font", "system-font-db"];

    internal static void ValidateSid(string sid)
    {
        if (sid.Length > 184 || new SecurityIdentifier(sid).Value != sid ||
            sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20") throw new SecurityException("予約の利用者 SID が不正です。");
    }

    internal static string[] GetIds(IReadOnlyList<CleanupTarget> targets)
    {
        if (targets.Count > Ids.Length) throw new IOException("予約対象が多すぎます。");
        var result = new List<string>();
        foreach (var target in targets)
        {
            var index = Array.FindIndex(FileCleanupGroups.ShellCacheTargets, template =>
                target.Kind == template.Kind && string.Equals(target.RawPath, template.RawPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(target.Pattern, template.Pattern, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new IOException("既知のキャッシュ以外は予約できません。");
            result.Add(Ids[index]);
        }
        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static CleanupTarget[] Resolve(RebootCleanupProfile profile, IReadOnlyList<string> ids) =>
        ids.Select(id =>
        {
            var index = Array.IndexOf(Ids, id);
            if (index < 0) throw new IOException("未知のキャッシュ ID です。");
            var template = FileCleanupGroups.ShellCacheTargets[index];
            return template with { RawPath = template.RawPath.Replace("%LOCALAPPDATA%", profile.LocalAppData, StringComparison.Ordinal)
                .Replace("%SystemRoot%", profile.WindowsRoot, StringComparison.Ordinal) };
        }).ToArray();

    internal static bool NeedsFontServices(IEnumerable<string> ids) => ids.Any(id => id is "user-font" or "service-font" or "system-font-db");
}
