namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>枝刈りが参照する環境。純関数にするため、実環境の値は <see cref="FromEnvironment"/> でだけ読む。</summary>
/// <param name="Folders">Windows・Program Files・ProgramData・プロファイル置き場・現在のプロファイル。</param>
/// <param name="SelfDirectories">Lumin4ti 自身 (実行ファイル・設定・ログ・保護データ) のフォルダー。</param>
public sealed record AdvancedProtectionContext(CustomFolderGuardContext Folders, IReadOnlyList<string> SelfDirectories)
{
    /// <summary>実環境から作る。</summary>
    public static AdvancedProtectionContext FromEnvironment()
    {
        var self = new List<string>();
        string?[] candidates =
        [
            AppContext.BaseDirectory,
            AppPaths.AppDataDirectory,
            Path.GetDirectoryName(AppPaths.LogsDirectory),
            Path.GetDirectoryName(AppPaths.ProtectedBackupsDirectory),
        ];
        self.AddRange(candidates.Where(static c => !string.IsNullOrWhiteSpace(c)).Select(static c => c!.TrimEnd('\\')));

        // Velopack は <root>\current に置くため、更新元を含む <root> も守る。
        var baseDirectory = AppContext.BaseDirectory.TrimEnd('\\');
        if (string.Equals(Path.GetFileName(baseDirectory), "current", StringComparison.OrdinalIgnoreCase) &&
            Path.GetDirectoryName(baseDirectory) is { Length: > 0 } root)
        {
            self.Add(root);
        }

        return new AdvancedProtectionContext(CustomFolderGuardContext.FromEnvironment(), self);
    }
}

/// <summary>
/// 高度なクリーンが踏み込まない場所 (枝刈り) と、名前だけで消してはいけないファイル。
/// I/O を行わない純関数で、走査と削除の両方が同じ判定を使う。
/// <c>%SystemRoot%</c> 全体、Program Files 系、<c>ProgramData\Microsoft</c>、Package Cache、ドライブ直下の
/// 復旧・ブート領域、他の利用者のプロファイル、認証情報のフォルダー、<c>.git</c>、Lumin4ti 自身を含む。
/// </summary>
public sealed class AdvancedProtectedPaths
{
    private static readonly string[] DriveRootNames =
    [
        "$Recycle.Bin", "System Volume Information", "Recovery", "Boot", "EFI", "$WinREAgent", "Config.Msi",
        "$WINDOWS.~BT", "$Windows.~WS", "Windows.old", "$SysReset",
    ];

    private static readonly string[] AnyDepthNames =
    [
        ".git", ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker",
        "Credentials", "Vault", "Protect", "Crypto", "Storage2", "tdata",
    ];

    private static readonly string[] ProtectedFilePatterns =
    [
        "Login Data*", "logins.json", "key3.db", "key4.db", "Local State", "*.kdbx*", "*.pfx", "*.p12", "*.ppk", "id_rsa*", "id_ed25519*",
        // C:\ 直下のクラッシュダンプ用の予約領域。*.tmp に一致するが、OS が管理するため既定の対象にしない。
        "DumpStack.log.tmp",
    ];

    private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _rootNames = new(DriveRootNames, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _anyDepthNames = new(AnyDepthNames, StringComparer.OrdinalIgnoreCase);
    private readonly string[] _profileContainers;
    private readonly string? _currentProfile;

    /// <summary>実環境の枝刈り。</summary>
    public static AdvancedProtectedPaths Default => new(AdvancedProtectionContext.FromEnvironment());

    public AdvancedProtectedPaths(AdvancedProtectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var folders = context.Folders;
        AddExact(folders.WindowsDirectory);
        foreach (var dir in folders.ProgramFilesDirectories) AddExact(dir);
        if (Normalize(folders.ProgramDataDirectory) is { } programData)
        {
            _exact.Add(Path.Combine(programData, "Microsoft"));
            _exact.Add(Path.Combine(programData, "Package Cache"));
        }

        foreach (var dir in context.SelfDirectories) AddExact(dir);
        _profileContainers = [.. folders.ProfileContainers.Select(Normalize).OfType<string>()];
        _currentProfile = Normalize(folders.CurrentProfile);
    }

    private void AddExact(string? path)
    {
        if (Normalize(path) is { } normalized) _exact.Add(normalized);
    }

    /// <summary>ディレクトリを枝刈りするか。<paramref name="parentPath"/> はその親の絶対パス。</summary>
    public bool IsPrunedDirectory(string fullPath, string name, string parentPath)
    {
        if (_anyDepthNames.Contains(name)) return true;
        if (_exact.Contains(fullPath.TrimEnd('\\'))) return true;
        if (Path.GetPathRoot(parentPath) is { } root && IsSame(parentPath, root) && _rootNames.Contains(name)) return true;
        return _profileContainers.Any(container => IsSame(parentPath, container) &&
            (_currentProfile is null || !IsSame(fullPath, _currentProfile)));
    }

    /// <summary>パスの祖先または自身のどれかが枝刈りの対象か (削除直前の再確認と、走査の起点の検査に使う)。</summary>
    public bool IsProtectedPath(string fullPath)
    {
        var path = Normalize(fullPath);
        if (path is null) return true;
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var segment in path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var parent = current;
            current = Path.Combine(current, segment);
            if (IsPrunedDirectory(current, segment, parent)) return true;
        }

        return false;
    }

    /// <summary>パスが枝刈りの対象のどれかを含む、または含まれるか (カスタムフォルダーが自身などを巻き込まないための検査)。</summary>
    public bool OverlapsProtected(string fullPath)
    {
        var path = Normalize(fullPath);
        if (path is null) return true;
        return IsProtectedPath(path) || _exact.Any(exact => exact.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>名前だけで保護するファイル (認証情報・鍵・パスワード DB)。</summary>
    public static bool IsProtectedFile(string fileName) =>
        ProtectedFilePatterns.Any(pattern => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, fileName, ignoreCase: true));

    private static bool IsSame(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try
        {
            var full = Path.GetFullPath(path.Trim());
            return full.Length > 3 ? Path.TrimEndingDirectorySeparator(full) : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
