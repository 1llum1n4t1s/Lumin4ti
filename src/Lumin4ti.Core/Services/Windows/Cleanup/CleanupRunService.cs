using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Services.Windows.Actions;
using Lumin4ti.Core.Services.Windows.Cleanup.Handlers;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>起動中の製品を閉じるかの選択。</summary>
public enum CleanupProcessChoice
{
    /// <summary>終了する (WM_CLOSE を送り、最大 15 秒待つ)。</summary>
    Close,

    /// <summary>この項目を飛ばす。</summary>
    SkipRule,

    /// <summary>中止する (残りの項目も実行しない)。</summary>
    Abort,
}

/// <summary>確認画面に示す、動いている製品の情報。</summary>
/// <param name="ProcessNames">動いているプロセス名 (拡張子なし)。</param>
public sealed record CleanupProcessPrompt(string RuleId, string RuleName, IReadOnlyList<string> ProcessNames);

/// <summary>実行中に利用者へ確認する境界。画面が実装する。</summary>
public interface ICleanupRunPrompts
{
    /// <summary>手順 1: 起動中の製品名を示し、終了する・この項目を飛ばす・中止のどれかを選ばせる。</summary>
    Task<CleanupProcessChoice> ChooseForRunningProcessesAsync(CleanupProcessPrompt prompt, CancellationToken ct);

    /// <summary>
    /// 手順 3: WM_CLOSE で残ったときだけ呼ばれる。未保存の入力が失われることを示して再確認し、
    /// 同意があれば同一セッション・同一利用者の残りのプロセスだけを強制終了する。
    /// </summary>
    Task<bool> ConfirmForceTerminationAsync(CleanupProcessPrompt prompt, CancellationToken ct);
}

/// <summary>実行の指定。<paramref name="Snapshot"/> を走査した規則のうち、選択したものだけを実行する。</summary>
/// <param name="Snapshot">走査結果。現在のもの (世代・設定が一致) でなければ何も実行しない。</param>
/// <param name="SelectedRuleIds">選択した規則 ID。走査結果に無い ID は実行しない。</param>
public sealed record CleanupRunRequest(CleanupScanSnapshot Snapshot, IReadOnlyCollection<string> SelectedRuleIds)
{
    /// <summary>受信ファイルなどの <c>UserFiles</c> (カスタムフォルダーを含む) のうち、専用の再確認を終えた規則 ID。無ければ実行しない。</summary>
    public IReadOnlyCollection<string> ConfirmedUserFileRuleIds { get; init; } = [];

    /// <summary>実行中のプロセス確認。<c>null</c> なら、動いている製品の規則は飛ばす。</summary>
    public ICleanupRunPrompts? Prompts { get; init; }
}

/// <summary>
/// 通常クリーンの実行。走査済みで選択された規則だけを、走査時の除外・走査後に作られたものの保持・安全な削除の設定で処理する。
/// 状態を変更するため、呼び出し側が <c>MaintenanceOperationCoordinator</c> の lease (<c>showProcessing:true</c>) を取ってから呼ぶ。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CleanupRunService
{
    /// <summary>エンジンが、中にあったリンクを触らずに残したときに記録する文言の末尾。失敗ではなく通知として扱う。</summary>
    private const string LinkProtectedSuffix = "(リンクを保護するため)";

    private readonly CleanupScanService _scan;
    private readonly RunningProcessGuard _guard;
    private readonly Func<IReadOnlyList<string>, IProgress<string>?, CancellationToken, Task<ServiceSuspension>> _suspend;
    private readonly Func<DateTimeOffset> _now;

    public CleanupRunService(CleanupScanService scan, ICommandExecutor executor)
        : this(
            scan,
            new RunningProcessGuard(),
            (names, progress, ct) => WindowsServiceControl.SuspendAsync(executor, names, progress, ct),
            () => DateTimeOffset.Now)
    {
    }

    internal CleanupRunService(
        CleanupScanService scan,
        RunningProcessGuard guard,
        Func<IReadOnlyList<string>, IProgress<string>?, CancellationToken, Task<ServiceSuspension>> suspend,
        Func<DateTimeOffset> now)
    {
        _scan = scan;
        _guard = guard;
        _suspend = suspend;
        _now = now;
    }

    /// <summary>
    /// 選択した規則を実行する。取消は例外にせず、実行済み・取消・未着手を区別した結果で返す。
    /// 走査結果が失効していれば何も実行せず <see cref="CleanupRunResult.StaleScan"/> を返す。
    /// </summary>
    public async Task<CleanupRunResult> RunAsync(CleanupRunRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = _now();
        var snapshot = request.Snapshot;
        if (!_scan.TryBeginRun(snapshot)) return new CleanupRunResult([], true, false, started, started);

        var selected = new HashSet<string>(request.SelectedRuleIds, StringComparer.OrdinalIgnoreCase);
        var confirmed = new HashSet<string>(request.ConfirmedUserFileRuleIds, StringComparer.OrdinalIgnoreCase);
        var scope = _scan.Parts.Scope();
        var results = new List<CleanupRuleRunResult>();
        var canceled = false;
        // 古さの基準時刻は実行全体で 1 つにし、エンジンの実行と先に処理した規則の領域が同じ判定を下す。
        var handled = new ClaimSet { Now = DateTime.UtcNow };
        foreach (var item in snapshot.Items.Where(i => selected.Remove(i.Rule.Id)))
        {
            if (canceled || ct.IsCancellationRequested)
            {
                canceled = true;
                results.Add(Result(item.Rule, CleanupRunStatus.NotRun));
                continue;
            }

            var outcome = await RunRuleAsync(item, snapshot, scope, confirmed.Contains(item.Rule.Id), request.Prompts, handled, progress, ct).ConfigureAwait(false);
            results.Add(outcome.Result);
            canceled = outcome.Canceled;
        }

        results.AddRange(selected.Select(id => Result(UnknownRule(id), CleanupRunStatus.Skipped, CleanupSkipReason.NotScanned)));
        var completed = _now();
        var run = new CleanupRunResult(results, false, canceled, started, completed);
        RecordRun(run, completed);
        return run;
    }

    private void RecordRun(CleanupRunResult run, DateTimeOffset completed)
    {
        var settings = _scan.Parts.Settings;
        settings.RecordRun(run.FilesDeleted, run.BytesFreed, run.FilesSkipped, !run.IsComplete, completed);
        try
        {
            settings.SaveAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LoggerBootstrap.Log.Warn($"クリーンアップの実績を保存できませんでした ({ex.GetType().Name})");
        }
    }

    private static CleanupRuleInfo UnknownRule(string id) =>
        new(id, id, null, CleanupRuleKind.Other, CleanupRuleCategory.Apps, CleanupRuleImpact.Junk, false, null, false, [], [], false);

    private static CleanupRuleRunResult Result(
        CleanupRuleInfo rule, CleanupRunStatus status, CleanupSkipReason skip = CleanupSkipReason.None, IReadOnlyList<string>? details = null) =>
        new(rule, status, skip, 0, 0, 0, 0, 0, 0, 0, 0, details ?? []);

    private readonly record struct RuleOutcome(CleanupRuleRunResult Result, bool Canceled);

    // ----- 1 規則の実行 -----

    private async Task<RuleOutcome> RunRuleAsync(
        CleanupScanItem item,
        CleanupScanSnapshot snapshot,
        InteractiveScopeResult scope,
        bool userFilesConfirmed,
        ICleanupRunPrompts? prompts,
        ClaimSet handled,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (Precheck(item, snapshot, scope, userFilesConfirmed) is { } skipped) return new RuleOutcome(skipped, false);
        var plan = snapshot.Plans[item.Rule.Id];
        try
        {
            var closed = await EnsureClosedAsync(plan.Rule, item, prompts, ct).ConfigureAwait(false);
            if (closed.Abort) return new RuleOutcome(Result(item.Rule, CleanupRunStatus.NotRun), true);
            if (closed.Skipped is not null) return new RuleOutcome(closed.Skipped, false);
        }
        catch (OperationCanceledException)
        {
            return new RuleOutcome(Result(item.Rule, CleanupRunStatus.NotRun), true);
        }

        return await ExecuteAsync(item, plan, snapshot.Context, handled, progress, ct).ConfigureAwait(false);
    }

    /// <summary>実行前の検査。実行してはならない理由があれば、その結果を返す。</summary>
    private CleanupRuleRunResult? Precheck(CleanupScanItem item, CleanupScanSnapshot snapshot, InteractiveScopeResult scope, bool userFilesConfirmed)
    {
        var info = item.Rule;
        switch (item.State)
        {
            case CleanupItemState.NotDetected:
                return Result(info, CleanupRunStatus.Skipped, CleanupSkipReason.NothingFound);
            case CleanupItemState.Empty:
                return Result(info, CleanupRunStatus.NothingToDo);
            case CleanupItemState.Unavailable:
                return Result(info, CleanupRunStatus.Skipped, CleanupSkipReason.Unavailable, item.Detail);
        }

        if (!snapshot.Plans.TryGetValue(info.Id, out var plan)) return Result(info, CleanupRunStatus.Skipped, CleanupSkipReason.NotScanned);
        if (info.Impact == CleanupRuleImpact.UserFiles && !userFilesConfirmed)
        {
            return Result(info, CleanupRunStatus.Skipped, CleanupSkipReason.UserFilesNotConfirmed);
        }

        if (CleanupScanService.IsUserScoped(plan.Rule) && !scope.UserScopeAvailable)
        {
            return Result(info, CleanupRunStatus.Skipped, CleanupSkipReason.Unavailable, [CleanupMessage.Of("UserScopeUnavailable")]);
        }

        if (info.IsCustom && plan.Targets.Any(t => !CustomFolderGuard.Check(t.Path, _scan.Parts.GuardContext()).IsAllowed))
        {
            return Result(info, CleanupRunStatus.Skipped, CleanupSkipReason.Unavailable, [CleanupMessage.Of("CustomFolderRejectedAtRun")]);
        }

        return null;
    }

    private readonly record struct CloseOutcome(CleanupRuleRunResult? Skipped, bool Abort);

    /// <summary>
    /// 規則の製品が動いていれば、確認 → WM_CLOSE → (残れば) 再確認のうえ強制終了の順に進める。
    /// 閉じられなければ、その規則だけを Skipped にする (成功と表示しない)。
    /// </summary>
    private async Task<CloseOutcome> EnsureClosedAsync(CleanupRule rule, CleanupScanItem item, ICleanupRunPrompts? prompts, CancellationToken ct)
    {
        if (rule.Processes.Count == 0) return default;
        var detection = _guard.Detect(rule);
        if (!detection.IsRunning) return default;
        if (detection.UnverifiableCount > 0) return Skip(item, CleanupSkipReason.ProcessUnverifiable);
        if (prompts is null) return Skip(item, CleanupSkipReason.ProcessRunningDeclined);

        var prompt = new CleanupProcessPrompt(rule.Id, rule.Name, [.. detection.Verified.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase)]);
        switch (await prompts.ChooseForRunningProcessesAsync(prompt, ct).ConfigureAwait(false))
        {
            case CleanupProcessChoice.Abort:
                return new CloseOutcome(null, true);
            case not CleanupProcessChoice.Close:
                return Skip(item, CleanupSkipReason.ProcessRunningDeclined);
        }

        var closed = await _guard.CloseAsync(
            rule,
            detection,
            (remaining, token) => prompts.ConfirmForceTerminationAsync(
                prompt with { ProcessNames = [.. remaining.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase)] }, token),
            ct).ConfigureAwait(false);
        return closed.Status == ProcessGuardStatus.Clear ? default : Skip(item, ToSkipReason(closed.Reason));
    }

    private static CloseOutcome Skip(CleanupScanItem item, CleanupSkipReason reason) =>
        new(Result(item.Rule, CleanupRunStatus.Skipped, reason), false);

    private static CleanupSkipReason ToSkipReason(ProcessGuardSkipReason reason) => reason switch
    {
        ProcessGuardSkipReason.Unverifiable => CleanupSkipReason.ProcessUnverifiable,
        ProcessGuardSkipReason.ForceNotAllowed => CleanupSkipReason.ProcessStillRunning,
        ProcessGuardSkipReason.ForceDeclined => CleanupSkipReason.ForceTerminationDeclined,
        ProcessGuardSkipReason.IdentityMismatch => CleanupSkipReason.ProcessIdentityMismatch,
        _ => CleanupSkipReason.TerminateFailed,
    };

    // ----- 削除の実行 -----

    /// <summary>1 規則の集計。</summary>
    private sealed class RunTally
    {
        /// <summary>この実行で先に処理した規則の領域。後の規則は、先の規則が消した (または失敗を報告した) ものを数えず、失敗にもしない。</summary>
        public ClaimSet Handled { get; init; } = new();

        public long Files { get; set; }

        public long Directories { get; set; }

        public long Items { get; set; }

        public long Bytes { get; set; }

        public long Retained { get; set; }

        public long Blocked { get; set; }

        public long Wiped { get; set; }

        public long WipeSkipped { get; set; }

        /// <summary>失敗・部分失敗の数。0 でなければ成功としない。</summary>
        public int Failures { get; set; }

        /// <summary>止めたサービスを元へ戻せなかった。取消・成功に隠さない。</summary>
        public bool ResumeFailed { get; set; }

        /// <summary>ハンドラーが Failed を返した。削除件数にかかわらず規則を Failed にする。</summary>
        public bool HandlerFailed { get; set; }

        public List<string> Details { get; } = [];

        public long Deleted => Files + Directories + Items;

        public void Fail(string message)
        {
            Failures++;
            Note(message);
        }

        public void Note(string message)
        {
            if (Details.Count < 16) Details.Add(message.Length <= 1024 ? message : message[..1024] + "…");
        }
    }

    private async Task<RuleOutcome> ExecuteAsync(
        CleanupScanItem item, RulePlan plan, CleanupScanContext context, ClaimSet handled, IProgress<string>? progress, CancellationToken ct)
    {
        var tally = new RunTally { Handled = handled };
        ServiceSuspension? suspension = null;
        var canceled = false;
        try
        {
            // 停止の要求前に復帰対象へ登録され、停止確認の失敗でも finally へ戻る。
            if (plan.Rule.Services.Count > 0) suspension = await _suspend([.. plan.Rule.Services], progress, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (suspension is { FailedToStop.Count: > 0 })
            {
                tally.Fail(CleanupMessage.Of("ServiceStopFailed", string.Join(", ", suspension.FailedToStop)));
            }
            else
            {
                await RunTargetsAsync(plan, context, tally, progress, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            tally.Fail(CleanupMessage.Of("DeleteFailed", CommandFailureDiagnostic.Sanitize(ex.Message)));
        }
        finally
        {
            await ResumeAsync(suspension, tally).ConfigureAwait(false);
        }

        return new RuleOutcome(Finish(item.Rule, tally, canceled), canceled);
    }

    /// <summary>止めたサービスを元へ戻す。取消でも必ず実行し、失敗は <see cref="RunTally.Failures"/> に残す (取消や成功で隠さない)。</summary>
    private static async Task ResumeAsync(ServiceSuspension? suspension, RunTally tally)
    {
        if (suspension is null) return;
        try
        {
            var failures = await suspension.ResumeAsync().ConfigureAwait(false);
            if (failures.Count == 0) return;
            var reason = string.Join("; ", suspension.ResumeFailureDetails.Count > 0 ? suspension.ResumeFailureDetails : failures);
            tally.Fail(CleanupMessage.Of("ServiceResumeFailedList", string.Join(", ", failures), reason));
            tally.ResumeFailed = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            tally.Fail(CleanupMessage.Of("ServiceResumeFailed", CommandFailureDiagnostic.Sanitize(ex.Message)));
            tally.ResumeFailed = true;
        }
    }

    private static CleanupRuleRunResult Finish(CleanupRuleInfo info, RunTally tally, bool canceled)
    {
        var status = (tally, canceled) switch
        {
            ({ ResumeFailed: true }, _) => CleanupRunStatus.Failed,
            ({ HandlerFailed: true }, _) => CleanupRunStatus.Failed,
            (_, true) => CleanupRunStatus.Canceled,
            ({ Failures: > 0, Deleted: > 0 }, _) => CleanupRunStatus.Partial,
            ({ Failures: > 0 }, _) => CleanupRunStatus.Failed,
            ({ Deleted: 0 }, _) => CleanupRunStatus.NothingToDo,
            _ => CleanupRunStatus.Succeeded,
        };
        return new CleanupRuleRunResult(
            info, status, CleanupSkipReason.None, tally.Files, tally.Directories, tally.Items, tally.Bytes,
            tally.Retained, tally.Blocked, tally.Wiped, tally.WipeSkipped, tally.Details);
    }

    private async Task RunTargetsAsync(RulePlan plan, CleanupScanContext context, RunTally tally, IProgress<string>? progress, CancellationToken ct)
    {
        foreach (var target in plan.Targets)
        {
            ct.ThrowIfCancellationRequested();
            // 領域は対象 1 件を処理した後にだけ登録する。処理していない対象を、後の規則が「先の規則が扱った」と飛ばさない。
            try { await RunTargetAsync(target, context, tally, progress, ct).ConfigureAwait(false); }
            finally { tally.Handled.Add(plan.Rule.Id, [target]); }
        }
    }

    private async Task RunTargetAsync(TargetPlan target, CleanupScanContext context, RunTally tally, IProgress<string>? progress, CancellationToken ct)
    {
        // レジストリ・DB・INI は同期の API。呼び出しスレッド (画面) を塞がないよう、スレッドプールで実行する。
        switch (target.Source.Type)
        {
            case CleanupTargetType.Contents or CleanupTargetType.Files:
                await Task.Run(() => RunFiles(target, context, tally, progress, ct), ct).ConfigureAwait(false);
                break;
            case CleanupTargetType.RegistryValues or CleanupTargetType.RegistryTree:
                await Task.Run(() => RunRegistry(target, tally, ct), ct).ConfigureAwait(false);
                break;
            case CleanupTargetType.Sqlite:
                await Task.Run(() => RunDatabase(target, context, tally, ct), ct).ConfigureAwait(false);
                break;
            case CleanupTargetType.IniValues:
                await Task.Run(() => RunIni(target, tally, ct), ct).ConfigureAwait(false);
                break;
            case CleanupTargetType.Handler:
                await RunHandlerAsync(target, tally, progress, ct).ConfigureAwait(false);
                break;
        }
    }

    private static void RunFiles(TargetPlan target, CleanupScanContext context, RunTally tally, IProgress<string>? progress, CancellationToken ct)
    {
        // 走査時の除外と、走査完了より後に作られたものの保持、安全な削除の設定をそのまま使う。
        var options = new CleanupRunOptions(context.Matcher, target.CreatedBefore, context.SecureDelete)
        {
            Now = tally.Handled.Now,
            AlreadyHandled = tally.Handled.RunResolver(target.Engine!),
        };
        var outcome = FileCleanupEngine.Run([target.Engine!], scheduleBlockedForReboot: false, progress, ct, options);
        tally.Files += outcome.DeletedFiles;
        tally.Directories += outcome.DeletedDirectories;
        tally.Bytes += outcome.FreedBytes;
        tally.Retained += outcome.Retained;
        tally.Wiped += outcome.WipedFiles;
        tally.WipeSkipped += outcome.WipeSkipped;
        if (outcome.Blocked > 0)
        {
            tally.Blocked += outcome.Blocked;
            tally.Failures++;
            foreach (var example in outcome.BlockedExamples) tally.Note(example);
        }

        foreach (var rejected in outcome.RejectedTargets)
        {
            if (rejected.EndsWith(LinkProtectedSuffix, StringComparison.Ordinal)) tally.Note(rejected);
            else tally.Fail(rejected);
        }
    }

    private void RunRegistry(TargetPlan target, RunTally tally, CancellationToken ct)
    {
        using var root = _scan.Parts.OpenUserRoot();
        if (root is null)
        {
            tally.Fail(CleanupMessage.Of("UserRegistryOpenFailed"));
            return;
        }

        var editor = new CleanupRegistryEditor(root);
        var source = target.Source;
        var result = source.Type == CleanupTargetType.RegistryTree
            ? editor.DeleteTree(source.Key ?? string.Empty, ct)
            : editor.DeleteValues(source.Key ?? string.Empty, source.Values ?? [], ct);
        tally.Items += result.Values + result.Keys;
        if (result.Status is CleanupRegistryStatus.Done or CleanupRegistryStatus.Missing) return;
        tally.Fail($"{source.Key}: {result.Status} ({string.Join(" / ", result.Examples)})");
    }

    private void RunDatabase(TargetPlan target, CleanupScanContext context, RunTally tally, CancellationToken ct)
    {
        var result = _scan.Parts.Databases.Clean(target.Source, target.Path!, context.CookieDomains, ct);
        if (result.Succeeded) tally.Items += result.Rows;
        else tally.Fail($"{target.Path}: {result.Reason}");
    }

    private void RunIni(TargetPlan target, RunTally tally, CancellationToken ct)
    {
        var result = _scan.Parts.Ini.Remove(target.Path!, target.Source.IniKeys ?? [], ct);
        switch (result.Status)
        {
            case CleanupIniStatus.Changed:
                tally.Items += result.RemovedKeys;
                break;
            case CleanupIniStatus.Skipped or CleanupIniStatus.Failed:
                var restored = result.Status == CleanupIniStatus.Failed && !result.FileRestored ? CleanupMessage.Of("IniContentUnverified") : string.Empty;
                tally.Fail(CleanupMessage.Of("PathReason", target.Path, result.Reason, restored));
                break;
        }
    }

    private async Task RunHandlerAsync(TargetPlan target, RunTally tally, IProgress<string>? progress, CancellationToken ct)
    {
        if (target.Source.Handler is not { } kind || !_scan.Parts.Handlers.TryGetValue(kind, out var handler))
        {
            tally.Fail(CleanupMessage.Of("HandlerUnavailable", target.Source.Handler));
            return;
        }

        var result = await handler.RunAsync(progress, ct).ConfigureAwait(false);
        tally.Items += result.Items;
        tally.Bytes += result.Bytes;
        foreach (var detail in result.Details) tally.Note(detail);
        switch (result.Status)
        {
            case CleanupHandlerStatus.Canceled:
                throw new OperationCanceledException(ct);
            case CleanupHandlerStatus.Partial or CleanupHandlerStatus.Failed:
                tally.Failures++;
                // ハンドラー自身が Failed を返したら、他の対象で消した件数にかかわらず規則を Failed にする。
                if (result.Status == CleanupHandlerStatus.Failed) tally.HandlerFailed = true;
                if (result.Details.Count == 0) tally.Note($"{handler.Id}: {result.Status}");
                break;
        }
    }
}
