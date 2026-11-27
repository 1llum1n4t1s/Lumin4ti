using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Core.Services.Windows.Cleanup.Slim;

/// <summary>Windows の保護状態。<see cref="Unprotected"/> だけが削除候補になる。</summary>
public enum FileProtectionState
{
    /// <summary>Windows リソース保護の対象でなく、TrustedInstaller 所有でもない。</summary>
    Unprotected,

    /// <summary>SfcIsFileProtected が保護と答えた。</summary>
    WindowsResourceProtection,

    /// <summary>所有者が TrustedInstaller (所有権を奪わないため対象にしない)。</summary>
    TrustedInstallerOwned,

    /// <summary>保護状態または所有者を確認できなかった (安全側で対象にしない)。</summary>
    Unknown,
}

/// <summary>開いたファイルの保護状態を調べる境界。</summary>
internal interface IProtectedFileProbe
{
    FileProtectionState Probe(NativeCleanupEntry file);
}

/// <summary>保護状態の走査結果。何も変更していない。</summary>
public sealed class ProtectedFileScan
{
    /// <summary>削除候補 (保護されておらず TrustedInstaller 所有でないファイル)。</summary>
    public IReadOnlyList<CleanupFileIdentity> Candidates { get; internal set; } = [];

    /// <summary>候補の合計サイズ。</summary>
    public long CandidateBytes { get; internal set; }

    /// <summary>Windows リソース保護のため対象外にした数。</summary>
    public long ProtectedCount { get; internal set; }

    /// <summary>TrustedInstaller 所有のため対象外にした数。</summary>
    public long TrustedInstallerCount { get; internal set; }

    /// <summary>保護状態を確認できず対象外にした数。</summary>
    public long UnknownCount { get; internal set; }

    /// <summary>列挙・取得に失敗して調べられなかった数。</summary>
    public long Unreadable { get; internal set; }

    /// <summary>存在しなかった基点フォルダーの数。</summary>
    public int MissingRoots { get; internal set; }

    /// <summary>安全ガードで拒否した基点フォルダーの数。</summary>
    public int RejectedRoots { get; internal set; }

    /// <summary>候補が 0 件で、Windows が保護しているために対象なしになった。</summary>
    public bool NothingBecauseProtected => Candidates.Count == 0 && (ProtectedCount > 0 || TrustedInstallerCount > 0);

    /// <summary>走査を終えた時刻 (UTC)。</summary>
    public DateTime CompletedAtUtc { get; internal set; }
}

/// <summary>
/// Windows 同梱のヘルプ・壁紙などのうち、Windows リソース保護 (SfcIsFileProtected) の対象でなく
/// TrustedInstaller 所有でもないファイルだけを扱う。takeown・ACL 変更は行わず所有権も奪わない。
/// Windows 10/11 では通常すべて保護されているため候補は 0 件になる。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProtectedSystemFiles
{
    private const int MaxDepth = 32;
    private readonly IProtectedFileProbe _probe;

    internal ProtectedSystemFiles(IProtectedFileProbe probe) => _probe = probe;

    /// <summary>実機の SfcIsFileProtected と所有者で判定する。</summary>
    public static ProtectedSystemFiles Create() => new(new WindowsProtectedFileProbe());

    /// <summary>基点フォルダー以下を走査する。リンクは辿らず、ファイルの内容・時刻・属性を変えない。</summary>
    public ProtectedFileScan Scan(IEnumerable<string> roots, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var scan = new ProtectedFileScan();
        var candidates = new List<CleanupFileIdentity>();
        try
        {
            foreach (var root in roots)
            {
                ct.ThrowIfCancellationRequested();
                ScanRoot(root, scan, candidates, ct);
            }
        }
        finally
        {
            scan.Candidates = candidates;
            scan.CompletedAtUtc = DateTime.UtcNow;
        }

        return scan;
    }

    /// <summary>
    /// 走査で得た候補を削除する。削除の直前に同一性 (FileId・サイズ・更新時刻) と保護状態を取り直し、
    /// 変わっていたものは残して <see cref="CleanupOutcome.Retained"/> に数える。
    /// </summary>
    public CleanupOutcome Run(IEnumerable<CleanupFileIdentity> candidates, IProgress<string>? progress, CancellationToken ct, DateTime? createdBefore = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var recheckRetained = 0L;
        var verified = new List<CleanupFileIdentity>();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (StillUnprotected(candidate)) verified.Add(candidate);
            else recheckRetained++;
        }

        var outcome = FileCleanupEngine.DeleteVerified(verified, progress, ct, new CleanupRunOptions(CreatedBefore: createdBefore));
        outcome.Retained += recheckRetained;
        return outcome;
    }

    private bool StillUnprotected(CleanupFileIdentity expected)
    {
        try
        {
            using var file = NativeCleanupEntry.Open(expected.Path, false, security: true);
            var identity = file.Identity;
            return identity.VolumeSerialNumber == expected.VolumeSerialNumber &&
                   identity.FileIdLow == expected.FileIdLow && identity.FileIdHigh == expected.FileIdHigh &&
                   file.Length == expected.Size && file.Times.LastWrite == expected.LastWriteFileTime &&
                   _probe.Probe(file) == FileProtectionState.Unprotected;
        }
        catch (Exception ex) when (IsNativeFailure(ex))
        {
            return false;
        }
    }

    private void ScanRoot(string root, ProtectedFileScan scan, List<CleanupFileIdentity> candidates, CancellationToken ct)
    {
        if (!FileCleanupEngine.TryResolveActual(root, allowProtectedDirectory: true, out var fullPath, out _))
        {
            scan.RejectedRoots++;
            return;
        }

        if (!Directory.Exists(fullPath))
        {
            scan.MissingRoots++;
            return;
        }

        if (!FileCleanupEngine.TryAcquireTraversalGuard(fullPath, out var guard))
        {
            scan.RejectedRoots++;
            return;
        }

        using (guard)
        {
            ScanDirectory(guard, guard, scan, candidates, 0, ct);
        }
    }

    private void ScanDirectory(NativeCleanupEntry guard, NativeCleanupEntry directory, ProtectedFileScan scan, List<CleanupFileIdentity> candidates, int depth, CancellationToken ct)
    {
        if (depth > MaxDepth)
        {
            scan.Unreadable++;
            return;
        }

        IReadOnlyList<CleanupEntryInfo> entries;
        try { entries = directory.EnumerateDetailed().ToArray(); }
        catch (Exception ex) when (IsNativeFailure(ex)) { scan.Unreadable++; return; }

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var path = System.IO.Path.Combine(directory.Path, entry.Name);
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                scan.UnknownCount++;
            }
            else if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                ScanSubdirectory(guard, path, scan, candidates, depth, ct);
            }
            else
            {
                ClassifyFile(guard, path, entry, scan, candidates);
            }
        }
    }

    private void ScanSubdirectory(NativeCleanupEntry guard, string path, ProtectedFileScan scan, List<CleanupFileIdentity> candidates, int depth, CancellationToken ct)
    {
        try
        {
            using var child = guard.OpenDescendant(path, true);
            ScanDirectory(guard, child, scan, candidates, depth + 1, ct);
        }
        catch (Exception ex) when (IsNativeFailure(ex)) { scan.Unreadable++; }
    }

    private void ClassifyFile(NativeCleanupEntry guard, string path, CleanupEntryInfo entry, ProtectedFileScan scan, List<CleanupFileIdentity> candidates)
    {
        try
        {
            using var file = guard.OpenDescendant(path, false, security: true);
            switch (_probe.Probe(file))
            {
                case FileProtectionState.Unprotected:
                    var identity = file.Identity;
                    candidates.Add(new CleanupFileIdentity(path, identity.VolumeSerialNumber, identity.FileIdLow, identity.FileIdHigh, entry.Size, entry.LastWriteTime));
                    scan.CandidateBytes += entry.Size;
                    break;
                case FileProtectionState.WindowsResourceProtection:
                    scan.ProtectedCount++;
                    break;
                case FileProtectionState.TrustedInstallerOwned:
                    scan.TrustedInstallerCount++;
                    break;
                default:
                    scan.UnknownCount++;
                    break;
            }
        }
        catch (Exception ex) when (IsNativeFailure(ex)) { scan.Unreadable++; }
    }

    private static bool IsNativeFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception;
}

/// <summary>SfcIsFileProtected と開いたハンドルの所有者で保護状態を決める。所有権は変更しない。</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsProtectedFileProbe : IProtectedFileProbe
{
    private const int ErrorFileNotFound = 2;
    private const int SeFileObject = 1;
    private const uint OwnerSecurityInformation = 1;

    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    public FileProtectionState Probe(NativeCleanupEntry file)
    {
        if (SfcIsFileProtected(IntPtr.Zero, file.Path))
        {
            return FileProtectionState.WindowsResourceProtection;
        }

        // 保護対象でないときは ERROR_FILE_NOT_FOUND (または 0) が返る。それ以外は判定不能として扱う。
        var error = Marshal.GetLastWin32Error();
        if (error is not (0 or ErrorFileNotFound))
        {
            return FileProtectionState.Unknown;
        }

        var owner = ReadOwner(file);
        return owner is null
            ? FileProtectionState.Unknown
            : owner.Equals(TrustedInstaller) ? FileProtectionState.TrustedInstallerOwned : FileProtectionState.Unprotected;
    }

    private static SecurityIdentifier? ReadOwner(NativeCleanupEntry file)
    {
        var handle = file.Handle;
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var status = GetSecurityInfo(handle.DangerousGetHandle(), SeFileObject, OwnerSecurityInformation, out var owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var descriptor);
            if (status != 0 || owner == IntPtr.Zero)
            {
                return null;
            }

            try { return new SecurityIdentifier(owner); }
            finally { LocalFree(descriptor); }
        }
        finally
        {
            if (added) handle.DangerousRelease();
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("sfc.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SfcIsFileProtected(IntPtr rpcHandle, string protectedFileName);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(IntPtr handle, int objectType, uint securityInfo, out IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr securityDescriptor);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
