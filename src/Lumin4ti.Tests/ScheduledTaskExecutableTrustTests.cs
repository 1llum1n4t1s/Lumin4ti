using System.Security.AccessControl;
using System.Security.Principal;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class ScheduledTaskExecutableTrustTests
{
    [TestMethod]
    public void 最高権限タスクへ登録できるのはProgramFilesの正規実体だけである()
    {
        const string programFiles = @"C:\Program Files";

        Assert.IsTrue(ScheduledTaskExecutableTrust.IsExpectedInstalledPath(
            @"C:\Program Files\Lumin4ti\current\Lumin4ti.UI.exe",
            programFiles));
        Assert.IsFalse(ScheduledTaskExecutableTrust.IsExpectedInstalledPath(
            @"D:\Custom\Lumin4ti\current\Lumin4ti.UI.exe",
            programFiles));
        Assert.IsFalse(ScheduledTaskExecutableTrust.IsExpectedInstalledPath(
            @"C:\Program Files\Lumin4ti\current\subdir\Lumin4ti.UI.exe",
            programFiles));
        Assert.IsFalse(ScheduledTaskExecutableTrust.IsExpectedInstalledPath(
            @"C:\Program Files\Lumin4ti\current\Lumin4ti.UI-copy.exe",
            programFiles));
    }

    [TestMethod]
    public void 最高権限タスクの実体ACLは一般ユーザーの読み取りだけを許可する()
    {
        var security = CreateTaskExecutableAcl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute,
            AccessControlType.Allow));

        Assert.IsTrue(ScheduledTaskExecutableTrust.IsSafeAcl(security, out var reason), reason);
    }

    [TestMethod]
    public void 最高権限タスクの実体ACLは一般ユーザーの書き込みを拒否する()
    {
        var security = CreateTaskExecutableAcl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Write,
            AccessControlType.Allow));

        Assert.IsFalse(ScheduledTaskExecutableTrust.IsSafeAcl(security, out var reason));
        StringAssert.Contains(reason, "非管理者");
    }

    [TestMethod]
    public void 最高権限タスクの実体ACLは一般ユーザー所有を拒否する()
    {
        var security = CreateTaskExecutableAcl();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null));

        Assert.IsFalse(ScheduledTaskExecutableTrust.IsSafeAcl(security, out var reason));
        StringAssert.Contains(reason, "所有者");
    }

    [TestMethod]
    public void 最高権限タスクの実体と祖先では再解析ポイントを拒否する()
    {
        Assert.IsTrue(ScheduledTaskExecutableTrust.IsSafePathAttributes(FileAttributes.Archive));
        Assert.IsFalse(ScheduledTaskExecutableTrust.IsSafePathAttributes(
            FileAttributes.Directory | FileAttributes.ReparsePoint));
    }

    private static FileSecurity CreateTaskExecutableAcl()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        return security;
    }
}
