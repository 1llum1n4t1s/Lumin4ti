using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Core.Services.Windows.Cleanup.Handlers;

/// <summary><c>System32\sru</c> (ネットワーク使用量の記録) の走査・削除の境界。</summary>
internal interface INetworkUsageStore
{
    /// <summary>削除せずに数える。</summary>
    CleanupScanResult Scan(CancellationToken ct);

    /// <summary>中身を削除する (フォルダー自体は残す)。</summary>
    CleanupOutcome Delete(IProgress<string>? progress, CancellationToken ct);
}

/// <summary>
/// ネットワーク使用量 (SRUM) の記録を消す。DPS (診断ポリシーサービス) がデータベースを握っているため、
/// <see cref="WindowsServiceControl"/> で停止を確認してから <c>System32\sru</c> の中身を削除し、
/// 成功・失敗・取消のどれでも <c>finally</c> で DPS を復帰する。復帰に失敗したら成功としない。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NetworkUsageHandler : ICleanupHandler
{
    private const string ServiceName = "DPS";

    private readonly INetworkUsageStore _store;
    private readonly Func<IReadOnlyList<string>, IProgress<string>?, CancellationToken, Task<ServiceSuspension>> _suspend;

    public NetworkUsageHandler(ICommandExecutor executor)
        : this(
            new SruStore(),
            (names, progress, ct) => WindowsServiceControl.SuspendAsync(executor, names, progress, ct))
    {
    }

    internal NetworkUsageHandler(
        INetworkUsageStore store,
        Func<IReadOnlyList<string>, IProgress<string>?, CancellationToken, Task<ServiceSuspension>> suspend)
    {
        _store = store;
        _suspend = suspend;
    }

    public string Id => "networkUsage";

    public IReadOnlyList<string> Services => [ServiceName];

    public Task<CleanupHandlerScan> ScanAsync(CancellationToken ct)
    {
        var scan = _store.Scan(ct);
        if (scan.Rejection is not null)
        {
            return Task.FromResult(new CleanupHandlerScan(CleanupHandlerScanState.Unavailable, Detail: scan.Rejection));
        }

        if (scan.Missing || scan.Files == 0)
        {
            return Task.FromResult(new CleanupHandlerScan(CleanupHandlerScanState.Empty, 0, 0));
        }

        return Task.FromResult(new CleanupHandlerScan(CleanupHandlerScanState.Found, scan.Files, scan.Bytes));
    }

    public async Task<CleanupHandlerResult> RunAsync(IProgress<string>? progress, CancellationToken ct)
    {
        ServiceSuspension? suspension = null;
        CleanupOutcome? outcome = null;
        ExceptionDispatchInfo? failure = null;
        IReadOnlyList<string> resumeFailures = [];
        try
        {
            // 停止の要求前に復帰対象へ登録され、停止確認の失敗やタイムアウトでも finally へ戻る。
            suspension = await _suspend([ServiceName], progress, ct);
            ct.ThrowIfCancellationRequested();
            if (suspension.FailedToStop.Count == 0)
            {
                outcome = _store.Delete(progress, ct);
            }
        }
        catch (Exception ex)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            if (suspension is not null)
            {
                resumeFailures = await suspension.ResumeAsync();
            }
        }

        return Classify(suspension, outcome, failure?.SourceException, resumeFailures);
    }

    /// <summary>復帰失敗を最優先にし、停止未確認・削除失敗・取消を成功と区別する。</summary>
    internal static CleanupHandlerResult Classify(
        ServiceSuspension? suspension,
        CleanupOutcome? outcome,
        Exception? failure,
        IReadOnlyList<string> resumeFailures)
    {
        var details = new List<string>();
        if (resumeFailures.Count > 0)
        {
            var reason = string.Join("; ", suspension?.ResumeFailureDetails ?? resumeFailures);
            details.Add(CleanupMessage.Of("NetworkDpsResumeFailed", reason));
        }

        if (resumeFailures.Count > 0)
        {
            return new CleanupHandlerResult(
                CleanupHandlerStatus.Failed,
                (outcome?.DeletedFiles ?? 0) + (outcome?.DeletedDirectories ?? 0),
                outcome?.FreedBytes ?? 0)
            { Details = details };
        }

        if (failure is OperationCanceledException)
        {
            return new CleanupHandlerResult(CleanupHandlerStatus.Canceled);
        }

        if (failure is not null)
        {
            details.Add(CleanupMessage.Of("NetworkDeleteFailed", CommandFailureDiagnostic.Sanitize(failure.Message)));
        }
        else if (suspension is { FailedToStop.Count: > 0 })
        {
            details.Add(CleanupMessage.Of("NetworkDpsStopUnverified"));
        }

        if (outcome is null)
        {
            return new CleanupHandlerResult(CleanupHandlerStatus.Failed) { Details = details };
        }

        return ClassifyOutcome(outcome, details);
    }

    private static CleanupHandlerResult ClassifyOutcome(CleanupOutcome outcome, List<string> details)
    {
        var items = outcome.DeletedFiles + outcome.DeletedDirectories;
        details.AddRange(outcome.RejectedTargets);
        details.AddRange(outcome.BlockedExamples);
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

    [SupportedOSPlatform("windows")]
    private sealed class SruStore : INetworkUsageStore
    {
        private static readonly CleanupTarget Target = CleanupTarget.Contents(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sru")) with { IsActualPath = true };

        public CleanupScanResult Scan(CancellationToken ct) => FileCleanupScanner.Scan(Target, null, ct);

        public CleanupOutcome Delete(IProgress<string>? progress, CancellationToken ct) =>
            FileCleanupEngine.Run([Target], scheduleBlockedForReboot: false, progress, ct);
    }
}
