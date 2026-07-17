using System.Security.AccessControl;
using System.Security.Principal;
using Lumin4ti.Core.Services;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class ProtectedBackupStorageTests
{
    [TestMethod]
    public void 保護ACLはAdministratorsとSystemだけにFullControlを与える()
    {
        var created = ProtectedBackupStorage.CreateDirectorySecurity();
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorBinaryForm(created.GetSecurityDescriptorBinaryForm());

        Assert.IsTrue(ProtectedBackupStorage.IsProtectedAcl(security, out var reason), reason);
        Assert.IsTrue(security.AreAccessRulesProtected);
        Assert.AreEqual(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            security.GetOwner(typeof(SecurityIdentifier)));

        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        Assert.HasCount(2, rules);
        Assert.IsTrue(rules.All(rule => rule.FileSystemRights == FileSystemRights.FullControl));
        Assert.IsTrue(rules.Any(rule => rule.IdentityReference.Equals(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null))));
        Assert.IsTrue(rules.Any(rule => rule.IdentityReference.Equals(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null))));
    }

    [TestMethod]
    public void 保護ファイルACLもAdministratorsとSystemだけにFullControlを与える()
    {
        var created = ProtectedBackupStorage.CreateFileSecurity();
        var security = new FileSecurity();
        security.SetSecurityDescriptorBinaryForm(created.GetSecurityDescriptorBinaryForm());

        Assert.IsTrue(ProtectedBackupStorage.IsProtectedAcl(security, out var reason), reason);
        Assert.IsTrue(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        Assert.HasCount(2, rules);
        Assert.IsTrue(rules.All(rule => rule.FileSystemRights == FileSystemRights.FullControl));
    }

    [TestMethod]
    public void 一般ユーザー書込ACEを含むACLは拒否する()
    {
        var security = ProtectedBackupStorage.CreateDirectorySecurity();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Write,
            AccessControlType.Allow));

        Assert.IsFalse(ProtectedBackupStorage.IsProtectedAcl(security, out var reason));
        StringAssert.Contains(reason, "許可していない");
    }

    [TestMethod]
    public void 保護ルート外へ脱出する相対パスは拒否する()
    {
        var trustedParent = Path.Combine(Path.GetTempPath(), "Lumin4ti.Tests", "ProgramData");
        var root = Path.Combine(trustedParent, "Lumin4ti", "backups");
        var storage = new ProtectedBackupStorage(root, trustedParent);

        Assert.ThrowsExactly<ArgumentException>(() => storage.GetFullPath("..\\forged.json"));
    }

    [TestMethod]
    public void 正本はProgramData配下でAppDataとは分離される()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Lumin4ti",
            "backups");

        Assert.AreEqual(expected, AppPaths.ProtectedBackupsDirectory);
        Assert.AreNotEqual(
            Path.GetFullPath(AppPaths.AppDataDirectory),
            Path.GetFullPath(AppPaths.ProtectedBackupsDirectory));
    }
}
