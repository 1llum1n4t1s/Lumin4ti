namespace Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;

/// <summary>
/// OS 所有の保護場所。<c>%SystemRoot%</c> 配下、<c>Program Files*\Windows*</c> (WindowsApps を含む)、
/// <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c>。フォントだけは <c>%windir%\Fonts</c> を許可できる。
/// 正規化できないパスは保護として扱う。
/// </summary>
/// <param name="WindowsDirectory">Windows フォルダー。</param>
/// <param name="ProgramFilesDirectories">Program Files (64 ビット・x86) の各フォルダー。</param>
/// <param name="LocalAppData">現在の利用者の LocalAppData (確認できなければ null)。</param>
internal sealed record RegistryOwnedLocations(
    string? WindowsDirectory,
    IReadOnlyList<string> ProgramFilesDirectories,
    string? LocalAppData)
{
    /// <summary>OS の既知フォルダーから作る。</summary>
    public static RegistryOwnedLocations FromEnvironment() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ],
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>保護場所の中か。<paramref name="allowWindowsFonts"/> が真なら <c>%windir%\Fonts</c> 配下は保護しない。</summary>
    public bool IsProtected(string path, bool allowWindowsFonts = false)
    {
        var full = Normalize(path);
        if (full is null)
        {
            return true;
        }

        if (IsUnder(full, WindowsDirectory))
        {
            return !(allowWindowsFonts && IsUnder(full, Combine(WindowsDirectory, "Fonts")));
        }

        return ProgramFilesDirectories.Any(dir => IsWindowsPrefixedChild(full, dir)) ||
               IsUnder(full, Combine(LocalAppData, @"Microsoft\WindowsApps"));
    }

    /// <summary>1 つでも保護場所にある候補があれば真 (候補なしは保護として扱う)。</summary>
    public bool IsAnyProtected(IReadOnlyList<string> candidates, bool allowWindowsFonts = false) =>
        candidates.Count == 0 || candidates.Any(candidate => IsProtected(candidate, allowWindowsFonts));

    private static bool IsWindowsPrefixedChild(string full, string programFiles)
    {
        var root = Normalize(programFiles);
        if (root is null || !IsUnder(full, root) || full.Length <= root.Length + 1)
        {
            return false;
        }

        var child = full[(root.Length + 1)..].Split('\\')[0];
        return child.StartsWith("Windows", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Combine(string? folder, string relative) =>
        string.IsNullOrWhiteSpace(folder) ? null : Path.Combine(folder, relative);

    private static bool IsUnder(string full, string? folder)
    {
        var root = Normalize(folder);
        return root is not null &&
               (string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase));
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            return PathNames.ToLongName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }
}
