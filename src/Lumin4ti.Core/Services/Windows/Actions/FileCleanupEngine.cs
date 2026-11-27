using System.Globalization;
using System.Runtime.Versioning;
using Lumin4ti.Core.Services.Windows.Cleanup;

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
/// <param name="Pattern">
/// ファイル名パターン。<see cref="CleanupTargetKind.Files"/> では必須。
/// <see cref="CleanupTargetKind.Contents"/> に付けるとパターン一致ファイルだけを対象にする。
/// </param>
/// <param name="Recurse"><see cref="CleanupTargetKind.Files"/> でサブフォルダーも辿るか (Contents は常に辿る)。</param>
/// <param name="RemoveEmptyDirs">絞り込み時に、対象を消した結果空になったサブフォルダーも消すか。基点自体は残す。</param>
/// <param name="OlderThanDays">更新時刻がこの日数より古いファイルだけを対象にする。</param>
public sealed record CleanupTarget(
    string RawPath,
    CleanupTargetKind Kind,
    string? Pattern = null,
    bool Recurse = false,
    bool RemoveEmptyDirs = false,
    int? OlderThanDays = null)
{
    /// <summary>フォルダの中身だけ消す。</summary>
    public static CleanupTarget Contents(string rawPath) => new(rawPath, CleanupTargetKind.Contents);

    /// <summary>フォルダ直下のパターン一致ファイルだけ消す (<paramref name="recurse"/> でサブフォルダーも辿る)。</summary>
    public static CleanupTarget Files(string rawDirectory, string pattern, bool recurse = false, bool removeEmptyDirs = false, int? olderThanDays = null) =>
        new(rawDirectory, CleanupTargetKind.Files, pattern, recurse, removeEmptyDirs, olderThanDays);

    /// <summary>
    /// 利用者が選んだフォルダー (カスタムフォルダー・高度クリーンのカスタムフォルダー) の中身を空にする指定。
    /// 認証情報の拒否表に当たる名前のファイル・フォルダーは、走査でも削除でも残す。
    /// </summary>
    public static CleanupTarget CustomFolder(string fullPath) => new(fullPath, CleanupTargetKind.Contents) { KeepCredentials = true, IsActualPath = true };

    /// <summary>
    /// <see cref="RawPath"/> が展開済みの実パスか (規則の展開・カスタム・高度クリーンが作る対象)。true なら環境変数を再展開せず、
    /// <c>%</c> を含む実在の名前もそのまま扱う。基点の値に残った <c>%名前%</c> が、信頼の検査の後に別の場所へ展開されるのを防ぐ。
    /// </summary>
    public bool IsActualPath { get; init; }

    /// <summary>認証情報の拒否表に当たる名前の子孫を残すか (走査と削除が <see cref="FileCleanupEngine.KeepsAsCredential"/> で共有する)。</summary>
    public bool KeepCredentials { get; init; }

    /// <summary>ファイル単位で絞り込むか。絞り込み時はフォルダーを丸ごとではなく空になったものだけ消す。</summary>
    internal bool Filtered => Kind == CleanupTargetKind.Files || Pattern is not null || OlderThanDays is not null;

    /// <summary>サブフォルダーへ入るか。</summary>
    internal bool Descends => Kind == CleanupTargetKind.Contents || Recurse;
}

/// <summary>削除の追加条件。走査結果から作り、走査と削除で同じ除外を使う。</summary>
/// <param name="Filter">除外の一致器。</param>
/// <param name="CreatedBefore">この時刻より後に作られたファイル・フォルダーは消さずに残す (走査後に作られたものの保護)。</param>
/// <param name="Wipe">1 パス上書きしてから消す。リンク数が 2 以上・圧縮・スパース・EFS・読取専用は上書きせず削除だけ行う。</param>
public sealed record CleanupRunOptions(CleanupExclusionMatcher? Filter = null, DateTime? CreatedBefore = null, bool Wipe = false)
{
    /// <summary>古さの基準時刻。既定は現在 (テスト用に差し替える)。</summary>
    public DateTime? Now { get; init; }

    /// <summary>同じ実行で先に処理した別の規則が既に扱ったものか。(パス, フォルダーか, 更新時刻) を受け、true なら数えず・失敗にもせず黙って飛ばす。</summary>
    internal Func<string, bool, long, bool>? AlreadyHandled { get; init; }
}

/// <summary>走査時に確認したファイルの実体。削除時に FileId・サイズ・更新時刻が一致した同一ハンドルだけを消す。</summary>
/// <param name="Path">絶対パス。</param>
/// <param name="VolumeSerialNumber">ボリューム通し番号。</param>
/// <param name="FileIdLow">FileId の下位 64 ビット。</param>
/// <param name="FileIdHigh">FileId の上位 64 ビット。</param>
/// <param name="Size">EndOfFile (バイト)。</param>
/// <param name="LastWriteFileTime">更新時刻 (FILETIME)。</param>
public sealed record CleanupFileIdentity(string Path, ulong VolumeSerialNumber, ulong FileIdLow, ulong FileIdHigh, long Size, long LastWriteFileTime);

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

    /// <summary>除外・走査後の作成・走査時との不一致により、意図して残した項目数。失敗ではない。</summary>
    public long Retained { get; internal set; }

    /// <summary>先に処理した別の規則が扱ったため飛ばした項目数。残るフォルダーの削除失敗 (145) を二重の失敗にしないために使う。</summary>
    internal long HandledElsewhere { get; set; }

    /// <summary>1 パス上書きしてから削除したファイル数。</summary>
    public long WipedFiles { get; internal set; }

    /// <summary>上書きできず (対象外の属性・リンク共有・失敗・取消)、削除だけ行ったファイル数。</summary>
    public long WipeSkipped { get; internal set; }

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
    /// <param name="options">除外・走査後に作られたものの保持・1 パス上書き。省略時は従来どおり。</param>
    public static CleanupOutcome Run(
        IEnumerable<CleanupTarget> targets,
        bool scheduleBlockedForReboot,
        IProgress<string>? progress,
        CancellationToken ct,
        CleanupRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var outcome = new CleanupOutcome();
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();

            if (!IsValidFilter(target, out var invalid))
            {
                outcome.RejectedTargets.Add($"{target.RawPath} ({invalid})");
                continue;
            }

            // ファイル名指定は保護フォルダ直下でも安全 (IconCache.db / FNTCACHE.DAT 等)。
            // フォルダの中身ごと消す指定だけ基点フォルダを禁止する。
            var allowProtectedDirectory = target.Kind == CleanupTargetKind.Files;

            if (!TryResolveTarget(target, allowProtectedDirectory, out var fullPath, out var rejection))
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

            if (options?.Filter?.IsExcluded(fullPath, true) == true)
            {
                outcome.Retained++;
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

                DeleteContents(pathGuard, new RunContext(target, outcome, scheduleBlockedForReboot, options, ct), depth: 0);
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
        if (!TryResolveTarget(target, allowProtectedDirectory, out var fullPath, out _) ||
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
    /// <summary>対象のパスを解決する。実パスの対象 (<see cref="CleanupTarget.IsActualPath"/>) は環境変数を展開しない。</summary>
    internal static bool TryResolveTarget(CleanupTarget target, bool allowProtectedDirectory, out string fullPath, out string rejection) =>
        target.IsActualPath
            ? TryResolveActual(target.RawPath, allowProtectedDirectory, out fullPath, out rejection)
            : TryResolve(target.RawPath, allowProtectedDirectory, out fullPath, out rejection);

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

        return TryResolveActual(expanded, allowProtectedDirectory, out fullPath, out rejection);
    }

    /// <summary>
    /// 実パス (環境変数を含まない、走査で得たパスなど) を正規化して検査する。環境変数は展開しないため、
    /// <c>%</c> を含む実在の名前 (<c>report%20x.tmp</c> など) も拒否しない。
    /// </summary>
    internal static bool TryResolveActual(string expanded, bool allowProtectedDirectory, out string fullPath, out string rejection)
    {
        fullPath = string.Empty;
        rejection = string.Empty;
        if (string.IsNullOrWhiteSpace(expanded))
        {
            rejection = "パスが空です";
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

    /// <summary>1 回の <see cref="Run"/> で 1 対象を処理する間の共有状態。</summary>
    private sealed class RunContext
    {
        internal RunContext(CleanupTarget target, CleanupOutcome outcome, bool schedule, CleanupRunOptions? options, CancellationToken ct)
        {
            Target = target;
            Outcome = outcome;
            Schedule = schedule;
            Matcher = options?.Filter;
            Wipe = options?.Wipe == true;
            AlreadyHandled = options?.AlreadyHandled;
            Ct = ct;
            CreatedCutoff = options?.CreatedBefore is { } created ? created.ToUniversalTime().ToFileTimeUtc() : null;
            AgeCutoff = AgeCutoffOf(target, options?.Now);
        }

        internal CleanupTarget Target { get; }
        internal CleanupOutcome Outcome { get; }
        internal bool Schedule { get; }
        internal CleanupExclusionMatcher? Matcher { get; }
        internal bool Wipe { get; }
        internal Func<string, bool, long, bool>? AlreadyHandled { get; }
        internal CancellationToken Ct { get; }
        internal long? CreatedCutoff { get; }
        internal long? AgeCutoff { get; }

        /// <summary>除外または走査後の作成のため残すべきか (走査と同じ判定)。</summary>
        internal bool Retains(string path, bool isDirectory, long creationTime) =>
            RetentionOf(Matcher, path, isDirectory, creationTime, CreatedCutoff) != CleanupRetention.None;
    }

    /// <summary>対象を残す理由。<see cref="None"/> なら対象。</summary>
    internal enum CleanupRetention
    {
        /// <summary>残す理由がない。</summary>
        None,
        /// <summary>利用者の除外に当たる。</summary>
        Excluded,
        /// <summary>走査より後に作られた。</summary>
        CreatedAfterScan,
    }

    /// <summary>走査と削除が共有する、除外と走査後作成の判定。</summary>
    internal static CleanupRetention RetentionOf(CleanupExclusionMatcher? matcher, string path, bool isDirectory, long creationTime, long? createdCutoff)
    {
        if (matcher?.IsExcluded(path, isDirectory) == true) return CleanupRetention.Excluded;
        return createdCutoff is { } cutoff && creationTime > cutoff ? CleanupRetention.CreatedAfterScan : CleanupRetention.None;
    }

    /// <summary>走査と削除が共有する、パターンと更新時刻による絞り込み。ファイルだけに使う。</summary>
    internal static bool IsTargetedFile(CleanupTarget target, string name, long lastWriteTime, long? ageCutoff) =>
        (target.Pattern is null || MatchesCleanupPattern(name, target.Pattern)) && (ageCutoff is not { } cutoff || lastWriteTime < cutoff);

    /// <summary>
    /// カスタムフォルダー (<see cref="CleanupTarget.KeepCredentials"/>) で、認証情報の拒否表に当たる名前の子孫か。
    /// 走査 (<see cref="FileCleanupScanner"/>) と削除が同じ判定を使い、当たるものは数えず・消さずに残す。
    /// </summary>
    internal static bool KeepsAsCredential(CleanupTarget target, string name) =>
        target.KeepCredentials && CustomFolderGuard.KeepsByName(name);

    internal static long? AgeCutoffOf(CleanupTarget target, DateTime? now) =>
        target.OlderThanDays is { } days
            ? (now ?? DateTime.UtcNow).ToUniversalTime().AddDays(-days).ToFileTimeUtc()
            : null;

    /// <summary>絞り込みの指定が実行できる形か (パターンの空文字や負の日数を拒否する)。</summary>
    internal static bool IsValidFilter(CleanupTarget target, out string reason)
    {
        reason = string.Empty;
        if ((target.Kind == CleanupTargetKind.Files || target.Pattern is not null) && string.IsNullOrWhiteSpace(target.Pattern)) reason = "ファイル名パターンが空です";
        else if (target.OlderThanDays is < 0) reason = "日数が負です";
        return reason.Length == 0;
    }

    private static void DeleteContents(NativeCleanupEntry directory, RunContext ctx, int depth)
    {
        var outcome = ctx.Outcome;
        if (depth > MaxDepth)
        {
            outcome.RecordBlocked(directory.Path, "安全な再帰深度の上限を超えました");
            return;
        }

        IReadOnlyList<CleanupEntryInfo> entries;
        try { entries = directory.EnumerateDetailed().ToArray(); }
        catch (Exception ex) when (IsNativeFailure(ex)) { outcome.RecordBlocked(directory.Path, ex); return; }
        foreach (var entry in entries.OrderBy(entry => (entry.Attributes & FileAttributes.Directory) != 0))
        {
            ctx.Ct.ThrowIfCancellationRequested();
            var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
            // 絞り込み対象外は触らない。再帰しない指定ではサブフォルダーも見ない。
            if (isDirectory ? !ctx.Target.Descends : !IsTargetedFile(ctx.Target, entry.Name, entry.LastWriteTime, ctx.AgeCutoff)) continue;
            var path = Path.Combine(directory.Path, entry.Name);
            if (ctx.AlreadyHandled?.Invoke(path, isDirectory, entry.LastWriteTime) == true)
            {
                // 先の規則が扱った (消した・失敗を報告した)。残るフォルダーの削除失敗は、この規則の失敗にしない。
                outcome.HandledElsewhere++;
                continue;
            }
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                outcome.RejectedTargets.Add($"{path} (リンクを保護するため)");
                continue;
            }
            if (KeepsAsCredential(ctx.Target, entry.Name))
            {
                outcome.Retained++;
                continue;
            }
            if (ctx.Retains(path, isDirectory, entry.CreationTime))
            {
                outcome.Retained++;
                continue;
            }
            DeleteEntry(directory, path, isDirectory, ctx, depth);
        }
    }

    /// <summary>
    /// 走査時に記録した実体 (FileId・サイズ・更新時刻) と一致する同一ハンドルだけを削除する。
    /// 祖先を固定して開いた後に照合し、差し替わっていたものは消さずに <see cref="CleanupOutcome.Retained"/> に数える。
    /// </summary>
    public static CleanupOutcome DeleteVerified(
        IEnumerable<CleanupFileIdentity> files,
        IProgress<string>? progress,
        CancellationToken ct,
        CleanupRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        var outcome = new CleanupOutcome();
        var ctx = new RunContext(new CleanupTarget(string.Empty, CleanupTargetKind.Contents), outcome, false, options, ct);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            // file.Path は走査で得た実パス。環境変数を展開しない (% を含む実在の名前を誤って拒否しない)。
            if (!TryResolveActual(file.Path, allowProtectedDirectory: false, out var fullPath, out var rejection))
            {
                outcome.RejectedTargets.Add($"{file.Path} ({rejection})");
                continue;
            }
            progress?.Report($"  - クリーンアップ: {fullPath}");
            DeleteVerifiedFile(file, fullPath, ctx);
        }
        return outcome;
    }

    private static void DeleteVerifiedFile(CleanupFileIdentity expected, string fullPath, RunContext ctx)
    {
        var outcome = ctx.Outcome;
        try
        {
            // 祖先と対象を no-follow で固定してから照合する。以降の削除は照合した同じ実体だけに作用する。
            using var entry = OpenFileForDelete(null, fullPath, ctx.Wipe);
            var identity = entry.Identity;
            var times = entry.Times;
            if ((entry.Attributes & FileAttributes.Directory) != 0 ||
                identity.VolumeSerialNumber != expected.VolumeSerialNumber || identity.FileIdLow != expected.FileIdLow ||
                identity.FileIdHigh != expected.FileIdHigh || entry.Length != expected.Size ||
                times.LastWrite != expected.LastWriteFileTime ||
                RetentionOf(ctx.Matcher, fullPath, false, times.Creation, ctx.CreatedCutoff) != CleanupRetention.None)
            {
                outcome.Retained++;
                return;
            }
            RemoveOpened(entry, false, ctx, 0);
        }
        catch (Exception ex) when (IsNativeFailure(ex)) { outcome.RecordBlocked(fullPath, ex); }
    }

    /// <summary>上書きを要求されたら書込み付きで開く。書込みを拒否された場合は削除だけの実体で開き直す。</summary>
    private static NativeCleanupEntry OpenFileForDelete(NativeCleanupEntry? parent, string path, bool wantWrite)
    {
        if (wantWrite)
        {
            try { return parent is null ? NativeCleanupEntry.Open(path, false, delete: true, write: true) : parent.OpenDescendant(path, false, delete: true, write: true); }
            catch (System.ComponentModel.Win32Exception) { /* 読取専用・共有違反などは削除だけに切り替える。 */ }
        }
        return parent is null ? NativeCleanupEntry.Open(path, false, delete: true) : parent.OpenDescendant(path, false, delete: true);
    }

    private static bool CanOverwrite(NativeCleanupEntry entry, FileAttributes attributes, long length)
    {
        const FileAttributes unsafeToOverwrite = FileAttributes.Compressed | FileAttributes.SparseFile | FileAttributes.Encrypted |
            FileAttributes.Offline | FileAttributes.ReparsePoint | (FileAttributes)0x00040000 | (FileAttributes)0x00400000;
        return entry.Writable && length > 0 && (attributes & unsafeToOverwrite) == 0 && entry.LinkCount == 1;
    }

    /// <summary>1 パス上書きを試みる。できなかった場合は削除だけ行うので、結果を集計に残す。</summary>
    private static void TryWipe(NativeCleanupEntry entry, FileAttributes attributes, long length, RunContext ctx)
    {
        if (length == 0) return;
        try
        {
            if (CanOverwrite(entry, attributes, length) && entry.OverwriteWithZeros(length, ctx.Ct)) { ctx.Outcome.WipedFiles++; return; }
        }
        catch (Exception ex) when (IsNativeFailure(ex)) { /* 上書き失敗でも対象は削除する。 */ }
        ctx.Outcome.WipeSkipped++;
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

    private static void DeleteEntry(NativeCleanupEntry parent, string path, bool directory, RunContext ctx, int depth)
    {
        var outcome = ctx.Outcome;
        var blockedBefore = outcome.Blocked;
        var rejectedBefore = outcome.RejectedTargets.Count;
        var retainedBefore = outcome.Retained;
        var handledBefore = outcome.HandledElsewhere;
        try
        {
            // 祖先は外側の directory handle 群で固定済み。別の祖先 handle を開かず、
            // DELETE を要求した同じ実体を再帰終了・属性復元まで保持する。
            if (directory && ctx.Target.Filtered && !ctx.Target.RemoveEmptyDirs)
            {
                // 空フォルダーを消さない絞り込みでは、フォルダーは辿るだけで削除権限を要求しない。
                using var visited = parent.OpenDescendant(path, true);
                DeleteContents(visited, ctx, depth + 1);
                return;
            }
            using var entry = directory ? parent.OpenDescendant(path, true, delete: true) : OpenFileForDelete(parent, path, ctx.Wipe);
            // 列挙後に作り直された・更新されたファイルは、開いた実体で再確認して残す。
            if (!directory && !StillQualifies(entry, ctx)) return;
            RemoveOpened(entry, directory, ctx, depth);
        }
        catch (Exception ex) when (IsNativeFailure(ex))
        {
            if (directory && ex is System.ComponentModel.Win32Exception { NativeErrorCode: 145 } &&
                (outcome.Blocked > blockedBefore || outcome.RejectedTargets.Count > rejectedBefore || outcome.Retained > retainedBefore ||
                 outcome.HandledElsewhere > handledBefore)) return;
            // 遅延したパス削除は行わない。呼出し側が既知ターゲットの意図だけを予約する。
            outcome.RecordBlocked(path, ex);
        }
    }

    /// <summary>開いた実体の時刻で、列挙時の判定がまだ成り立つか確認する。成り立たなければ残す。</summary>
    private static bool StillQualifies(NativeCleanupEntry entry, RunContext ctx)
    {
        if (ctx.CreatedCutoff is null && ctx.AgeCutoff is null) return true;
        var times = entry.Times;
        if (ctx.AgeCutoff is { } age && times.LastWrite >= age) return false;
        if (RetentionOf(null, entry.Path, false, times.Creation, ctx.CreatedCutoff) == CleanupRetention.None) return true;
        ctx.Outcome.Retained++;
        return false;
    }

    /// <summary>開いた同じ実体に対して、属性解除・上書き・削除・集計を行う。</summary>
    private static void RemoveOpened(NativeCleanupEntry entry, bool directory, RunContext ctx, int depth)
    {
        var outcome = ctx.Outcome;
        var original = entry.Attributes;
        var changed = false;
        try
        {
            if (directory)
            {
                DeleteContents(entry, ctx, depth + 1);
                // 絞り込み時は、対象を消した結果空になったフォルダーだけを消す。
                if (ctx.Target.Filtered && entry.Enumerate().Any()) return;
            }
            ctx.Ct.ThrowIfCancellationRequested();
            entry.EnsureNotReparse();
            var clearAttributes = directory ? FileAttributes.ReadOnly : FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;
            if ((original & clearAttributes) != 0)
            {
                entry.SetAttributes(original & ~clearAttributes);
                changed = true;
            }
            var length = directory ? 0 : entry.Length;
            if (!directory && ctx.Wipe) TryWipe(entry, original, length, ctx);
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

        if (outcome.Retained > 0)
        {
            lines.Add($"  - 除外または走査後の変更のため {outcome.Retained:N0} 個の項目を残しました");
        }

        if (outcome.WipedFiles > 0 || outcome.WipeSkipped > 0)
        {
            lines.Add($"  - 1 パス上書きしたファイル {outcome.WipedFiles:N0} 個、上書きできず削除のみ行ったファイル {outcome.WipeSkipped:N0} 個 (SSD では完全消去を保証しません)");
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
