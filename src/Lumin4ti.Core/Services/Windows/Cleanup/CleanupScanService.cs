using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Services.Windows.Actions;
using Lumin4ti.Core.Services.Windows.Cleanup.Handlers;
using EngineTarget = Lumin4ti.Core.Services.Windows.Actions.CleanupTarget;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>
/// 規則の列挙元を 1 か所にまとめる。埋め込み定義 (<c>Definitions/*.json</c>) の製品定義を
/// <see cref="CleanupEngineExpander"/> で規則へ展開し (プロファイルは実行時に展開するため静的な形)、
/// 設定の 0→1 移行・走査・実行が同じ一覧を見る。
/// </summary>
internal static class CleanupRuleSource
{
    private static readonly Lazy<IReadOnlyList<CleanupRule>> Cached =
        new(static () => Compose(CleanupRuleCatalog.Load().Rules));

    /// <summary>使える全規則 (定義 + 生成)。違反した規則は含めない。</summary>
    public static IReadOnlyList<CleanupRule> All() => Cached.Value;

    /// <summary>定義から製品定義を取り除き、生成した規則を加えて検証する。重複する id・対象は検証器が無効にする。</summary>
    internal static IReadOnlyList<CleanupRule> Compose(IEnumerable<CleanupRule> catalog)
    {
        var validated = CleanupEngineExpander.ExpandAll(catalog, source: null);
        foreach (var violation in validated.Violations)
        {
            LoggerBootstrap.Log.Warn($"クリーンアップ規則 {violation.RuleId} を無効にしました ({violation.Code}): {violation.Message}");
        }

        return validated.Valid;
    }
}

/// <summary>SQLite の固定操作の境界。テストでは差し替え、実 DB を変更しない。</summary>
internal interface ICleanupDatabaseOps
{
    DatabaseCleanResult Count(CleanupRuleTarget target, string path, IReadOnlyCollection<string> excludedDomains, CancellationToken ct);

    DatabaseCleanResult Clean(CleanupRuleTarget target, string path, IReadOnlyCollection<string> excludedDomains, CancellationToken ct);
}

[SupportedOSPlatform("windows")]
internal sealed class SystemCleanupDatabaseOps : ICleanupDatabaseOps
{
    public DatabaseCleanResult Count(CleanupRuleTarget target, string path, IReadOnlyCollection<string> excludedDomains, CancellationToken ct) =>
        target.Operation switch
        {
            CleanupSqliteOperation.ClearTable => AppDatabaseCleaner.Count(path, target.Table ?? string.Empty, target.Columns ?? [], ct),
            { } operation => BrowserDatabaseCleaner.Count(operation, path, excludedDomains, ct),
            _ => DatabaseCleanResult.Partial("操作が指定されていません"),
        };

    public DatabaseCleanResult Clean(CleanupRuleTarget target, string path, IReadOnlyCollection<string> excludedDomains, CancellationToken ct) =>
        target.Operation switch
        {
            CleanupSqliteOperation.ClearTable => AppDatabaseCleaner.Clean(path, target.Table ?? string.Empty, target.Columns ?? [], ct),
            { } operation => BrowserDatabaseCleaner.Clean(operation, path, excludedDomains, ct),
            _ => DatabaseCleanResult.Partial("操作が指定されていません"),
        };
}

/// <summary>走査と実行が共有する、OS・設定への境界。テストでは実機を変えない実装に差し替える。</summary>
internal sealed record CleanupParts(
    CleanupSettingsStore Settings,
    Func<IReadOnlyList<CleanupRule>> Rules,
    CleanupTargetResolver Resolver,
    Func<InteractiveScopeResult> Scope,
    IReadOnlyDictionary<CleanupHandlerKind, ICleanupHandler> Handlers,
    Func<ICleanupRegistryKey?> OpenUserRoot,
    CleanupIniEditor Ini,
    ICleanupDatabaseOps Databases,
    Func<IReadOnlySet<string>> RunningProcessNames,
    Func<CustomFolderGuardContext> GuardContext)
{
    [SupportedOSPlatform("windows")]
    public static CleanupParts Create(CleanupSettingsStore settings, ICommandExecutor executor) => new(
        settings,
        CleanupRuleSource.All,
        new CleanupTargetResolver(),
        InteractiveScope.EvaluateCurrent,
        new Dictionary<CleanupHandlerKind, ICleanupHandler>
        {
            [CleanupHandlerKind.RecycleBin] = new RecycleBinHandler(),
            [CleanupHandlerKind.DnsCache] = new DnsCacheHandler(executor),
            [CleanupHandlerKind.IeHistory] = new IeHistoryHandler(),
            [CleanupHandlerKind.NetworkUsage] = new NetworkUsageHandler(executor),
            [CleanupHandlerKind.InvalidShortcut] = new InvalidShortcutHandler(),
            [CleanupHandlerKind.InvalidScheduledTask] = new InvalidScheduledTaskHandler(),
        },
        OpenCurrentUserHive,
        new CleanupIniEditor(),
        new SystemCleanupDatabaseOps(),
        CurrentProcessNames,
        CustomFolderGuardContext.FromEnvironment);

    [SupportedOSPlatform("windows")]
    private static ICleanupRegistryKey? OpenCurrentUserHive()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User is { } sid ? NativeRegistryKey.OpenUserHive(sid.Value) : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static IReadOnlySet<string> CurrentProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { names.Add(process.ProcessName); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* 列挙中に終了した。 */ }
            }
        }

        return names;
    }
}

/// <summary>走査時に決めた対象 1 件。実行はこの対象だけを、走査時の条件で処理する。</summary>
/// <param name="CreatedBefore">ファイル系だけ。これより後に作られたものは残す。</param>
internal sealed record TargetPlan(CleanupRuleTarget Source, string? Path, EngineTarget? Engine, DateTime? CreatedBefore);

internal sealed record RulePlan(CleanupRule Rule, IReadOnlyList<TargetPlan> Targets);

/// <summary>走査時の設定の写し。実行は設定の現在値ではなく、これを使う。</summary>
internal sealed record CleanupScanContext(
    CleanupExclusionMatcher Matcher,
    IReadOnlyList<string> CookieDomains,
    bool SecureDelete,
    string Fingerprint);

/// <summary>通常クリーンの走査結果。世代番号を持ち、設定変更・削除開始・再走査で失効する。</summary>
public sealed class CleanupScanSnapshot
{
    internal CleanupScanSnapshot(
        long generation,
        DateTime completedAtUtc,
        CleanupScopeStatus userScope,
        IReadOnlyList<CleanupScanItem> items,
        IReadOnlyDictionary<string, RulePlan> plans,
        CleanupScanContext context)
    {
        Generation = generation;
        CompletedAtUtc = completedAtUtc;
        UserScope = userScope;
        Items = items;
        Plans = plans;
        Context = context;
    }

    /// <summary>この走査の世代番号。</summary>
    public long Generation { get; }

    /// <summary>走査を終えた時刻 (UTC)。</summary>
    public DateTime CompletedAtUtc { get; }

    /// <summary>利用者スコープの規則を使えるか。使えない場合、<see cref="CleanupItemState.Unavailable"/> の理由に出る。</summary>
    public CleanupScopeStatus UserScope { get; }

    /// <summary>規則ごとの結果 (規則の並び順、カスタムフォルダーは末尾)。</summary>
    public IReadOnlyList<CleanupScanItem> Items { get; }

    public long TotalFiles => Measure(FoundIds()).Files;

    public long TotalItems => Measure(FoundIds()).Items;

    public long TotalBytes => Measure(FoundIds()).Bytes;

    private IEnumerable<string> FoundIds() => Items.Where(i => i.State == CleanupItemState.Found).Select(i => i.Rule.Id);

    /// <summary>
    /// 選んだ規則の合計。規則別の件数・サイズは足すだけだが、包含関係の対象 (例: %TEMP% 全体と %TEMP%\NeroSupport) では
    /// 同じファイルが両方に入るため、先の規則 (走査順) も選ばれているものは後の規則の分から引き、1 回だけ数える。
    /// 選べない規則 (走査できなかったものなど) は無視する。
    /// </summary>
    public CleanupSelectionTotals Measure(IEnumerable<string> ruleIds)
    {
        var wanted = new HashSet<string>(ruleIds, StringComparer.OrdinalIgnoreCase);
        var chosen = Items.Where(i => i.CanSelect && wanted.Contains(i.Rule.Id)).ToList();
        var ids = new HashSet<string>(chosen.Select(i => i.Rule.Id), StringComparer.OrdinalIgnoreCase);
        long files = 0, directories = 0, items = 0, bytes = 0;
        foreach (var item in chosen)
        {
            files += item.Files;
            directories += item.Directories;
            items += item.Items;
            bytes += item.Bytes;
            foreach (var overlap in item.Overlaps ?? [])
            {
                if (!overlap.ClaimedBy.Any(ids.Contains)) continue;
                files -= overlap.Files;
                directories -= overlap.Directories;
                bytes -= overlap.Bytes;
            }
        }

        return new CleanupSelectionTotals(files, directories, items, bytes);
    }

    internal IReadOnlyDictionary<string, RulePlan> Plans { get; }

    internal CleanupScanContext Context { get; }
}

/// <summary>
/// 通常クリーンの走査。何も変更せず、規則ごとの件数・サイズ・状態を返す。取消・進捗に対応し、世代番号で結果の鮮度を管理する。
/// 走査は <c>showProcessing:false</c> の lease の中で呼ぶ (画面内に進捗と中断を出す)。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CleanupScanService
{
    private const int RepresentativeLimit = 3;
    private const int MaxDetails = 8;

    private readonly CleanupParts _parts;
    private long _generation;

    public CleanupScanService(CleanupSettingsStore settings, ICommandExecutor executor)
        : this(CleanupParts.Create(settings, executor))
    {
    }

    internal CleanupScanService(CleanupParts parts) => _parts = parts;

    internal CleanupParts Parts => _parts;

    /// <summary>現在の世代番号。</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>現在の走査結果を失効させる。設定変更・削除開始・再走査の前に呼ぶ。</summary>
    public void Invalidate() => Interlocked.Increment(ref _generation);

    /// <summary>走査結果が現在のものか。世代と、走査に影響する設定 (除外・カスタムフォルダー・安全な削除) の両方を見る。</summary>
    public bool IsCurrent(CleanupScanSnapshot snapshot) =>
        snapshot.Generation == Generation && snapshot.Context.Fingerprint == CreateContext().Fingerprint;

    /// <summary>走査結果を 1 回だけ実行に使うため、現在のものなら世代を進めて失効させる (別の実行・再利用を拒否する)。</summary>
    internal bool TryBeginRun(CleanupScanSnapshot snapshot) =>
        snapshot.Context.Fingerprint == CreateContext().Fingerprint &&
        Interlocked.CompareExchange(ref _generation, snapshot.Generation + 1, snapshot.Generation) == snapshot.Generation;

    /// <summary>
    /// 全規則を走査する (<paramref name="ruleIds"/> を渡せばその規則だけ)。何も変更しない。
    /// 取消は <see cref="OperationCanceledException"/> で伝える。
    /// </summary>
    public async Task<CleanupScanSnapshot> ScanAsync(
        IReadOnlyCollection<string>? ruleIds,
        IProgress<CleanupScanProgress>? progress,
        CancellationToken ct)
    {
        var generation = Interlocked.Increment(ref _generation);
        var context = CreateContext();
        var scope = _parts.Scope();
        var inputs = BuildInputs(ruleIds);
        var running = _parts.RunningProcessNames();

        var items = new List<CleanupScanItem>(inputs.Count);
        var plans = new Dictionary<string, RulePlan>(StringComparer.OrdinalIgnoreCase);
        var claims = new ClaimSet();
        for (var index = 0; index < inputs.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var input = inputs[index];
            progress?.Report(new CleanupScanProgress(index, inputs.Count, input.Rule.Id, input.Rule.Name));
            var (item, plan) = await ScanRuleAsync(input, scope, context, running, claims, ct).ConfigureAwait(false);
            items.Add(item);
            if (plan is null) continue;
            plans[input.Rule.Id] = plan;
            claims.Add(input.Rule.Id, plan.Targets);
        }

        progress?.Report(new CleanupScanProgress(inputs.Count, inputs.Count, null, null));
        return new CleanupScanSnapshot(generation, DateTime.UtcNow, ToStatus(scope), items, plans, context);
    }

    // ----- 入力の組み立て -----

    /// <summary>走査する規則 1 件。カスタムフォルダーは <see cref="Custom"/> に実行時の検査結果を持つ。</summary>
    private sealed record ScanInput(CleanupRule Rule, CustomFolderCheck? Custom);

    private List<ScanInput> BuildInputs(IReadOnlyCollection<string>? ruleIds)
    {
        var wanted = ruleIds is null ? null : new HashSet<string>(ruleIds, StringComparer.OrdinalIgnoreCase);
        var inputs = _parts.Rules()
            .Where(rule => wanted is null || wanted.Contains(rule.Id))
            .Select(rule => new ScanInput(rule, null))
            .ToList();
        inputs.AddRange(CustomFolderInputs().Where(input => wanted is null || wanted.Contains(input.Rule.Id)));
        return inputs;
    }

    private List<ScanInput> CustomFolderInputs()
    {
        var guard = _parts.GuardContext();
        var result = new List<ScanInput>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalid = 0;
        foreach (var check in _parts.Settings.GetCustomFolders(guard))
        {
            var key = check.FullPath ?? $"invalid:{invalid++}";
            if (seen.Add(key)) result.Add(new ScanInput(CustomRule(key, check, guard), check));
        }

        return result;
    }

    /// <summary>カスタムフォルダーを表す規則。受信ファイル相当 (<c>UserFiles</c>) として扱い、既定未選択・再確認必須にする。</summary>
    internal static CleanupRule CustomRule(string key, CustomFolderCheck check, CustomFolderGuardContext guard)
    {
        var id = "custom." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToLowerInvariant())))[..12].ToLowerInvariant();
        var underProfile = check.FullPath is { } full && guard.CurrentProfile is { Length: > 0 } profile &&
                           full.StartsWith(Path.TrimEndingDirectorySeparator(profile) + "\\", StringComparison.OrdinalIgnoreCase);
        return new CleanupRule
        {
            Id = id,
            Name = check.FullPath ?? key,
            Kind = CleanupKind.Other,
            Category = CleanupCategory.Custom,
            Scope = underProfile ? CleanupScope.User : CleanupScope.Machine,
            Recommended = false,
            Impact = CleanupImpact.UserFiles,
            Targets = check.FullPath is { } path
                ? [new CleanupRuleTarget { Type = CleanupTargetType.Contents, Path = path }]
                : [],
        };
    }

    private CleanupScanContext CreateContext()
    {
        var settings = _parts.Settings;
        var domains = settings.GetExclusions(CleanupExclusionKind.CookieDomain);
        var secure = settings.SecureDelete;
        var text = new StringBuilder();
        foreach (var kind in Enum.GetValues<CleanupExclusionKind>())
        {
            text.Append((int)kind).Append(':').AppendJoin('\u0001', settings.GetExclusions(kind)).Append('\u0002');
        }

        // 実行時の検査結果は含めない (環境の変化は実行直前の再検査が拒否する)。保存されている一覧の変更だけを見る。
        text.Append("custom:").AppendJoin('\u0001', _parts.Settings.GetCustomFolders(_parts.GuardContext()).Select(c => c.FullPath ?? string.Empty));
        text.Append("|secure:").Append(secure);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        return new CleanupScanContext(settings.CreateExclusionMatcher(), domains, secure, fingerprint);
    }

    private static CleanupScopeStatus ToStatus(InteractiveScopeResult scope) => scope.Reason switch
    {
        InteractiveScopeReason.DifferentUser => CleanupScopeStatus.DifferentUser,
        InteractiveScopeReason.UserUnknown => CleanupScopeStatus.Unknown,
        _ => scope.UserScopeAvailable ? CleanupScopeStatus.Available : CleanupScopeStatus.Unknown,
    };

    // ----- 1 規則の走査 -----

    /// <summary>利用者スコープの規則か。HKCU・プロファイル・Shell を触るハンドラーは、データの scope によらず利用者スコープとして扱う。</summary>
    internal static bool IsUserScoped(CleanupRule rule) =>
        rule.Scope == CleanupScope.User ||
        rule.Targets.Any(t => t.Handler is CleanupHandlerKind.RecycleBin or CleanupHandlerKind.IeHistory or CleanupHandlerKind.InvalidShortcut);

    private async Task<(CleanupScanItem Item, RulePlan? Plan)> ScanRuleAsync(
        ScanInput input,
        InteractiveScopeResult scope,
        CleanupScanContext context,
        IReadOnlySet<string> running,
        ClaimSet claims,
        CancellationToken ct)
    {
        var rule = input.Rule;
        var processes = rule.Processes.Where(running.Contains).ToList();
        var info = CleanupRuleInfo.From(rule, ServicesOf(rule));
        if (IsUserScoped(rule) && !scope.UserScopeAvailable)
        {
            var reason = scope.Reason == InteractiveScopeReason.DifferentUser ? CleanupUnavailableReason.UserScopeDifferentUser : CleanupUnavailableReason.UserScopeUnknown;
            return (Unavailable(info, reason, processes, []), null);
        }

        if (input.Custom is { IsAllowed: false } rejected)
        {
            return (Unavailable(info, CleanupUnavailableReason.CustomFolderRejected, processes, [CleanupMessage.Of("CustomFolderRejected", CleanupMessage.Of($"Cleanup.CustomFolder.Reject.{rejected.Rejection}"))]), null);
        }

        try
        {
            var tally = new ScanTally { Claims = claims };
            var detected = await Task.Run(() => ScanLocal(input, context, tally, ct), ct).ConfigureAwait(false);
            if (!detected) return (Build(info, CleanupItemState.NotDetected, CleanupUnavailableReason.None, tally, processes, false), null);
            await ScanHandlersAsync(rule, tally, ct).ConfigureAwait(false);
            return Finish(info, rule, tally, processes);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            LoggerBootstrap.Log.Warn($"クリーンアップ走査に失敗しました ({rule.Id}): {ex.GetType().Name}");
            return (Unavailable(info, CleanupUnavailableReason.ScanFailed, processes, [CommandFailureDiagnostic.Sanitize(ex.Message)]), null);
        }
    }

    private static IReadOnlyList<string> ServicesOf(CleanupRule rule) => [.. rule.Services];

    private CleanupScanItem Unavailable(CleanupRuleInfo info, CleanupUnavailableReason reason, IReadOnlyList<string> processes, IReadOnlyList<string> detail) =>
        new(info, CleanupItemState.Unavailable, reason, 0, 0, 0, 0, 0, 0, 0, false, [], processes, detail);

    private (CleanupScanItem Item, RulePlan? Plan) Finish(CleanupRuleInfo info, CleanupRule rule, ScanTally tally, IReadOnlyList<string> processes)
    {
        var measured = tally.Files + tally.Directories + tally.Items > 0;
        var (state, reason) = (measured, tally) switch
        {
            (true, _) or (_, { HandlerFound: true }) => (CleanupItemState.Found, CleanupUnavailableReason.None),
            (_, { HandlerUnmeasurable: true }) => (CleanupItemState.Unmeasurable, CleanupUnavailableReason.None),
            (_, { Scanned: 0, Rejected: > 0 }) => (CleanupItemState.Unavailable, tally.FailureReason),
            _ => (CleanupItemState.Empty, CleanupUnavailableReason.None),
        };
        var selectable = state is CleanupItemState.Found or CleanupItemState.Unmeasurable;
        var defaultSelected = selectable && info is { IsCustom: false, Impact: not CleanupRuleImpact.UserFiles } &&
                              (_parts.Settings.GetRuleOverride(rule.Id) ?? rule.Recommended);
        var item = Build(info, state, reason, tally, processes, defaultSelected);
        return (item, tally.Plans.Count == 0 ? null : new RulePlan(rule, tally.Plans));
    }

    private static CleanupScanItem Build(
        CleanupRuleInfo info, CleanupItemState state, CleanupUnavailableReason reason, ScanTally tally, IReadOnlyList<string> processes, bool defaultSelected) =>
        new(
            info with { Services = [.. info.Services.Concat(tally.HandlerServices).Distinct(StringComparer.OrdinalIgnoreCase)] },
            state,
            reason,
            tally.Files,
            tally.Directories,
            tally.Items,
            tally.Bytes,
            tally.Retained,
            tally.Unreadable,
            tally.SkippedLinks,
            defaultSelected,
            tally.Representatives,
            processes,
            tally.Details,
            tally.OverlapList());

    /// <summary>1 規則の走査で積み上げる値。</summary>
    private sealed class ScanTally
    {
        private readonly List<string> _details = [];
        private readonly List<string> _representatives = [];
        private readonly Dictionary<string, OverlapCount> _overlaps = new(StringComparer.Ordinal);

        /// <summary>先に走査した規則の領域。包含関係の重なりを見分けるのに使う。</summary>
        public ClaimSet Claims { get; init; } = new();

        public void AddOverlaps(IReadOnlyDictionary<string, OverlapCount> found)
        {
            foreach (var (key, count) in found)
            {
                if (!_overlaps.TryGetValue(key, out var total)) _overlaps[key] = total = new OverlapCount();
                total.Files += count.Files;
                total.Directories += count.Directories;
                total.Bytes += count.Bytes;
            }
        }

        public IReadOnlyList<CleanupScanOverlap>? OverlapList() => _overlaps.Count == 0
            ? null
            : [.. _overlaps.Select(o => new CleanupScanOverlap(o.Key.Split(ClaimSet.KeySeparator), o.Value.Files, o.Value.Directories, o.Value.Bytes))];

        public long Files { get; set; }

        public long Directories { get; set; }

        public long Items { get; set; }

        public long Bytes { get; set; }

        public long Retained { get; set; }

        public long Unreadable { get; set; }

        public long SkippedLinks { get; set; }

        /// <summary>拒否・失敗せずに調べられた対象の数。</summary>
        public int Scanned { get; set; }

        public int Rejected { get; set; }

        public CleanupUnavailableReason FailureReason { get; set; } = CleanupUnavailableReason.TargetRejected;

        public bool HandlerFound { get; set; }

        public bool HandlerUnmeasurable { get; set; }

        public List<TargetPlan> Plans { get; } = [];

        public List<string> HandlerServices { get; } = [];

        public IReadOnlyList<string> Details => _details;

        public IReadOnlyList<string> Representatives => _representatives;

        public void Reject(string message, CleanupUnavailableReason reason = CleanupUnavailableReason.TargetRejected)
        {
            Rejected++;
            FailureReason = reason;
            Note(message);
        }

        public void Note(string message)
        {
            if (_details.Count < MaxDetails) _details.Add(message.Length <= 512 ? message : message[..512] + "…");
        }

        public void AddRepresentatives(IEnumerable<string> paths)
        {
            foreach (var path in paths.Where(_ => _representatives.Count < RepresentativeLimit)) _representatives.Add(path);
        }
    }

    /// <summary>ハンドラー以外の対象を調べる。この PC に無ければ false。</summary>
    private bool ScanLocal(ScanInput input, CleanupScanContext context, ScanTally tally, CancellationToken ct)
    {
        var rule = input.Rule;
        if (input.Custom is null && !_parts.Resolver.IsDetected(rule)) return false;
        if (input.Custom is { FullPath: { } customPath })
        {
            var source = rule.Targets[0];
            // カスタムフォルダーは、認証情報の名前の子孫を数えず・消さずに残す (走査と削除が同じ判定を使う)。
            ScanFiles(new ResolvedTarget(source, customPath, null), context, tally, ct, keepCredentials: true);
            return true;
        }

        var resolved = _parts.Resolver.Resolve(rule);
        if (resolved.Truncated) tally.Note(CleanupMessage.Of("ExpansionTruncated", CleanupTargetResolver.MaxExpansions));
        foreach (var target in resolved.Targets.Where(t => t.Source.Type != CleanupTargetType.Handler))
        {
            ct.ThrowIfCancellationRequested();
            ScanTarget(target, context, tally, ct);
        }

        return true;
    }

    private void ScanTarget(ResolvedTarget target, CleanupScanContext context, ScanTally tally, CancellationToken ct)
    {
        if (target.Rejection is not null)
        {
            // 基点の拒否は文言の符号のまま渡す (画面が翻訳する)。それ以外は従来の診断文にパスを添える。
            tally.Reject(CleanupMessage.TryParse(target.Rejection, out _, out _) ? target.Rejection : $"{target.Path ?? target.Source.Path}: {target.Rejection}");
            return;
        }

        switch (target.Source.Type)
        {
            case CleanupTargetType.Contents or CleanupTargetType.Files:
                ScanFiles(target, context, tally, ct);
                break;
            case CleanupTargetType.RegistryValues or CleanupTargetType.RegistryTree:
                ScanRegistry(target, tally, ct);
                break;
            case CleanupTargetType.Sqlite:
                ScanDatabase(target, context, tally, ct);
                break;
            case CleanupTargetType.IniValues:
                ScanIni(target, tally, ct);
                break;
        }
    }

    private static void ScanFiles(ResolvedTarget target, CleanupScanContext context, ScanTally tally, CancellationToken ct, bool keepCredentials = false)
    {
        var engine = CleanupEngineTargets.From(target.Source, target.Path!, keepCredentials);
        var options = new CleanupScanOptions(context.Matcher, RepresentativeLimit) { ClaimKeyOf = tally.Claims.ScanResolver(engine) };
        var result = FileCleanupScanner.Scan(engine, options, ct);
        if (result.Rejection is not null)
        {
            tally.Reject($"{result.Path}: {result.Rejection}");
            return;
        }

        if (result.Missing) return;
        tally.Scanned++;
        tally.Files += result.Files;
        tally.Directories += result.Directories;
        tally.Bytes += result.Bytes;
        tally.AddOverlaps(result.Overlaps);
        tally.Retained += result.Retained;
        tally.Unreadable += result.Unreadable;
        tally.SkippedLinks += result.SkippedLinks;
        tally.AddRepresentatives(result.Representatives);
        tally.Plans.Add(new TargetPlan(target.Source, target.Path, engine, result.CreatedBefore));
    }

    private void ScanRegistry(ResolvedTarget target, ScanTally tally, CancellationToken ct)
    {
        using var root = _parts.OpenUserRoot();
        if (root is null)
        {
            tally.Reject(CleanupMessage.Of("UserRegistryOpenFailed"), CleanupUnavailableReason.ScanFailed);
            return;
        }

        var source = target.Source;
        var result = new CleanupRegistryEditor(root).Scan(source.Key ?? string.Empty, source.Type == CleanupTargetType.RegistryTree ? null : source.Values, ct);
        if (result.Status == CleanupRegistryStatus.Missing) return;
        if (result.Status is CleanupRegistryStatus.Rejected or CleanupRegistryStatus.Failed)
        {
            tally.Reject($"{source.Key}: {string.Join(" / ", result.Examples)}", CleanupUnavailableReason.ScanFailed);
            return;
        }

        tally.Scanned++;
        tally.Items += result.Values + result.Keys;
        tally.Unreadable += result.Skipped;
        foreach (var example in result.Examples) tally.Note(example);
        tally.Plans.Add(new TargetPlan(source, null, null, null));
    }

    private void ScanDatabase(ResolvedTarget target, CleanupScanContext context, ScanTally tally, CancellationToken ct)
    {
        if (!File.Exists(target.Path)) return;
        var result = _parts.Databases.Count(target.Source, target.Path!, context.CookieDomains, ct);
        if (!result.Succeeded)
        {
            tally.Reject($"{target.Path}: {result.Reason}", CleanupUnavailableReason.ScanFailed);
            return;
        }

        tally.Scanned++;
        tally.Items += result.Rows;
        tally.Plans.Add(new TargetPlan(target.Source, target.Path, null, null));
    }

    private void ScanIni(ResolvedTarget target, ScanTally tally, CancellationToken ct)
    {
        var result = _parts.Ini.Scan(target.Path!, target.Source.IniKeys ?? [], ct);
        if (result.Status == CleanupIniStatus.Missing) return;
        if (result.Status is CleanupIniStatus.Skipped or CleanupIniStatus.Failed)
        {
            tally.Reject($"{target.Path}: {result.Reason}", CleanupUnavailableReason.ScanFailed);
            return;
        }

        tally.Scanned++;
        tally.Items += result.RemovedKeys;
        tally.Plans.Add(new TargetPlan(target.Source, target.Path, null, null));
    }

    private async Task ScanHandlersAsync(CleanupRule rule, ScanTally tally, CancellationToken ct)
    {
        foreach (var target in rule.Targets.Where(t => t.Type == CleanupTargetType.Handler))
        {
            ct.ThrowIfCancellationRequested();
            if (target.Handler is not { } kind || !_parts.Handlers.TryGetValue(kind, out var handler))
            {
                tally.Reject(CleanupMessage.Of("HandlerUnavailable", target.Handler), CleanupUnavailableReason.HandlerUnavailable);
                continue;
            }

            tally.HandlerServices.AddRange(handler.Services);
            var scan = await handler.ScanAsync(ct).ConfigureAwait(false);
            if (scan.State == CleanupHandlerScanState.Unavailable)
            {
                tally.Reject(scan.Detail ?? CleanupMessage.Of("HandlerUnavailable", handler.Id), CleanupUnavailableReason.HandlerUnavailable);
                continue;
            }

            tally.Scanned++;
            tally.Items += scan.Items ?? 0;
            tally.Bytes += scan.Bytes ?? 0;
            tally.HandlerFound |= scan.State == CleanupHandlerScanState.Found;
            tally.HandlerUnmeasurable |= scan.State == CleanupHandlerScanState.Unmeasurable;
            if (scan.Detail is not null) tally.Note(scan.Detail);
            tally.Plans.Add(new TargetPlan(target, null, null, null));
        }
    }
}
