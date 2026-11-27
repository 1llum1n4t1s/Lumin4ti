namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>カスタムフォルダーを拒否した理由。</summary>
public enum CustomFolderRejection
{
    /// <summary>拒否しない。</summary>
    None = 0,

    /// <summary>絶対パスでない・ネットワーク/デバイスパス・使えない文字・短縮名など。</summary>
    Invalid,

    /// <summary>ドライブ直下。</summary>
    DriveRoot,

    /// <summary>Windows ディレクトリ (配下・自身・祖先)。</summary>
    Windows,

    /// <summary>Program Files 系 (配下・自身・祖先)。</summary>
    ProgramFiles,

    /// <summary>ProgramData (配下・自身・祖先)。</summary>
    ProgramData,

    /// <summary>他の利用者のプロファイル、またはプロファイルを束ねるフォルダー。</summary>
    OtherUserProfile,

    /// <summary>自分のプロファイル直下・AppData・AppData\Local・AppData\Roaming (中身を空にすると全データを失う)。</summary>
    ProfileRoot,

    /// <summary>認証情報・鍵を持つフォルダーを含む。</summary>
    Credential,
}

/// <summary>検査結果。<see cref="FullPath"/> は許可・拒否のどちらでも正規化できたときだけ入る。</summary>
public readonly record struct CustomFolderCheck(string? FullPath, CustomFolderRejection Rejection)
{
    public bool IsAllowed => Rejection == CustomFolderRejection.None;
}

/// <summary>検査が参照する環境。純関数にするため、実環境の値は <see cref="FromEnvironment"/> でだけ読む。</summary>
/// <param name="WindowsDirectory">Windows ディレクトリ。</param>
/// <param name="ProgramFilesDirectories">Program Files 系のフォルダー。</param>
/// <param name="ProgramDataDirectory">ProgramData。</param>
/// <param name="ProfileContainers">利用者プロファイルを束ねるフォルダー (例: <c>C:\Users</c>)。</param>
/// <param name="CurrentProfile">現在の利用者のプロファイル。配下だけを許可する。</param>
public sealed record CustomFolderGuardContext(
    string? WindowsDirectory,
    IReadOnlyList<string> ProgramFilesDirectories,
    string? ProgramDataDirectory,
    IReadOnlyList<string> ProfileContainers,
    string? CurrentProfile)
{
    /// <summary>実環境から作る。昇格利用者と対話利用者が異なる場合の扱いは呼び出し側が決める。</summary>
    public static CustomFolderGuardContext FromEnvironment()
    {
        // プロファイルは環境変数ではなく OS の既知フォルダーで、HKLM の ProfileList と一致したものだけを信頼する。確認できなければ null (自分のプロファイルとして扱わない)。
        var profile = VerifiedProfile.Current();
        var known = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var containers = new List<string>();
        if (!string.IsNullOrEmpty(known) && Path.GetDirectoryName(known.TrimEnd('\\')) is { Length: > 0 } parent)
        {
            containers.Add(parent);
        }

        if (Environment.GetEnvironmentVariable("SystemDrive") is { Length: > 0 } drive)
        {
            containers.Add(Path.Combine(drive + "\\", "Users"));
        }

        string?[] programFiles =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432"),
        ];

        return new CustomFolderGuardContext(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            [.. programFiles.OfType<string>().Where(static p => p.Length > 0)],
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            containers,
            profile);
    }
}

/// <summary>
/// カスタムフォルダー (中身だけを空にする) の検査。<c>settings.json</c> は非昇格で書けるため、
/// 追加時だけでなく実行時にも呼ぶ。I/O を行わない純関数で、リンク・結合点の確認は呼び出し側が
/// ハンドルで行う。拒否した対象は走査結果でも既定未選択にする。
/// </summary>
public static class CustomFolderGuard
{
    private static readonly HashSet<string> CredentialSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker",
        "Credentials", "Vault", "Protect", "Crypto", "Storage2", "tdata",
    };

    private static readonly char[] InvalidChars = ['*', '?', '<', '>', '|', '"', '\0'];

    /// <summary>1件を検査する。</summary>
    public static CustomFolderCheck Check(string? path, CustomFolderGuardContext context)
    {
        if (!TryNormalize(path, out var full))
        {
            return new(null, CustomFolderRejection.Invalid);
        }

        return new(full, Classify(full, context));
    }

    /// <summary>許可された正規化済みパスだけを返す (重複は除く)。実行時の再検査に使う。</summary>
    public static IReadOnlyList<string> FilterAllowed(IEnumerable<string?> paths, CustomFolderGuardContext context) =>
    [
        .. paths.Select(p => Check(p, context))
            .Where(static c => c.IsAllowed)
            .Select(static c => c.FullPath!)
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    private static CustomFolderRejection Classify(string full, CustomFolderGuardContext context)
    {
        if (Path.GetPathRoot(full) is { } root && IsSame(full, root))
        {
            return CustomFolderRejection.DriveRoot;
        }

        if (Related(full, context.WindowsDirectory)) return CustomFolderRejection.Windows;
        if (context.ProgramFilesDirectories.Any(dir => Related(full, dir))) return CustomFolderRejection.ProgramFiles;
        if (Related(full, context.ProgramDataDirectory)) return CustomFolderRejection.ProgramData;

        var profile = ClassifyProfile(full, context);
        if (profile != CustomFolderRejection.None) return profile;

        var protectedBase = ClassifyProfileBases(full, Normalize(context.CurrentProfile));
        if (protectedBase != CustomFolderRejection.None) return protectedBase;

        var relative = full[Path.GetPathRoot(full)!.Length..];
        return relative.Split('\\', StringSplitOptions.RemoveEmptyEntries).Any(IsCredentialSegment)
            ? CustomFolderRejection.Credential
            : CustomFolderRejection.None;
    }

    private static bool IsCredentialSegment(string segment) =>
        CredentialSegments.Contains(segment) || CleanupRuleValidator.IsCredentialFileName(segment);

    /// <summary>ブラウザーのプロファイルを持つフォルダーを、名前で残すためのパターン (カスタムフォルダーが、中のプロファイルを消さないため)。</summary>
    private static readonly string[] ProfileDataPatterns = ["Web Data*", "Cookies*", "Preferences", "Local State*", "Login Data*"];

    /// <summary>
    /// カスタムフォルダーの子孫を、名前だけで残すか。<see cref="IsCredentialSegment"/> と同じ和集合 (拒否表と <c>.docker</c> など)
    /// に加え、ブラウザーのプロファイルの Web Data・Cookies・Preferences・Local State・Login Data 系。走査と削除が共有する。
    /// </summary>
    internal static bool KeepsByName(string name) =>
        IsCredentialSegment(name) ||
        ProfileDataPatterns.Any(pattern => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true));

    /// <summary>
    /// プロファイル配下の保護する基点。検証器の保護フォルダーは自身と祖先を拒否する。
    /// 既知のブラウザー・メールのプロファイル基点 (<c>WithDescendants</c>) は、中にプロファイル・認証情報を持つため子孫も拒否する。
    /// </summary>
    private static readonly (string Relative, CustomFolderRejection Rejection, bool WithDescendants)[] ProtectedProfileBases =
    [
        (@"AppData\LocalLow", CustomFolderRejection.ProfileRoot, false),
        (@"AppData\Local\Microsoft", CustomFolderRejection.ProfileRoot, false),
        (@"AppData\Roaming\Microsoft", CustomFolderRejection.ProfileRoot, false),
        ("Documents", CustomFolderRejection.ProfileRoot, false),
        ("Pictures", CustomFolderRejection.ProfileRoot, false),
        ("Videos", CustomFolderRejection.ProfileRoot, false),
        ("Music", CustomFolderRejection.ProfileRoot, false),
        ("Desktop", CustomFolderRejection.ProfileRoot, false),
        (@"AppData\Local\Google\Chrome\User Data", CustomFolderRejection.Credential, true),
        (@"AppData\Local\Google\Chrome Beta\User Data", CustomFolderRejection.Credential, true),
        (@"AppData\Local\Microsoft\Edge\User Data", CustomFolderRejection.Credential, true),
        (@"AppData\Local\BraveSoftware\Brave-Browser\User Data", CustomFolderRejection.Credential, true),
        (@"AppData\Local\Vivaldi\User Data", CustomFolderRejection.Credential, true),
        (@"AppData\Local\Chromium\User Data", CustomFolderRejection.Credential, true),
        (@"AppData\Roaming\Opera Software\Opera Stable", CustomFolderRejection.Credential, true),
        (@"AppData\Roaming\Opera Software\Opera GX Stable", CustomFolderRejection.Credential, true),
        (@"AppData\Roaming\Mozilla\Firefox", CustomFolderRejection.Credential, true),
        (@"AppData\Local\Mozilla\Firefox", CustomFolderRejection.Credential, true),
        (@"AppData\Roaming\Thunderbird", CustomFolderRejection.Credential, true),
        (@"AppData\Local\Thunderbird", CustomFolderRejection.Credential, true),
        (@"AppData\Roaming\Microsoft\Outlook", CustomFolderRejection.Credential, true),
        (@"AppData\Local\Microsoft\Outlook", CustomFolderRejection.Credential, true),
    ];

    private static CustomFolderRejection ClassifyProfileBases(string full, string? profile)
    {
        if (profile is null) return CustomFolderRejection.None;
        foreach (var (relative, rejection, withDescendants) in ProtectedProfileBases)
        {
            var folder = Path.Combine(profile, relative);
            if (IsSame(full, folder) || IsUnder(folder, full) || (withDescendants && IsUnder(full, folder))) return rejection;
        }

        return CustomFolderRejection.None;
    }

    private static CustomFolderRejection ClassifyProfile(string full, CustomFolderGuardContext context)
    {
        var current = Normalize(context.CurrentProfile);
        foreach (var container in context.ProfileContainers.Select(Normalize).OfType<string>())
        {
            if (IsSame(full, container) || IsUnder(container, full))
            {
                return CustomFolderRejection.OtherUserProfile;
            }

            if (!IsUnder(full, container)) continue;
            if (current is null || !IsUnder(full, current) && !IsSame(full, current))
            {
                return CustomFolderRejection.OtherUserProfile;
            }
        }

        return current is not null && IsProfileSpecialFolder(full, current)
            ? CustomFolderRejection.ProfileRoot
            : CustomFolderRejection.None;
    }

    /// <summary>プロファイル直下と AppData 系の3フォルダー自身か。</summary>
    private static bool IsProfileSpecialFolder(string full, string profile) =>
        IsSame(full, profile) ||
        IsSame(full, Path.Combine(profile, "AppData")) ||
        IsSame(full, Path.Combine(profile, "AppData", "Local")) ||
        IsSame(full, Path.Combine(profile, "AppData", "Roaming"));

    private static bool TryNormalize(string? path, out string full)
    {
        full = string.Empty;
        var text = path?.Trim();
        if (string.IsNullOrEmpty(text) || text.StartsWith(@"\\", StringComparison.Ordinal) || text.StartsWith("//", StringComparison.Ordinal) ||
            text.IndexOfAny(InvalidChars) >= 0 || text.Contains('%') || !Path.IsPathFullyQualified(text) ||
            text.IndexOf(':', 2) >= 0)
        {
            return false;
        }

        try { full = Path.GetFullPath(text); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }

        full = TrimSeparator(full);
        return !full.Split('\\', StringSplitOptions.RemoveEmptyEntries).Skip(1).Any(IsShortName);
    }

    /// <summary>8.3 短縮名 (<c>PROGRA~1</c> など)。実体を展開できない純関数では、別名による回避を防ぐため拒否する。</summary>
    private static bool IsShortName(string segment)
    {
        var tilde = segment.LastIndexOf('~');
        return tilde > 0 && segment.Length <= 12 && tilde + 1 < segment.Length && char.IsAsciiDigit(segment[tilde + 1]);
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try { return TrimSeparator(Path.GetFullPath(path.Trim())); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static string TrimSeparator(string path) =>
        path.Length > 3 ? Path.TrimEndingDirectorySeparator(path) : path.TrimEnd('\\');

    private static bool Related(string full, string? protectedRoot)
    {
        var root = Normalize(protectedRoot);
        return root is not null && (IsSame(full, root) || IsUnder(full, root) || IsUnder(root, full));
    }

    private static bool IsSame(string a, string b) =>
        string.Equals(TrimSeparator(a), TrimSeparator(b), StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="path"/> が <paramref name="folder"/> の厳密な配下か。</summary>
    private static bool IsUnder(string path, string folder) =>
        path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && !IsSame(path, folder);
}
