using System.Runtime.Versioning;
using Lumin4ti.Core.Services.Windows.Actions;
using EngineTarget = Lumin4ti.Core.Services.Windows.Actions.CleanupTarget;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>画面で選択された削除対象。走査結果 (<see cref="AdvancedScanResult"/>) の行とカスタムフォルダーだけが有効。</summary>
/// <param name="Items">選択した行。パスで走査結果と突き合わせ、含まれないものは削除しない。</param>
/// <param name="Folders">選択したカスタムフォルダー (中身だけを空にする)。</param>
public sealed record AdvancedCleanSelection(IReadOnlyCollection<AdvancedCleanItem> Items, IReadOnlyCollection<AdvancedCustomFolderScan> Folders);

/// <summary>削除の指定。</summary>
public sealed record AdvancedCleanOptions
{
    /// <summary>除外の一致器。走査と同じものを渡す。</summary>
    public CleanupExclusionMatcher? Filter { get; init; }

    /// <summary>1 パス上書きしてから消す (設定の「安全な削除」)。</summary>
    public bool Wipe { get; init; }

    /// <summary>カスタムフォルダーの実行時の再検査に使う環境。省略時は実環境。</summary>
    public CustomFolderGuardContext? GuardContext { get; init; }

    /// <summary>枝刈り。省略時は実環境。</summary>
    public AdvancedProtectedPaths? Protection { get; init; }

    /// <summary>無効なショートカットの欠損を再確認する関数。省略時は <c>StartupCommandParser.IsConfirmedMissing</c>。</summary>
    public Func<string, bool>? IsShortcutTargetMissing { get; init; }
}

/// <summary>削除の集計。部分失敗・取消・失効を成功と区別できる。</summary>
public sealed class AdvancedCleanOutcome
{
    private const int MaxExamples = 8;
    private readonly List<string> _blockedExamples = [];

    /// <summary>走査結果が失効していたため、何も削除しなかった。</summary>
    public bool Expired { get; internal set; }

    /// <summary>取消により途中で止まった。</summary>
    public bool Cancelled { get; internal set; }

    /// <summary>削除したファイル数。</summary>
    public long DeletedFiles { get; internal set; }

    /// <summary>削除したフォルダー数 (カスタムフォルダー内)。</summary>
    public long DeletedDirectories { get; internal set; }

    /// <summary>解放したバイト数。</summary>
    public long FreedBytes { get; internal set; }

    /// <summary>使用中・権限不足などで削除できなかった数 (部分失敗)。</summary>
    public long Blocked { get; internal set; }

    /// <summary>除外・走査後の作成・走査時との不一致 (差し替え・更新) により、意図して残した数。失敗ではない。</summary>
    public long Retained { get; internal set; }

    /// <summary>削除の時点で既に無かった数。失敗ではない。</summary>
    public long Gone { get; internal set; }

    /// <summary>選択したカスタムフォルダーに含まれるため、個別には扱わなかった行数。</summary>
    public long CoveredByFolders { get; internal set; }

    /// <summary>1 パス上書きしてから削除した数。</summary>
    public long WipedFiles { get; internal set; }

    /// <summary>上書きできず、削除だけ行った数 (リンク共有・圧縮・失敗など)。</summary>
    public long WipeSkipped { get; internal set; }

    /// <summary>安全ガードや走査外のため実行を拒否した対象。</summary>
    public List<string> Rejected { get; } = [];

    /// <summary>削除できなかった理由とパスの代表例。</summary>
    public IReadOnlyList<string> BlockedExamples => _blockedExamples;

    /// <summary>成功と偽れない状態か (削除できなかったもの・拒否・取消がある)。失効は <see cref="Expired"/> で別に示す。</summary>
    public bool IsPartial => Blocked > 0 || Rejected.Count > 0 || Cancelled;

    /// <summary>失効も部分失敗もなく完了したか。</summary>
    public bool Succeeded => !Expired && !IsPartial;

    internal void AddBlocked(IEnumerable<string> examples)
    {
        foreach (var example in examples)
        {
            if (_blockedExamples.Count < MaxExamples) _blockedExamples.Add(example);
        }
    }

    internal void Merge(CleanupOutcome part)
    {
        DeletedFiles += part.DeletedFiles;
        DeletedDirectories += part.DeletedDirectories;
        FreedBytes += part.FreedBytes;
        Retained += part.Retained;
        WipedFiles += part.WipedFiles;
        WipeSkipped += part.WipeSkipped;
        Rejected.AddRange(part.RejectedTargets);
    }
}

/// <summary>
/// 高度なクリーンの削除。走査結果の世代が現在のときだけ動き、開始と同時に世代を進めて二重削除を防ぐ。
/// ファイルは FileId・サイズ・更新時刻が走査時と一致した同一ハンドルだけを削除し、差し替わっていたものは残す。
/// カスタムフォルダーは中身だけを空にし、走査後に作られたものは残す。所有権・ACL の変更や再起動時削除は行わない。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AdvancedCleaner(AdvancedScanLedger ledger)
{
    /// <summary>
    /// 選択を削除する。取消は例外にせず <see cref="AdvancedCleanOutcome.Cancelled"/> で返す。
    /// 呼び出し側は削除の lease (<c>showProcessing:true</c>) を取り、確認画面で元に戻せないことを示してから呼ぶ。
    /// </summary>
    public AdvancedCleanOutcome Clean(
        AdvancedScanResult scan,
        AdvancedCleanSelection selection,
        AdvancedCleanOptions? options,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(selection);
        options ??= new AdvancedCleanOptions();
        var outcome = new AdvancedCleanOutcome();
        if (!ReferenceEquals(scan.Ledger, ledger) || !ledger.TryBeginDelete(scan.Generation))
        {
            outcome.Expired = true;
            return outcome;
        }

        var protection = options.Protection ?? AdvancedProtectedPaths.Default;
        try
        {
            var folders = ResolveFolders(scan, selection, options, protection, outcome);
            foreach (var item in ResolveItems(scan, selection, folders, outcome))
            {
                ct.ThrowIfCancellationRequested();
                DeleteItem(item, options, protection, progress, outcome, ct);
            }

            foreach (var folder in folders)
            {
                ct.ThrowIfCancellationRequested();
                EmptyFolder(folder, options, progress, outcome, ct);
            }
        }
        catch (OperationCanceledException)
        {
            outcome.Cancelled = true;
        }

        return outcome;
    }

    /// <summary>選択したフォルダーのうち、走査済みで、実行時の検査にも通るものだけを返す。</summary>
    private static List<AdvancedCustomFolderScan> ResolveFolders(
        AdvancedScanResult scan, AdvancedCleanSelection selection, AdvancedCleanOptions options, AdvancedProtectedPaths protection, AdvancedCleanOutcome outcome)
    {
        var context = options.GuardContext ?? CustomFolderGuardContext.FromEnvironment();
        var folders = new List<AdvancedCustomFolderScan>();
        foreach (var requested in selection.Folders)
        {
            var scanned = scan.CustomFolders.FirstOrDefault(f => f.Path is not null &&
                string.Equals(f.Path, requested.Path, StringComparison.OrdinalIgnoreCase));
            if (scanned is null || !scanned.IsSelectable)
            {
                outcome.Rejected.Add($"{requested.Path} (走査していない、または対象にできないフォルダー)");
            }
            else if (!CustomFolderGuard.Check(scanned.Path, context).IsAllowed || protection.OverlapsProtected(scanned.Path!))
            {
                outcome.Rejected.Add($"{scanned.Path} (実行時の検査で拒否)");
            }
            else if (!folders.Contains(scanned))
            {
                folders.Add(scanned);
            }
        }

        return folders;
    }

    /// <summary>選択した行のうち走査結果に含まれるものを返す。選択済みフォルダーの配下は個別に扱わない。</summary>
    private static List<AdvancedCleanItem> ResolveItems(
        AdvancedScanResult scan, AdvancedCleanSelection selection, List<AdvancedCustomFolderScan> folders, AdvancedCleanOutcome outcome)
    {
        // 大文字小文字だけが違う同名のファイルが同じフォルダーに並ぶことがある (フォルダー単位の大文字小文字区別)。
        // 完全一致のパスで引き、無ければ大文字小文字を無視して 1 件に決まるときだけ採る。決まらないものは削除しない。
        var exact = new Dictionary<string, AdvancedCleanItem>(StringComparer.Ordinal);
        foreach (var scannedItem in scan.Items) exact.TryAdd(scannedItem.Path, scannedItem);
        var loose = scan.Items.ToLookup(static i => i.Path, StringComparer.OrdinalIgnoreCase);
        var items = new List<AdvancedCleanItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requested in selection.Items)
        {
            if (!exact.TryGetValue(requested.Path, out var item))
            {
                var candidates = loose[requested.Path].DistinctBy(static i => i.Path, StringComparer.Ordinal).Take(2).ToList();
                if (candidates.Count == 1) item = candidates[0];
                else
                {
                    outcome.Rejected.Add(candidates.Count == 0
                        ? $"{requested.Path} (走査していないファイル)"
                        : $"{requested.Path} (大文字小文字だけが違う複数のファイルと区別できない)");
                    continue;
                }
            }

            if (folders.Any(f => item.Path.StartsWith(f.Path!.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) outcome.CoveredByFolders++;
            else if (seen.Add(item.Path)) items.Add(item);
        }

        return items;
    }

    private static void DeleteItem(
        AdvancedCleanItem item, AdvancedCleanOptions options, AdvancedProtectedPaths protection, IProgress<string>? progress, AdvancedCleanOutcome outcome, CancellationToken ct)
    {
        if (protection.IsProtectedPath(item.Path) || AdvancedProtectedPaths.IsProtectedFile(item.Name))
        {
            outcome.Rejected.Add($"{item.Path} (保護されたパス)");
            return;
        }

        if (item.Kind == AdvancedRuleKind.InvalidShortcut && !StillMissing(item, options))
        {
            outcome.Retained++;
            return;
        }

        var part = FileCleanupEngine.DeleteVerified([item.Identity], progress, ct, new CleanupRunOptions(options.Filter, null, options.Wipe));
        outcome.Merge(part);
        if (part.Blocked == 0) return;

        // 消そうとした時点で既に無かったものは失敗ではない (一時ファイルは走査後に消えやすい)。
        if (!File.Exists(item.Path))
        {
            outcome.Gone += part.Blocked;
            return;
        }

        outcome.Blocked += part.Blocked;
        outcome.AddBlocked(part.BlockedExamples);
    }

    private static bool StillMissing(AdvancedCleanItem item, AdvancedCleanOptions options)
    {
        var isMissing = options.IsShortcutTargetMissing ?? (static path => StartupCommandParser.IsConfirmedMissing(path));
        return item.ShortcutTarget is { } target && isMissing(target);
    }

    private static void EmptyFolder(
        AdvancedCustomFolderScan folder, AdvancedCleanOptions options, IProgress<string>? progress, AdvancedCleanOutcome outcome, CancellationToken ct)
    {
        var part = FileCleanupEngine.Run(
            [EngineTarget.CustomFolder(folder.Path!)], false, progress, ct,
            new CleanupRunOptions(options.Filter, folder.CreatedBefore, options.Wipe));
        outcome.Merge(part);
        outcome.Blocked += part.Blocked;
        outcome.AddBlocked(part.BlockedExamples);
    }
}
