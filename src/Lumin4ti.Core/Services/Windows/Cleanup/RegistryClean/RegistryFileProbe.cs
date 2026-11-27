using System.Security;

namespace Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;

/// <summary>実体ファイルの存在判定。<see cref="ConfirmedMissing"/> だけがクリーンアップの候補になる。</summary>
internal enum RegistryFileState
{
    /// <summary>存在する。</summary>
    Exists,

    /// <summary>準備済みの固定ドライブ上で、OS が不存在を明示し、祖先に再解析点が無い。</summary>
    ConfirmedMissing,

    /// <summary>判定できない (保持する)。</summary>
    Unknown,
}

/// <summary>パスの存在を 3 値で判定する。</summary>
internal interface IRegistryFileProbe
{
    /// <summary>1 つのパスを判定する。</summary>
    RegistryFileState Probe(string path);

    /// <summary>
    /// 候補すべてを判定して 1 つにまとめる。1 つでも存在すれば <see cref="RegistryFileState.Exists"/>、
    /// 全候補が欠損確定のときだけ <see cref="RegistryFileState.ConfirmedMissing"/>、それ以外 (候補なしを含む) は <see cref="RegistryFileState.Unknown"/>。
    /// </summary>
    RegistryFileState ProbeAll(IReadOnlyList<string> candidates);
}

/// <summary>
/// <see cref="IRegistryFileProbe"/> の実装。欠損の確定は <c>StartupCommandParser.IsConfirmedMissing</c> と同じ基準
/// (準備済みの固定ドライブ・祖先に再解析点なし・OS が FileNotFound / DirectoryNotFound を返した)。
/// ドライブ種別と属性の取得は注入でき、テストは実ファイルシステムに触れない。
/// </summary>
internal sealed class RegistryFileProbe(
    Func<string, (DriveType DriveType, bool IsReady)>? probeDrive = null,
    Func<string, FileAttributes>? getAttributes = null) : IRegistryFileProbe
{
    private readonly Func<string, (DriveType DriveType, bool IsReady)> _probeDrive = probeDrive ?? DefaultProbeDrive;
    private readonly Func<string, FileAttributes> _getAttributes = getAttributes ?? File.GetAttributes;

    public RegistryFileState Probe(string path)
    {
        if (!IsLocalFullPath(path) || !IsFixedReadyDrive(path))
        {
            return RegistryFileState.Unknown;
        }

        try
        {
            _ = _getAttributes(path);
            return RegistryFileState.Exists;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return HasOnlyPlainAncestors(path) ? RegistryFileState.ConfirmedMissing : RegistryFileState.Unknown;
        }
        catch (Exception ex) when (IsIndeterminate(ex))
        {
            return RegistryFileState.Unknown;
        }
    }

    public RegistryFileState ProbeAll(IReadOnlyList<string> candidates)
    {
        if (candidates.Count == 0)
        {
            return RegistryFileState.Unknown;
        }

        var allMissing = true;
        foreach (var candidate in candidates)
        {
            var state = Probe(candidate);
            if (state == RegistryFileState.Exists)
            {
                return RegistryFileState.Exists;
            }

            allMissing &= state == RegistryFileState.ConfirmedMissing;
        }

        return allMissing ? RegistryFileState.ConfirmedMissing : RegistryFileState.Unknown;
    }

    private static bool IsIndeterminate(Exception ex) =>
        ex is UnauthorizedAccessException or SecurityException or IOException or ArgumentException or NotSupportedException;

    /// <summary>ドライブ文字つきの完全修飾パスだけ。UNC・デバイス名前空間 (<c>\\?\</c> <c>\\.\</c>)・相対名・<c>..</c> を含むものは除く。</summary>
    private static bool IsLocalFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/'))
        {
            return false;
        }

        return !path.Split(['\\', '/']).Contains("..");
    }

    private bool IsFixedReadyDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            var status = _probeDrive(root);
            return status.DriveType == DriveType.Fixed && status.IsReady;
        }
        catch (Exception ex) when (IsIndeterminate(ex))
        {
            return false;
        }
    }

    /// <summary>既存の祖先がすべて通常のディレクトリ (再解析点・ファイルでない) で、残りが存在しないことを確かめる。</summary>
    private bool HasOnlyPlainAncestors(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            var directory = Path.GetDirectoryName(full);
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            var current = root;
            if (!IsPlainDirectory(current))
            {
                return false;
            }

            var segments = Path.GetRelativePath(root, directory).Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);
                try
                {
                    if (!IsPlainDirectory(current))
                    {
                        return false;
                    }
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    return true;
                }
            }

            return true;
        }
        catch (Exception ex) when (IsIndeterminate(ex))
        {
            return false;
        }
    }

    private bool IsPlainDirectory(string path)
    {
        var attributes = _getAttributes(path);
        return (attributes & FileAttributes.ReparsePoint) == 0 && (attributes & FileAttributes.Directory) != 0;
    }

    private static (DriveType DriveType, bool IsReady) DefaultProbeDrive(string root)
    {
        var drive = new DriveInfo(root);
        return (drive.DriveType, drive.IsReady);
    }
}
