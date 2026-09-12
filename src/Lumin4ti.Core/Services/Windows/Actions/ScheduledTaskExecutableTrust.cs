using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// UAC なしで最高権限起動するスケジュールタスクへ固定登録してよい実行ファイルかを検証する。
/// 署名と MSI マーカーに加え、配置を製品の正規パスへ限定し、パスの差し替え耐性も確認する。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ScheduledTaskExecutableTrust
{
    private const string AppDirectoryName = "Lumin4ti";
    private const string CurrentDirectoryName = "current";
    private const string UiExecutableName = "Lumin4ti.UI.exe";
    private const string StableExecutableName = "Lumin4ti.exe";

    private static readonly SecurityIdentifier Administrators =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem =
        new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    private const FileSystemRights WriteLikeRights =
        FileSystemRights.Write |
        FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership |
        (FileSystemRights)0x10000000 | // GENERIC_ALL
        (FileSystemRights)0x40000000;  // GENERIC_WRITE

    public static bool IsTrustedInstalledExecutable(string processPath)
    {
        try
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!IsExpectedInstalledPath(processPath, programFiles))
            {
                return false;
            }

            var installRoot = Path.Combine(programFiles, AppDirectoryName);
            var stableExecutable = Path.Combine(installRoot, StableExecutableName);
            if (!WindowsPerMachineMigration.IsCurrentProcessPerMachine(processPath, stableExecutable))
            {
                return false;
            }

            var currentDirectory = Path.Combine(installRoot, CurrentDirectoryName);
            return IsProtectedPathElement(programFiles, isDirectory: true, out _)
                   && IsProtectedPathElement(installRoot, isDirectory: true, out _)
                   && IsProtectedPathElement(currentDirectory, isDirectory: true, out _)
                   && IsProtectedPathElement(processPath, isDirectory: false, out _);
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            SecurityException or
            ArgumentException or
            NotSupportedException)
        {
            return false;
        }
    }

    internal static bool IsExpectedInstalledPath(string processPath, string programFiles)
    {
        if (string.IsNullOrWhiteSpace(processPath) ||
            string.IsNullOrWhiteSpace(programFiles) ||
            !Path.IsPathFullyQualified(processPath) ||
            !Path.IsPathFullyQualified(programFiles))
        {
            return false;
        }

        var expected = Path.GetFullPath(Path.Combine(
            programFiles,
            AppDirectoryName,
            CurrentDirectoryName,
            UiExecutableName));
        return Path.GetFullPath(processPath).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSafeAcl(FileSystemSecurity security, out string failureReason)
    {
        ArgumentNullException.ThrowIfNull(security);

        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !IsTrustedPrincipal(owner))
        {
            failureReason = $"所有者が Administrators / SYSTEM / TrustedInstaller ではありません ({owner?.Value ?? "不明"})";
            return false;
        }

        var rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
            {
                continue;
            }

            if (rule.IdentityReference is not SecurityIdentifier sid)
            {
                failureReason = $"ACL の主体を SID として確認できません ({rule.IdentityReference.Value})";
                return false;
            }

            if (!IsTrustedPrincipal(sid) && (rule.FileSystemRights & WriteLikeRights) != 0)
            {
                failureReason = $"非管理者が変更できる ACL です ({sid.Value}: {rule.FileSystemRights})";
                return false;
            }
        }

        failureReason = string.Empty;
        return true;
    }

    internal static bool IsSafePathAttributes(FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) == 0;

    private static bool IsProtectedPathElement(string path, bool isDirectory, out string failureReason)
    {
        var attributes = File.GetAttributes(path);
        if (!IsSafePathAttributes(attributes))
        {
            failureReason = $"再解析ポイントです ({path})";
            return false;
        }

        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        return IsSafeAcl(security, out failureReason);
    }

    private static bool IsTrustedPrincipal(SecurityIdentifier sid) =>
        sid.Equals(Administrators) || sid.Equals(LocalSystem) || sid.Equals(TrustedInstaller);
}
