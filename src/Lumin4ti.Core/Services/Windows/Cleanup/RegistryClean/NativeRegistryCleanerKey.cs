using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;

/// <summary>実レジストリのルートを開く工場。</summary>
internal sealed class NativeRegistryCleanerKeyFactory : IRegistryCleanerKeyFactory
{
    public IRegistryCleanerKey OpenLocalMachine(RegistryCleanView view) =>
        NativeRegistryCleanerKey.OpenRoot(NativeRegistryCleanerKey.HkeyLocalMachine, string.Empty, view);

    public IRegistryCleanerKey OpenUserHive(string sid, RegistryCleanView view)
    {
        var parsed = new SecurityIdentifier(sid);
        return NativeRegistryCleanerKey.OpenRoot(NativeRegistryCleanerKey.HkeyUsers, parsed.Value, view);
    }
}

/// <summary>
/// 実レジストリのキー。各階層を <c>REG_OPTION_OPEN_LINK</c> で開いてリンクを拒否し、削除は開いたハンドルに対して行う
/// (パスを再解決しないので、途中でリンクに差し替えられても別のキーへ作用しない)。単体テストはしない (偽の実装で代替)。
/// </summary>
internal sealed class NativeRegistryCleanerKey : IRegistryCleanerKey
{
    internal static readonly nint HkeyLocalMachine = new(unchecked((int)0x80000002));
    internal static readonly nint HkeyUsers = new(unchecked((int)0x80000003));

    private const uint OpenLink = 0x8;
    private const uint KeyRead = 0x20019;
    private const uint KeySetValue = 0x2;
    private const uint KeyCreateSubKey = 0x4;
    private const uint Delete = 0x10000;
    private const uint Wow64Key64 = 0x100;
    private const uint Wow64Key32 = 0x200;
    private const uint RegLink = 6;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorMoreData = 234;
    private const int ErrorNoMoreItems = 259;
    private const int StatusBufferOverflow = unchecked((int)0x80000005);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);
    private const int KeyNameInformation = 3;
    private const int SeRegistryKey = 4;
    private const uint OwnerSecurityInformation = 1;
    private const int MaxDepth = 64;
    private const int MaxValueBytes = 16 * 1024 * 1024;
    private readonly SafeRegistryHandle _handle;
    private readonly RegistryCleanView _view;

    private NativeRegistryCleanerKey(SafeRegistryHandle handle, RegistryCleanView view)
    {
        _handle = handle;
        _view = view;
    }

    /// <summary>ハイブのルート (<paramref name="subPath"/> が空なら HKLM 自身) を開く。</summary>
    internal static NativeRegistryCleanerKey OpenRoot(nint hive, string subPath, RegistryCleanView view)
    {
        using var root = new SafeRegistryHandle(hive, ownsHandle: false);
        var error = RegOpenKeyEx(root, subPath, OpenLink, KeyRead | ViewFlag(view), out var handle);
        if (error != 0) throw new Win32Exception(error);
        return Wrap(handle, view, subPath);
    }

    public IRegistryCleanerKey? OpenChild(string name, RegistryCleanAccess access, RegistryCleanView view) => OpenNative(name, access, view);

    private NativeRegistryCleanerKey? OpenNative(string name, RegistryCleanAccess access, RegistryCleanView view)
    {
        CheckName(name);
        var error = RegOpenKeyEx(_handle, name, OpenLink, Sam(access) | ViewFlag(view), out var handle);
        if (error is ErrorFileNotFound or ErrorPathNotFound) return null;
        if (error != 0) throw new Win32Exception(error);
        return Wrap(handle, view, name);
    }

    public IRegistryCleanerKey CreateChild(string name, RegistryCleanView view)
    {
        CheckName(name);
        var error = RegCreateKeyEx(_handle, name, 0, null, OpenLink, Sam(RegistryCleanAccess.DeleteTree) | ViewFlag(view), 0, out var handle, out _);
        if (error != 0) throw new Win32Exception(error);
        return Wrap(handle, view, name);
    }

    public RegistryRawValue? GetValue(string name)
    {
        uint size = 0;
        var error = RegQueryValueEx(_handle, name, 0, out var type, null, ref size);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (error is ErrorFileNotFound) return null;
            if (error is not (0 or ErrorMoreData)) throw new Win32Exception(error);
            if (size > MaxValueBytes) throw new IOException($"registry value too large: {name}");
            var buffer = new byte[size];
            var capacity = size;
            error = RegQueryValueEx(_handle, name, 0, out type, buffer, ref capacity);
            if (error == 0) return new RegistryRawValue(type, buffer[..(int)capacity]);
            if (error is ErrorMoreData) size = Math.Max(size, capacity);
        }

        throw new IOException($"registry value too large: {name}");
    }

    public void SetValue(string name, RegistryRawValue value)
    {
        var error = RegSetValueEx(_handle, name, 0, value.Type, value.Data, (uint)value.Data.Length);
        if (error != 0) throw new Win32Exception(error);
    }

    public IReadOnlyList<string> GetSubKeyNames() => Enumerate(255 + 1, (index, buffer, size) => RegEnumKeyEx(_handle, index, buffer, ref size, 0, 0, 0, 0));

    public IReadOnlyList<string> GetValueNames() => Enumerate(16383 + 1, (index, buffer, size) => RegEnumValue(_handle, index, buffer, ref size, 0, 0, 0, 0));

    public void DeleteValue(string name)
    {
        var error = RegDeleteValue(_handle, name);
        if (error is not (0 or ErrorFileNotFound)) throw new Win32Exception(error);
    }

    public void DeleteSelf() => DeleteTree(this, 0);

    public RegistryKeyInfo QueryInfo()
    {
        var error = RegQueryInfoKey(_handle, 0, 0, 0, out var subKeys, 0, 0, out var values, 0, 0, 0, out var lastWrite);
        if (error != 0) throw new Win32Exception(error);
        return new RegistryKeyInfo(DateTime.FromFileTimeUtc(lastWrite), (int)subKeys, (int)values);
    }

    public string KernelName()
    {
        var buffer = new byte[512];
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var status = NtQueryKey(_handle, KeyNameInformation, buffer, (uint)buffer.Length, out var needed);
            if (status >= 0)
            {
                var nameBytes = BitConverter.ToInt32(buffer, 0);
                return Encoding.Unicode.GetString(buffer, sizeof(int), nameBytes);
            }

            if (status is not (StatusBufferOverflow or StatusBufferTooSmall)) throw new Win32Exception((int)RtlNtStatusToDosError(status));
            buffer = new byte[Math.Max(needed, (uint)buffer.Length * 2)];
        }

        throw new IOException("registry kernel name too long");
    }

    public string OwnerSid()
    {
        var error = GetSecurityInfo(_handle, SeRegistryKey, OwnerSecurityInformation, out var owner, 0, 0, 0, out var descriptor);
        if (error != 0) throw new Win32Exception((int)error);
        try { return new SecurityIdentifier(owner).Value; }
        finally { LocalFree(descriptor); }
    }

    public void Dispose() => _handle.Dispose();

    /// <summary>
    /// 先に子孫をすべて削除用の権限で開き (各階層 OPEN_LINK・リンク拒否)、1 つでも開けなければ何も消さずに例外を返す。
    /// 開けたら、そのハンドルのまま子から順に消し、最後に自分を消す。削除中の失敗はそのまま例外にする。
    /// </summary>
    private static void DeleteTree(NativeRegistryCleanerKey key, int depth)
    {
        var descendants = new List<NativeRegistryCleanerKey>();
        try
        {
            OpenSubtree(key, depth, descendants);
            foreach (var descendant in descendants) DeleteOpened(descendant);
            DeleteOpened(key);
        }
        finally
        {
            foreach (var descendant in descendants) descendant.Dispose();
        }
    }

    /// <summary>子孫を <see cref="RegistryCleanAccess.DeleteTree"/> で開き、子が親より前に来る順 (後順) で積む。</summary>
    private static void OpenSubtree(NativeRegistryCleanerKey key, int depth, List<NativeRegistryCleanerKey> opened)
    {
        if (depth >= MaxDepth) throw new IOException(CleanupMessage.Of("RegistryDepthLimit"));
        foreach (var name in key.GetSubKeyNames())
        {
            var child = key.OpenNative(name, RegistryCleanAccess.DeleteTree, key._view);
            if (child is null) continue;
            try
            {
                OpenSubtree(child, depth + 1, opened);
            }
            catch
            {
                child.Dispose();
                throw;
            }

            opened.Add(child);
        }
    }

    private static void DeleteOpened(NativeRegistryCleanerKey key)
    {
        var status = NtDeleteKey(key._handle);
        if (status < 0) throw new Win32Exception((int)RtlNtStatusToDosError(status));
    }

    private static NativeRegistryCleanerKey Wrap(SafeRegistryHandle handle, RegistryCleanView view, string name)
    {
        try
        {
            if (IsLink(handle)) throw new RegistryCleanLinkException(name);
            return new NativeRegistryCleanerKey(handle, view);
        }
        catch { handle.Dispose(); throw; }
    }

    private static void CheckName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('\\')) throw new ArgumentException("1 階層の名前だけを指定できます", nameof(name));
    }

    private static uint ViewFlag(RegistryCleanView view) => view == RegistryCleanView.Registry32 ? Wow64Key32 : Wow64Key64;

    private static uint Sam(RegistryCleanAccess access) => access switch
    {
        RegistryCleanAccess.Read => KeyRead,
        RegistryCleanAccess.ModifyValues => KeyRead | KeySetValue,
        _ => KeyRead | KeySetValue | KeyCreateSubKey | Delete,
    };

    private static List<string> Enumerate(int capacity, EnumerateCall call)
    {
        var names = new List<string>();
        var buffer = new StringBuilder(capacity);
        for (uint index = 0; ; index++)
        {
            buffer.Clear();
            var error = call(index, buffer, (uint)capacity);
            if (error == ErrorNoMoreItems) return names;
            if (error != 0) throw new Win32Exception(error);
            names.Add(buffer.ToString());
        }
    }

    /// <summary>リンクのキーは REG_LINK 型の <c>SymbolicLinkValue</c> を持つ (OPEN_LINK で開いたハンドルで確認できる)。</summary>
    private static bool IsLink(SafeRegistryHandle handle) =>
        RegQueryValueEx(handle, "SymbolicLinkValue", 0, out var type, 0, 0) == 0 && type == RegLink;

    private delegate int EnumerateCall(uint index, StringBuilder buffer, uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyEx(SafeRegistryHandle key, string subKey, uint options, uint access, out SafeRegistryHandle result);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyEx(SafeRegistryHandle key, string subKey, uint reserved, string? cls, uint options, uint access, nint security, out SafeRegistryHandle result, out uint disposition);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", EntryPoint = "RegEnumKeyExW", CharSet = CharSet.Unicode)]
    private static extern int RegEnumKeyEx(SafeRegistryHandle key, uint index, StringBuilder name, ref uint size, nint reserved, nint cls, nint classSize, nint lastWrite);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", EntryPoint = "RegEnumValueW", CharSet = CharSet.Unicode)]
    private static extern int RegEnumValue(SafeRegistryHandle key, uint index, StringBuilder name, ref uint size, nint reserved, nint type, nint data, nint dataSize);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueEx(SafeRegistryHandle key, string name, nint reserved, out uint type, nint data, nint dataSize);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueEx(SafeRegistryHandle key, string name, nint reserved, out uint type, byte[]? data, ref uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", EntryPoint = "RegSetValueExW", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueEx(SafeRegistryHandle key, string name, uint reserved, uint type, byte[] data, uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", EntryPoint = "RegDeleteValueW", CharSet = CharSet.Unicode)]
    private static extern int RegDeleteValue(SafeRegistryHandle key, string name);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW", CharSet = CharSet.Unicode)]
    private static extern int RegQueryInfoKey(SafeRegistryHandle key, nint cls, nint classSize, nint reserved, out uint subKeys, nint maxSubKeyLen, nint maxClassLen, out uint values, nint maxValueNameLen, nint maxValueLen, nint securityDescriptor, out long lastWrite);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeRegistryHandle handle, int objectType, uint securityInfo, out nint owner, nint group, nint dacl, nint sacl, out nint descriptor);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ntdll.dll")]
    private static extern int NtQueryKey(SafeRegistryHandle key, int informationClass, byte[] buffer, uint length, out uint resultLength);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ntdll.dll")]
    private static extern int NtDeleteKey(SafeRegistryHandle key);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
