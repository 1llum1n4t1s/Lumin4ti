using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>名前の再解決によるリンク追従を避け、開いた実体だけを列挙・変更する。</summary>
internal sealed class NativeCleanupEntry : IDisposable
{
    private const uint ObjCaseInsensitive = 0x40;
    private const uint ObjDontReparse = 0x1000;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint FileSynchronousIoNonAlert = 0x20;
    private const int FileFullDirectoryInfo = 14;
    private const int FileFullDirectoryRestartInfo = 15;
    private readonly List<SafeFileHandle> _ancestors = [];
    internal SafeFileHandle Handle { get; }
    internal string Path { get; }
    internal string NativePath { get; }

    private NativeCleanupEntry(string path, string nativePath, SafeFileHandle handle)
    {
        Path = path;
        NativePath = nativePath;
        Handle = handle;
    }

    internal static NativeCleanupEntry Open(string path, bool directory, bool delete = false, bool pinAncestors = true, bool security = false, bool readData = false)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        return OpenResolved(fullPath, ResolveNativePath(fullPath), directory, delete, pinAncestors, security, readData);
    }

    internal NativeCleanupEntry OpenDescendant(string path, bool directory, bool delete = false, bool pinAncestors = false, bool security = false, bool readData = false)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        if (!fullPath.Equals(Path, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(System.IO.Path.TrimEndingDirectorySeparator(Path) + "\\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("保持したルートの外側は開けません");
        return OpenResolved(fullPath, NativePath + fullPath[Path.Length..], directory, delete, pinAncestors, security, readData);
    }

    private static NativeCleanupEntry OpenResolved(string fullPath, string nativePath, bool directory, bool delete, bool pinAncestors, bool security, bool readData)
    {
        var ancestors = new List<SafeFileHandle>();
        try
        {
            if (pinAncestors)
            {
                var root = System.IO.Path.GetPathRoot(fullPath)!;
                var current = root;
                var relative = System.IO.Path.GetRelativePath(root, fullPath);
                var devicePrefix = nativePath[..(nativePath.Length - (fullPath.Length - 2))];
                var parts = relative.Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                for (var index = 0; index < parts.Length; index++)
                {
                    ancestors.Add(OpenHandle(devicePrefix + current[2..], true, false, false, security, false));
                    current = System.IO.Path.Combine(current, parts[index]);
                }
            }

            var entry = new NativeCleanupEntry(fullPath, nativePath, OpenHandle(nativePath, directory, delete, true, security, readData));
            entry._ancestors.AddRange(ancestors);
            ancestors.Clear();
            return entry;
        }
        finally
        {
            foreach (var ancestor in ancestors) ancestor.Dispose();
        }
    }

    private static SafeFileHandle OpenHandle(string nativePath, bool directory, bool delete, bool list, bool security, bool readData)
    {
        var text = Marshal.StringToHGlobalUni(nativePath);
        var unicode = new UnicodeString { Length = checked((ushort)(nativePath.Length * 2)), MaximumLength = checked((ushort)(nativePath.Length * 2 + 2)), Buffer = text };
        var unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            Marshal.StructureToPtr(unicode, unicodePointer, false);
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), ObjectName = unicodePointer, Attributes = ObjCaseInsensitive | ObjDontReparse };
            uint access = 0x80 | 0x100000; // READ_ATTRIBUTES | SYNCHRONIZE
            if (directory && list) access |= 1; // LIST_DIRECTORY
            if (readData) access |= 1; // FILE_READ_DATA
            if (security) access |= 0x20000; // READ_CONTROL
            // 予約検証の祖先は、再帰処理が既に保持する DELETE handle と共存させる。
            // 名前の固定は外側の走査 handle、予約時の信頼は全 owner/ACL の検証で担う。
            var share = readData ? 1u : security && !list ? 7u : 3u;
            // MAXIMUM_ALLOWED は不要な WRITE_DATA を取得し、読取共有との衝突を起こす。
            // 開けなかった場合だけ権限を減らし、成功した最終 handle を全処理で使う。
            uint[] attempts = delete
                ? directory ? [access | 0x10100, access | 0x10000, access] : [access | 0x10100, access | 0x10000]
                : [access];
            SafeFileHandle handle = null!;
            var status = 0;
            foreach (var desiredAccess in attempts)
            {
                status = NtCreateFile(out handle, desiredAccess, ref attributes, out _, 0, 0, share, 1,
                    FileOpenReparsePoint | FileSynchronousIoNonAlert | (directory ? 1u : 0x40u), 0, 0);
                if (status >= 0) break;
                handle?.Dispose();
                var error = RtlNtStatusToDosError(status);
                if (error != 5 && !(directory && error == 32)) break;
            }
            if (status < 0 || handle is null)
            {
                handle?.Dispose();
                throw new Win32Exception((int)RtlNtStatusToDosError(status));
            }

            try
            {
                if ((ReadAttributes(handle) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("リンク自身とリンク先を保護するため対象から除外しました");
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }
        finally { Marshal.FreeHGlobal(unicodePointer); Marshal.FreeHGlobal(text); }
    }

    internal static string ResolveNativePath(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
            throw new IOException("ローカルのドライブ絶対パスだけを扱います");
        var mapping = new System.Text.StringBuilder(32768);
        if (QueryDosDevice(path[..2], mapping, mapping.Capacity) == 0) ThrowLastError();
        return ToNativePath(path, mapping.ToString().Split('\0')[0]);
    }

    internal static string ToNativePath(string path, string mapping)
    {
        const string prefix = @"\Device\HarddiskVolume";
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            !mapping.StartsWith(prefix, StringComparison.Ordinal) || mapping.Length == prefix.Length ||
            mapping.AsSpan(prefix.Length).ContainsAnyExceptInRange('0', '9'))
            throw new IOException("確認できないドライブの割り当てを拒否しました");
        return mapping + path[2..];
    }

    internal FileAttributes Attributes => ReadAttributes(Handle);

    private static FileAttributes ReadAttributes(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 0, out BasicInfo info, (uint)Marshal.SizeOf<BasicInfo>())) ThrowLastError();
        return (FileAttributes)info.Attributes;
    }

    internal void EnsureNotReparse()
    {
        if ((Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("走査中にリンクへ変更されたため対象を保持しました");
    }

    internal IEnumerable<(string Name, FileAttributes Attributes)> Enumerate()
    {
        EnsureNotReparse();
        const int capacity = 65536;
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            var restart = true;
            while (true)
            {
                EnsureNotReparse();
                if (!GetDirectoryInformation(Handle, restart ? FileFullDirectoryRestartInfo : FileFullDirectoryInfo, buffer, capacity))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 18) break;
                    throw new Win32Exception(error);
                }
                restart = false;
                var offset = 0;
                while (true)
                {
                    if (offset < 0 || offset > capacity - 68) throw new IOException("ディレクトリ情報の範囲が不正です");
                    var record = buffer + offset;
                    var next = Marshal.ReadInt32(record);
                    var length = Marshal.ReadInt32(record, 60);
                    var end = next == 0 ? capacity - offset : next;
                    if (length <= 0 || (length & 1) != 0 || length > end - 68 ||
                        next < 0 || (next != 0 && (next < 68 || (next & 7) != 0 || next > capacity - offset)))
                        throw new IOException("ディレクトリ名の範囲が不正です");
                    var name = Marshal.PtrToStringUni(record + 68, length / 2)!;
                    if (name is not "." and not "..")
                    {
                        if (name.IndexOfAny(['\\', '/', ':', '\0']) >= 0) throw new IOException("単一ファイル名ではありません");
                        yield return (name, (FileAttributes)Marshal.ReadInt32(record, 56));
                    }
                    if (next == 0) break;
                    offset += next;
                }
            }
            EnsureNotReparse();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal long Length
    {
        get
        {
            if (!GetStandardInformation(Handle, 1, out var info, (uint)Marshal.SizeOf<StandardInfo>())) ThrowLastError();
            return info.EndOfFile;
        }
    }

    internal void SetAttributes(FileAttributes attributes)
    {
        EnsureNotReparse();
        var info = new BasicInfo { Attributes = (uint)(attributes == 0 ? FileAttributes.Normal : attributes) };
        if (!SetBasicInformation(Handle, 0, ref info, (uint)Marshal.SizeOf<BasicInfo>())) ThrowLastError();
    }

    internal void Delete()
    {
        EnsureNotReparse();
        var disposition = 1;
        if (!SetDispositionInformation(Handle, 4, ref disposition, 4)) ThrowLastError();
    }

    internal string ReadText(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        EnsureNotReparse();
        var length = Length;
        if (length < 0 || length > limit) throw new IOException("設定ファイルの読取上限を超えています。");
        var bytes = new byte[(int)length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = RandomAccess.Read(Handle, bytes.AsSpan(offset), offset);
            if (read == 0) throw new IOException("設定ファイルの読取中に内容が変わりました。");
            offset += read;
        }
        EnsureNotReparse();
        if (Length != length) throw new IOException("設定ファイルの長さが読取中に変わりました。");
        using var reader = new StreamReader(new MemoryStream(bytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal bool MatchesGlobalDosPath(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\') return false;
        EnsureNotReparse();
        using var verification = OpenScheduleIdentity(@"\\?\GLOBALROOT\GLOBAL??\" + path, 0x80, 7, 0, 3, 0x02200000, 0);
        if (verification.IsInvalid || (ReadAttributes(verification) & FileAttributes.ReparsePoint) != 0 ||
            !GetFinalName(verification, 2).Equals(NativePath, StringComparison.OrdinalIgnoreCase)) return false;
        return GetFileIdentity(Handle, 18, out var expected, 24) && GetFileIdentity(verification, 18, out var actual, 24) &&
               expected.VolumeSerialNumber == actual.VolumeSerialNumber && expected.FileIdLow == actual.FileIdLow && expected.FileIdHigh == actual.FileIdHigh;
    }

    private static string GetFinalName(SafeFileHandle handle, uint format)
    {
        var buffer = new System.Text.StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, format);
        if (length == 0) ThrowLastError();
        if (length >= buffer.Capacity) throw new IOException("取得した最終パスが長すぎます");
        return buffer.ToString();
    }

    private static void ThrowLastError() => throw new Win32Exception(Marshal.GetLastWin32Error());
    public void Dispose() { Handle.Dispose(); foreach (var handle in _ancestors.AsEnumerable().Reverse()) handle.Dispose(); }

    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length, MaximumLength; public nint Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes { public int Length; public nint RootDirectory, ObjectName; public uint Attributes; public nint SecurityDescriptor, SecurityQualityOfService; }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatusBlock { public nint Status, Information; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct StandardInfo { public long AllocationSize, EndOfFile; public uint Links; public byte DeletePending, Directory; }
    [StructLayout(LayoutKind.Sequential)] private struct FileIdentity { public ulong VolumeSerialNumber, FileIdLow, FileIdHigh; }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("ntdll.dll")] private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatusBlock status, nint allocationSize, uint fileAttributes, uint share, uint disposition, uint options, nint ea, uint eaLength);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out BasicInfo info, uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetStandardInformation(SafeFileHandle handle, int kind, out StandardInfo info, uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetDirectoryInformation(SafeFileHandle handle, int kind, nint buffer, int size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetBasicInformation(SafeFileHandle handle, int kind, ref BasicInfo info, uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetDispositionInformation(SafeFileHandle handle, int kind, ref int info, uint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint QueryDosDevice(string deviceName, System.Text.StringBuilder targetPath, int capacity);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, System.Text.StringBuilder path, uint capacity, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle OpenScheduleIdentity(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileIdentity(SafeFileHandle handle, int kind, out FileIdentity identity, uint size);
}
