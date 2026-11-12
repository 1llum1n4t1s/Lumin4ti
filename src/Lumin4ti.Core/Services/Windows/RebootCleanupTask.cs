using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Xml.Linq;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Core.Services.Windows;

internal interface IRebootCleanupTask
{
    void Ensure(string sid, DateTimeOffset expires);
    void RemoveIfOwned(string sid);
}

/// <summary>保護フォルダー内で、利用者 SID ごとの S4U・最高権限タスクを管理する。</summary>
internal sealed class RebootCleanupTask(string helperPath) : IRebootCleanupTask
{
    internal const string Folder = @"\Lumin4tiRebootCleanup";
    internal const string SecurityDescriptor = "O:BAG:BAD:P(A;;FA;;;BA)(A;;FA;;;SY)";
    private const string Source = "Lumin4ti.RebootCleanup.v1";

    // ITaskFolder::RegisterTask の TASK_CREATION フラグ (taskschd.h)。値は従来の数値リテラルと同一。
    private const int TaskCreate = 0x2;                 // TASK_CREATE: 新規登録
    private const int TaskUpdate = 0x4;                 // TASK_UPDATE: 既存定義の更新
    private const int TaskDontAddPrincipalAce = 0x10;   // TASK_DONT_ADD_PRINCIPAL_ACE: Principal への ACE を自動付与しない (DACL は SecurityDescriptor で明示する)

    internal static string TaskName(string sid)
    {
        RebootCleanupTargets.ValidateSid(sid);
        return "CacheCleanup-" + sid;
    }

    public void Ensure(string sid, DateTimeOffset expires)
    {
        var name = TaskName(sid);
        ValidateCallerSid(sid);
        if (!ScheduledTaskExecutableTrust.IsTrustedRebootCleanupExecutable(helperPath))
            throw new SecurityException("署名済みの正規配置の予約専用プログラムを確認できません。");
        WithFolder(true, folder =>
        {
            object? existing = null;
            object? registered = null;
            try
            {
                try { existing = ((dynamic)folder).GetTask(name); }
                catch (COMException ex) when (IsMissing(ex)) { }
                if (existing is not null) ValidateTask(existing, sid);
                var rawFolder = (ITaskFolderRaw)folder;
                var result = rawFolder.RegisterTask(name, BuildXml(helperPath, expires, sid), (existing is null ? TaskCreate : TaskUpdate) | TaskDontAddPrincipalAce,
                    sid, null, 2, SecurityDescriptor, out registered);
                CompleteRegistration(result, existing is not null,
                    () => ValidateTask(registered ?? throw new SecurityException("登録したタスクを取得できません。"), sid),
                    () =>
                    {
                        // GetTask が同じ COM identity を返しても、回収と finally で二重に解放しない。
                        Release(registered);
                        registered = null;
                        RecoverCreatedTask(folder, name, sid, expires);
                    });
            }
            finally { Release(registered); Release(existing); }
        });
    }

    internal static void CompleteRegistration(int result, bool updatingExisting, Action validate, Action recoverCreated)
    {
        try
        {
            if (result != 0)
            {
                var reason = result switch
                {
                    0x0004131B => "一部の起動トリガーを登録できませんでした",
                    0x0004131C => "利用者のバッチログオン権限を確認できませんでした",
                    _ => "タスク登録が正常完了しませんでした",
                };
                var updateUncertainty = updatingExisting
                    ? "既存タスクが更新された可能性があり、起動可否は未確認です。" : string.Empty;
                throw new COMException($"{reason} (HRESULT 0x{result:X8})。予約成功とは扱いません。{updateUncertainty}", result);
            }
            validate();
        }
        catch (Exception error) when (error is COMException or SecurityException)
        {
            // 既存タスクの更新後の起動可否は不明だが、旧 pending の経路を無条件には破棄しない。
            if (!updatingExisting)
            {
                try { recoverCreated(); }
                catch (Exception recovery) when (recovery is COMException or SecurityException)
                {
                    throw new SecurityException($"{error.Message} 未確認の登録タスクを回収できませんでした: {recovery.Message}", error);
                }
            }
            throw;
        }
    }

    internal void RecoverCreatedTask(object folder, string name, string sid, DateTimeOffset expires)
    {
        if (name != TaskName(sid)) throw new SecurityException("別の利用者の登録タスクは回収しません。");
        object? task = null;
        try
        {
            try { task = ((dynamic)folder).GetTask(name); }
            catch (COMException error) when (IsMissing(error)) { return; }
            ValidateCreatedTask(task, sid, expires);
            ((dynamic)task).Enabled = false;
            // 無効化後にも同じ保護された定義を確認してから、今回の固定名だけを削除する。
            ValidateCreatedTask(task, sid, expires);
            ((dynamic)folder).DeleteTask(name, 0);
        }
        finally { Release(task); }
    }

    private void ValidateCreatedTask(object task, string sid, DateTimeOffset expires)
    {
        ValidateTask(task, sid);
        var xml = XDocument.Parse((string)((dynamic)task).Xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        if (!DateTimeOffset.TryParse(xml.Root?.Element(ns + "Triggers")?.Element(ns + "BootTrigger")?.Element(ns + "EndBoundary")?.Value,
                out var actualExpiry) || actualExpiry != expires)
            throw new SecurityException("今回登録した期限のタスクではないため回収しません。");
    }

    internal static (string Name, string Path, string Security) ReadRawFolderIdentity(object folder)
    {
        var raw = (ITaskFolderRaw)folder;
        Marshal.ThrowExceptionForHR(raw.GetName(out var name));
        Marshal.ThrowExceptionForHR(raw.GetPath(out var path));
        Marshal.ThrowExceptionForHR(raw.GetSecurityDescriptor(7, out var security));
        return (name, path, security);
    }

    public void RemoveIfOwned(string sid)
    {
        var name = TaskName(sid);
        ValidateCallerSid(sid);
        WithFolder(false, folder =>
        {
            object? task = null;
            try
            {
                try { task = ((dynamic)folder).GetTask(name); }
                catch (COMException ex) when (IsMissing(ex)) { return; }
                ValidateTask(task, sid);
                ((dynamic)folder).DeleteTask(name, 0);
            }
            finally { Release(task); }
        });
    }

    private void ValidateTask(object task, string sid)
    {
        if (!IsProtectedDescriptor(((dynamic)task).GetSecurityDescriptor(7)) ||
            !IsOwnedDefinition(((dynamic)task).Xml, helperPath, sid))
            throw new SecurityException("既存の予約タスクの所有者または定義を確認できないため変更しません。");
        object? definition = null;
        object? principal = null;
        try
        {
            definition = ((dynamic)task).Definition;
            principal = ((dynamic)definition).Principal;
            if ((int)((dynamic)principal).LogonType != 2 || (int)((dynamic)principal).RunLevel != 1 ||
                !IsPrincipalSid((string)((dynamic)principal).UserId, sid))
                throw new SecurityException("利用者 SID の S4U・最高権限の実効定義ではありません。");
        }
        finally { Release(principal); Release(definition); }
    }

    private static void ValidateCallerSid(string sid)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != sid)
            throw new SecurityException("別の利用者 SID の予約タスクは変更しません。");
    }

    internal static bool IsPrincipalSid(string userId, string sid)
    {
        try
        {
            // Task Scheduler の Principal.UserId は、XML に SID を指定してもアカウント名を返す。
            var actual = userId.StartsWith("S-", StringComparison.OrdinalIgnoreCase)
                ? new SecurityIdentifier(userId)
                : (SecurityIdentifier)new NTAccount(userId).Translate(typeof(SecurityIdentifier));
            return actual.Value == sid;
        }
        catch (Exception ex) when (ex is ArgumentException or IdentityNotMappedException or SecurityException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private static void WithFolder(bool create, Action<object> action)
    {
        object? service = null;
        object? root = null;
        object? folder = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
            service = Activator.CreateInstance(type)!;
            ((dynamic)service).Connect();
            root = ((dynamic)service).GetFolder(@"\");
            try { folder = ((dynamic)service).GetFolder(Folder); }
            catch (COMException ex) when (IsMissing(ex))
            {
                if (!create) return;
                folder = ((dynamic)root).CreateFolder(Folder.TrimStart('\\'), SecurityDescriptor);
            }
            if (!IsProtectedDescriptor(((dynamic)folder).GetSecurityDescriptor(7)))
                throw new SecurityException("予約タスク用フォルダーが Administrators / SYSTEM の保護下にありません。");
            action(folder);
        }
        finally { Release(folder); Release(root); Release(service); }
    }

    internal static bool IsProtectedDescriptor(string sddl)
    {
        try
        {
            var raw = new RawSecurityDescriptor(sddl);
            if (raw.Owner?.Value is not ("S-1-5-18" or "S-1-5-32-544") ||
                (raw.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 || raw.DiscretionaryAcl is null) return false;
            var administrators = false;
            var system = false;
            foreach (GenericAce ace in raw.DiscretionaryAcl)
            {
                if (ace is not CommonAce { IsCallback: false, AceQualifier: AceQualifier.AccessAllowed } common ||
                    common.AceFlags != AceFlags.None || common.SecurityIdentifier.Value is not ("S-1-5-18" or "S-1-5-32-544") ||
                    common.AccessMask != (int)FileSystemRights.FullControl) return false;
                administrators |= common.SecurityIdentifier.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
                system |= common.SecurityIdentifier.IsWellKnown(WellKnownSidType.LocalSystemSid);
            }
            return administrators && system;
        }
        catch (ArgumentException) { return false; }
    }

    internal static bool IsOwnedDefinition(string xml, string path, string sid)
    {
        try
        {
            RebootCleanupTargets.ValidateSid(sid);
            var root = XDocument.Parse(xml).Root!;
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            if (root.Name != ns + "Task") return false;
            string? Value(XElement? element, string name) => element?.Element(ns + name)?.Value;
            var principals = root.Element(ns + "Principals")?.Elements().ToArray();
            var triggers = root.Element(ns + "Triggers")?.Elements().ToArray();
            var actions = root.Element(ns + "Actions")?.Elements().ToArray();
            if (principals is not [{ Name.LocalName: "Principal" } principal] ||
                triggers is not [{ Name.LocalName: "BootTrigger" } trigger] ||
                actions is not [{ Name.LocalName: "Exec" } executable]) return false;
            var settings = root.Element(ns + "Settings");
            return Value(root.Element(ns + "RegistrationInfo"), "Source") == Source &&
                   Value(principal, "UserId") == sid && Value(principal, "LogonType") == "S4U" &&
                   (string?)principal.Attribute("id") == "Owner" && (string?)root.Element(ns + "Actions")?.Attribute("Context") == "Owner" &&
                   Value(principal, "RunLevel") == "HighestAvailable" && Value(executable, "Command") == path &&
                   string.IsNullOrWhiteSpace(Value(executable, "Arguments")) && Value(executable, "WorkingDirectory") == Path.GetDirectoryName(path) &&
                   Value(trigger, "Enabled") == "true" && Value(trigger, "Delay") is null or "PT0S" &&
                   DateTimeOffset.TryParse(Value(trigger, "EndBoundary"), out _) &&
                   Value(settings, "MultipleInstancesPolicy") == "IgnoreNew" && Value(settings, "ExecutionTimeLimit") == "PT0S" &&
                   Value(settings, "AllowHardTerminate") == "false" &&
                   Value(settings, "DeleteExpiredTaskAfter") == "PT1M" && Value(settings, "AllowStartOnDemand") == "false" &&
                   Value(settings, "StartWhenAvailable") == "false" && Value(settings, "DisallowStartIfOnBatteries") == "false" &&
                   Value(settings, "StopIfGoingOnBatteries") == "false" && settings?.Element(ns + "RestartOnFailure") is null;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or ArgumentException or NullReferenceException or SecurityException) { return false; }
    }

    internal static string BuildXml(string path, DateTimeOffset expires, string sid)
    {
        RebootCleanupTargets.ValidateSid(sid);
        // 寿命は helper の取消と有限のサービス待機で管理し、SCM 復帰の finally を scheduler に強制終了させない。
        // EndBoundary は新規起動の期限、DeleteExpiredTaskAfter は登録の後始末として維持する。
        return $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Source>{Source}</Source></RegistrationInfo>
          <Triggers><BootTrigger><EndBoundary>{expires.UtcDateTime:O}</EndBoundary><Enabled>true</Enabled></BootTrigger></Triggers>
          <Principals><Principal id="Owner"><UserId>{sid}</UserId><LogonType>S4U</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
          <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>false</AllowHardTerminate><StartWhenAvailable>false</StartWhenAvailable><AllowStartOnDemand>false</AllowStartOnDemand><Enabled>true</Enabled><Hidden>true</Hidden><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><DeleteExpiredTaskAfter>PT1M</DeleteExpiredTaskAfter></Settings>
          <Actions Context="Owner"><Exec><Command>{SecurityElement.Escape(path)}</Command><WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(path))}</WorkingDirectory></Exec></Actions>
        </Task>
        """;
    }

    private static bool IsMissing(COMException ex) => ex.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003);
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}

// Windows SDK taskschd.h の ITaskFolder (IUnknown + IDispatch 4 メソッド + 宣言順)。
// RegisterTask の成功 HRESULT 警告も保持するため、IDispatch.Invoke には委ねない。
[ComImport]
[Guid("8cfac062-a080-4c15-9a88-aa7c2af80dfc")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITaskFolderRaw
{
    [PreserveSig] int GetTypeInfoCount(out uint count);
    [PreserveSig] int GetTypeInfo(uint index, uint locale, out IntPtr typeInfo);
    [PreserveSig] int GetIdsOfNames(ref Guid interfaceId, IntPtr names, uint count, uint locale, IntPtr dispatchIds);
    [PreserveSig] int Invoke(int dispatchId, ref Guid interfaceId, uint locale, ushort flags,
        IntPtr parameters, IntPtr result, IntPtr exception, IntPtr argumentError);
    [PreserveSig] int GetName([MarshalAs(UnmanagedType.BStr)] out string name);
    [PreserveSig] int GetPath([MarshalAs(UnmanagedType.BStr)] out string path);
    [PreserveSig] int GetFolder([MarshalAs(UnmanagedType.BStr)] string path, [MarshalAs(UnmanagedType.Interface)] out object folder);
    [PreserveSig] int GetFolders(int flags, [MarshalAs(UnmanagedType.Interface)] out object folders);
    [PreserveSig] int CreateFolder([MarshalAs(UnmanagedType.BStr)] string name, [MarshalAs(UnmanagedType.Struct)] object? security,
        [MarshalAs(UnmanagedType.Interface)] out object folder);
    [PreserveSig] int DeleteFolder([MarshalAs(UnmanagedType.BStr)] string name, int flags);
    [PreserveSig] int GetTask([MarshalAs(UnmanagedType.BStr)] string path, [MarshalAs(UnmanagedType.Interface)] out object task);
    [PreserveSig] int GetTasks(int flags, [MarshalAs(UnmanagedType.Interface)] out object tasks);
    [PreserveSig] int DeleteTask([MarshalAs(UnmanagedType.BStr)] string name, int flags);
    [PreserveSig] int RegisterTask([MarshalAs(UnmanagedType.BStr)] string path, [MarshalAs(UnmanagedType.BStr)] string xml,
        int flags, [MarshalAs(UnmanagedType.Struct)] object? userId, [MarshalAs(UnmanagedType.Struct)] object? password,
        int logonType, [MarshalAs(UnmanagedType.Struct)] object? security, [MarshalAs(UnmanagedType.Interface)] out object? task);
    [PreserveSig] int RegisterTaskDefinition([MarshalAs(UnmanagedType.BStr)] string path, [MarshalAs(UnmanagedType.Interface)] object definition,
        int flags, [MarshalAs(UnmanagedType.Struct)] object? userId, [MarshalAs(UnmanagedType.Struct)] object? password,
        int logonType, [MarshalAs(UnmanagedType.Struct)] object? security, [MarshalAs(UnmanagedType.Interface)] out object? task);
    [PreserveSig] int GetSecurityDescriptor(int securityInformation, [MarshalAs(UnmanagedType.BStr)] out string security);
    [PreserveSig] int SetSecurityDescriptor([MarshalAs(UnmanagedType.BStr)] string security, int flags);
}
