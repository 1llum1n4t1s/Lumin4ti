using System.Runtime.Versioning;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Core.Services.Windows.Cleanup.Handlers;

/// <summary>ショートカットを探す既知の場所。</summary>
/// <param name="Path">フォルダー。</param>
/// <param name="Recursive">サブフォルダーも探すか。</param>
internal readonly record struct ShortcutRoot(string Path, bool Recursive);

/// <summary>
/// デスクトップ・スタートメニューの既知の場所にある、リンク先の欠損が確定したショートカットだけを削除する。
/// <list type="bullet">
/// <item>欠損の確定は <see cref="ShellLinkReader"/> が固定ドライブ上のパスを読めた場合に限り、
/// <see cref="StartupCommandParser.IsConfirmedMissing"/> が OS の「不存在」の明示を得たときだけ。
/// PIDL のみ・広告型・ネットワーク・固定以外・環境変数のみ・壊れた構造・アクセス不能は保持する。</item>
/// <item>削除は走査時の FileId・サイズ・更新時刻と一致した同一ハンドルだけ (<see cref="FileCleanupEngine.DeleteVerified"/>)。
/// 実行の直前にもリンク先の欠損を再確認し、復活していたら残す。</item>
/// <item>リンクは辿らず、走査は何も変更しない。</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InvalidShortcutHandler : ICleanupHandler
{
    private const int MaxDepth = 8;

    private readonly IReadOnlyList<ShortcutRoot> _roots;
    private readonly Func<string, bool> _isConfirmedMissing;
    private IReadOnlyList<ShortcutCandidate>? _scanned;

    public InvalidShortcutHandler()
        : this(KnownRoots(), static path => StartupCommandParser.IsConfirmedMissing(path))
    {
    }

    internal InvalidShortcutHandler(IReadOnlyList<ShortcutRoot> roots, Func<string, bool> isConfirmedMissing)
    {
        _roots = roots;
        _isConfirmedMissing = isConfirmedMissing;
    }

    public string Id => "invalidShortcut";

    public IReadOnlyList<string> Services => [];

    public Task<CleanupHandlerScan> ScanAsync(CancellationToken ct)
    {
        var scan = Collect(ct);
        _scanned = scan.Candidates;
        if (scan.RejectedRoots > 0 && scan.RejectedRoots == _roots.Count)
        {
            return Task.FromResult(new CleanupHandlerScan(CleanupHandlerScanState.Unavailable, Detail: CleanupMessage.Of("ShortcutLocationOpenFailed")));
        }

        var detail = scan.Unreadable > 0 ? CleanupMessage.Of("ShortcutsUnreadable", scan.Unreadable) : null;
        return Task.FromResult(scan.Candidates.Count == 0
            ? new CleanupHandlerScan(CleanupHandlerScanState.Empty, 0, 0, detail)
            : new CleanupHandlerScan(CleanupHandlerScanState.Found, scan.Candidates.Count, scan.Bytes, detail));
    }

    public Task<CleanupHandlerResult> RunAsync(IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            return Task.FromResult(Run(progress, ct));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(new CleanupHandlerResult(CleanupHandlerStatus.Canceled));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(CleanupHandlerResult.Failed(CleanupMessage.Of("ShortcutDeleteFailed", CommandFailureDiagnostic.Sanitize(ex.Message))));
        }
    }

    private CleanupHandlerResult Run(IProgress<string>? progress, CancellationToken ct)
    {
        // 走査していない対象は削除しない。走査が無ければここで走査する。
        var candidates = _scanned ?? Collect(ct).Candidates;
        _scanned = null;

        var verified = new List<CleanupFileIdentity>();
        var changed = 0L;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (_isConfirmedMissing(candidate.TargetPath)) verified.Add(candidate.Identity);
            else changed++;
        }

        var outcome = FileCleanupEngine.DeleteVerified(verified, progress, ct);
        outcome.Retained += changed;
        return Classify(outcome);
    }

    internal static CleanupHandlerResult Classify(CleanupOutcome outcome)
    {
        var details = new List<string>(outcome.RejectedTargets);
        details.AddRange(outcome.BlockedExamples);
        var items = outcome.DeletedFiles + outcome.DeletedDirectories;
        if (details.Count == 0 && outcome.Blocked == 0)
        {
            return outcome.DidNothing
                ? new CleanupHandlerResult(CleanupHandlerStatus.NothingToDo)
                : new CleanupHandlerResult(CleanupHandlerStatus.Succeeded, items, outcome.FreedBytes);
        }

        return items > 0
            ? new CleanupHandlerResult(CleanupHandlerStatus.Partial, items, outcome.FreedBytes) { Details = details }
            : new CleanupHandlerResult(CleanupHandlerStatus.Failed) { Details = details };
    }

    private static IReadOnlyList<ShortcutRoot> KnownRoots() =>
    [
        new(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false),
        new(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), false),
        new(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), true),
        new(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), true),
    ];

    private ShortcutScan Collect(CancellationToken ct)
    {
        var scan = new ShortcutScan();
        foreach (var root in _roots)
        {
            ct.ThrowIfCancellationRequested();
            ScanRoot(root, scan, ct);
        }

        return scan;
    }

    private void ScanRoot(ShortcutRoot root, ShortcutScan scan, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(root.Path)) return;
        if (!FileCleanupEngine.TryResolveActual(root.Path, allowProtectedDirectory: true, out var fullPath, out _))
        {
            scan.RejectedRoots++;
            return;
        }

        if (!Directory.Exists(fullPath)) return;
        if (!FileCleanupEngine.TryAcquireTraversalGuard(fullPath, out var guard))
        {
            scan.RejectedRoots++;
            return;
        }

        using (guard)
        {
            ScanDirectory(guard, guard, root.Recursive, 0, scan, ct);
        }
    }

    private void ScanDirectory(NativeCleanupEntry guard, NativeCleanupEntry directory, bool recursive, int depth, ShortcutScan scan, CancellationToken ct)
    {
        IReadOnlyList<CleanupEntryInfo> entries;
        try { entries = directory.EnumerateDetailed().ToArray(); }
        catch (Exception ex) when (IsNativeFailure(ex)) { scan.Unreadable++; return; }

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;

            var path = System.IO.Path.Combine(directory.Path, entry.Name);
            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                if (recursive && depth < MaxDepth) ScanSubdirectory(guard, path, depth, scan, ct);
            }
            else if (entry.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                Classify(guard, path, scan);
            }
        }
    }

    private void ScanSubdirectory(NativeCleanupEntry guard, string path, int depth, ShortcutScan scan, CancellationToken ct)
    {
        try
        {
            using var child = guard.OpenDescendant(path, true);
            ScanDirectory(guard, child, true, depth + 1, scan, ct);
        }
        catch (Exception ex) when (IsNativeFailure(ex)) { scan.Unreadable++; }
    }

    private void Classify(NativeCleanupEntry guard, string path, ShortcutScan scan)
    {
        try
        {
            using var file = guard.OpenDescendant(path, false, readData: true);
            var length = file.Length;
            var lastWrite = file.Times.LastWrite;
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || length <= 0 || length > ShellLinkReader.MaxLength)
            {
                scan.Unreadable++;
                return;
            }

            var bytes = new byte[(int)length];
            if (!TryReadAll(file, bytes) || file.Length != length || file.Times.LastWrite != lastWrite)
            {
                scan.Unreadable++;
                return;
            }

            var target = ShellLinkReader.Read(bytes);
            if (!target.IsDeterminable || !_isConfirmedMissing(target.Path!)) return;

            var identity = file.Identity;
            scan.Candidates.Add(new ShortcutCandidate(
                new CleanupFileIdentity(path, identity.VolumeSerialNumber, identity.FileIdLow, identity.FileIdHigh, length, lastWrite),
                target.Path!));
            scan.Bytes += length;
        }
        catch (Exception ex) when (IsNativeFailure(ex)) { scan.Unreadable++; }
    }

    private static bool TryReadAll(NativeCleanupEntry file, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = RandomAccess.Read(file.Handle, buffer.AsSpan(offset), offset);
            if (read == 0) return false;
            offset += read;
        }

        return true;
    }

    private static bool IsNativeFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception;

    private sealed record ShortcutCandidate(CleanupFileIdentity Identity, string TargetPath);

    private sealed class ShortcutScan
    {
        public List<ShortcutCandidate> Candidates { get; } = [];

        public long Bytes { get; set; }

        public int Unreadable { get; set; }

        public int RejectedRoots { get; set; }
    }
}
