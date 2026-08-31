using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>掃除対象の扱い方。</summary>
public enum CleanupTargetKind
{
    /// <summary>フォルダ自体は残し、中身だけ削除する (バッチの :CleanDirectory 相当)。</summary>
    Contents,

    /// <summary>フォルダ内のパターン一致ファイルだけ削除する (del 相当)。サブフォルダは辿らない。</summary>
    Files,
}

/// <summary>
/// 掃除対象 1 件。<see cref="RawPath"/> は %LOCALAPPDATA% 等を含む未展開のパスで持ち、
/// 実行時に展開する (昇格後もプロセスの環境変数は起動ユーザーのものを引き継ぐ)。
/// </summary>
/// <param name="RawPath">未展開のフォルダパス。</param>
/// <param name="Kind">削除の仕方。</param>
/// <param name="Pattern"><see cref="CleanupTargetKind.Files"/> のときのファイル名パターン。</param>
public sealed record CleanupTarget(string RawPath, CleanupTargetKind Kind, string? Pattern = null)
{
    /// <summary>フォルダの中身だけ消す。</summary>
    public static CleanupTarget Contents(string rawPath) => new(rawPath, CleanupTargetKind.Contents);

    /// <summary>フォルダ直下のパターン一致ファイルだけ消す。</summary>
    public static CleanupTarget Files(string rawDirectory, string pattern) =>
        new(rawDirectory, CleanupTargetKind.Files, pattern);

}

/// <summary>削除処理の集計。</summary>
public sealed class CleanupOutcome
{
    /// <summary>削除できたファイル数。</summary>
    public long DeletedFiles { get; internal set; }

    /// <summary>削除できたフォルダ数。</summary>
    public long DeletedDirectories { get; internal set; }

    /// <summary>解放したバイト数。</summary>
    public long FreedBytes { get; internal set; }

    /// <summary>使用中・権限不足で削除できなかった項目数。</summary>
    public long Blocked { get; internal set; }

    /// <summary>再起動時削除として予約した項目数。</summary>
    public long ScheduledForReboot { get; internal set; }

    /// <summary>存在しなかった対象の数 (未インストールのアプリ等。異常ではない)。</summary>
    public int MissingTargets { get; internal set; }

    /// <summary>安全ガードにより実行を拒否した対象。</summary>
    public List<string> RejectedTargets { get; } = [];

    /// <summary>1 件も対象を処理しなかったか (存在しない・拒否のみ)。</summary>
    public bool DidNothing =>
        DeletedFiles == 0 && DeletedDirectories == 0 && ScheduledForReboot == 0;
}

/// <summary>
/// 一時ファイル・キャッシュの削除を行う共通エンジン。
/// 既知のキャッシュ／ログフォルダの中身と、既知のキャッシュファイルだけを C# ネイティブに削除し、
/// 使用中のファイルは飛ばして続行し、何をどれだけ消したかを集計して返す。
/// </summary>
[SupportedOSPlatform("windows")]
public static class FileCleanupEngine
{
    private const int MaxDepth = 64;
    private const int MoveFileDelayUntilReboot = 0x00000004;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    /// <summary>削除してはいけないフォルダ (これ自身、およびドライブ直下は対象にできない)。</summary>
    private static readonly Lazy<HashSet<string>> ProtectedPaths = new(BuildProtectedPaths);

    /// <summary>
    /// 対象を順に処理する。フォルダごとの成否は集計に畳み込み、途中で例外を投げない
    /// (キャンセルのみ伝播する)。
    /// </summary>
    /// <param name="targets">処理する対象。</param>
    /// <param name="scheduleBlockedForReboot">
    /// 使用中で消せなかったファイルを再起動時削除として予約するか。
    /// アイコン・フォントキャッシュのようにシェルが握って離さないファイルにだけ使う。
    /// </param>
    /// <param name="progress">進捗行の通知先。</param>
    /// <param name="ct">キャンセルトークン。</param>
    public static CleanupOutcome Run(
        IEnumerable<CleanupTarget> targets,
        bool scheduleBlockedForReboot,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var outcome = new CleanupOutcome();
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();

            // ファイル名指定は保護フォルダ直下でも安全 (IconCache.db / FNTCACHE.DAT 等)。
            // フォルダの中身ごと消す指定だけ基点フォルダを禁止する。
            var allowProtectedDirectory = target.Kind == CleanupTargetKind.Files;

            if (!TryResolve(target.RawPath, allowProtectedDirectory, out var fullPath, out var rejection))
            {
                outcome.RejectedTargets.Add($"{target.RawPath} ({rejection})");
                LoggerBootstrap.Log.Error($"cleanup: 対象を拒否しました {target.RawPath}: {rejection}");
                continue;
            }

            if (!Directory.Exists(fullPath))
            {
                outcome.MissingTargets++;
                continue;
            }

            // 対象だけでなく全祖先を no-follow で開き、走査が終わるまで名前の差し替えを防ぐ。
            // ブラウザプロファイル等の祖先がジャンクションでもリンク先へ入らない。
            if (!DirectoryHandleGuard.TryOpenPath(fullPath, out var pathGuard, out var pathRejection))
            {
                outcome.RejectedTargets.Add($"{fullPath} ({pathRejection})");
                continue;
            }

            using (pathGuard)
            {
                progress?.Report($"  - クリーンアップ: {fullPath}");

                switch (target.Kind)
                {
                    case CleanupTargetKind.Contents:
                        DeleteContents(new DirectoryInfo(fullPath), outcome, scheduleBlockedForReboot, depth: 0, ct);
                        break;
                    case CleanupTargetKind.Files:
                        DeleteMatchingFiles(fullPath, target.Pattern!, outcome, scheduleBlockedForReboot, ct);
                        break;
                }
            }
        }

        return outcome;
    }

    /// <summary>
    /// 対象に、現在削除できるファイルまたはフォルダが 1 件以上あるかを安全に確認する。
    /// チェックリストへ「この PC で実際に検出できた対象」だけを表示するための軽量な事前判定で、
    /// <see cref="CleanupTargetKind.Contents"/> は直下に削除可能な項目があるか、
    /// <see cref="CleanupTargetKind.Files"/> は直下にパターン一致ファイルがあるかだけを見る。
    /// </summary>
    internal static bool HasCleanupCandidates(CleanupTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var allowProtectedDirectory = target.Kind == CleanupTargetKind.Files;
        if (!TryResolve(target.RawPath, allowProtectedDirectory, out var fullPath, out _) ||
            !Directory.Exists(fullPath))
        {
            return false;
        }

        try
        {
            if (!DirectoryHandleGuard.TryOpenPath(fullPath, out var pathGuard, out _))
            {
                return false;
            }

            using (pathGuard)
            {
                var directory = new DirectoryInfo(fullPath);
                return target.Kind switch
                {
                    CleanupTargetKind.Contents => directory
                        .EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly)
                        .Any(IsRemovableEntry),
                    CleanupTargetKind.Files => directory
                        .EnumerateFiles(target.Pattern!, SearchOption.TopDirectoryOnly)
                        .Any(file => MatchesCleanupPattern(file.Name, target.Pattern!) && IsRemovableEntry(file)),
                    _ => false,
                };
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // 読み取れない対象は「検出済み」と断定せず、チェックリストから隠す。
            return false;
        }
    }

    private static bool IsRemovableEntry(FileSystemInfo entry)
    {
        return TryGetAttributes(entry, out var attributes) &&
               (attributes & FileAttributes.ReparsePoint) == 0;
    }

    /// <summary>
    /// 列挙元が通常ディレクトリだけで構成されているかを no-follow で確認する。
    /// 可変のブラウザプロファイルや ETL ツリーを組み立てる側の防御にも使う。
    /// </summary>
    internal static bool CanSafelyTraverseDirectory(string path)
    {
        if (!Directory.Exists(path) ||
            !DirectoryHandleGuard.TryOpenPath(path, out var pathGuard, out _))
        {
            return false;
        }

        pathGuard.Dispose();
        return true;
    }

    private static bool TryGetAttributes(FileSystemInfo entry, out FileAttributes attributes)
    {
        try
        {
            attributes = entry.Attributes;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            attributes = default;
            return false;
        }
    }

    /// <summary>
    /// 未展開パスを絶対パスへ解決し、削除して良い場所かを検査する。
    /// 環境変数が未定義でパスが壊れた場合 (例: %ProgramData% 未定義で "\LGHUB\cache") に
    /// 意図しない場所を消さないための最終ガード。
    /// </summary>
    internal static bool TryResolve(string rawPath, out string fullPath, out string rejection) =>
        TryResolve(rawPath, allowProtectedDirectory: false, out fullPath, out rejection);

    /// <param name="allowProtectedDirectory">
    /// Windows やユーザープロファイルの基点フォルダ自体を対象として許可するか。
    /// フォルダ内の特定ファイルだけを消す指定 (<see cref="CleanupTargetKind.Files"/>) でのみ true にする。
    /// </param>
    /// <inheritdoc cref="TryResolve(string, out string, out string)"/>
    internal static bool TryResolve(
        string rawPath,
        bool allowProtectedDirectory,
        out string fullPath,
        out string rejection)
    {
        fullPath = string.Empty;
        rejection = string.Empty;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            rejection = "パスが空です";
            return false;
        }

        var expanded = Environment.ExpandEnvironmentVariables(rawPath);
        if (expanded.Contains('%'))
        {
            rejection = "環境変数を解決できませんでした";
            return false;
        }

        if (!Path.IsPathFullyQualified(expanded))
        {
            rejection = "絶対パスではありません";
            return false;
        }

        string normalized;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            rejection = "パスを正規化できませんでした";
            return false;
        }

        var root = Path.GetPathRoot(normalized);
        if (root is null)
        {
            rejection = "ドライブ直下は対象にできません";
            return false;
        }

        var isDriveRoot = normalized.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);
        if (isDriveRoot)
        {
            rejection = "ドライブ直下は対象にできません";
            return false;
        }

        if (!allowProtectedDirectory && ProtectedPaths.Value.Contains(normalized))
        {
            rejection = "システムまたはユーザープロファイルの基点フォルダです";
            return false;
        }

        fullPath = normalized;
        return true;
    }

    /// <summary>Windows / Program Files / 各プロファイル基点など、丸ごと消してはいけない場所。</summary>
    private static HashSet<string> BuildProtectedPaths()
    {
        var folders = new[]
        {
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.DesktopDirectory,
        };

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(path))
            {
                result.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
            }
        }

        // Users 直下 (プロファイル置き場) も丸ごと対象になり得ないように加える。
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var usersRoot = string.IsNullOrWhiteSpace(profile) ? null : Path.GetDirectoryName(profile);
        if (!string.IsNullOrWhiteSpace(usersRoot))
        {
            result.Add(Path.TrimEndingDirectorySeparator(usersRoot));
        }

        return result;
    }

    private static void DeleteContents(
        DirectoryInfo directory,
        CleanupOutcome outcome,
        bool scheduleBlockedForReboot,
        int depth,
        CancellationToken ct)
    {
        if (depth > MaxDepth)
        {
            outcome.Blocked++;
            return;
        }

        // 呼び出し元が祖先を固定したまま、現在の要素も no-follow で固定する。
        // 属性検査と列挙の間に通常フォルダをジャンクションへ差し替える競合を防ぐ。
        if (!DirectoryHandleGuard.TryOpenDirectory(directory.FullName, out var directoryGuard, out var rejection))
        {
            outcome.RejectedTargets.Add($"{directory.FullName} ({rejection})");
            return;
        }

        using var pinnedDirectory = directoryGuard;

        FileInfo[] files;
        DirectoryInfo[] subdirectories;
        try
        {
            files = directory.GetFiles();
            subdirectories = directory.GetDirectories();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            outcome.Blocked++;
            return;
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            TryDeleteFile(file, outcome, scheduleBlockedForReboot);
        }

        foreach (var subdirectory in subdirectories)
        {
            ct.ThrowIfCancellationRequested();

            // ジャンクション・シンボリックリンクは辿らず、リンク自体も利用者が意図した
            // キャッシュ配置設定かもしれないため削除しない。
            // (%LOCALAPPDATA% 配下には旧 "Application Data" 等の再解析ポイントがあり、
            //  追従すると同じ場所を無限に降りたり対象外を消したりする)。
            if (!TryGetAttributes(subdirectory, out var subdirectoryAttributes))
            {
                outcome.Blocked++;
                continue;
            }

            if ((subdirectoryAttributes & FileAttributes.ReparsePoint) != 0)
            {
                outcome.RejectedTargets.Add($"{subdirectory.FullName} (リンクを保護するため)");
                continue;
            }

            DeleteContents(subdirectory, outcome, scheduleBlockedForReboot, depth + 1, ct);
            TryDeleteDirectory(subdirectory, outcome);
        }
    }

    private static void DeleteMatchingFiles(
        string directory,
        string pattern,
        CleanupOutcome outcome,
        bool scheduleBlockedForReboot,
        CancellationToken ct)
    {
        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(directory)
                .GetFiles(pattern, SearchOption.TopDirectoryOnly)
                .Where(file => MatchesCleanupPattern(file.Name, pattern))
                .ToArray();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            outcome.Blocked++;
            return;
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            TryDeleteFile(file, outcome, scheduleBlockedForReboot);
        }
    }

    /// <summary>
    /// .NET の 3 文字拡張子ワイルドカードは、*.etl で .etl1 等も返すため、
    /// 掃除で使う単純な拡張子・固定名パターンは名前側でも厳密に絞る。
    /// </summary>
    internal static bool MatchesCleanupPattern(string fileName, string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        if (pattern.StartsWith("*.", StringComparison.Ordinal) &&
            pattern.AsSpan(2).IndexOfAny('*', '?') < 0)
        {
            return string.Equals(Path.GetExtension(fileName), pattern[1..], StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.AsSpan().IndexOfAny('*', '?') < 0)
        {
            return string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase);
        }

        // 現行カタログに複雑なワイルドカードは無い。追加時は OS の列挙結果を維持する。
        return true;
    }

    private static void TryDeleteFile(FileInfo file, CleanupOutcome outcome, bool scheduleBlockedForReboot)
    {
        FileAttributes originalAttributes;
        try
        {
            originalAttributes = file.Attributes;
            if ((originalAttributes & FileAttributes.ReparsePoint) != 0)
            {
                outcome.RejectedTargets.Add($"{file.FullName} (リンクを保護するため)");
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            outcome.Blocked++;
            return;
        }

        long length;
        try
        {
            length = file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            length = 0;
        }

        var attributesChanged = false;
        try
        {
            if ((originalAttributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
            {
                file.Attributes = FileAttributes.Normal;
                attributesChanged = true;
            }

            file.Delete();
            outcome.DeletedFiles++;
            outcome.FreedBytes += length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (scheduleBlockedForReboot && TryScheduleDeleteOnReboot(file.FullName))
            {
                outcome.ScheduledForReboot++;
                return;
            }

            if (attributesChanged)
            {
                TryRestoreAttributes(file, originalAttributes);
            }

            outcome.Blocked++;
        }
    }

    private static void TryDeleteDirectory(DirectoryInfo directory, CleanupOutcome outcome)
    {
        var originalAttributes = FileAttributes.Normal;
        var attributesChanged = false;
        try
        {
            originalAttributes = directory.Attributes;
            if ((originalAttributes & FileAttributes.ReparsePoint) != 0)
            {
                outcome.RejectedTargets.Add($"{directory.FullName} (リンクを保護するため)");
                return;
            }

            if ((originalAttributes & FileAttributes.ReadOnly) != 0)
            {
                directory.Attributes = FileAttributes.Directory;
                attributesChanged = true;
            }

            directory.Delete(recursive: false);
            outcome.DeletedDirectories++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (attributesChanged)
            {
                TryRestoreAttributes(directory, originalAttributes);
            }

            // 中身が残っている (使用中) 場合はファイル側で Blocked を数えているため二重計上しない。
        }
    }

    private static void TryRestoreAttributes(FileSystemInfo entry, FileAttributes attributes)
    {
        try
        {
            entry.Attributes = attributes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 削除失敗を属性復元失敗で上書きせず、残った項目として集計を続ける。
        }
    }

    /// <summary>
    /// 各ディレクトリを FILE_FLAG_OPEN_REPARSE_POINT で開き、リンクを辿らず属性を確認する。
    /// FILE_SHARE_DELETE を付けずに保持することで、走査中の名前差し替えも防ぐ。
    /// </summary>
    private sealed class DirectoryHandleGuard(List<SafeFileHandle> handles) : IDisposable
    {
        public static bool TryOpenPath(
            string path,
            out DirectoryHandleGuard guard,
            out string rejection)
        {
            var handles = new List<SafeFileHandle>();
            guard = null!;
            rejection = string.Empty;

            try
            {
                var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                var root = Path.GetPathRoot(fullPath);
                if (string.IsNullOrWhiteSpace(root))
                {
                    rejection = "パスのルートを確認できませんでした";
                    return false;
                }

                if (!TryOpenComponent(root, handles, out rejection))
                {
                    return false;
                }

                var current = root;
                var relative = Path.GetRelativePath(root, fullPath);
                foreach (var segment in relative.Split(
                             [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, segment);
                    if (!TryOpenComponent(current, handles, out rejection))
                    {
                        return false;
                    }
                }

                guard = new DirectoryHandleGuard(handles);
                handles = [];
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                rejection = "再解析点の安全確認に失敗しました";
                return false;
            }
            finally
            {
                foreach (var handle in handles)
                {
                    handle.Dispose();
                }
            }
        }

        public static bool TryOpenDirectory(
            string path,
            out DirectoryHandleGuard guard,
            out string rejection)
        {
            var handles = new List<SafeFileHandle>();
            guard = null!;
            if (!TryOpenComponent(path, handles, out rejection))
            {
                return false;
            }

            guard = new DirectoryHandleGuard(handles);
            return true;
        }

        private static bool TryOpenComponent(
            string path,
            List<SafeFileHandle> handles,
            out string rejection)
        {
            var handle = CreateFile(
                path,
                FileReadAttributes,
                FileShare.ReadWrite,
                nint.Zero,
                FileMode.Open,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                nint.Zero);

            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                rejection = $"再解析点の安全確認に失敗しました (Win32 {error})";
                return false;
            }

            if (!GetFileInformationByHandle(handle, out var information))
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                rejection = $"再解析点の属性を確認できませんでした (Win32 {error})";
                return false;
            }

            if ((information.FileAttributes & FileAttributes.ReparsePoint) != 0)
            {
                handle.Dispose();
                rejection = "リンク先の実体を消さないため";
                return false;
            }

            if ((information.FileAttributes & FileAttributes.Directory) == 0)
            {
                handle.Dispose();
                rejection = "ディレクトリではありません";
                return false;
            }

            handles.Add(handle);
            rejection = string.Empty;
            return true;
        }

        public void Dispose()
        {
            for (var i = handles.Count - 1; i >= 0; i--)
            {
                handles[i].Dispose();
            }

            handles.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public FileAttributes FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    /// <summary>
    /// 使用中のファイルを再起動時削除として予約する。シェルが常時開いている
    /// アイコン・フォントキャッシュを消すための正規手段。
    /// </summary>
    private static bool TryScheduleDeleteOnReboot(string path)
    {
        try
        {
            return MoveFileEx(path, null, MoveFileDelayUntilReboot);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    }

    /// <summary>集計を利用者向けの日本語の行に整形する。</summary>
    public static IReadOnlyList<string> DescribeOutcome(CleanupOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var lines = new List<string>
        {
            $"  - {outcome.DeletedFiles:N0} 個のファイルと {outcome.DeletedDirectories:N0} 個のフォルダを削除し、{FormatBytes(outcome.FreedBytes)} を解放しました",
        };

        if (outcome.ScheduledForReboot > 0)
        {
            lines.Add($"  - 使用中の {outcome.ScheduledForReboot:N0} 個のファイルは次回の再起動時に削除するよう予約しました");
        }

        if (outcome.Blocked > 0)
        {
            lines.Add($"  - 使用中または権限不足で {outcome.Blocked:N0} 個の項目を削除できませんでした (次回起動直後の再実行で消えることがあります)");
        }

        if (outcome.MissingTargets > 0)
        {
            lines.Add($"  - {outcome.MissingTargets:N0} 箇所は存在しなかったためスキップしました (未インストールのアプリ等)");
        }

        foreach (var rejected in outcome.RejectedTargets)
        {
            lines.Add($"  - 安全のため対象から除外しました: {rejected}");
        }

        return lines;
    }

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes:N0} B"
            : value.ToString("N1", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        nint securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, int flags);
}
