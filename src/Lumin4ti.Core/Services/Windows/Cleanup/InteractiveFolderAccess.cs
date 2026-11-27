using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>DACL の ACE 1 件 (純粋な判定の入力)。</summary>
/// <param name="Sid">対象の SID (文字列形式)。</param>
/// <param name="Allow">許可 ACE か (false は拒否 ACE)。</param>
/// <param name="Mask">アクセスマスク。</param>
/// <param name="InheritOnly">子にだけ適用し、そのフォルダー自身には効かない ACE。</param>
internal readonly record struct FolderAce(string Sid, bool Allow, int Mask, bool InheritOnly);

/// <summary>
/// 「対話利用者が自力で中身を変更できるフォルダー」の判定。TEMP をプロファイルの外へ移すのは正当だが、
/// 管理者権限で動く削除が、利用者に書けない場所 (他者・システムの領域) を TEMP の名で消さないための条件に使う。
/// 判定に使う値 (所有者・ACE) の読み取りと、純粋な判定を分ける。
/// </summary>
internal static class InteractiveFolderAccess
{
    private const int FileAddFile = 0x2;
    private const int FileAddSubdirectory = 0x4;
    private const int Delete = 0x10000;
    private const int GenericAll = 0x10000000;
    private const int GenericWrite = 0x40000000;

    /// <summary>Modify 相当 (ファイルとフォルダーの作成・削除。Modify は FILE_DELETE_CHILD を含まず、子の DELETE は継承で与える)。</summary>
    internal const int ModifyRequired = FileAddFile | FileAddSubdirectory | Delete;

    private const string BuiltinUsers = "S-1-5-32-545";
    private const string AuthenticatedUsers = "S-1-5-11";
    private const string Everyone = "S-1-1-0";

    /// <summary>
    /// 純粋な判定。所有者が利用者なら可。そうでなければ、利用者・Users・Authenticated Users・Everyone への許可 ACE の合計が
    /// Modify 相当を満たし、同じ主体への拒否 ACE がそれを欠かさないときだけ可。DACL を確認できない (<paramref name="entries"/> が null) ときは不可。
    /// </summary>
    public static bool AllowsUserModify(string? ownerSid, IReadOnlyList<FolderAce>? entries, string userSid)
    {
        if (string.IsNullOrEmpty(userSid)) return false;
        if (string.Equals(ownerSid, userSid, StringComparison.OrdinalIgnoreCase)) return true;
        if (entries is null) return false;

        var allowed = 0;
        var denied = 0;
        foreach (var entry in entries)
        {
            if (entry.InheritOnly) continue;
            var mask = Expand(entry.Mask);
            // 判定対象外の主体 (利用者が属する別のグループなど) への拒否でも、Modify に掛かるなら利用者が書けるとは言えない。
            if (!entry.Allow && (mask & ModifyRequired) != 0) return false;
            if (!IsUserPrincipal(entry.Sid, userSid)) continue;
            if (entry.Allow) allowed |= mask;
            else denied |= mask;
        }

        return (allowed & ~denied & ModifyRequired) == ModifyRequired;
    }

    private static bool IsUserPrincipal(string sid, string userSid) =>
        string.Equals(sid, userSid, StringComparison.OrdinalIgnoreCase) ||
        sid is BuiltinUsers or AuthenticatedUsers or Everyone;

    /// <summary>GENERIC_ALL・GENERIC_WRITE を具体的な権利へ広げる (保存済みの DACL では通常すでに展開済み)。</summary>
    private static int Expand(int mask)
    {
        var result = mask;
        if ((mask & GenericAll) != 0) result |= ModifyRequired;
        if ((mask & GenericWrite) != 0) result |= FileAddFile | FileAddSubdirectory;
        return result;
    }

    /// <summary>
    /// フォルダーを再解析点を辿らずに開き (リンクなら不可)、そのハンドルから所有者と DACL を読んで判定する。
    /// 開けない・読めない・利用者が分からない場合は不可。何も変更しない。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool CanCurrentUserModify(string directory)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is not { } user) return false;
            using var entry = NativeCleanupEntry.Open(directory, directory: true, security: true);
            entry.EnsureNotReparse();
            if (!TryReadSecurity(entry, out var owner, out var entries)) return false;
            return AllowsUserModify(owner, entries, user.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryReadSecurity(NativeCleanupEntry entry, out string? owner, out IReadOnlyList<FolderAce>? entries)
    {
        owner = null;
        entries = null;
        var handle = entry.Handle;
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var status = GetSecurityInfo(handle.DangerousGetHandle(), SeFileObject, OwnerSecurityInformation | DaclSecurityInformation,
                out _, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var descriptor);
            if (status != 0 || descriptor == IntPtr.Zero) return false;
            try
            {
                var bytes = new byte[GetSecurityDescriptorLength(descriptor)];
                Marshal.Copy(descriptor, bytes, 0, bytes.Length);
                var raw = new RawSecurityDescriptor(bytes, 0);
                owner = raw.Owner?.Value;
                if (raw.DiscretionaryAcl is null) return true; // DACL なし (全員に許可) は確認できたこととせず、所有者だけで判定する。
                var list = new List<FolderAce>();
                foreach (var ace in raw.DiscretionaryAcl)
                {
                    if (ace is not QualifiedAce { AceQualifier: AceQualifier.AccessAllowed or AceQualifier.AccessDenied } qualified) continue;
                    list.Add(new FolderAce(
                        qualified.SecurityIdentifier.Value,
                        qualified.AceQualifier == AceQualifier.AccessAllowed,
                        qualified.AccessMask,
                        (qualified.AceFlags & AceFlags.InheritOnly) != 0));
                }

                entries = list;
                return true;
            }
            finally
            {
                LocalFree(descriptor);
            }
        }
        finally
        {
            if (added) handle.DangerousRelease();
        }
    }

    private const int SeFileObject = 1;
    private const uint OwnerSecurityInformation = 0x1;
    private const uint DaclSecurityInformation = 0x4;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(IntPtr handle, int objectType, uint securityInfo, out IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr securityDescriptor);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
