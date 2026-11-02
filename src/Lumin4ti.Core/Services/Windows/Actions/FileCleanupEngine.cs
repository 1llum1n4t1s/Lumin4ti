using System.Globalization;
using System.Runtime.Versioning;

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
    private const int MaxBlockedExamples = 8;
    private readonly List<string> _blockedExamples = [];

    /// <summary>削除できたファイル数。</summary>
    public long DeletedFiles { get; internal set; }

    /// <summary>削除できたフォルダ数。</summary>
    public long DeletedDirectories { get; internal set; }

    /// <summary>解放したバイト数。</summary>
    public long FreedBytes { get; internal set; }

    /// <summary>使用中・権限不足で削除できなかった項目数。</summary>
    public long Blocked { get; internal set; }
    internal long LockBlocked { get; private set; }

    /// <summary>削除できなかった理由とパスの代表例。大量のファイルでも記録量を一定に保つ。</summary>
    public IReadOnlyList<string> BlockedExamples => _blockedExamples;

    internal void RecordBlocked(string path, Exception exception)
    {
        var error = exception is System.ComponentModel.Win32Exception native
            ? 0x80070000u | (uint)native.NativeErrorCode : unchecked((uint)exception.HResult);
        var locked = error is 0x80070020 or 0x80070021;
        RecordBlockedCore(path, $"{exception.Message} (HRESULT=0x{error:X8})", locked);
    }

    internal void RecordBlocked(string path, string reason)
        => RecordBlockedCore(path, reason, false);

    private void RecordBlockedCore(string path, string reason, bool locked)
    {
        Blocked++;
        if (locked) LockBlocked++;
        if (_blockedExamples.Count < MaxBlockedExamples)
        {
            var diagnostic = $"{path}: {reason}".Replace('\r', ' ').Replace('\n', ' ');
            _blockedExamples.Add(diagnostic.Length <= 1024 ? diagnostic : diagnostic[..1024] + "…");
        }
    }

    /// <summary>再起動時削除として予約した項目数。</summary>
    public long ScheduledForReboot { get; internal set; }

    internal List<CleanupTarget> BlockedTargets { get; } = [];

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
            if (!TryAcquireTraversalGuard(fullPath, out var pathGuard))
            {
                outcome.RejectedTargets.Add($"{fullPath} (リンク非追従で開けませんでした)");
                continue;
            }

            using (pathGuard)
            {
                var blockedBefore = outcome.Blocked;
                var locksBefore = outcome.LockBlocked;
                var rejectedBefore = outcome.RejectedTargets.Count;
                progress?.Report($"  - クリーンアップ: {fullPath}");

                switch (target.Kind)
                {
                    case CleanupTargetKind.Contents:
                        DeleteContents(pathGuard, outcome, scheduleBlockedForReboot, depth: 0, ct);
                        break;
                    case CleanupTargetKind.Files:
                        DeleteMatchingFiles(pathGuard, target.Pattern!, outcome, scheduleBlockedForReboot, ct);
                        break;
                }
                var locks = outcome.LockBlocked - locksBefore;
                if (scheduleBlockedForReboot && locks > 0 && locks == outcome.Blocked - blockedBefore &&
                    outcome.RejectedTargets.Count == rejectedBefore) outcome.BlockedTargets.Add(target);
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
            using var directory = NativeCleanupEntry.Open(fullPath, true);
            return directory.Enumerate().Any(entry =>
                (entry.Attributes & FileAttributes.ReparsePoint) == 0 &&
                (target.Kind == CleanupTargetKind.Contents ||
                 ((entry.Attributes & FileAttributes.Directory) == 0 && MatchesCleanupPattern(entry.Name, target.Pattern!))));
        }
        catch (Exception ex) when (IsNativeFailure(ex)) { return false; }
    }
    /// <summary>
    /// 列挙元と全祖先を no-follow で確認し、名前差し替えを防ぐハンドルを返す。
    /// 呼び出し側は列挙が終わるまで保持し、リンク先を列挙しない。
    /// </summary>
    internal static bool TryAcquireTraversalGuard(string path, out NativeCleanupEntry guard, NativeCleanupEntry? context = null)
    {
        guard = null!;
        try { guard = context is null ? NativeCleanupEntry.Open(path, true) : context.OpenDescendant(path, true); return true; }
        catch (Exception ex) when (IsNativeFailure(ex)) { return false; }
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
        NativeCleanupEntry directory, CleanupOutcome outcome, bool scheduleBlockedForReboot, int depth, CancellationToken ct)
    {
        if (depth > MaxDepth)
        {
            outcome.RecordBlocked(directory.Path, "安全な再帰深度の上限を超えました");
            return;
        }

        IReadOnlyList<(string Name, FileAttributes Attributes)> entries;
        try { entries = directory.Enumerate().ToArray(); }
        catch (Exception ex) when (IsNativeFailure(ex)) { outcome.RecordBlocked(directory.Path, ex); return; }
        foreach (var entry in entries.OrderBy(entry => (entry.Attributes & FileAttributes.Directory) != 0))
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(directory.Path, entry.Name);
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                outcome.RejectedTargets.Add($"{path} (リンクを保護するため)");
                continue;
            }
            var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
            DeleteEntry(directory, path, isDirectory, outcome, scheduleBlockedForReboot, depth, ct);
        }
    }

    private static void DeleteMatchingFiles(
        NativeCleanupEntry directory, string pattern, CleanupOutcome outcome, bool scheduleBlockedForReboot, CancellationToken ct)
    {
        IReadOnlyList<(string Name, FileAttributes Attributes)> entries;
        try { entries = directory.Enumerate().ToArray(); }
        catch (Exception ex) when (IsNativeFailure(ex)) { outcome.RecordBlocked(directory.Path, ex); return; }
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if ((entry.Attributes & FileAttributes.Directory) != 0 || !MatchesCleanupPattern(entry.Name, pattern)) continue;
            var path = Path.Combine(directory.Path, entry.Name);
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                outcome.RejectedTargets.Add($"{path} (リンクを保護するため)");
                continue;
            }
            DeleteEntry(directory, path, false, outcome, scheduleBlockedForReboot, 0, ct);
        }
    }

    internal static bool MatchesCleanupPattern(string fileName, string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        if (pattern.StartsWith("*.", StringComparison.Ordinal) && pattern.AsSpan(2).IndexOfAny('*', '?') < 0)
            return string.Equals(Path.GetExtension(fileName), pattern[1..], StringComparison.OrdinalIgnoreCase);
        if (pattern.IndexOfAny(['*', '?']) < 0) return string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase);
        return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, fileName, ignoreCase: true);
    }

    private static bool IsNativeFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception;

    private static void DeleteEntry(NativeCleanupEntry parent, string path, bool directory, CleanupOutcome outcome, bool schedule, int depth, CancellationToken ct)
    {
        var blockedBefore = outcome.Blocked;
        var rejectedBefore = outcome.RejectedTargets.Count;
        try
        {
            // 祖先は外側の directory handle 群で固定済み。別の祖先 handle を開かず、
            // DELETE を要求した同じ実体を再帰終了・属性復元まで保持する。
            using var entry = parent.OpenDescendant(path, directory, delete: true);
            var original = entry.Attributes;
            var changed = false;
            try
            {
                if (directory) DeleteContents(entry, outcome, schedule, depth + 1, ct);
                ct.ThrowIfCancellationRequested();
                entry.EnsureNotReparse();
                var clearAttributes = directory ? FileAttributes.ReadOnly : FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;
                if ((original & clearAttributes) != 0)
                {
                    entry.SetAttributes(original & ~clearAttributes);
                    changed = true;
                }
                var length = directory ? 0 : entry.Length;
                entry.Delete();
                if (directory) outcome.DeletedDirectories++;
                else { outcome.DeletedFiles++; outcome.FreedBytes += length; }
            }
            catch
            {
                if (changed)
                {
                    try { entry.SetAttributes(original); }
                    catch (Exception ex) when (IsNativeFailure(ex)) { /* 元の失敗を維持する。 */ }
                }
                throw;
            }
        }
        catch (Exception ex) when (IsNativeFailure(ex))
        {
            if (directory && ex is System.ComponentModel.Win32Exception { NativeErrorCode: 145 } &&
                (outcome.Blocked > blockedBefore || outcome.RejectedTargets.Count > rejectedBefore)) return;
            // 遅延したパス削除は行わない。呼出し側が既知ターゲットの意図だけを予約する。
            outcome.RecordBlocked(path, ex);
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
            lines.Add($"  - 使用中のキャッシュ {outcome.ScheduledForReboot:N0} 種を、起動時に再列挙するよう予約しました");
        }

        if (outcome.Blocked > 0)
        {
            lines.Add($"  - 使用中または権限不足で {outcome.Blocked:N0} 個の項目を削除できませんでした");
            foreach (var example in outcome.BlockedExamples)
            {
                lines.Add($"  - 削除できなかった項目: {example}");
            }
            if (outcome.Blocked > outcome.BlockedExamples.Count)
            {
                lines.Add($"  - 残り {outcome.Blocked - outcome.BlockedExamples.Count:N0} 件の詳細は省略しました");
            }
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

}
