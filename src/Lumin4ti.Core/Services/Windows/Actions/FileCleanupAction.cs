using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// 一時ファイル・キャッシュをグループ単位でまとめて削除する実行型項目。
/// 対象一覧 (<see cref="FileCleanupGroups"/>) を差し替えるだけでボタンが増やせるよう、
/// 削除ロジックは <see cref="FileCleanupEngine"/> に集約している。
/// </summary>
[SupportedOSPlatform("windows")]
public class FileCleanupAction : IMaintenanceAction
{
    private readonly Func<IEnumerable<CleanupTarget>> _targetProvider;
    private readonly ICommandExecutor? _executor;
    private readonly IReadOnlyList<string> _servicesToStop;
    private readonly bool _scheduleBlockedForReboot;

    /// <param name="id">項目 Id (ローカライズキーの基点)。</param>
    /// <param name="label">日本語マスターのラベル。</param>
    /// <param name="description">日本語マスターの説明。</param>
    /// <param name="targetProvider">削除対象を返す関数 (実行時に評価する)。</param>
    /// <param name="executor">サービス停止に使う実行器。<paramref name="servicesToStop"/> があるときは必須。</param>
    /// <param name="servicesToStop">削除前に停止し、完了後に元の稼働状態へ戻すサービス。</param>
    /// <param name="requiresReboot">反映に再起動が必要か。</param>
    /// <param name="affectsExplorer">シェルの再起動が必要か。</param>
    /// <param name="scheduleBlockedForReboot">使用中のファイルを再起動時削除として予約するか。</param>
    public FileCleanupAction(
        string id,
        string label,
        string description,
        Func<IEnumerable<CleanupTarget>> targetProvider,
        ICommandExecutor? executor = null,
        IReadOnlyList<string>? servicesToStop = null,
        bool requiresReboot = false,
        bool affectsExplorer = false,
        bool scheduleBlockedForReboot = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(targetProvider);

        _servicesToStop = servicesToStop ?? [];
        if (_servicesToStop.Count > 0)
        {
            ArgumentNullException.ThrowIfNull(executor);
        }

        Id = id;
        Label = label;
        Description = description;
        _targetProvider = targetProvider;
        _executor = executor;
        RequiresReboot = requiresReboot;
        AffectsExplorer = affectsExplorer;
        _scheduleBlockedForReboot = scheduleBlockedForReboot;
    }

    public string Id { get; }

    public string Label { get; }

    public string Description { get; }

    public CommandCategory Category => CommandCategory.Cleanup;

    public bool RequiresReboot { get; }

    public bool AffectsExplorer { get; }

    /// <summary>数万ファイル規模の走査になり得るため、常に長時間扱いにする。</summary>
    public bool IsLongRunning => true;

    public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default) => ExecuteAsync(null, ct);

    public async Task<MaintenanceActionResult> ExecuteAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        ServiceSuspension? suspension = null;
        IReadOnlyList<string> resumeFailures = [];
        CleanupOutcome? outcome = null;

        try
        {
            if (_servicesToStop.Count > 0)
            {
                suspension = await WindowsServiceControl.SuspendAsync(_executor!, _servicesToStop, progress, ct);
            }

            var targets = _targetProvider().ToList();
            outcome = await Task.Run(
                () => FileCleanupEngine.Run(targets, _scheduleBlockedForReboot, progress, ct),
                ct);
        }
        finally
        {
            if (suspension is not null)
            {
                // 削除の失敗・キャンセルにかかわらず、止めたサービスは必ず元へ戻す。
                resumeFailures = await suspension.ResumeAsync();
            }
        }

        var lines = FileCleanupEngine.DescribeOutcome(outcome!).ToList();
        var degraded = false;

        if (suspension is { FailedToStop.Count: > 0 })
        {
            degraded = true;
            lines.Add($"  - 次のサービスを停止できず、使用中のファイルが残った可能性があります: {string.Join(", ", suspension.FailedToStop)}");
        }

        if (outcome!.RejectedTargets.Count > 0)
        {
            degraded = true;
        }

        LoggerBootstrap.Log.Info(
            $"{Id}: files={outcome.DeletedFiles} dirs={outcome.DeletedDirectories} " +
            $"bytes={outcome.FreedBytes} blocked={outcome.Blocked} missing={outcome.MissingTargets}");

        if (resumeFailures.Count > 0)
        {
            lines.Add($"  - 停止したサービスを再開できませんでした: {string.Join(", ", resumeFailures)}");
            lines.Add("  - PC を再起動すると自動的に開始されます");
            return MaintenanceActionResult.Fail(string.Join(Environment.NewLine, lines));
        }

        return degraded
            ? MaintenanceActionResult.Partial(lines)
            : MaintenanceActionResult.Ok(lines);
    }
}
