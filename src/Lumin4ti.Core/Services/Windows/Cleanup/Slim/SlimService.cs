using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Services.Windows.Actions;
using EngineTarget = Lumin4ti.Core.Services.Windows.Actions.CleanupTarget;

namespace Lumin4ti.Core.Services.Windows.Cleanup.Slim;

/// <summary>スリム化が扱う場所。テストでは一時フォルダーへ差し替える。</summary>
/// <param name="SystemRoot">%SystemRoot% (例 C:\Windows)。</param>
/// <param name="PublicRoot">共有ユーザーフォルダー (例 C:\Users\Public)。確認できなければ null。</param>
public sealed record SlimPaths(string SystemRoot, string? PublicRoot)
{
    public string InstallerDirectory => Path.Combine(SystemRoot, "Installer");

    public string PatchCacheDirectory => Path.Combine(InstallerDirectory, "$PatchCache$");

    public string UpdateDownloadDirectory => Path.Combine(SystemRoot, "SoftwareDistribution", "Download");

    public IReadOnlyList<string> HelpRoots => [Path.Combine(SystemRoot, "Help")];

    public IReadOnlyList<string> WallpaperRoots =>
        [Path.Combine(SystemRoot, "Web", "Wallpaper"), Path.Combine(SystemRoot, "Web", "Screen")];

    /// <summary>実機の場所。共有ユーザーフォルダーは既知フォルダーから導き、環境変数は使わない。</summary>
    public static SlimPaths FromEnvironment()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        var publicRoot = string.IsNullOrWhiteSpace(documents) ? null : Path.GetDirectoryName(documents);
        if (publicRoot is null || !string.Equals(Path.GetFileName(publicRoot), "Public", StringComparison.OrdinalIgnoreCase))
        {
            publicRoot = null;
        }

        return new(Environment.GetFolderPath(Environment.SpecialFolder.Windows), publicRoot);
    }
}

/// <summary>サービスの一時停止の境界 (WindowsServiceControl)。</summary>
internal interface ISlimServiceSuspender
{
    Task<ServiceSuspension> SuspendAsync(IReadOnlyList<string> serviceNames, IProgress<string>? progress, CancellationToken ct);
}

/// <summary>復元ポイントの境界 (RestorePointService)。</summary>
internal interface ISlimRestorePoints
{
    Task<RestorePointResult> CreateAsync(string description, CancellationToken ct);

    Task<RestorePointProtectionResult> EnableProtectionAsync(CancellationToken ct);
}

/// <summary>SlimService の構成部品。</summary>
internal sealed record SlimServiceParts(
    SlimPaths Paths,
    InstallerOrphanScanner Installer,
    ComponentStore Components,
    LanguageCapabilityService Languages,
    ProtectedSystemFiles Protected,
    ISlimRestorePoints RestorePoints,
    ISlimServiceSuspender Services);

/// <summary>
/// スリム化 15 項目の一覧・走査・実行・復元ポイント連携・警告区分をまとめ、UI が使う契約を提供する。
/// 走査は何も変更しない。実行は走査結果の世代が現在のものと一致するときだけ行い、
/// 状態を変更するため呼び出し側が <c>MaintenanceOperationCoordinator</c> の lease を取ってから呼ぶ。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SlimService
{
    private const string RestorePointDescription = "Lumin4ti スリム化の前";
    private static readonly string[] UpdateCacheServices = ["wuauserv", "bits"];
    private static readonly string[] PatchCacheServices = ["msiserver"];

    private static readonly SlimItemId[] ExecutionOrder =
    [
        SlimItemId.SampleMusic, SlimItemId.SamplePictures, SlimItemId.SampleVideos, SlimItemId.SampleRecordedTv,
        SlimItemId.Help, SlimItemId.DefaultWallpapers,
        SlimItemId.InstallerOrphans, SlimItemId.PatchCache, SlimItemId.UpdateCache,
        SlimItemId.UpdateRollback, SlimItemId.UpdateBackup,
        SlimItemId.LanguageJa, SlimItemId.LanguageKo, SlimItemId.LanguageZhTw, SlimItemId.LanguageZhCn,
    ];

    // Windows 7 の既知ファイル名だけ。フォルダーの中身は一括で消さない (利用者のファイルが混ざるため)。
    private static readonly Dictionary<SlimItemId, (string[] Folder, string[] Files)> SampleSets = new()
    {
        [SlimItemId.SampleMusic] = (["Music", "Sample Music"], ["Kalimba.mp3", "Maid with the Flaxen Hair.mp3", "Sleep Away.mp3"]),
        [SlimItemId.SamplePictures] = (["Pictures", "Sample Pictures"],
            ["Chrysanthemum.jpg", "Desert.jpg", "Hydrangeas.jpg", "Jellyfish.jpg", "Koala.jpg", "Lighthouse.jpg", "Penguins.jpg", "Tulips.jpg"]),
        [SlimItemId.SampleVideos] = (["Videos", "Sample Videos"], ["Wildlife.wmv"]),
        [SlimItemId.SampleRecordedTv] = (["Recorded TV", "Sample Media"], ["win7_scenic-demo.wmv"]),
    };

    private static readonly Dictionary<SlimItemId, string> LanguageTags = new()
    {
        [SlimItemId.LanguageJa] = "ja-JP",
        [SlimItemId.LanguageKo] = "ko-KR",
        [SlimItemId.LanguageZhTw] = "zh-TW",
        [SlimItemId.LanguageZhCn] = "zh-CN",
    };

    private readonly SlimServiceParts _parts;
    private long _generation;

    public SlimService(ICommandExecutor executor)
        : this(CreateParts(executor, SlimPaths.FromEnvironment()))
    {
    }

    internal SlimService(SlimServiceParts parts) => _parts = parts;

    /// <summary>15 項目の定義 (表示順)。</summary>
    public static IReadOnlyList<SlimItemDefinition> Definitions { get; } = BuildDefinitions();

    /// <summary>現在の走査結果を失効させる。設定変更・削除開始・再走査の前に呼ぶ。</summary>
    public void Invalidate() => Interlocked.Increment(ref _generation);

    /// <summary>全項目を走査する。何も変更しない。取消は <see cref="OperationCanceledException"/>。</summary>
    public async Task<SlimScanResult> ScanAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var generation = Interlocked.Increment(ref _generation);
        var items = Definitions.Select(d => new SlimScanItem(d)).ToList();
        var payloads = new Dictionary<SlimItemId, object>();
        SlimScanItem Of(SlimItemId id) => items.First(i => i.Definition.Id == id);

        progress?.Report("  - Windows Installer の登録を確認しています");
        await Task.Run(() => ScanInstaller(Of(SlimItemId.InstallerOrphans), payloads, ct), ct).ConfigureAwait(false);
        progress?.Report("  - コンポーネントストアを解析しています");
        await ScanComponentStoreAsync(Of(SlimItemId.UpdateRollback), Of(SlimItemId.UpdateBackup), progress, ct).ConfigureAwait(false);
        await Task.Run(() =>
        {
            ScanFolder(Of(SlimItemId.UpdateCache), _parts.Paths.UpdateDownloadDirectory, payloads, ct);
            ScanFolder(Of(SlimItemId.PatchCache), _parts.Paths.PatchCacheDirectory, payloads, ct);
        }, ct).ConfigureAwait(false);
        progress?.Report("  - 言語の入力補助機能を確認しています");
        await ScanLanguagesAsync(items, progress, ct).ConfigureAwait(false);
        await Task.Run(() =>
        {
            ScanProtected(Of(SlimItemId.Help), _parts.Paths.HelpRoots, payloads, ct);
            ScanProtected(Of(SlimItemId.DefaultWallpapers), _parts.Paths.WallpaperRoots, payloads, ct);
            foreach (var id in SampleSets.Keys) ScanSamples(Of(id), payloads, ct);
        }, ct).ConfigureAwait(false);

        return new SlimScanResult(generation, items, payloads) { CompletedAtUtc = DateTime.UtcNow };
    }

    /// <summary>
    /// 選択された項目を実行する。走査結果が古ければ何も実行しない。
    /// 復元ポイントを要求された場合は、対象項目の前に作成し、利用できなければ利用者の判断に従う。
    /// 項目ごとの失敗・一部失敗・取消はそのまま区分して返し、成功と偽らない。
    /// </summary>
    public async Task<SlimRunResult> RunAsync(SlimRunRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Scan.Generation != Interlocked.Read(ref _generation))
        {
            return new([], null, CleanupMessage.Of("SlimStale"), false);
        }

        var results = new Dictionary<SlimItemId, SlimItemResult>();
        var runnable = SelectRunnable(request, results);
        if (runnable.Count == 0)
        {
            return Finish(results, null, false, results.Count == 0 ? CleanupMessage.Of("SlimNothingSelected") : null);
        }

        // 実行を始める時点で走査結果を失効させ、同じ結果での再実行を防ぐ。
        Invalidate();

        RestorePointResult? restorePoint = null;
        if (request.CreateRestorePoint && runnable.Any(id => DefinitionOf(id).NeedsRestorePoint))
        {
            (restorePoint, var proceed) = await EnsureRestorePointAsync(request, progress, ct).ConfigureAwait(false);
            if (!proceed)
            {
                var cancelled = restorePoint.Outcome == RestorePointOutcome.Cancelled;
                foreach (var id in runnable)
                {
                    results[id] = Skipped(id, cancelled
                        ? CleanupMessage.Of("SlimCanceledNotRun")
                        : CleanupMessage.Of("SlimRestorePointUnavailable", restorePoint.Message));
                }

                return Finish(results, restorePoint, false, null);
            }
        }

        var reboot = await ExecuteAsync(request, runnable, results, progress, ct).ConfigureAwait(false);
        return Finish(results, restorePoint, reboot, null);
    }

    private static SlimServiceParts CreateParts(ICommandExecutor executor, SlimPaths paths) => new(
        paths,
        new InstallerOrphanScanner(paths.InstallerDirectory, [new MsiApiRegistrationSource(), new InstallerUserDataRegistrationSource()]),
        new ComponentStore(executor),
        new LanguageCapabilityService(executor, new WindowsUserLanguageSource()),
        ProtectedSystemFiles.Create(),
        new RestorePointAdapter(new RestorePointService(executor)),
        new ServiceSuspender(executor));

    private static SlimItemDefinition DefinitionOf(SlimItemId id) => Definitions.First(d => d.Id == id);

    private static SlimItemDefinition Define(SlimItemId id, string key, SlimWarningLevel warning, bool secondConfirmation, bool restorePoint, bool hasWarningKey = true) => new(
        id,
        key,
        $"Text.Cleanup.Slim.{key}.Name",
        $"Text.Cleanup.Slim.{key}.Description",
        hasWarningKey && warning != SlimWarningLevel.None ? $"Text.Cleanup.Slim.{key}.Warning" : null,
        warning,
        secondConfirmation,
        restorePoint);

    private static List<SlimItemDefinition> BuildDefinitions() =>
    [
        Define(SlimItemId.InstallerOrphans, "InstallerOrphans", SlimWarningLevel.Notice, false, true),
        Define(SlimItemId.UpdateRollback, "UpdateRollback", SlimWarningLevel.Irreversible, true, true),
        Define(SlimItemId.UpdateBackup, "UpdateBackup", SlimWarningLevel.Notice, false, true),
        Define(SlimItemId.UpdateCache, "UpdateCache", SlimWarningLevel.None, false, false),
        Define(SlimItemId.PatchCache, "PatchCache", SlimWarningLevel.Caution, false, true),
        Define(SlimItemId.LanguageJa, "LanguageJa", SlimWarningLevel.Caution, false, true),
        Define(SlimItemId.LanguageKo, "LanguageKo", SlimWarningLevel.Caution, false, true),
        Define(SlimItemId.LanguageZhTw, "LanguageZhTw", SlimWarningLevel.Caution, false, true),
        Define(SlimItemId.LanguageZhCn, "LanguageZhCn", SlimWarningLevel.Caution, false, true),
        Define(SlimItemId.Help, "Help", SlimWarningLevel.Notice, false, true),
        Define(SlimItemId.DefaultWallpapers, "DefaultWallpapers", SlimWarningLevel.Notice, false, true),
        Define(SlimItemId.SampleMusic, "SampleMusic", SlimWarningLevel.None, false, false),
        Define(SlimItemId.SamplePictures, "SamplePictures", SlimWarningLevel.None, false, false),
        Define(SlimItemId.SampleVideos, "SampleVideos", SlimWarningLevel.None, false, false),
        Define(SlimItemId.SampleRecordedTv, "SampleRecordedTv", SlimWarningLevel.None, false, false),
    ];

    // ───────────── 走査 ─────────────

    private void ScanInstaller(SlimScanItem item, Dictionary<SlimItemId, object> payloads, CancellationToken ct)
    {
        var scan = _parts.Installer.Scan(ct);
        payloads[item.Definition.Id] = scan;
        if (!scan.Available)
        {
            Mark(item, SlimScanStatus.Unavailable, SlimReason.EnumerationFailed, scan.UnavailableReason ?? string.Empty);
            return;
        }

        if (scan.Orphans.Count == 0)
        {
            Mark(item, SlimScanStatus.NothingToClean, SlimReason.NotPresent, string.Empty);
            return;
        }

        Mark(item, SlimScanStatus.Available, SlimReason.None, string.Empty);
        item.Bytes = scan.Bytes;
        item.Files = scan.Orphans.Count;
        item.Representatives = [.. scan.Orphans.Take(5).Select(o => o.Path)];
    }

    private async Task ScanComponentStoreAsync(SlimScanItem rollback, SlimScanItem backup, IProgress<string>? progress, CancellationToken ct)
    {
        var analysis = await _parts.Components.AnalyzeAsync(progress, ct).ConfigureAwait(false);
        if (analysis.Analysis is not { } value)
        {
            Mark(rollback, SlimScanStatus.Unavailable, SlimReason.ToolFailed, analysis.Error ?? string.Empty);
            Mark(backup, SlimScanStatus.Unavailable, SlimReason.ToolFailed, analysis.Error ?? string.Empty);
            return;
        }

        // ResetBase は解析で空き容量を見積もれない。ストア全体の整理として常に実行できる。
        Mark(rollback, SlimScanStatus.Available, SlimReason.None, string.Empty);
        if (value.EstimatedReclaimableBytes == 0 && (value.ReclaimablePackages ?? 0) == 0)
        {
            Mark(backup, SlimScanStatus.NothingToClean, SlimReason.NotPresent, string.Empty);
            return;
        }

        Mark(backup, SlimScanStatus.Available, SlimReason.None, string.Empty);
        backup.Bytes = value.EstimatedReclaimableBytes;
        backup.BytesIsEstimate = true;
    }

    private static void ScanFolder(SlimScanItem item, string folder, Dictionary<SlimItemId, object> payloads, CancellationToken ct)
    {
        var scan = FileCleanupScanner.Scan(EngineTarget.Contents(folder) with { IsActualPath = true }, null, ct);
        payloads[item.Definition.Id] = scan;
        if (scan.Rejection is { } rejection)
        {
            Mark(item, SlimScanStatus.Unavailable, SlimReason.Rejected, rejection);
        }
        else if (scan.Missing || scan.Files + scan.Directories == 0)
        {
            Mark(item, SlimScanStatus.NothingToClean, SlimReason.NotPresent, string.Empty);
        }
        else
        {
            Mark(item, SlimScanStatus.Available, SlimReason.None, string.Empty);
            item.Bytes = scan.Bytes;
            item.Files = scan.Files;
            item.Representatives = scan.Representatives;
        }
    }

    private async Task ScanLanguagesAsync(List<SlimScanItem> items, IProgress<string>? progress, CancellationToken ct)
    {
        var states = await _parts.Languages.ScanAsync(progress, ct).ConfigureAwait(false);
        foreach (var (id, tag) in LanguageTags)
        {
            var item = items.First(i => i.Definition.Id == id);
            var state = states.First(s => s.Tag == tag);
            switch (state.Status)
            {
                case LanguageCapabilityStatus.Removable:
                    Mark(item, SlimScanStatus.Available, SlimReason.None, state.Detail);
                    break;
                case LanguageCapabilityStatus.NotInstalled:
                    Mark(item, SlimScanStatus.NothingToClean, SlimReason.NotPresent, state.Detail);
                    break;
                case LanguageCapabilityStatus.InUse:
                    Mark(item, SlimScanStatus.Unavailable, SlimReason.LanguageInUse, state.Detail);
                    break;
                default:
                    Mark(item, SlimScanStatus.Unavailable, state.UserSettingsUnreadable ? SlimReason.UserHiveNotLoaded : SlimReason.ToolFailed, state.Detail);
                    break;
            }
        }
    }

    private void ScanProtected(SlimScanItem item, IReadOnlyList<string> roots, Dictionary<SlimItemId, object> payloads, CancellationToken ct)
    {
        var scan = _parts.Protected.Scan(roots, ct);
        payloads[item.Definition.Id] = scan;
        if (scan.Candidates.Count > 0)
        {
            Mark(item, SlimScanStatus.Available, SlimReason.None, string.Empty);
            item.Bytes = scan.CandidateBytes;
            item.Files = scan.Candidates.Count;
            item.Representatives = [.. scan.Candidates.Take(5).Select(c => c.Path)];
        }
        else if (scan.NothingBecauseProtected)
        {
            Mark(item, SlimScanStatus.NothingToClean, SlimReason.ProtectedByWindows,
                $"Windows が保護している (保護 {scan.ProtectedCount} 件・TrustedInstaller 所有 {scan.TrustedInstallerCount} 件)");
        }
        else if (scan.UnknownCount + scan.Unreadable > 0)
        {
            Mark(item, SlimScanStatus.Unavailable, SlimReason.EnumerationFailed, "保護状態を確認できないファイルがあります。");
        }
        else
        {
            Mark(item, SlimScanStatus.NothingToClean, SlimReason.NotPresent, string.Empty);
        }
    }

    private void ScanSamples(SlimScanItem item, Dictionary<SlimItemId, object> payloads, CancellationToken ct)
    {
        var targets = SampleTargets(item.Definition.Id);
        var scans = new List<(EngineTarget Target, CleanupScanResult Scan)>();
        foreach (var target in targets)
        {
            scans.Add((target, FileCleanupScanner.Scan(target, null, ct)));
        }

        payloads[item.Definition.Id] = scans;
        var found = scans.Where(s => s.Scan.Files > 0).ToList();
        if (scans.Any(s => s.Scan.Rejection is not null))
        {
            Mark(item, SlimScanStatus.Unavailable, SlimReason.Rejected, scans.First(s => s.Scan.Rejection is not null).Scan.Rejection!);
        }
        else if (found.Count == 0)
        {
            Mark(item, SlimScanStatus.NothingToClean, SlimReason.NotPresent, string.Empty);
        }
        else
        {
            Mark(item, SlimScanStatus.Available, SlimReason.None, string.Empty);
            item.Bytes = found.Sum(s => s.Scan.Bytes);
            item.Files = found.Sum(s => s.Scan.Files);
            item.Representatives = [.. found.SelectMany(s => s.Scan.Representatives).Take(5)];
        }
    }

    private List<EngineTarget> SampleTargets(SlimItemId id)
    {
        var root = _parts.Paths.PublicRoot;
        if (root is null || !SampleSets.TryGetValue(id, out var set))
        {
            return [];
        }

        var folder = Path.Combine([root, .. set.Folder]);
        return [.. set.Files.Select(file => EngineTarget.Files(folder, file) with { IsActualPath = true })];
    }

    private static void Mark(SlimScanItem item, SlimScanStatus status, SlimReason reason, string detail)
    {
        item.Status = status;
        item.Reason = reason;
        item.Detail = detail;
    }

    // ───────────── 実行 ─────────────

    private List<SlimItemId> SelectRunnable(SlimRunRequest request, Dictionary<SlimItemId, SlimItemResult> results)
    {
        var runnable = new List<SlimItemId>();
        foreach (var id in ExecutionOrder.Where(request.Items.Contains))
        {
            var item = request.Scan.Item(id);
            if (!item.Selectable)
            {
                results[id] = Skipped(id, CleanupMessage.Of("SlimItemUnavailable"));
            }
            else if (item.Definition.RequiresSecondConfirmation && !request.ConfirmedIrreversible)
            {
                results[id] = Skipped(id, CleanupMessage.Of("SlimNeedsTwoStep"));
            }
            else
            {
                runnable.Add(id);
            }
        }

        return runnable;
    }

    private async Task<(RestorePointResult Result, bool Proceed)> EnsureRestorePointAsync(SlimRunRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("  - 復元ポイントを作成しています");
        var point = await _parts.RestorePoints.CreateAsync(RestorePointDescription, ct).ConfigureAwait(false);
        var retried = false;
        while (true)
        {
            if (point.IsUsable) return (point, true);
            if (point.Outcome == RestorePointOutcome.Cancelled || request.OnRestorePointUnavailable is null) return (point, false);

            var decision = await request.OnRestorePointUnavailable(point, ct).ConfigureAwait(false);
            if (decision == RestorePointDecision.ContinueWithout) return (point, true);
            if (decision != RestorePointDecision.EnableProtectionAndRetry || retried || point.Outcome != RestorePointOutcome.ProtectionDisabled)
            {
                return (point, false);
            }

            retried = true;
            var enabled = await _parts.RestorePoints.EnableProtectionAsync(ct).ConfigureAwait(false);
            point = enabled.Enabled
                ? await _parts.RestorePoints.CreateAsync(RestorePointDescription, ct).ConfigureAwait(false)
                : new RestorePointResult(RestorePointOutcome.Failed, enabled.Message, null);
        }
    }

    private async Task<bool> ExecuteAsync(SlimRunRequest request, List<SlimItemId> runnable, Dictionary<SlimItemId, SlimItemResult> results, IProgress<string>? progress, CancellationToken ct)
    {
        var reboot = false;
        var stopped = false;
        var rollbackRan = false;
        foreach (var id in runnable)
        {
            if (stopped || ct.IsCancellationRequested)
            {
                results[id] = Skipped(id, CleanupMessage.Of("SlimCanceledNotRun"));
                stopped = true;
                continue;
            }

            try
            {
                var (result, needsReboot) = id == SlimItemId.UpdateBackup && rollbackRan
                    ? (CoveredByRollback(results[SlimItemId.UpdateRollback]), false)
                    : await ExecuteItemAsync(request, id, progress, ct).ConfigureAwait(false);
                results[id] = result;
                reboot |= needsReboot;
                rollbackRan |= id == SlimItemId.UpdateRollback;
            }
            catch (OperationCanceledException)
            {
                results[id] = new(id, SlimItemStatus.Cancelled, 0, 0, CleanupMessage.Of("SlimCanceledPartial"));
                stopped = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LoggerBootstrap.Log.Error($"slim {id}: {ex.Message}");
                results[id] = new(id, SlimItemStatus.Failed, 0, 0, CleanupMessage.Of("SlimFailed", ex.Message));
            }
        }

        return reboot;
    }

    private async Task<(SlimItemResult Result, bool RebootRequired)> ExecuteItemAsync(SlimRunRequest request, SlimItemId id, IProgress<string>? progress, CancellationToken ct)
    {
        // DISM 系の項目 (ロールバック・バックアップ・言語) は走査時の実体を持たない。
        var payload = request.Scan.Payloads.GetValueOrDefault(id)!;
        switch (id)
        {
            case SlimItemId.InstallerOrphans:
                return (await RunInstallerAsync(id, (InstallerOrphanScan)payload, progress, ct).ConfigureAwait(false), false);
            case SlimItemId.UpdateCache:
                return (await RunFolderAsync(id, _parts.Paths.UpdateDownloadDirectory, UpdateCacheServices, (CleanupScanResult)payload, progress, ct).ConfigureAwait(false), false);
            case SlimItemId.PatchCache:
                return (await RunFolderAsync(id, _parts.Paths.PatchCacheDirectory, PatchCacheServices, (CleanupScanResult)payload, progress, ct).ConfigureAwait(false), false);
            case SlimItemId.UpdateRollback:
            case SlimItemId.UpdateBackup:
                return DismResult(id, await _parts.Components.CleanupAsync(id == SlimItemId.UpdateRollback, progress, ct).ConfigureAwait(false));
            case SlimItemId.Help:
            case SlimItemId.DefaultWallpapers:
                return (await Task.Run(() => RunProtected(id, (ProtectedFileScan)payload, progress, ct), ct).ConfigureAwait(false), false);
            case SlimItemId.SampleMusic:
            case SlimItemId.SamplePictures:
            case SlimItemId.SampleVideos:
            case SlimItemId.SampleRecordedTv:
                return (await Task.Run(() => RunSamples(id, (List<(EngineTarget Target, CleanupScanResult Scan)>)payload, progress, ct), ct).ConfigureAwait(false), false);
            default:
                return DismResult(id, await _parts.Languages.RemoveAsync(LanguageTags[id], progress, ct).ConfigureAwait(false));
        }
    }

    private async Task<SlimItemResult> RunInstallerAsync(SlimItemId id, InstallerOrphanScan scan, IProgress<string>? progress, CancellationToken ct)
    {
        var run = await Task.Run(() => _parts.Installer.Run(scan, progress, ct), ct).ConfigureAwait(false);
        return run.Outcome is { } outcome
            ? FromOutcome(id, outcome)
            : new(id, SlimItemStatus.Failed, 0, 0, CleanupMessage.Of("SlimRecheckFailed", run.UnavailableReason));
    }

    private async Task<SlimItemResult> RunFolderAsync(SlimItemId id, string folder, string[] services, CleanupScanResult scan, IProgress<string>? progress, CancellationToken ct)
    {
        var suspension = await _parts.Services.SuspendAsync(services, progress, ct).ConfigureAwait(false);
        SlimItemResult? result = null;
        ExceptionDispatchInfo? error = null;
        try
        {
            if (suspension.FailedToStop.Count > 0)
            {
                result = new(id, SlimItemStatus.Failed, 0, 0, CleanupMessage.Of("ServiceStopFailed", string.Join(", ", suspension.FailedToStop)));
            }
            else
            {
                var outcome = await Task.Run(
                    () => FileCleanupEngine.Run([EngineTarget.Contents(folder) with { IsActualPath = true }], false, progress, ct, new CleanupRunOptions(CreatedBefore: scan.CreatedBefore)),
                    ct).ConfigureAwait(false);
                result = FromOutcome(id, outcome);
            }
        }
        catch (Exception ex)
        {
            // 取消・例外のときも必ず復帰を試み、復帰の失敗を取消・例外より先に結果へ出す。
            error = ExceptionDispatchInfo.Capture(ex);
        }

        var resumeFailures = await suspension.ResumeAsync().ConfigureAwait(false);
        if (resumeFailures.Count > 0)
        {
            // 削除の成否によらず、復帰の失敗は成功にしない。
            return new(id, SlimItemStatus.Failed, 0, 0, CleanupMessage.Of("SlimServiceResumeFailed", string.Join(", ", resumeFailures)));
        }

        error?.Throw();
        return result!;
    }

    private SlimItemResult RunProtected(SlimItemId id, ProtectedFileScan scan, IProgress<string>? progress, CancellationToken ct) =>
        FromOutcome(id, _parts.Protected.Run(scan.Candidates, progress, ct, scan.CompletedAtUtc - CleanupScanResult.CreationMargin));

    private static SlimItemResult RunSamples(SlimItemId id, List<(EngineTarget Target, CleanupScanResult Scan)> scans, IProgress<string>? progress, CancellationToken ct)
    {
        var targets = scans.Where(s => s.Scan.Files > 0).ToList();
        var total = new CleanupOutcome();
        foreach (var (target, scan) in targets)
        {
            var outcome = FileCleanupEngine.Run([target], false, progress, ct, new CleanupRunOptions(CreatedBefore: scan.CreatedBefore));
            total.DeletedFiles += outcome.DeletedFiles;
            total.DeletedDirectories += outcome.DeletedDirectories;
            total.FreedBytes += outcome.FreedBytes;
            total.Retained += outcome.Retained;
            total.Blocked += outcome.Blocked;
            total.RejectedTargets.AddRange(outcome.RejectedTargets);
        }

        return FromOutcome(id, total);
    }

    private static (SlimItemResult Result, bool RebootRequired) DismResult(SlimItemId id, DismOperationResult result) =>
        (new SlimItemResult(id, result.Success ? SlimItemStatus.Succeeded : SlimItemStatus.Failed, 0, 0, result.Message), result.Success && result.RebootRequired);

    private static SlimItemResult CoveredByRollback(SlimItemResult rollback) =>
        rollback.Status == SlimItemStatus.Succeeded
            ? new(SlimItemId.UpdateBackup, SlimItemStatus.Succeeded, 0, 0, CleanupMessage.Of("SlimBackupIncluded"))
            : Skipped(SlimItemId.UpdateBackup, CleanupMessage.Of("SlimBackupSkipped", rollback.Message));

    /// <summary>削除の集計から結果区分を決める。使用中・権限不足・拒否が 1 件でもあれば成功にしない。</summary>
    internal static SlimItemResult FromOutcome(SlimItemId id, CleanupOutcome outcome)
    {
        var deleted = outcome.DeletedFiles + outcome.DeletedDirectories;
        var problems = outcome.Blocked + outcome.RejectedTargets.Count;
        var status = problems == 0 ? SlimItemStatus.Succeeded : deleted > 0 ? SlimItemStatus.Partial : SlimItemStatus.Failed;
        var example = outcome.BlockedExamples.FirstOrDefault() ?? outcome.RejectedTargets.FirstOrDefault();
        var message = example is null
            ? CleanupMessage.Of("SlimOutcome", outcome.DeletedFiles, outcome.FreedBytes, outcome.Retained, outcome.Blocked, outcome.RejectedTargets.Count)
            : CleanupMessage.Of("SlimOutcomeExample", outcome.DeletedFiles, outcome.FreedBytes, outcome.Retained, outcome.Blocked, outcome.RejectedTargets.Count, example);

        return new(id, status, outcome.FreedBytes, outcome.DeletedFiles, message);
    }

    private static SlimItemResult Skipped(SlimItemId id, string message) => new(id, SlimItemStatus.Skipped, 0, 0, message);

    private static SlimRunResult Finish(Dictionary<SlimItemId, SlimItemResult> results, RestorePointResult? restorePoint, bool reboot, string? rejected) =>
        new([.. ExecutionOrder.Where(results.ContainsKey).Select(id => results[id])], restorePoint, rejected, reboot);

    private sealed class RestorePointAdapter(RestorePointService service) : ISlimRestorePoints
    {
        public Task<RestorePointResult> CreateAsync(string description, CancellationToken ct) => service.CreateAsync(description, ct);

        public Task<RestorePointProtectionResult> EnableProtectionAsync(CancellationToken ct) => service.EnableProtectionAsync(ct);
    }

    private sealed class ServiceSuspender(ICommandExecutor executor) : ISlimServiceSuspender
    {
        public Task<ServiceSuspension> SuspendAsync(IReadOnlyList<string> serviceNames, IProgress<string>? progress, CancellationToken ct) =>
            WindowsServiceControl.SuspendAsync(executor, serviceNames, progress, ct);
    }
}
