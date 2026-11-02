using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Xml.Linq;
using Lumin4ti.Core.Services.Windows;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class RebootCleanupTaskTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private const string OtherSid = "S-1-5-21-1-2-3-1002";
    private const string Helper = @"C:\Program Files\Lumin4ti\current\Lumin4ti.RebootCleanup.exe";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [TestMethod]
    public void 利用者SID別の固定名とS4U定義は他SIDや別操作を所有と判定しない()
    {
        Assert.AreEqual("CacheCleanup-" + Sid, RebootCleanupTask.TaskName(Sid));
        Assert.AreNotEqual(RebootCleanupTask.TaskName(Sid), RebootCleanupTask.TaskName(OtherSid));
        Assert.IsFalse(RebootCleanupTask.IsPrincipalSid(Sid, OtherSid));
        Assert.IsFalse(RebootCleanupTask.IsPrincipalSid("", Sid));
        Assert.ThrowsExactly<SecurityException>(() => RebootCleanupTask.TaskName("S-1-5-18"));
        Assert.ThrowsExactly<SecurityException>(() => RebootCleanupTask.TaskName(Sid.ToLowerInvariant()));
        var expires = DateTimeOffset.UtcNow.AddDays(7);
        var xml = RebootCleanupTask.BuildXml(Helper, expires, Sid);
        Assert.IsTrue(RebootCleanupTask.IsOwnedDefinition(xml, Helper, Sid));
        Assert.IsFalse(RebootCleanupTask.IsOwnedDefinition(xml, Helper, OtherSid));
        Assert.IsFalse(RebootCleanupTask.IsOwnedDefinition(xml.Replace("BootTrigger", "LogonTrigger", StringComparison.Ordinal), Helper, Sid));
        Assert.IsFalse(RebootCleanupTask.IsOwnedDefinition(xml.Replace("S4U", "ServiceAccount", StringComparison.Ordinal), Helper, Sid));
        Assert.IsFalse(RebootCleanupTask.IsOwnedDefinition(xml.Replace(Sid, "S-1-5-18", StringComparison.Ordinal), Helper, Sid));
        Assert.IsFalse(RebootCleanupTask.IsOwnedDefinition(xml.Replace("HighestAvailable", "LeastPrivilege", StringComparison.Ordinal), Helper, Sid));
        Assert.IsFalse(RebootCleanupTask.IsOwnedDefinition(xml.Replace("PT0S", "PT6M", StringComparison.Ordinal), Helper, Sid));
        Assert.IsFalse(RebootCleanupTask.IsOwnedDefinition(xml.Replace("<AllowHardTerminate>false</AllowHardTerminate>", "<AllowHardTerminate>true</AllowHardTerminate>", StringComparison.Ordinal), Helper, Sid));
        Assert.IsFalse(RebootCleanupTask.IsOwnedDefinition(xml.Replace("<AllowHardTerminate>false</AllowHardTerminate>", "", StringComparison.Ordinal), Helper, Sid));
        Assert.IsFalse(RebootCleanupTask.IsOwnedDefinition(xml.Replace("Lumin4ti.RebootCleanup.exe", "Lumin4ti.UI.exe", StringComparison.Ordinal), Helper, Sid));
        var arguments = XDocument.Parse(xml);
        arguments.Descendants(Ns + "Exec").Single().Add(new XElement(Ns + "Arguments", "--arbitrary"));
        Assert.IsFalse(RebootCleanupTask.IsOwnedDefinition(arguments.ToString(), Helper, Sid));
        var missingExpiry = XDocument.Parse(xml);
        missingExpiry.Descendants(Ns + "EndBoundary").Single().Remove();
        Assert.IsFalse(RebootCleanupTask.IsOwnedDefinition(missingExpiry.ToString(), Helper, Sid));
        Assert.AreEqual(expires.UtcDateTime, DateTimeOffset.Parse(XDocument.Parse(xml).Descendants(Ns + "EndBoundary").Single().Value).UtcDateTime);
    }

    [TestMethod]
    public void フォルダーとタスクはBAとSYSTEMだけの保護DACLを必要とする()
    {
        Assert.IsTrue(RebootCleanupTask.IsProtectedDescriptor(RebootCleanupTask.SecurityDescriptor));
        Assert.IsFalse(RebootCleanupTask.IsProtectedDescriptor(RebootCleanupTask.SecurityDescriptor.Replace("O:BA", "O:" + Sid, StringComparison.Ordinal)));
        Assert.IsFalse(RebootCleanupTask.IsProtectedDescriptor(RebootCleanupTask.SecurityDescriptor.Replace("D:P", "D:", StringComparison.Ordinal)));
        Assert.IsFalse(RebootCleanupTask.IsProtectedDescriptor(RebootCleanupTask.SecurityDescriptor + "(A;;FA;;;BU)"));
        var raw = new RawSecurityDescriptor(RebootCleanupTask.SecurityDescriptor);
        var acl = new RawAcl(4, raw.DiscretionaryAcl!.Count + 1);
        foreach (GenericAce ace in raw.DiscretionaryAcl) acl.InsertAce(acl.Count, ace);
        acl.InsertAce(acl.Count, new ObjectAce(AceFlags.None, AceQualifier.AccessAllowed,
            (int)FileSystemRights.WriteAttributes, new SecurityIdentifier("S-1-1-0"),
            ObjectAceFlags.ObjectAceTypePresent, Guid.NewGuid(), Guid.Empty, false, null));
        raw.DiscretionaryAcl = acl;
        Assert.IsFalse(RebootCleanupTask.IsProtectedDescriptor(raw.GetSddlForm(AccessControlSections.All)));
    }

    [TestMethod]
    public void 未登録のメモリ定義はTaskSchedulerのXMLschemaでS4UとHighestとして受理される()
    {
        object? service = null;
        object? folder = null;
        object? definition = null;
        object? principal = null;
        object? settings = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder(@"\");
            var rawIdentity = RebootCleanupTask.ReadRawFolderIdentity(folder);
            Assert.AreEqual((string)((dynamic)folder).Name, rawIdentity.Name);
            Assert.AreEqual((string)((dynamic)folder).Path, rawIdentity.Path);
            Assert.AreEqual((string)((dynamic)folder).GetSecurityDescriptor(7), rawIdentity.Security);
            definition = ((dynamic)service).NewTask(0);
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User!.Value;
            var xml = RebootCleanupTask.BuildXml(Helper, DateTimeOffset.UtcNow.AddDays(7), sid);
            // RegisterTask / CreateFolder は呼ばない。COM の未登録 TaskDefinition だけで検証する。
            ((dynamic)definition).XmlText = xml;
            var normalized = (string)((dynamic)definition).XmlText;
            Assert.IsTrue(RebootCleanupTask.IsOwnedDefinition(normalized, Helper, sid), normalized);
            principal = ((dynamic)definition).Principal;
            Assert.AreEqual(2, (int)((dynamic)principal).LogonType);
            Assert.AreEqual(1, (int)((dynamic)principal).RunLevel);
            Assert.IsTrue(RebootCleanupTask.IsPrincipalSid((string)((dynamic)principal).UserId, sid));
            Assert.IsFalse(RebootCleanupTask.IsPrincipalSid((string)((dynamic)principal).UserId, OtherSid));
            settings = ((dynamic)definition).Settings;
            Assert.AreEqual("PT0S", (string)((dynamic)settings).ExecutionTimeLimit);
            Assert.IsFalse((bool)((dynamic)settings).AllowHardTerminate);
            Assert.AreEqual("PT1M", (string)((dynamic)settings).DeleteExpiredTaskAfter);
        }
        finally
        {
            Release(settings);
            Release(principal);
            Release(definition);
            Release(folder);
            Release(service);
        }
    }

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(0x0004131B, false)]
    [DataRow(0x0004131C, false)]
    [DataRow(unchecked((int)0x80070005), false)]
    [DataRow(0x0004131B, true)]
    [DataRow(0x0004131C, true)]
    [DataRow(unchecked((int)0x80070005), true)]
    public void 生HRESULTはS_OKだけ成功とし新規warning実体を回収し既存更新の動作保証はせず無条件削除しない(int hresult, bool updatingExisting)
    {
        var expires = DateTimeOffset.UtcNow.AddDays(7);
        var task = new FakeRegisteredTask(RebootCleanupTask.BuildXml(Helper, expires, Sid));
        var folder = new FakeFolder(task) { Present = hresult >= 0 || updatingExisting };
        var validated = 0;
        var registration = new RebootCleanupTask(Helper);
        void Complete() => RebootCleanupTask.CompleteRegistration(hresult, updatingExisting, () => validated++,
            () => registration.RecoverCreatedTask(folder, RebootCleanupTask.TaskName(Sid), Sid, expires));
        if (hresult == 0) Complete();
        else
        {
            var error = Assert.ThrowsExactly<COMException>(Complete);
            Assert.AreEqual(hresult, error.HResult);
            StringAssert.Contains(error.Message, $"0x{hresult:X8}");
            if (updatingExisting)
                StringAssert.Contains(error.Message, "既存タスクが更新された可能性があり、起動可否は未確認です");
        }
        Assert.AreEqual(hresult == 0 ? 1 : 0, validated);
        var recovered = hresult > 0 && !updatingExisting;
        Assert.AreEqual(recovered ? 1 : 0, folder.Deleted);
        Assert.AreEqual(!recovered, task.Enabled);
        Assert.AreEqual(updatingExisting || hresult == 0, folder.Present);
    }

    [TestMethod]
    [DataRow("sid")]
    [DataRow("expiry")]
    [DataRow("acl")]
    public void warning回収でも他SIDや別期限や保護不明のタスクは無効化も削除もしない(string difference)
    {
        var expires = DateTimeOffset.UtcNow.AddDays(7);
        var task = new FakeRegisteredTask(RebootCleanupTask.BuildXml(Helper,
            difference == "expiry" ? expires.AddDays(1) : expires, difference == "sid" ? OtherSid : Sid));
        if (difference == "acl") task.Security = RebootCleanupTask.SecurityDescriptor + "(A;;FA;;;BU)";
        var folder = new FakeFolder(task);
        var error = Assert.ThrowsExactly<SecurityException>(() => RebootCleanupTask.CompleteRegistration(0x0004131C, false,
            () => Assert.Fail("警告の登録結果を成功として検証してはいけません。"),
            () => new RebootCleanupTask(Helper).RecoverCreatedTask(folder, RebootCleanupTask.TaskName(Sid), Sid, expires)));
        StringAssert.Contains(error.Message, "0x0004131C");
        StringAssert.Contains(error.Message, "回収できませんでした");
        Assert.IsTrue(task.Enabled);
        Assert.IsTrue(folder.Present);
        Assert.AreEqual(0, folder.Deleted);
    }

    public sealed class FakeFolder(FakeRegisteredTask task)
    {
        public bool Present { get; set; } = true;
        public int Deleted { get; private set; }
        public object GetTask(string name)
        {
            Assert.AreEqual(RebootCleanupTask.TaskName(Sid), name);
            return Present ? task : throw new COMException("not found", unchecked((int)0x80070002));
        }
        public void DeleteTask(string name, int flags)
        {
            Assert.AreEqual(RebootCleanupTask.TaskName(Sid), name);
            Assert.AreEqual(0, flags);
            Assert.IsFalse(task.Enabled);
            Present = false;
            Deleted++;
        }
    }

    public sealed class FakeRegisteredTask(string xml)
    {
        public string Xml => xml;
        public bool Enabled { get; set; } = true;
        public string Security { get; set; } = RebootCleanupTask.SecurityDescriptor;
        public object Definition => new FakeDefinition();
        public string GetSecurityDescriptor(int information) => Security;
    }
    public sealed class FakeDefinition
    {
        public object Principal => new FakePrincipal();
    }
    public sealed class FakePrincipal
    {
        public int LogonType => 2;
        public int RunLevel => 1;
        public string UserId => Sid;
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}
