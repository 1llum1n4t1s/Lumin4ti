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
public class FileCleanupAction : IMaintenanceAction, IMaintenanceCheckList
{
    private readonly Func<IEnumerable<CleanupTarget>> _targetProvider;
    private readonly ICommandExecutor? _executor;
    private readonly IReadOnlyList<string> _servicesToStop;
    private readonly bool _scheduleBlockedForReboot;
    private readonly ICleanupPreferences? _preferences;
    private readonly Func<CleanupTarget, string> _checkListKeySelector;
    private readonly Func<CleanupTarget, string> _checkListLabelSelector;

    /// <param name="id">項目 Id (ローカライズキーの基点)。</param>
    /// <param name="label">日本語マスターのラベル。</param>
    /// <param name="description">日本語マスターの説明。</param>
    /// <param name="targetProvider">削除対象を返す関数 (実行時に評価する)。</param>
    /// <param name="executor">サービス停止に使う実行器。<paramref name="servicesToStop"/> があるときは必須。</param>
    /// <param name="servicesToStop">削除前に停止し、完了後に元の稼働状態へ戻すサービス。</param>
    /// <param name="requiresReboot">反映に再起動が必要か。</param>
    /// <param name="affectsExplorer">シェルの再起動が必要か。</param>
    /// <param name="scheduleBlockedForReboot">使用中のファイルを再起動時削除として予約するか。</param>
    /// <param name="preferences">利用者が外した対象を除くための設定 (null なら全対象を消す)。</param>
    /// <param name="checkListKeySelector">
    /// チェックリストの粒度。既定は対象 1 件 = 1 チェックだが、対象が数百〜数千件になるグループ
    /// (ブラウザのプロファイル別キャッシュ等) は上位のまとまりを返して件数を畳む。
    /// </param>
    /// <param name="checkListLabelSelector">
    /// チェックリストへ表示する名前。保存キーと表示を分けたい場合に指定する
    /// (ブラウザは未展開のルートを保存し、製品名を表示する)。
    /// </param>
    public FileCleanupAction(
        string id,
        string label,
        string description,
        Func<IEnumerable<CleanupTarget>> targetProvider,
        ICommandExecutor? executor = null,
        IReadOnlyList<string>? servicesToStop = null,
        bool requiresReboot = false,
        bool affectsExplorer = false,
        bool scheduleBlockedForReboot = false,
        ICleanupPreferences? preferences = null,
        Func<CleanupTarget, string>? checkListKeySelector = null,
        Func<CleanupTarget, string>? checkListLabelSelector = null)
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
        _preferences = preferences;
        _checkListKeySelector = checkListKeySelector ?? DescribeTarget;
        _checkListLabelSelector = checkListLabelSelector ?? _checkListKeySelector;
    }

    /// <summary>
    /// 対象 1 件を一意に表す文字列。設定への保存キーと画面表示を兼ねるため、
    /// パターン指定は「フォルダ + パターン」まで含めて別対象として区別する
    /// (同じフォルダに対する複数パターンを 1 つのチェックに潰さない)。
    /// </summary>
    public static string DescribeTarget(CleanupTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return target.Kind switch
        {
            CleanupTargetKind.Files => $@"{target.RawPath.TrimEnd('\\')}\{target.Pattern}",
            _ => target.RawPath,
        };
    }

    /// <summary>設定で外されたものを含む、この項目が扱う全対象。</summary>
    public IReadOnlyList<CleanupTarget> EnumerateAllTargets() => [.. _targetProvider()];

    /// <summary>実際に削除する対象 (利用者が外したものを除く)。</summary>
    public IReadOnlyList<CleanupTarget> EnumerateSelectedTargets()
    {
        var targets = _targetProvider().ToList();
        var selectedGroups = targets
            .GroupBy(_checkListKeySelector, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.All(IsTargetSelected))
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 画面は group.All(IsTargetSelected) で 1 行へ畳んでいるため、旧パス単位の除外が
        // 1 件でも残るグループは実行時も全体を外す。表示が OFF なのに一部だけ消す状態を作らない。
        return [.. targets.Where(target => selectedGroups.Contains(_checkListKeySelector(target)))];
    }

    /// <summary>この対象がチェックリストのどの行に属するか。</summary>
    public string GetCheckListKey(CleanupTarget target) => _checkListKeySelector(target);

    private bool IsTargetSelected(CleanupTarget target) =>
        _preferences is null ||
        (_preferences.IsTargetEnabled(Id, _checkListKeySelector(target)) &&
         _preferences.IsTargetEnabled(Id, DescribeTarget(target)));

    public string CheckListCaption => "削除する対象を選ぶ";

    /// <summary>見出しは全グループ共通なので、項目ごとに翻訳キーを増やさず 1 つを共有する。</summary>
    public string CheckListCaptionKey => "CheckList.Targets";

    public IReadOnlyList<MaintenanceCheckListEntry> GetCheckListEntries() =>
    [
        .. EnumerateAllTargets()
            .GroupBy(_checkListKeySelector, StringComparer.OrdinalIgnoreCase)
            .Select(group => new MaintenanceCheckListEntry(
                group.Key,
                _checkListLabelSelector(group.First()),
                group.All(IsTargetSelected))),
    ];

    public async Task SetCheckListEntrySelectedAsync(string value, bool selected, CancellationToken ct = default)
    {
        if (_preferences is null)
        {
            return;
        }

        _preferences.SetTargetEnabled(Id, value, selected);

        // 以前は対象パス単位で保存していた項目も、現在はブラウザ／アプリ／用途単位に畳める。
        // グループを明示的に操作した時点で、その配下に残る旧形式の除外値を消し、
        // 画面のチェック状態と実際の削除対象が一致するようにする。
        foreach (var target in EnumerateAllTargets().Where(t =>
                     string.Equals(_checkListKeySelector(t), value, StringComparison.OrdinalIgnoreCase)))
        {
            var legacyKey = DescribeTarget(target);
            if (!string.Equals(legacyKey, value, StringComparison.OrdinalIgnoreCase))
            {
                _preferences.SetTargetEnabled(Id, legacyKey, enabled: true);
            }
        }

        await _preferences.SaveAsync(ct);
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

        var targets = EnumerateSelectedTargets();
        if (targets.Count == 0)
        {
            // 全対象のチェックが外れている状態。サービスを止める意味も無いのでここで返す。
            LoggerBootstrap.Log.Info($"{Id}: 対象が 1 件も選ばれていないため何もしませんでした");
            return MaintenanceActionResult.Ok("  - 対象が選ばれていないため、何も削除しませんでした");
        }

        try
        {
            if (_servicesToStop.Count > 0)
            {
                suspension = await WindowsServiceControl.SuspendAsync(_executor!, _servicesToStop, progress, ct);
            }

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
