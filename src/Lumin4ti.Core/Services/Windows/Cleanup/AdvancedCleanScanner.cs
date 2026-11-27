using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using Lumin4ti.Core.Services.Windows.Actions;
using EngineTarget = Lumin4ti.Core.Services.Windows.Actions.CleanupTarget;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>
/// 走査結果の世代番号。再走査・設定変更・削除開始で現在の世代が進み、古い結果は削除に使えなくなる。
/// アプリで 1 つ (Singleton) 持ち、走査と削除が共有する。
/// </summary>
public sealed class AdvancedScanLedger
{
    private long _current;

    /// <summary>現在の世代。</summary>
    public long Current => Interlocked.Read(ref _current);

    /// <summary>新しい走査を始める。以前の結果はすべて失効し、この走査の世代を返す。</summary>
    public long BeginScan() => Interlocked.Increment(ref _current);

    /// <summary>設定の変更などで、走査済みの結果を失効させる。</summary>
    public void Invalidate() => Interlocked.Increment(ref _current);

    /// <summary>結果がまだ有効か。</summary>
    public bool IsCurrent(long generation) => Current == generation;

    /// <summary>世代が現在のものなら、削除の開始として世代を進める (同じ結果での二重削除を防ぐ)。</summary>
    internal bool TryBeginDelete(long generation) => Interlocked.CompareExchange(ref _current, generation + 1, generation) == generation;
}

/// <summary>走査できるドライブ。</summary>
/// <param name="Root">ドライブ直下 (<c>C:\</c>)。</param>
/// <param name="Label">ボリュームラベル。</param>
/// <param name="Type">種類。</param>
/// <param name="DefaultSelected">既定で選択するか。準備済みの固定ドライブだけ true。リムーバブルは表示のみ。</param>
public sealed record AdvancedDrive(string Root, string Label, DriveType Type, bool DefaultSelected);

/// <summary>走査の進捗。</summary>
public sealed record AdvancedScanProgress(long Directories, long Files, long Matched, long Bytes, string CurrentDirectory);

/// <summary>起点を開けなかった・保護されていたため走査しなかった場所。</summary>
public sealed record AdvancedRootFailure(string Root, string Reason);

/// <summary>5 列の結果 1 行 (名前・フォルダー・サイズ・更新日時・規則) と、区分・安全度・削除時の実体。</summary>
/// <param name="Path">絶対パス。</param>
/// <param name="Name">ファイル名。</param>
/// <param name="Folder">親フォルダー。</param>
/// <param name="DriveRoot">所属ドライブ (<c>C:\</c>)。</param>
/// <param name="Size">サイズ (EndOfFile)。</param>
/// <param name="LastWriteFileTime">更新時刻 (FILETIME)。</param>
/// <param name="Kind">規則の種類。</param>
/// <param name="Rule">一致した規則 (パターン。特別な規則では空)。</param>
/// <param name="Group">区分。</param>
/// <param name="Verdict">安全度。</param>
/// <param name="Hint">既定で選択しない・判定不能の理由。</param>
/// <param name="DefaultSelected">既定で選択するか。</param>
/// <param name="Identity">削除時に照合する実体 (FileId・サイズ・更新時刻)。</param>
/// <param name="ShortcutTarget">無効なショートカットのリンク先。</param>
public sealed record AdvancedCleanItem(
    string Path,
    string Name,
    string Folder,
    string DriveRoot,
    long Size,
    long LastWriteFileTime,
    AdvancedRuleKind Kind,
    string Rule,
    AdvancedPatternGroup Group,
    AdvancedVerdict Verdict,
    AdvancedHint Hint,
    bool DefaultSelected,
    CleanupFileIdentity Identity,
    string? ShortcutTarget = null)
{
    /// <summary>更新日時 (UTC)。表示用。</summary>
    public DateTime LastWriteUtc => LastWriteFileTime is > 0 and < 2_650_467_743_990_000_000L ? DateTime.FromFileTimeUtc(LastWriteFileTime) : DateTime.MinValue;
}

/// <summary>カスタムフォルダー 1 件の走査結果 (中身だけを空にする)。</summary>
public sealed record AdvancedCustomFolderScan(
    string? Path,
    CustomFolderRejection Rejection,
    string? OtherReason,
    bool Missing,
    long Files,
    long Directories,
    long Bytes,
    long Retained,
    long SkippedLinks,
    long Unreadable,
    IReadOnlyList<string> Representatives,
    DateTime ScannedAtUtc)
{
    /// <summary>削除の対象にできるか。拒否・不存在のものは選べない。</summary>
    public bool IsSelectable => Path is not null && Rejection == CustomFolderRejection.None && OtherReason is null && !Missing;

    /// <summary>既定で選択するか。設定ファイルは非昇格でも書けるため、許可されたものも常に未選択にする。</summary>
    public bool DefaultSelected => false;

    /// <summary>削除時の <see cref="CleanupRunOptions.CreatedBefore"/> に渡す。これより後に作られたものは残す。</summary>
    public DateTime CreatedBefore => ScannedAtUtc - CleanupScanResult.CreationMargin;
}

/// <summary>走査の指定。</summary>
/// <param name="Roots">走査の起点 (通常は <see cref="AdvancedCleanScanner.GetDrives"/> で選んだドライブ直下)。</param>
public sealed record AdvancedScanOptions(IReadOnlyList<string> Roots)
{
    /// <summary><see cref="CleanupSettingsStore.GetPatterns"/> の値。</summary>
    public IReadOnlyDictionary<string, bool>? SavedPatterns { get; init; }

    /// <summary>0 バイトのファイルを含めるか。</summary>
    public bool IncludeZeroByte { get; init; }

    /// <summary>リンク先の欠損が確定した .lnk を含めるか。</summary>
    public bool IncludeInvalidShortcuts { get; init; }

    /// <summary>この日数より古いファイルを含める (1 以上のときだけ有効)。</summary>
    public int? OlderThanDays { get; init; }

    /// <summary>除外の一致器。削除時と同じものを渡す。</summary>
    public CleanupExclusionMatcher? Filter { get; init; }

    /// <summary><see cref="CleanupSettingsStore.GetCustomFolders"/> の値 (実行時の検査つき)。</summary>
    public IReadOnlyList<CustomFolderCheck> CustomFolders { get; init; } = [];

    /// <summary>枝刈り。省略時は実環境。</summary>
    public AdvancedProtectedPaths? Protection { get; init; }

    /// <summary>結果として保持する最大件数。超過分は数えるだけで一覧にも削除対象にも入れない。</summary>
    public int MaxItems { get; init; } = 200_000;

    /// <summary>並列度。0 以下なら CPU 数 (最大 8)。</summary>
    public int MaxDegreeOfParallelism { get; init; }

    /// <summary>進捗を通知する最小間隔。</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>基準時刻。既定は現在 (テスト用)。</summary>
    public DateTime? Now { get; init; }

    /// <summary>リンク先の欠損を確定する関数。省略時は <c>StartupCommandParser.IsConfirmedMissing</c>。</summary>
    public Func<string, bool>? IsShortcutTargetMissing { get; init; }
}

/// <summary>走査結果。何も変更していない。世代が現在でなくなったものは削除に使えない。</summary>
public sealed class AdvancedScanResult
{
    private readonly AdvancedScanLedger _ledger;

    internal AdvancedScanResult(AdvancedScanLedger ledger, long generation)
    {
        _ledger = ledger;
        Generation = generation;
    }

    /// <summary>世代番号。</summary>
    public long Generation { get; }

    /// <summary>結果がまだ有効か (再走査・設定変更・削除開始で失効する)。</summary>
    public bool IsCurrent => _ledger.IsCurrent(Generation);

    internal AdvancedScanLedger Ledger => _ledger;

    /// <summary>一覧の行 (パス順)。件数は <see cref="AdvancedScanOptions.MaxItems"/> まで。</summary>
    public IReadOnlyList<AdvancedCleanItem> Items { get; internal set; } = [];

    /// <summary>一致したファイルの総数 (一覧に載らなかったものを含む)。</summary>
    public long TotalMatched { get; internal set; }

    /// <summary>一致したファイルの総サイズ。</summary>
    public long TotalBytes { get; internal set; }

    /// <summary>上限を超えたため一覧に載せず、削除もしないファイル数。</summary>
    public long OmittedFiles { get; internal set; }

    /// <summary>上限を超えたファイルの総サイズ。</summary>
    public long OmittedBytes { get; internal set; }

    /// <summary>上限を超えて、結果を打ち切ったか。</summary>
    public bool Truncated => OmittedFiles > 0;

    /// <summary>走査したフォルダー数。</summary>
    public long DirectoriesScanned { get; internal set; }

    /// <summary>見たファイル数。</summary>
    public long FilesScanned { get; internal set; }

    /// <summary>枝刈りしたフォルダー数。</summary>
    public long PrunedDirectories { get; internal set; }

    /// <summary>リンクのため触れなかった数。</summary>
    public long SkippedLinks { get; internal set; }

    /// <summary>列挙・取得に失敗して調べられなかった数。</summary>
    public long Unreadable { get; internal set; }

    /// <summary>除外のため一覧に載せなかった数。</summary>
    public long Retained { get; internal set; }

    /// <summary>起点を走査できなかった場所。</summary>
    public IReadOnlyList<AdvancedRootFailure> UnavailableRoots { get; internal set; } = [];

    /// <summary>カスタムフォルダーの走査結果。</summary>
    public IReadOnlyList<AdvancedCustomFolderScan> CustomFolders { get; internal set; } = [];

    /// <summary>走査を終えた時刻 (UTC)。</summary>
    public DateTime CompletedAtUtc { get; internal set; }
}

/// <summary>
/// 高度なクリーンの走査。準備済みの固定ドライブをパスで列挙し (MFT は使わない)、リンクを辿らず、
/// フォルダーのハンドルを開いたまま並列に列挙する。ファイルの内容・時刻・属性を変えず、削除もしない。
/// 呼び出し側は走査の lease を取り (<c>showProcessing:false</c>)、取消トークンを渡す。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AdvancedCleanScanner(AdvancedScanLedger ledger)
{
    /// <summary>走査できるドライブ。準備済みの固定ドライブは既定で選択、リムーバブルは表示のみ (未選択)。</summary>
    public static IReadOnlyList<AdvancedDrive> GetDrives()
    {
        var drives = new List<AdvancedDrive>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady) continue;
                drives.Add(new AdvancedDrive(drive.RootDirectory.FullName, drive.VolumeLabel, drive.DriveType, drive.DriveType == DriveType.Fixed));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 取り外された・準備できないドライブは出さない。 */ }
        }

        return drives;
    }

    /// <summary>非同期に走査する (専用スレッドで列挙する)。取消は <see cref="OperationCanceledException"/>。</summary>
    public Task<AdvancedScanResult> ScanAsync(AdvancedScanOptions options, IProgress<AdvancedScanProgress>? progress, CancellationToken ct) =>
        Task.Run(() => Scan(options, progress, ct), ct);

    /// <summary>走査する。取消は <see cref="OperationCanceledException"/> で伝え、開いたハンドルは必ず閉じる。</summary>
    public AdvancedScanResult Scan(AdvancedScanOptions options, IProgress<AdvancedScanProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var generation = ledger.BeginScan();
        var protection = options.Protection ?? AdvancedProtectedPaths.Default;
        var run = new ScanRun(options, protection, progress, ct);
        try
        {
            run.OpenRoots();
            run.Execute();
        }
        finally { run.Dispose(); }

        var result = new AdvancedScanResult(ledger, generation);
        run.Fill(result);
        result.CustomFolders = ScanCustomFolders(options, protection, ct);
        result.CompletedAtUtc = DateTime.UtcNow;
        return result;
    }

    private static List<AdvancedCustomFolderScan> ScanCustomFolders(AdvancedScanOptions options, AdvancedProtectedPaths protection, CancellationToken ct)
    {
        var folders = new List<AdvancedCustomFolderScan>();
        foreach (var check in options.CustomFolders)
        {
            ct.ThrowIfCancellationRequested();
            if (!check.IsAllowed || check.FullPath is null)
            {
                folders.Add(Unselectable(check.FullPath, check.Rejection, null));
            }
            else if (protection.OverlapsProtected(check.FullPath))
            {
                folders.Add(Unselectable(check.FullPath, CustomFolderRejection.None, CleanupMessage.Of("AdvancedFolderProtected")));
            }
            else
            {
                folders.Add(ScanCustomFolder(check.FullPath, options.Filter, ct));
            }
        }

        return folders;
    }

    private static AdvancedCustomFolderScan Unselectable(string? path, CustomFolderRejection rejection, string? reason) =>
        new(path, rejection, reason, false, 0, 0, 0, 0, 0, 0, [], DateTime.UtcNow);

    private static AdvancedCustomFolderScan ScanCustomFolder(string path, CleanupExclusionMatcher? filter, CancellationToken ct)
    {
        var scan = FileCleanupScanner.Scan(EngineTarget.CustomFolder(path), new CleanupScanOptions(filter), ct);
        return new AdvancedCustomFolderScan(
            scan.Path.Length > 0 ? scan.Path : path,
            CustomFolderRejection.None,
            // 走査側の拒否理由は日本語の診断文のため、画面には翻訳できる符号で出す。
            scan.Rejection is null || CleanupMessage.TryParse(scan.Rejection, out _, out _) ? scan.Rejection : CleanupMessage.Of("AdvancedFolderNotScannable"),
            scan.Missing,
            scan.Files,
            scan.Directories,
            scan.Bytes,
            scan.Retained,
            scan.SkippedLinks,
            scan.Unreadable,
            scan.Representatives,
            scan.CompletedAtUtc);
    }

    /// <summary>1 回の走査の共有状態と、ハンドルを持ち回る並列の列挙。</summary>
    private sealed class ScanRun : IDisposable
    {
        private static readonly AdvancedPatternRule ZeroByteRule = AdvancedPatterns.Special(AdvancedRuleKind.ZeroByte);
        private static readonly AdvancedPatternRule ShortcutRule = AdvancedPatterns.Special(AdvancedRuleKind.InvalidShortcut);
        private static readonly AdvancedPatternRule OldRule = AdvancedPatterns.Special(AdvancedRuleKind.OldFile);
        private const int MaxDepth = 64;

        /// <summary>待ち行列に開いたまま持つフォルダーハンドルの上限 (幅の広いツリーでハンドルを使い切らないため)。</summary>
        private const int MaxPendingHandles = 256;

        private readonly AdvancedScanOptions _options;
        private readonly AdvancedProtectedPaths _protection;
        private readonly IProgress<AdvancedScanProgress>? _progress;
        private readonly CancellationToken _ct;
        private readonly AdvancedPatterns _patterns;
        private readonly DateTime _now;
        private readonly long? _oldCutoff;
        private readonly Func<string, bool> _isMissing;
        private readonly long _progressTicks;

        private readonly object _gate = new();
        private readonly Stack<(NativeCleanupEntry Directory, int Depth)> _pending = new();
        private readonly ConcurrentQueue<AdvancedCleanItem> _items = new();
        private readonly List<AdvancedRootFailure> _failures = [];
        private int _active;
        private bool _stopped;
        private ExceptionDispatchInfo? _error;
        private long _lastReport;

        private long _directories;
        private long _files;
        private long _pruned;
        private long _links;
        private long _unreadable;
        private long _retained;
        private long _matched;
        private long _matchedBytes;
        private long _omitted;
        private long _omittedBytes;

        internal ScanRun(AdvancedScanOptions options, AdvancedProtectedPaths protection, IProgress<AdvancedScanProgress>? progress, CancellationToken ct)
        {
            _options = options;
            _protection = protection;
            _progress = progress;
            _ct = ct;
            _patterns = AdvancedPatterns.Resolve(options.SavedPatterns);
            _now = (options.Now ?? DateTime.UtcNow).ToUniversalTime();
            _oldCutoff = options.OlderThanDays is >= 1 ? _now.AddDays(-options.OlderThanDays.Value).ToFileTimeUtc() : null;
            _isMissing = options.IsShortcutTargetMissing ?? (static path => StartupCommandParser.IsConfirmedMissing(path));
            _progressTicks = (long)Math.Max(0, options.ProgressInterval.TotalMilliseconds);
            _lastReport = Environment.TickCount64;
        }

        /// <summary>起点を検査して開く。保護された場所・開けない場所は結果に残し、他の起点は続ける。</summary>
        internal void OpenRoots()
        {
            var roots = new List<string>();
            foreach (var raw in _options.Roots)
            {
                var root = NormalizeRoot(raw);
                if (root is null) _failures.Add(new AdvancedRootFailure(raw ?? string.Empty, CleanupMessage.Of("AdvancedRootNotLocal")));
                else if (!roots.Contains(root, StringComparer.OrdinalIgnoreCase)) roots.Add(root);
            }

            foreach (var root in roots.Where(root => !roots.Any(other => IsNestedUnder(root, other))))
            {
                _ct.ThrowIfCancellationRequested();
                OpenRoot(root);
            }
        }

        private void OpenRoot(string root)
        {
            if (!IsDriveRoot(root) && _protection.IsProtectedPath(root))
            {
                _failures.Add(new AdvancedRootFailure(root, CleanupMessage.Of("AdvancedRootProtected")));
                return;
            }

            try { _pending.Push((NativeCleanupEntry.Open(root, true), 0)); }
            catch (Exception ex) when (IsNativeFailure(ex)) { _failures.Add(new AdvancedRootFailure(root, ex.Message)); }
        }

        internal void Execute()
        {
            var degree = _options.MaxDegreeOfParallelism > 0 ? _options.MaxDegreeOfParallelism : Math.Min(Environment.ProcessorCount, 8);
            var workers = Enumerable.Range(0, Math.Clamp(degree, 1, 32))
                .Select(_ => Task.Factory.StartNew(Work, TaskCreationOptions.LongRunning))
                .ToArray();
            Task.WaitAll(workers);
            _error?.Throw();
            _ct.ThrowIfCancellationRequested();
        }

        internal void Fill(AdvancedScanResult result)
        {
            result.Items = [.. _items.OrderBy(static i => i.Path, StringComparer.OrdinalIgnoreCase)];
            result.TotalMatched = _matched;
            result.TotalBytes = _matchedBytes;
            result.OmittedFiles = _omitted;
            result.OmittedBytes = _omittedBytes;
            result.DirectoriesScanned = _directories;
            result.FilesScanned = _files;
            result.PrunedDirectories = _pruned;
            result.SkippedLinks = _links;
            result.Unreadable = _unreadable;
            result.Retained = _retained;
            result.UnavailableRoots = _failures;
        }

        /// <summary>取り残したハンドル (取消・失敗時) を閉じる。</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                _stopped = true;
                while (_pending.Count > 0) _pending.Pop().Directory.Dispose();
            }
        }

        private void Work()
        {
            while (TryTake(out var item))
            {
                try
                {
                    using (item.Directory) ProcessDirectory(item.Directory, item.Depth);
                }
                catch (Exception ex)
                {
                    Fail(ex);
                }
                finally
                {
                    Complete();
                }
            }
        }

        private bool TryTake(out (NativeCleanupEntry Directory, int Depth) item)
        {
            lock (_gate)
            {
                while (true)
                {
                    if (!_stopped && _pending.Count > 0)
                    {
                        item = _pending.Pop();
                        _active++;
                        return true;
                    }

                    if (_stopped || _active == 0)
                    {
                        item = default;
                        Monitor.PulseAll(_gate);
                        return false;
                    }

                    Monitor.Wait(_gate);
                }
            }
        }

        private void Complete()
        {
            lock (_gate)
            {
                _active--;
                Monitor.PulseAll(_gate);
            }
        }

        private void Fail(Exception ex)
        {
            lock (_gate)
            {
                _error ??= ExceptionDispatchInfo.Capture(ex);
                _stopped = true;
                while (_pending.Count > 0) _pending.Pop().Directory.Dispose();
                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>待ち行列へ積む。停止中は閉じて true、待ち行列が <see cref="MaxPendingHandles"/> に達していれば積まず false。</summary>
        private bool TryPush(NativeCleanupEntry directory, int depth)
        {
            lock (_gate)
            {
                if (_stopped)
                {
                    directory.Dispose();
                    return true;
                }

                if (_pending.Count >= MaxPendingHandles) return false;
                _pending.Push((directory, depth));
                Monitor.Pulse(_gate);
                return true;
            }
        }

        private void ProcessDirectory(NativeCleanupEntry directory, int depth)
        {
            IReadOnlyList<CleanupEntryInfo> entries;
            try { entries = directory.EnumerateDetailed().ToArray(); }
            catch (Exception ex) when (IsNativeFailure(ex)) { Interlocked.Increment(ref _unreadable); return; }

            var files = 0L;
            foreach (var entry in entries)
            {
                _ct.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _stopped)) return;
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { Interlocked.Increment(ref _links); continue; }
                var path = System.IO.Path.Combine(directory.Path, entry.Name);
                if ((entry.Attributes & FileAttributes.Directory) != 0) { VisitDirectory(directory, path, entry.Name, depth); continue; }
                files++;
                EvaluateFile(directory, entry, path);
            }

            Interlocked.Add(ref _files, files);
            Interlocked.Increment(ref _directories);
            ReportProgress(directory.Path);
        }

        private void VisitDirectory(NativeCleanupEntry parent, string path, string name, int depth)
        {
            if (depth + 1 > MaxDepth) { Interlocked.Increment(ref _unreadable); return; }
            if (_protection.IsPrunedDirectory(path, name, parent.Path)) { Interlocked.Increment(ref _pruned); return; }
            if (_options.Filter?.IsExcluded(path, true) == true) { Interlocked.Increment(ref _retained); return; }
            NativeCleanupEntry child;
            try { child = OpenChild(parent, path, true); }
            catch (Exception ex) when (IsNativeFailure(ex)) { Interlocked.Increment(ref _unreadable); return; }
            if (TryPush(child, depth + 1)) return;

            // 待ち行列が満杯なら、開いたまま溜めずにこのスレッドで続けて処理する (親固定・リンク非追従は同じ手順のまま)。
            // 保持するハンドルは「待ち行列の上限 + 各スレッドの深さ (MaxDepth) 分」に収まる。
            using (child) ProcessDirectory(child, depth + 1);
        }

        private void EvaluateFile(NativeCleanupEntry directory, CleanupEntryInfo entry, string path)
        {
            string? shortcutTarget = null;
            var rule = _patterns.Match(entry.Name) ?? MatchSpecial(directory, entry, path, out shortcutTarget);
            if (rule is null || AdvancedProtectedPaths.IsProtectedFile(entry.Name)) return;
            if (_options.Filter?.IsExcluded(path, false) == true) { Interlocked.Increment(ref _retained); return; }
            Record(directory, entry, path, rule, shortcutTarget);
        }

        private AdvancedPatternRule? MatchSpecial(NativeCleanupEntry directory, CleanupEntryInfo entry, string path, out string? shortcutTarget)
        {
            shortcutTarget = null;
            if (_options.IncludeZeroByte && entry.Size == 0) return ZeroByteRule;
            if (_options.IncludeInvalidShortcuts && IsShortcut(entry) &&
                (shortcutTarget = MissingShortcutTarget(directory, entry, path)) is not null)
            {
                return ShortcutRule;
            }

            return _oldCutoff is { } cutoff && entry.LastWriteTime < cutoff ? OldRule : null;
        }

        private static bool IsShortcut(CleanupEntryInfo entry) =>
            entry.Size is > 0 and <= ShellLinkReader.MaxLength &&
            string.Equals(System.IO.Path.GetExtension(entry.Name), ".lnk", StringComparison.OrdinalIgnoreCase);

        /// <summary>同一ハンドルで読み、リンク先の欠損が確定したときだけそのパスを返す。</summary>
        private string? MissingShortcutTarget(NativeCleanupEntry directory, CleanupEntryInfo entry, string path)
        {
            try
            {
                using var file = OpenChild(directory, path, false, readData: true);
                var bytes = new byte[(int)entry.Size];
                var read = 0;
                while (read < bytes.Length)
                {
                    var count = RandomAccess.Read(file.Handle, bytes.AsSpan(read), read);
                    if (count == 0) return null;
                    read += count;
                }

                if (file.Length != entry.Size) return null;
                var target = ShellLinkReader.Read(bytes);
                return target.IsDeterminable && _isMissing(target.Path!) ? target.Path : null;
            }
            catch (Exception ex) when (IsNativeFailure(ex))
            {
                Interlocked.Increment(ref _unreadable);
                return null;
            }
        }

        private void Record(NativeCleanupEntry directory, CleanupEntryInfo entry, string path, AdvancedPatternRule rule, string? shortcutTarget)
        {
            var number = Interlocked.Increment(ref _matched);
            Interlocked.Add(ref _matchedBytes, entry.Size);
            if (number > _options.MaxItems)
            {
                Interlocked.Increment(ref _omitted);
                Interlocked.Add(ref _omittedBytes, entry.Size);
                return;
            }

            if (!TryCaptureIdentity(directory, entry, path, out var identity))
            {
                Interlocked.Decrement(ref _matched);
                Interlocked.Add(ref _matchedBytes, -entry.Size);
                Interlocked.Increment(ref _unreadable);
                return;
            }

            var folderName = System.IO.Path.GetFileName(directory.Path.TrimEnd('\\'));
            var verdict = AdvancedPatterns.Classify(rule, entry.Name, folderName, entry.Size, entry.LastWriteTime, _now);
            _items.Enqueue(new AdvancedCleanItem(
                path, entry.Name, System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetPathRoot(path)!,
                entry.Size, entry.LastWriteTime, rule.Kind, rule.Pattern, rule.Group,
                verdict.Verdict, verdict.Hint, verdict.DefaultSelected, identity, shortcutTarget));
        }

        /// <summary>読取属性だけで開き、列挙時のサイズ・更新時刻と一致するときだけ実体を記録する。</summary>
        private static bool TryCaptureIdentity(NativeCleanupEntry directory, CleanupEntryInfo entry, string path, out CleanupFileIdentity identity)
        {
            identity = null!;
            try
            {
                using var file = OpenChild(directory, path, false);
                var id = file.Identity;
                if (file.Length != entry.Size || file.Times.LastWrite != entry.LastWriteTime) return false;
                identity = new CleanupFileIdentity(path, id.VolumeSerialNumber, id.FileIdLow, id.FileIdHigh, entry.Size, entry.LastWriteTime);
                return true;
            }
            catch (Exception ex) when (IsNativeFailure(ex)) { return false; }
        }

        private void ReportProgress(string current)
        {
            if (_progress is null) return;
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastReport);
            if (now - last < _progressTicks || Interlocked.CompareExchange(ref _lastReport, now, last) != last) return;
            _progress.Report(new AdvancedScanProgress(
                Interlocked.Read(ref _directories), Interlocked.Read(ref _files), Interlocked.Read(ref _matched), Interlocked.Read(ref _matchedBytes), current));
        }
    }

    /// <summary>
    /// 親ハンドルの下を開く。ドライブ直下は <see cref="NativeCleanupEntry.OpenDescendant"/> の範囲検査が
    /// 末尾の区切りと合わず使えないため、祖先ごと固定して開く <see cref="NativeCleanupEntry.Open"/> を使う。
    /// </summary>
    private static NativeCleanupEntry OpenChild(NativeCleanupEntry parent, string path, bool directory, bool readData = false) =>
        IsDriveRoot(parent.Path)
            ? NativeCleanupEntry.Open(path, directory, readData: readData)
            : parent.OpenDescendant(path, directory, readData: readData);

    private static bool IsDriveRoot(string path) =>
        path.Length >= 3 && string.Equals(path.TrimEnd('\\'), System.IO.Path.GetPathRoot(path)!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool IsNestedUnder(string path, string other) =>
        !string.Equals(path, other, StringComparison.OrdinalIgnoreCase) &&
        path.StartsWith(other.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeRoot(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !System.IO.Path.IsPathFullyQualified(raw) || raw.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        try
        {
            var full = System.IO.Path.GetFullPath(raw.Trim());
            return full.Length > 3 ? System.IO.Path.TrimEndingDirectorySeparator(full) : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static bool IsNativeFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception;
}
