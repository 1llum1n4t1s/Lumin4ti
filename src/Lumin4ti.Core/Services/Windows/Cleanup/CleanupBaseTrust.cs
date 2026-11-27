using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>
/// 規則の基点 (環境変数) を信頼してよいかを決める根拠。環境変数は利用者が差し替えられるため、
/// OS が別経路で持つ値 (プロファイルは HKLM の ProfileList、機械側は既知フォルダー) と照合する。
/// 実環境の値は <see cref="FromEnvironment"/> でだけ読み、テストは値を注入する。
/// </summary>
/// <param name="Guard">Windows・Program Files・ProgramData・プロファイル親フォルダーと、確認済みの現在のプロファイル (<see cref="CustomFolderGuardContext.CurrentProfile"/>。確認できなければ null)。</param>
/// <param name="MachineFolder">機械側の変数名から、OS が示す既定の場所を返す。無ければ null (その変数は使わない)。</param>
/// <param name="UserCanModify">プロファイル外の TEMP が、対話利用者に中身を変更できるフォルダーか。null (確認できない) は不可として扱う。</param>
internal sealed record CleanupBaseTrust(CustomFolderGuardContext Guard, Func<string, string?> MachineFolder, Func<string, bool>? UserCanModify = null)
{
    /// <summary>実環境から作る。</summary>
    public static CleanupBaseTrust FromEnvironment() =>
        new(CustomFolderGuardContext.FromEnvironment(), MachineFolderOf, InteractiveFolderAccess.CanCurrentUserModify);

    private static string? MachineFolderOf(string variable)
    {
        var folder = variable.ToUpperInvariant() switch
        {
            "PROGRAMDATA" or "ALLUSERSPROFILE" => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SYSTEMROOT" or "WINDIR" => Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "PROGRAMFILES" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PROGRAMFILES(X86)" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "PUBLIC" => Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments)),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(folder) ? null : folder;
    }

    /// <summary>基点 <paramref name="baseDirectory"/> (変数 <paramref name="variable"/> の値) を使ってよいか。使えなければ画面向けの理由の符号を返す。</summary>
    public string? Check(string variable, string baseDirectory)
    {
        if (!CleanupRuleValidator.TryGetBaseScope(variable, out var scope)) return null;
        if (scope == CleanupScope.Machine) return CheckMachine(variable, baseDirectory);
        var profile = Guard.CurrentProfile;
        if (profile is not null && IsSameOrUnder(baseDirectory, profile)) return null;
        if (variable is not ("TEMP" or "TMP"))
        {
            return CleanupMessage.Of("BaseOutsideProfile", variable);
        }

        // TEMP は別ドライブへ移すのが正当な設定。保護された場所の中・他人のプロファイル・ドライブ直下そのもの・高度クリーンの枝刈りの表
        // ($Recycle.Bin・Recovery・Windows.old など) に当たる場所を拒否し、さらに対話利用者が自力で中身を変更できるフォルダーだけを許す。
        var check = CustomFolderGuard.Check(baseDirectory, Guard);
        var pruning = new AdvancedProtectedPaths(new AdvancedProtectionContext(Guard, []));
        var usable = check.IsAllowed && !pruning.IsProtectedPath(baseDirectory) && UserCanModify?.Invoke(baseDirectory) == true;
        return usable ? null : CleanupMessage.Of("BaseProtectedLocation", variable);
    }

    private string? CheckMachine(string variable, string baseDirectory)
    {
        var expected = MachineFolder(variable);
        var matches = expected is not null &&
            string.Equals(Normalize(expected), Normalize(baseDirectory), StringComparison.OrdinalIgnoreCase);
        return matches ? null : CleanupMessage.Of("BaseMachineMismatch", variable);
    }

    private static string Normalize(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return PathNames.ToLongName(full);
    }

    private static bool IsSameOrUnder(string path, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(folder);
        var value = Path.TrimEndingDirectorySeparator(path);
        return string.Equals(value, root, StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>パス名の正規化。</summary>
internal static class PathNames
{
    /// <summary>
    /// 8.3 短縮名 (<c>LONGUS~1</c> など) を長い名前へ戻す。短縮名を含まない・実在しない・変換できないパスはそのまま返す。
    /// 保護の照合・除外・重なり判定は長い名前で行うため、基点はこれを通してから渡す。
    /// </summary>
    public static string ToLongName(string path)
    {
        if (!path.Contains('~')) return path;
        var buffer = new StringBuilder(1024);
        var length = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
        return length > 0 && length < buffer.Capacity ? Path.TrimEndingDirectorySeparator(buffer.ToString()) : path;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint bufferLength);
}

/// <summary>現在の利用者のプロファイルを、HKLM の ProfileList と照合して確認する。</summary>
internal static class VerifiedProfile
{
    private const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\";

    /// <summary>
    /// <see cref="Environment.SpecialFolder.UserProfile"/> (環境変数 USERPROFILE ではなく OS の既知フォルダー) が、
    /// 現在の SID の ProfileList の値と一致するときだけそのパスを返す。確認できなければ null。
    /// </summary>
    public static string? Current()
    {
        try
        {
            var known = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            using var identity = WindowsIdentity.GetCurrent();
            if (string.IsNullOrWhiteSpace(known) || identity.User is not { } sid) return null;
            using var key = Registry.LocalMachine.OpenSubKey(ProfileList + sid.Value);
            var raw = key?.GetValue("ProfileImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            return Matches(known, raw) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(known)) : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>ProfileList の値 (<c>%SystemDrive%</c> を含み得る) と既知フォルダーが同じ場所か。</summary>
    internal static bool Matches(string known, string? profileImagePath)
    {
        if (string.IsNullOrWhiteSpace(profileImagePath)) return false;
        var drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))?.TrimEnd('\\');
        var expanded = drive is null ? profileImagePath : profileImagePath.Replace("%SystemDrive%", drive, StringComparison.OrdinalIgnoreCase);
        if (expanded.Contains('%') || !Path.IsPathFullyQualified(expanded)) return false;
        var left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(known));
        var right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
        return string.Equals(PathNames.ToLongName(left), PathNames.ToLongName(right), StringComparison.OrdinalIgnoreCase);
    }
}
