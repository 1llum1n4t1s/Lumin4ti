using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Lumin4ti.Core.Services;

/// <summary>
/// Path の保護バックアップ、タスク登録用 XML、移行保留情報を、Administrators と SYSTEM だけが
/// 変更できる ProgramData 配下へ保存する。危険な既存要素は隔離して作り直すが、その操作回は fail closed にする。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ProtectedBackupStorage
{
    private static readonly SecurityIdentifier Administrators =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem =
        new(WellKnownSidType.LocalSystemSid, null);

    public static ProtectedBackupStorage Default { get; } = new(
        AppPaths.ProtectedBackupsDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    private readonly string _trustedParentPath;

    internal ProtectedBackupStorage(string rootPath, string trustedParentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedParentPath);

        if (!Path.IsPathFullyQualified(rootPath) || !Path.IsPathFullyQualified(trustedParentPath))
        {
            throw new ArgumentException("保護バックアップ先は完全パスである必要があります。");
        }

        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        _trustedParentPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedParentPath));
        if (!IsPathInside(RootPath, _trustedParentPath))
        {
            throw new ArgumentException("保護バックアップ先は信頼済み親ディレクトリの配下に限ります。", nameof(rootPath));
        }
    }

    public string RootPath { get; }

    public string GetFullPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathFullyQualified(relativePath))
        {
            throw new ArgumentException("バックアップ名は相対パスで指定してください。", nameof(relativePath));
        }

        var fullPath = Path.GetFullPath(Path.Combine(RootPath, relativePath));
        if (!IsPathInside(fullPath, RootPath))
        {
            throw new ArgumentException("バックアップ先の外へ移動するパスは使用できません。", nameof(relativePath));
        }

        return fullPath;
    }

    public bool FileExists(string relativePath)
    {
        var fullPath = PrepareFilePath(relativePath);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            return false;
        }

        if (Directory.Exists(fullPath))
        {
            ReplaceUnsafeEntryAndThrow(fullPath, isDirectory: true, createDirectoryReplacement: false,
                "バックアップファイルと同名のディレクトリが存在しました");
        }

        ValidateFileOrRepairAndThrow(fullPath);
        return true;
    }

    public string ReadAllText(string relativePath)
    {
        if (!FileExists(relativePath))
        {
            throw new FileNotFoundException("保護バックアップが見つかりません。", GetFullPath(relativePath));
        }

        return File.ReadAllText(GetFullPath(relativePath));
    }

    internal IReadOnlyList<string> EnumerateFileNames(string relativeDirectory, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var directory = GetFullPath(relativeDirectory);
        EnsureDirectoryChain(directory);
        using var entry = Windows.Actions.NativeCleanupEntry.Open(directory, true);
        var names = new List<string>();
        foreach (var child in entry.Enumerate())
        {
            if ((child.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new SecurityException("保護キューに通常ファイル以外の要素があります。");
            if (names.Count >= limit) throw new IOException("保護キューのファイル数上限を超えています。");
            names.Add(child.Name);
        }
        return names;
    }

    public void WriteNewAtomically(string relativePath, Action<Stream> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (FileExists(relativePath))
        {
            throw new IOException("保護バックアップは既に存在します。");
        }

        WriteAtomicallyCore(relativePath, write);
    }

    private void WriteAtomicallyCore(string relativePath, Action<Stream> write)
    {
        var fullPath = PrepareFilePath(relativePath);

        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("保護バックアップ先ディレクトリを特定できません。");
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = FileSystemAclExtensions.Create(
                       new FileInfo(tempPath),
                       FileMode.CreateNew,
                       FileSystemRights.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough,
                       CreateFileSecurity()))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, fullPath, overwrite: false);
            ValidateFileOrRepairAndThrow(fullPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    public void Delete(string relativePath)
    {
        if (FileExists(relativePath))
        {
            File.Delete(GetFullPath(relativePath));
        }
    }

    internal static DirectorySecurity CreateDirectorySecurity()
    {
        var security = new DirectorySecurity();
        const InheritanceFlags inheritChildren =
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(Administrators);
        security.AddAccessRule(new FileSystemAccessRule(
            Administrators,
            FileSystemRights.FullControl,
            inheritChildren,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            LocalSystem,
            FileSystemRights.FullControl,
            inheritChildren,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    internal static FileSecurity CreateFileSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(Administrators);
        security.AddAccessRule(new FileSystemAccessRule(
            Administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            LocalSystem, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    internal static bool IsProtectedAcl(FileSystemSecurity security, out string failureReason)
    {
        ArgumentNullException.ThrowIfNull(security);
        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (raw.DiscretionaryAcl is null || raw.DiscretionaryAcl.Cast<GenericAce>().Any(ace => ace is not CommonAce { IsCallback: false }))
        {
            failureReason = "解釈できない DACL エントリがあります";
            return false;
        }
        if (!security.AreAccessRulesProtected)
        {
            failureReason = "DACL が継承から保護されていません";
            return false;
        }

        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || (!owner.Equals(Administrators) && !owner.Equals(LocalSystem)))
        {
            failureReason = $"所有者が Administrators / SYSTEM ではありません ({owner?.Value ?? "不明"})";
            return false;
        }

        var administratorHasFullControl = false;
        var systemHasFullControl = false;
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.IsInherited ||
                rule.AccessControlType != AccessControlType.Allow ||
                rule.IdentityReference is not SecurityIdentifier sid ||
                (!sid.Equals(Administrators) && !sid.Equals(LocalSystem)) ||
                rule.FileSystemRights != FileSystemRights.FullControl)
            {
                failureReason = $"許可していない ACL エントリがあります ({rule.IdentityReference.Value})";
                return false;
            }

            administratorHasFullControl |= sid.Equals(Administrators);
            systemHasFullControl |= sid.Equals(LocalSystem);
        }

        if (!administratorHasFullControl || !systemHasFullControl)
        {
            failureReason = "Administrators または SYSTEM の FullControl がありません";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private string PrepareFilePath(string relativePath)
    {
        var fullPath = GetFullPath(relativePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("保護バックアップ先ディレクトリを特定できません。");
        EnsureDirectoryChain(directory);
        return fullPath;
    }

    private void EnsureDirectoryChain(string targetDirectory)
    {
        if (!Directory.Exists(_trustedParentPath))
        {
            throw new DirectoryNotFoundException($"ProgramData が見つかりません: {_trustedParentPath}");
        }

        if ((File.GetAttributes(_trustedParentPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new SecurityException("ProgramData が再解析ポイントのためバックアップを使用できません。");
        }

        var relative = Path.GetRelativePath(_trustedParentPath, targetDirectory);
        if (Path.IsPathFullyQualified(relative) || relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new SecurityException("保護バックアップ先が ProgramData の外へ移動しています。");
        }

        var current = _trustedParentPath;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            EnsureSingleDirectory(current);
        }
    }

    private void EnsureSingleDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            if (File.Exists(path))
            {
                ReplaceUnsafeEntryAndThrow(path, isDirectory: false, createDirectoryReplacement: true,
                    "ディレクトリ位置にファイルが存在しました");
            }

            try
            {
                FileSystemAclExtensions.Create(new DirectoryInfo(path), CreateDirectorySecurity());
            }
            catch (IOException) when (Directory.Exists(path) || File.Exists(path))
            {
                ReplaceUnsafeEntryAndThrow(
                    path,
                    Directory.Exists(path),
                    createDirectoryReplacement: true,
                    "ディレクトリ作成中に既存要素が現れました");
            }
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            ReplaceUnsafeEntryAndThrow(path, isDirectory: true, createDirectoryReplacement: true,
                "再解析ポイントでした");
        }

        var security = new DirectoryInfo(path).GetAccessControl();
        if (!IsProtectedAcl(security, out var failureReason))
        {
            ReplaceUnsafeEntryAndThrow(path, isDirectory: true, createDirectoryReplacement: true, failureReason);
        }
    }

    private void ValidateFileOrRepairAndThrow(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            ReplaceUnsafeEntryAndThrow(path, isDirectory: false, createDirectoryReplacement: false,
                "バックアップファイルが再解析ポイントでした");
        }

        var security = new FileInfo(path).GetAccessControl();
        if (!IsProtectedAcl(security, out var failureReason))
        {
            ReplaceUnsafeEntryAndThrow(path, isDirectory: false, createDirectoryReplacement: false, failureReason);
        }
    }

    private static void ReplaceUnsafeEntryAndThrow(
        string path,
        bool isDirectory,
        bool createDirectoryReplacement,
        string reason)
    {
        var quarantinePath = path + $".untrusted-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        try
        {
            if (isDirectory)
            {
                Directory.Move(path, quarantinePath);
            }
            else
            {
                File.Move(path, quarantinePath);
            }

            if (createDirectoryReplacement)
            {
                FileSystemAclExtensions.Create(new DirectoryInfo(path), CreateDirectorySecurity());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SecurityException(
                $"危険な保護バックアップ要素を隔離できないため処理を中止しました: {path} ({reason})",
                ex);
        }

        throw new SecurityException(
            $"危険な保護バックアップ要素を {quarantinePath} へ隔離しました。" +
            $"既存ハンドルを使用しないよう今回の処理を中止します: {reason}");
    }

    private static bool IsPathInside(string path, string directory) =>
        path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
