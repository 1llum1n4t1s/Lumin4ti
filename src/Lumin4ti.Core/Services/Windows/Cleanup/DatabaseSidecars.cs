using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>DB のジャーナル方式。ヘッダー (オフセット 18・19) で決まる。</summary>
internal enum DatabaseJournalMode
{
    /// <summary>ロールバックジャーナル。<c>-journal</c> を固定し、journal_mode=TRUNCATE で使う。</summary>
    Rollback,

    /// <summary>WAL。<c>-wal</c> を固定し、EXCLUSIVE ロックで <c>-shm</c> を使わせない。</summary>
    Wal,
}

/// <summary>
/// SQLite が名前で開く <c>-journal</c> / <c>-wal</c> を、検査から操作の終了まで 1 つのハンドルで固定する。
/// 再解析点 (自分自身を開く)・ディレクトリ・ハードリンクは拒否し、他者による削除・改名・置換 (FILE_SHARE_DELETE) を許さない。
/// 無ければ自分で作成し、終了時に空のままなら実体の照合後に削除する。共有は READ|WRITE だけ (SQLite の共有に合わせる)。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SidecarPin : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint Delete = 0x00010000;
    private const uint ReadAttributes = 0x80;
    private const uint ShareReadWrite = 0x3;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int AlreadyExists = 183;

    private SafeFileHandle? _handle;

    /// <summary>この呼び出しで作成した (終了時に空なら削除する)。</summary>
    internal bool Created { get; private init; }

    internal string Path { get; }

    private SidecarPin(string path, SafeFileHandle handle)
    {
        Path = path;
        _handle = handle;
    }

    /// <summary>
    /// 固定して返す。再解析点・ディレクトリ・ハードリンクは <see cref="IOException"/>・<see cref="DatabaseRefusedException"/> で拒否し、
    /// 中身がある (使用中または未回復) なら無変更で <see cref="DatabaseRefusedException"/> にする。
    /// </summary>
    internal static SidecarPin OpenOrCreate(string path)
    {
        var handle = CreateFile(ToDevicePath(path), GenericRead | GenericWrite, ShareReadWrite, 0, OpenAlways, FileFlagOpenReparsePoint, 0);
        var error = Marshal.GetLastWin32Error();
        if (handle.IsInvalid) throw new Win32Exception(error);
        var pin = new SidecarPin(path, handle) { Created = error != AlreadyExists };
        try
        {
            pin.Validate();
            return pin;
        }
        catch
        {
            pin.Discard();
            throw;
        }
    }

    private void Validate()
    {
        var handle = Handle;
        if (!GetBasic(handle, 0, out Basic basic, (uint)Marshal.SizeOf<Basic>())) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((basic.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("サイドカーファイルが再解析点またはディレクトリです");
        if (!GetStandard(handle, 1, out Standard standard, (uint)Marshal.SizeOf<Standard>())) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (standard.Links > 1) throw new DatabaseRefusedException(CleanupMessage.Of("DbHardLinked"));
        if (standard.EndOfFile > 0) throw new DatabaseRefusedException(CleanupMessage.Of("DbJournalPending"));
    }

    private SafeFileHandle Handle => _handle ?? throw new ObjectDisposedException(nameof(SidecarPin));

    /// <summary>開いた実体の現在の長さ。</summary>
    internal long Length
    {
        get
        {
            if (!GetStandard(Handle, 1, out Standard standard, (uint)Marshal.SizeOf<Standard>())) throw new Win32Exception(Marshal.GetLastWin32Error());
            return standard.EndOfFile;
        }
    }

    /// <summary>
    /// SQLite が閉じた後に呼ぶ。自分が作って空のままなら削除する。SQLite は FILE_SHARE_DELETE なしで開くため、
    /// 固定ハンドルに DELETE 権限を持たせられない。そこで固定を解いた直後に別ハンドルを再解析点として開き直し、
    /// 実体の ID・非リンク・リンク数 1・長さ 0 を固定時の値と照合してから、そのハンドルで削除する (名前では消さない)。
    /// 失敗しても空ファイルが残るだけで無害。
    /// </summary>
    internal void Finish()
    {
        try
        {
            if (Created && _handle is not null && Length == 0)
            {
                var identity = ReadIdentity(_handle);
                Dispose();
                DeleteIfSame(Path, identity);
            }
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or UnauthorizedAccessException or ObjectDisposedException)
        {
            // 削除できなくても、空のサイドカーは SQLite にもブラウザーにも無害。
        }
        finally { Dispose(); }
    }

    private static FileId ReadIdentity(SafeFileHandle handle)
    {
        if (!GetId(handle, 18, out FileId id, (uint)Marshal.SizeOf<FileId>())) throw new Win32Exception(Marshal.GetLastWin32Error());
        return id;
    }

    private static void DeleteIfSame(string path, FileId expected)
    {
        using var handle = CreateFile(ToDevicePath(path), Delete | ReadAttributes, ShareAll, 0, OpenExisting, FileFlagOpenReparsePoint, 0);
        if (handle.IsInvalid) return;
        if (!GetBasic(handle, 0, out Basic basic, (uint)Marshal.SizeOf<Basic>()) ||
            (basic.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return;
        if (!GetStandard(handle, 1, out Standard standard, (uint)Marshal.SizeOf<Standard>()) || standard.Links != 1 || standard.EndOfFile != 0) return;
        var actual = ReadIdentity(handle);
        if (actual.Volume != expected.Volume || actual.Low != expected.Low || actual.High != expected.High) return;
        var disposition = 1;
        SetDisposition(handle, 4, ref disposition, 4);
    }

    /// <summary>検証に失敗した固定の後始末。作成した空ファイルだけを消す。</summary>
    private void Discard()
    {
        if (!Created) { Dispose(); return; }
        Finish();
    }

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
    }

    private static string ToDevicePath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + System.IO.Path.GetFullPath(path);

    [StructLayout(LayoutKind.Sequential)] private struct Basic { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct Standard { public long AllocationSize, EndOfFile; public uint Links; public byte DeletePending, Directory; }
    [StructLayout(LayoutKind.Sequential)] private struct FileId { public ulong Volume, Low, High; }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetId(SafeFileHandle handle, int kind, out FileId info, uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetBasic(SafeFileHandle handle, int kind, out Basic info, uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetStandard(SafeFileHandle handle, int kind, out Standard info, uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDisposition(SafeFileHandle handle, int kind, ref int info, uint size);
}

/// <summary>1 回の変更で固定するサイドカーの集合。SQLite の接続を閉じた後に <see cref="Finish"/> を呼ぶ。</summary>
[SupportedOSPlatform("windows")]
internal sealed class DatabaseSidecars : IDisposable
{
    private readonly List<SidecarPin> _pins = [];

    /// <summary>ヘッダーからジャーナル方式を読む。SQLite の DB でなければ <see cref="DatabaseRefusedException"/>。</summary>
    internal static DatabaseJournalMode ReadMode(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[100];
        if (RandomAccess.Read(handle, header, 0) < 100 || !header[..16].SequenceEqual("SQLite format 3\0"u8))
            throw new DatabaseRefusedException(CleanupMessage.Of("DbIntegrityFailed"));
        return header[18] == 2 || header[19] == 2 ? DatabaseJournalMode.Wal : DatabaseJournalMode.Rollback;
    }

    /// <summary>
    /// 方式によらず <c>-journal</c> と <c>-wal</c> の両方を固定する (どちらも空でなければ拒否。空の <c>-wal</c> は SQLite が「無い」と扱う)。
    /// ヘッダーの書換えで方式が食い違っても、SQLite が開く名前はどちらも固定済みで、方式の食い違いは <see cref="GuardedDatabase"/> が書込み前に拒否する。
    /// <c>-shm</c> は EXCLUSIVE ロックで使われないが、再解析点は拒否する。
    /// </summary>
    internal static DatabaseSidecars Acquire(string databasePath)
    {
        var sidecars = new DatabaseSidecars();
        try
        {
            sidecars._pins.Add(SidecarPin.OpenOrCreate(databasePath + "-journal"));
            sidecars._pins.Add(SidecarPin.OpenOrCreate(databasePath + "-wal"));
            Inspect(databasePath + "-shm", requireEmpty: false);
            return sidecars;
        }
        catch
        {
            sidecars.Finish();
            throw;
        }
    }

    /// <summary>既存のサイドカーを開いて、再解析点・ディレクトリ・(必要なら) 中身を確かめる。固定はしない。</summary>
    internal static void Inspect(string sidecar, bool requireEmpty)
    {
        if (!Path.Exists(sidecar)) return;
        using var entry = Actions.NativeCleanupEntry.Open(sidecar, directory: false, pinAncestors: false);
        if (requireEmpty && entry.Length > 0) throw new DatabaseRefusedException(CleanupMessage.Of("DbJournalPending"));
    }

    internal void Finish()
    {
        foreach (var pin in _pins) pin.Finish();
        _pins.Clear();
    }

    public void Dispose() => Finish();
}
