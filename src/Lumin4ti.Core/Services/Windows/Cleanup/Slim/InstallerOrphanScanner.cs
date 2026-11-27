using System.Runtime.Versioning;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Core.Services.Windows.Cleanup.Slim;

/// <summary>Windows Installer の登録情報から得た LocalPackage の一覧。<see cref="Failure"/> があれば列挙に失敗している。</summary>
/// <param name="LocalPackagePaths">登録されている LocalPackage の絶対パス。</param>
/// <param name="Failure">列挙に 1 件でも失敗した理由。</param>
public sealed record InstallerRegistrationSnapshot(IReadOnlySet<string> LocalPackagePaths, string? Failure)
{
    public static InstallerRegistrationSnapshot Failed(string reason) =>
        new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), reason);
}

/// <summary>登録済みの LocalPackage を列挙する境界 (MSI API、HKLM の UserData)。</summary>
public interface IInstallerRegistrationSource
{
    InstallerRegistrationSnapshot Read(CancellationToken ct);
}

/// <summary>孤児 Installer ファイルの走査結果。何も変更していない。</summary>
public sealed class InstallerOrphanScan
{
    /// <summary>登録情報をすべて列挙できて判定できたか。false のときこの項目は利用不可で、削除しない。</summary>
    public bool Available { get; internal set; }

    /// <summary>利用不可の理由。</summary>
    public string? UnavailableReason { get; internal set; }

    /// <summary>登録に無く、作成・更新とも 30 日以上前の *.msi / *.msp。</summary>
    public IReadOnlyList<CleanupFileIdentity> Orphans { get; internal set; } = [];

    /// <summary>孤児の合計サイズ。</summary>
    public long Bytes { get; internal set; }

    /// <summary>登録済みのため残すファイル数。</summary>
    public long RegisteredCount { get; internal set; }

    /// <summary>新しすぎて判定しないファイル数。</summary>
    public long RecentCount { get; internal set; }

    /// <summary>列挙・取得に失敗して調べられなかった数。</summary>
    public long Unreadable { get; internal set; }

    /// <summary>走査を終えた時刻 (UTC)。</summary>
    public DateTime CompletedAtUtc { get; internal set; }
}

/// <summary><see cref="InstallerOrphanScanner.Run"/> の結果。</summary>
/// <param name="Verified">実行直前の再照合に成功し、削除を試みた。false のときは何も削除していない。</param>
/// <param name="UnavailableReason">再照合に失敗した理由。</param>
/// <param name="Outcome">削除の集計 (Verified が false のときは null)。</param>
public sealed record InstallerOrphanRunResult(bool Verified, string? UnavailableReason, CleanupOutcome? Outcome);

/// <summary>
/// %SystemRoot%\Installer 直下の *.msi / *.msp のうち、どの登録にも載っていない孤児を見つける。
/// 登録は複数の経路 (MsiEnumProductsEx・MsiEnumPatchesEx の全 SID・全 context と HKLM の LocalPackage) の和集合で判定し、
/// どれか 1 件でも列挙に失敗したら項目ごと利用不可にする。実行直前にも登録を取り直し、
/// 削除は走査時と FileId・サイズ・更新時刻が一致した同一ハンドルだけに行う。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InstallerOrphanScanner(
    string installerDirectory,
    IReadOnlyList<IInstallerRegistrationSource> sources,
    TimeProvider? time = null)
{
    /// <summary>この日数より新しいファイルは、インストール途中の可能性があるため判定しない。</summary>
    public const int MinimumAgeDays = 30;

    private static readonly string[] Extensions = [".msi", ".msp"];
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// 孤児を走査する。登録を取得できなければ <see cref="InstallerOrphanScan.Available"/> が false になり、
    /// ファイルも列挙しない。
    /// </summary>
    public InstallerOrphanScan Scan(CancellationToken ct)
    {
        var scan = new InstallerOrphanScan();
        try
        {
            var registered = ReadRegistrations(ct, out var failure);
            if (registered is null)
            {
                scan.UnavailableReason = failure;
                return scan;
            }

            scan.Available = true;
            Enumerate(scan, registered, ct);
            return scan;
        }
        finally
        {
            scan.CompletedAtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// 走査で得た孤児を削除する。登録を取り直して 1 件でも列挙に失敗したら何も削除しない。
    /// 取り直した登録に載ったファイルは残し、残りは同一ハンドルで一致したものだけ削除する。
    /// </summary>
    public InstallerOrphanRunResult Run(InstallerOrphanScan scan, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (!scan.Available)
        {
            return new(false, scan.UnavailableReason ?? CleanupMessage.Of("InstallerNotEnumerated"), null);
        }

        var registered = ReadRegistrations(ct, out var failure);
        if (registered is null)
        {
            return new(false, failure, null);
        }

        var stillOrphan = new List<CleanupFileIdentity>();
        var newlyRegistered = 0L;
        foreach (var identity in scan.Orphans)
        {
            if (registered.Contains(Normalize(identity.Path))) newlyRegistered++;
            else stillOrphan.Add(identity);
        }

        var outcome = FileCleanupEngine.DeleteVerified(
            stillOrphan,
            progress,
            ct,
            new CleanupRunOptions(CreatedBefore: scan.CompletedAtUtc - CleanupScanResult.CreationMargin));
        outcome.Retained += newlyRegistered;
        return new(true, null, outcome);
    }

    /// <summary>登録の絶対パスを比較用に正規化する (大文字小文字と \\?\ 接頭辞を無視)。</summary>
    internal static string Normalize(string path)
    {
        var text = path.Trim();
        if (text.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        try { return Path.GetFullPath(text).ToUpperInvariant(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return text.ToUpperInvariant(); }
    }

    /// <summary>作成・更新とも <see cref="MinimumAgeDays"/> 日以上前か。</summary>
    internal bool IsOldEnough(CleanupEntryInfo entry)
    {
        var cutoff = _time.GetUtcNow().UtcDateTime.AddDays(-MinimumAgeDays).ToFileTimeUtc();
        // 作成も更新も古いものだけ。コピーで更新時刻だけ古い、インストール途中のファイルを除く。
        return entry.CreationTime < cutoff && entry.LastWriteTime < cutoff;
    }

    private HashSet<string>? ReadRegistrations(CancellationToken ct, out string? failure)
    {
        failure = null;
        if (sources.Count == 0)
        {
            failure = CleanupMessage.Of("InstallerNoSource");
            return null;
        }

        var union = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            InstallerRegistrationSnapshot snapshot;
            try { snapshot = source.Read(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = CleanupMessage.Of("InstallerEnumerationException", ex.Message);
                return null;
            }

            if (snapshot.Failure is { } reason)
            {
                failure = CleanupMessage.Of("InstallerEnumerationFailed", reason);
                return null;
            }

            foreach (var path in snapshot.LocalPackagePaths)
            {
                union.Add(Normalize(path));
            }
        }

        return union;
    }

    private void Enumerate(InstallerOrphanScan scan, HashSet<string> registered, CancellationToken ct)
    {
        var orphans = new List<CleanupFileIdentity>();
        try
        {
            if (!FileCleanupEngine.TryResolveActual(installerDirectory, allowProtectedDirectory: true, out var fullPath, out _))
            {
                scan.Available = false;
                scan.UnavailableReason = CleanupMessage.Of("InstallerFolderUnresolved");
                return;
            }

            if (!Directory.Exists(fullPath) || !FileCleanupEngine.TryAcquireTraversalGuard(fullPath, out var guard))
            {
                scan.Unreadable++;
                return;
            }

            using (guard)
            {
                IReadOnlyList<CleanupEntryInfo> entries;
                try { entries = guard.EnumerateDetailed().ToArray(); }
                catch (Exception ex) when (IsNativeFailure(ex)) { scan.Unreadable++; return; }
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    Classify(guard, entry, registered, scan, orphans);
                }
            }
        }
        finally
        {
            scan.Orphans = orphans;
        }
    }

    private void Classify(NativeCleanupEntry guard, CleanupEntryInfo entry, HashSet<string> registered, InstallerOrphanScan scan, List<CleanupFileIdentity> orphans)
    {
        var extensionMatches = (entry.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 &&
                               Extensions.Contains(Path.GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase);
        if (!extensionMatches)
        {
            return;
        }

        var path = Path.Combine(guard.Path, entry.Name);
        if (registered.Contains(Normalize(path)))
        {
            scan.RegisteredCount++;
            return;
        }

        if (!IsOldEnough(entry))
        {
            scan.RecentCount++;
            return;
        }

        try
        {
            using var file = guard.OpenDescendant(path, false);
            var identity = file.Identity;
            orphans.Add(new CleanupFileIdentity(path, identity.VolumeSerialNumber, identity.FileIdLow, identity.FileIdHigh, entry.Size, entry.LastWriteTime));
            scan.Bytes += entry.Size;
        }
        catch (Exception ex) when (IsNativeFailure(ex)) { scan.Unreadable++; }
    }

    private static bool IsNativeFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception;
}
