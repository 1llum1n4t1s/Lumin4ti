using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Core.Services.Windows;

public sealed record RebootCleanupQueueResult(bool Success, int PendingTargets, string Detail);

public interface IRebootCacheCleanupScheduler
{
    // 呼出し元はメンテナンス lease を保持する。選択0件・今回失敗0件でも最新意図を反映する。
    Task<RebootCleanupQueueResult> QueueAsync(IReadOnlyList<CleanupTarget> selected, IReadOnlyList<CleanupTarget> blocked, CancellationToken ct);
}

internal interface IRebootCleanupStore
{
    IReadOnlyList<string> Names();
    bool Exists(string name);
    string Read(string name);
    void WriteNew(string name, string json);
    void Delete(string name);
}

internal sealed class ProtectedRebootCleanupStore(ProtectedBackupStorage storage) : IRebootCleanupStore
{
    private const string DirectoryName = "reboot-cleanup-v1";
    private static string Relative(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['\\', '/', ':']) >= 0 || name is "." or "..") throw new SecurityException("キュー名が不正です。");
        return Path.Combine(DirectoryName, name);
    }
    public IReadOnlyList<string> Names() => storage.EnumerateFileNames(DirectoryName, 1024);
    public bool Exists(string name) => storage.FileExists(Relative(name));
    public string Read(string name)
    {
        if (!Exists(name)) throw new FileNotFoundException("キューが見つかりません。");
        if (new FileInfo(storage.GetFullPath(Relative(name))).Length > 16384) throw new IOException("キューが大きすぎます。");
        return storage.ReadAllText(Relative(name));
    }
    public void WriteNew(string name, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > 16384) throw new IOException("キューが大きすぎます。");
        storage.WriteNewAtomically(Relative(name), stream => stream.Write(bytes));
    }
    public void Delete(string name) => storage.Delete(Relative(name));
}

internal sealed record RebootCleanupRequest(int Schema, string Id, long Generation, string UserSid, DateTimeOffset CreatedUtc, string[] Selected, string[] Pending)
{
    internal DateTimeOffset Expires => CreatedUtc.AddDays(7);
}
internal sealed record RebootCleanupStarted(int Schema, string Id, int Attempt, DateTimeOffset At);
internal sealed record RebootCleanupResult(int Schema, string Id, string UserSid, int Attempt, bool Terminal, bool Success, string Detail);
internal sealed record RebootCleanupSelection(int Schema, string Id, int Attempt, string[] Allowed);

/// <summary>パスを保存せず、明示的に選ばれた5種のキャッシュ意図だけを特権キューへ載せる。</summary>
public sealed class RebootCacheCleanupService : IRebootCacheCleanupScheduler
{
    private const int Schema = 1;
    private const int MaxAttempts = 3;
    private readonly IRebootCleanupStore _store;
    private readonly IRebootCleanupTask _task;
    private readonly IRebootCleanupEnvironment _environment;
    private readonly ISettingsService? _settings;
    private readonly Func<RebootCleanupProfile, string[], CancellationToken, Task<MaintenanceActionResult>> _execute;
    private readonly Func<IDisposable?> _lease;
    private readonly Func<TimeSpan, CancellationToken, Task> _leaseDelay;
    private readonly Action<RebootCleanupResult> _reportResult = LogResult;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public RebootCacheCleanupService(ICommandExecutor executor, ISettingsService? settings = null)
    {
        _environment = new RebootCleanupEnvironment();
        _settings = settings;
        _store = new ProtectedRebootCleanupStore(ProtectedBackupStorage.Default);
        _task = new RebootCleanupTask(_environment.HelperPath);
        _lease = MaintenanceOperationProcessLock.TryAcquire;
        _leaseDelay = Task.Delay;
        _execute = (profile, ids, ct) => new FileCleanupAction("reboot-shell-cache", "起動時のキャッシュ再構築", "予約した対象のみ再列挙します",
            () => RebootCleanupTargets.Resolve(profile, ids), executor,
            servicesToStop: RebootCleanupTargets.NeedsFontServices(ids) ? FileCleanupGroups.ShellCacheServices : [])
            .ExecuteAsync(ct);
    }

    internal RebootCacheCleanupService(IRebootCleanupStore store, IRebootCleanupTask task, IRebootCleanupEnvironment environment,
        Func<RebootCleanupProfile, string[], CancellationToken, Task<MaintenanceActionResult>> execute, Func<IDisposable?> lease,
        Func<TimeSpan, CancellationToken, Task>? leaseDelay = null, ISettingsService? settings = null,
        Action<RebootCleanupResult>? reportResult = null)
    { _store = store; _task = task; _environment = environment; _settings = settings; _execute = execute; _lease = lease; _leaseDelay = leaseDelay ?? Task.Delay; _reportResult = reportResult ?? LogResult; }

    public Task<RebootCleanupQueueResult> QueueAsync(IReadOnlyList<CleanupTarget> selected, IReadOnlyList<CleanupTarget> blocked, CancellationToken ct) =>
        Task.Run(() => Queue(selected, blocked, ct), ct);

    private RebootCleanupQueueResult Queue(IReadOnlyList<CleanupTarget> selected, IReadOnlyList<CleanupTarget> blocked, CancellationToken ct)
    {
        var registered = false;
        var committed = false;
        var committedPending = 0;
        var hadPreviousPending = false;
        var readSucceeded = false;
        RebootCleanupRequest? attempted = null;
        List<RebootCleanupRequest>? previous = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!_environment.IsTrustedLauncher)
                return new(false, 0, "正規インストールの実行元を確認できず予約を更新できません。以前の起動時予約がある場合は残っています");
            var selectedIds = RebootCleanupTargets.GetIds(selected);
            var pending = RebootCleanupTargets.GetIds(blocked);
            if (pending.Except(selectedIds, StringComparer.Ordinal).Any()) throw new IOException("未選択の対象は予約できません。");
            var sid = _environment.CurrentSid;
            RebootCleanupTargets.ValidateSid(sid);
            previous = ReadRequests();
            readSucceeded = true;
            var old = previous.Where(request => request.UserSid == sid).ToArray();
            var latest = old.MaxBy(item => item.Generation);
            hadPreviousPending = latest is not null && IsActive(latest);
            if (pending.Length == 0 && !hadPreviousPending)
            {
                RemoveTaskIfIdle(previous);
                return new(true, 0, old.Length == 0 ? string.Empty : "以前の起動時予約は既に停止しています");
            }
            // 通常64件とは別に、既存SIDごとに1件の取消intentを確保する。
            if ((pending.Length > 0 && previous.Count >= 64) || previous.Count >= 128)
                throw new IOException("予約数の上限に達しました。以前の結果を読み込んでから再実行してください。");
            if (pending.Length > 0)
            {
                if (!_environment.IsTrustedHelper) throw new SecurityException("署名済みの正規配置の予約プログラムを確認できません。");
                var profile = _environment.GetProfile(sid);
                _environment.ValidateEnqueueProfile(profile, selected);
                if (_settings is not null)
                {
                    if (_settings.LoadStatus == SettingsLoadStatus.Failed) throw new IOException("設定の読込に失敗しているため原本を上書きせず予約を中止しました。");
                    _settings.SaveAsync(ct).GetAwaiter().GetResult();
                    _settings.FlushAsync(ct).GetAwaiter().GetResult();
                }
                // サインイン経路で読んだ別instanceは古い可能性があるため保存しない。
                if (_environment.LoadSettings(profile).LoadStatus != SettingsLoadStatus.Loaded)
                    throw new IOException("標準プロファイルへ設定を正常に保存したことを確認できません。");
            }
            var request = new RebootCleanupRequest(Schema, Guid.NewGuid().ToString("N"), checked(previous.Select(item => item.Generation).DefaultIfEmpty().Max() + 1),
                sid, _environment.UtcNow, selectedIds, pending);
            attempted = request;
            if (pending.Length > 0)
            {
                registered = true;
                _task.Ensure(sid, request.Expires);
            }
            ct.ThrowIfCancellationRequested();
            WriteRequestAndConfirm(request);
            committed = true;
            committedPending = pending.Length;
            foreach (var earlier in old.Where(item => !IsTerminal(item))) Finish(earlier, 0, true, "新しい選択により以前の予約を取り消しました");
            if (pending.Length == 0)
            {
                Finish(request, 0, true, "今回の選択・処理結果により起動時の削除予約を取り消しました");
                RemoveTaskIfIdle(ReadRequests());
            }
            return new(true, pending.Length, pending.Length > 0
                ? $"使用中のキャッシュ {pending.Length} 種を次回の Windows 起動時に同じ利用者で再列挙します（最大3回・7日間）。ロック中の項目は保持します"
                : "以前の起動時の削除予約を今回の選択に合わせて取り消しました");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (IsFailure(ex))
        {
            LoggerBootstrap.Log.Error("reboot-cleanup: 予約の更新に失敗しました", ex);
            return new(committed, committedPending, (committed ? "予約意図は保存済みですが後処理を完了できませんでした: " : "起動時の削除予約を更新できませんでした: ") + Diagnostic(ex.Message) +
                (!committed ? hadPreviousPending ? "。以前の起動時予約は変更できず残っています" :
                    !readSucceeded || attempted is not null ? "。起動時予約が残っている可能性があります" : string.Empty : string.Empty));
        }
        finally
        {
            if (registered && !committed && previous is not null)
            {
                try { RemoveTaskIfIdle(previous); }
                catch (Exception ex) when (IsFailure(ex)) { LoggerBootstrap.Log.Error("reboot-cleanup: 登録失敗後のタスク回収を次回へ残しました", ex); }
            }
        }
    }

    internal async Task<int> RunBootAsync(CancellationToken ct = default)
    {
        if (!_environment.IsBootAccount || !_environment.IsTrustedHelper) return 1;
        try { RebootCleanupTargets.ValidateSid(_environment.CurrentSid); }
        catch (Exception ex) when (IsFailure(ex)) { return 1; }
        using var lease = await WaitForLeaseAsync(ct).ConfigureAwait(false);
        if (lease is null) { LoggerBootstrap.Log.Info("reboot-cleanup: 実行枠の待機期限に達したため予約を保持します"); return ct.IsCancellationRequested ? 1 : 0; }
        try
        {
            var requests = ReadRequests();
            foreach (var request in requests.Where(item => item.UserSid == _environment.CurrentSid))
            {
                if (IsTerminal(request)) continue;
                if (requests.Any(other => other.UserSid == request.UserSid && other.Generation > request.Generation))
                { Finish(request, 0, true, "新しい選択で取り消された予約です"); continue; }
                if (EffectivePending(request).Length == 0) { Finish(request, 0, true, "削除予約は取り消されています"); continue; }
                var attempts = StartedCount(request);
                if (request.Expires <= _environment.UtcNow || attempts >= MaxAttempts)
                { Finish(request, attempts, false, "起動時の削除予約は期限または実行回数の上限に達しました"); continue; }
                ct.ThrowIfCancellationRequested();
                var attempt = attempts + 1;
                Write(StartName(request.Id, attempt), new RebootCleanupStarted(Schema, request.Id, attempt, _environment.UtcNow));
                MaintenanceActionResult result;
                try
                {
                    var profile = _environment.GetProfile(request.UserSid); // SYSTEM の settings/env では再構成しない。
                    var settings = _environment.LoadSettings(profile);
                    if (settings.LoadStatus != SettingsLoadStatus.Loaded) throw new IOException("現在の利用者設定を正常に読み込めないため削除しません。");
                    var selection = new FileCleanupAction("cleanup-shell-cache", "キャッシュ", "キャッシュ", () => FileCleanupGroups.ShellCacheTargets,
                        preferences: new CleanupPreferences(settings), checkListKeySelector: FileCleanupGroups.GetShellCacheGroupName);
                    var currentIds = RebootCleanupTargets.GetIds(selection.EnumerateSelectedTargets());
                    var allowed = EffectivePending(request).Intersect(request.Selected, StringComparer.Ordinal).Intersect(currentIds, StringComparer.Ordinal).ToArray();
                    Write(SelectionName(request.Id, attempt), new RebootCleanupSelection(Schema, request.Id, attempt, allowed));
                    result = allowed.Length == 0 ? MaintenanceActionResult.Ok("現在 OFF の対象を取り消しました") :
                        await _execute(profile, allowed, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { result = MaintenanceActionResult.Fail("起動時の削除に失敗しました: " + Diagnostic(ex.Message)); }
                var succeeded = result.Status == MaintenanceActionStatus.Success;
                var terminal = succeeded || attempt >= MaxAttempts || request.Expires <= _environment.UtcNow;
                Write(ResultName(request.Id, attempt), new RebootCleanupResult(Schema, request.Id, request.UserSid, attempt, terminal, succeeded, Diagnostic(result.Detail)));
                LoggerBootstrap.Log.Info($"reboot-cleanup: {request.UserSid} attempt={attempt} terminal={terminal} success={succeeded}: {Diagnostic(result.Detail)}");
                if (ct.IsCancellationRequested) break;
            }
            RemoveTaskIfIdle(ReadRequests());
            return ct.IsCancellationRequested ? 1 : 0;
        }
        catch (Exception ex) when (IsFailure(ex) || ex is OperationCanceledException)
        { LoggerBootstrap.Log.Error("reboot-cleanup: キューを保持して終了します", ex); return 1; }
    }

    private async Task<IDisposable?> WaitForLeaseAsync(CancellationToken ct)
    {
        // helper 内部期限(2分)の一部だけを待機へ使い、同じ起動中に取得できれば処理する。
        for (var attempt = 0; attempt < 60 && !ct.IsCancellationRequested; attempt++)
        {
            var lease = _lease();
            if (lease is not null) return lease;
            if (attempt == 59) break;
            try { await _leaseDelay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
        return null;
    }

    internal void ReportCurrentUserResults()
    {
        if (!_environment.IsTrustedLauncher) return;
        using var lease = _lease();
        if (lease is null) { LoggerBootstrap.Log.Info("reboot-cleanup: 別操作が実行中のため結果回収を次回へ延期しました"); return; }
        try
        {
            var requests = ReadRequests();
            var own = requests.Where(item => item.UserSid == _environment.CurrentSid).OrderBy(item => item.Generation).ToArray();
            if (own.Length == 0) return;
            if (own.Length > 0)
            {
                // 最新intentは古いjobの全削除を確認するまで保持し、古いpendingの復活を防ぐ。
                foreach (var earlier in own[..^1])
                {
                    var earlierResult = LastResult(earlier);
                    if (earlierResult is { Terminal: true }) _reportResult(earlierResult);
                    else LoggerBootstrap.Log.Info($"reboot-cleanup: 新しい意図で停止した以前の予約を回収します ({earlier.Id})");
                    DeleteJob(earlier);
                }
                var latest = own[^1];
                if (!IsTerminal(latest) && latest.Expires <= _environment.UtcNow)
                    Finish(latest, StartedCount(latest), false, "起動時の削除予約は実行されないまま期限を過ぎました");
                if (!IsTerminal(latest) && EffectivePending(latest).Length == 0)
                    Finish(latest, 0, true, "削除予約は取り消されています");
                if (IsTerminal(latest))
                {
                    var result = LastResult(latest)!;
                    _reportResult(result);
                    DeleteJob(latest);
                }
            }
        }
        catch (Exception ex) when (IsFailure(ex)) { LoggerBootstrap.Log.Error("reboot-cleanup: 最新意図と予約結果を保持しました", ex); }
    }

    private void DeleteJob(RebootCleanupRequest request)
    {
        _store.Delete(JobName(request.Id));
        if (_store.Exists(JobName(request.Id))) throw new IOException("古い予約の削除を確認できません。");
        for (var index = 0; index <= MaxAttempts; index++)
        {
            _store.Delete(ResultName(request.Id, index));
            if (index > 0) { _store.Delete(StartName(request.Id, index)); _store.Delete(SelectionName(request.Id, index)); }
        }
    }

    private void WriteRequestAndConfirm(RebootCleanupRequest request)
    {
        var expected = JsonSerializer.Serialize(request, Json);
        try { _store.WriteNew(JobName(request.Id), expected); }
        catch (Exception ex) when (IsFailure(ex))
        {
            // File.Move後の検証でthrowした可能性がある。最終名を信頼検証付きで再読する。
            if (!_store.Exists(JobName(request.Id)) || JsonSerializer.Serialize(Read<RebootCleanupRequest>(JobName(request.Id)), Json) != expected) throw;
            LoggerBootstrap.Log.Info("reboot-cleanup: 保存後の例外を検出しましたが最終名のcommitを再確認しました");
            return;
        }
        if (JsonSerializer.Serialize(Read<RebootCleanupRequest>(JobName(request.Id)), Json) != expected)
            throw new IOException("保存した予約の最終内容を確認できません。");
    }
    private List<RebootCleanupRequest> ReadRequests()
    {
        var names = _store.Names();
        if (names.Count > 1024) throw new IOException("予約ファイル数が上限を超えています。");
        var requests = new List<RebootCleanupRequest>();
        foreach (var name in names.Where(name => name.StartsWith("job-", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal)))
        {
            var request = Read<RebootCleanupRequest>(name);
            if (request.Schema != Schema || !Guid.TryParseExact(request.Id, "N", out _) || name != JobName(request.Id) || request.Generation <= 0 ||
                request.CreatedUtc > _environment.UtcNow.AddMinutes(5) || string.IsNullOrWhiteSpace(request.UserSid) || request.Selected is null || request.Pending is null ||
                request.Selected.Length > 5 || request.Pending.Length > 5 || request.Selected.Distinct().Count() != request.Selected.Length ||
                request.Pending.Distinct().Count() != request.Pending.Length || request.Selected.Except(RebootCleanupTargets.Ids).Any() ||
                request.Pending.Except(request.Selected).Any()) throw new IOException("未知または不正な削除予約を検出しました。");
            RebootCleanupTargets.ValidateSid(request.UserSid);
            requests.Add(request);
        }
        if (requests.Count > 128 || requests.GroupBy(item => item.Generation).Any(group => group.Count() != 1)) throw new IOException("予約の世代を確定できません。");
        foreach (var request in requests) { _ = StartedCount(request); _ = LastResult(request); _ = EffectivePending(request); }
        return requests;
    }

    private int StartedCount(RebootCleanupRequest request)
    {
        var count = 0;
        for (var index = 1; index <= MaxAttempts; index++)
        {
            if (!_store.Exists(StartName(request.Id, index))) continue;
            var started = Read<RebootCleanupStarted>(StartName(request.Id, index));
            if (started.Schema != Schema || started.Id != request.Id || started.Attempt != index || index != count + 1)
                throw new IOException("予約の実行履歴を確定できません。");
            count++;
        }
        return count;
    }

    private RebootCleanupResult? LastResult(RebootCleanupRequest request)
    {
        RebootCleanupResult? last = null;
        for (var index = 0; index <= MaxAttempts; index++)
        {
            if (!_store.Exists(ResultName(request.Id, index))) continue;
            var result = Read<RebootCleanupResult>(ResultName(request.Id, index));
            if (result.Schema != Schema || result.Id != request.Id || result.UserSid != request.UserSid || result.Attempt != index || result.Detail is null)
                throw new IOException("予約結果を確定できません。");
            if (last is null || result.Terminal || !last.Terminal) last = result;
        }
        return last;
    }

    private string[] EffectivePending(RebootCleanupRequest request)
    {
        var pending = request.Pending;
        var started = StartedCount(request);
        for (var index = 1; index <= MaxAttempts; index++)
        {
            if (!_store.Exists(SelectionName(request.Id, index))) continue;
            var selection = Read<RebootCleanupSelection>(SelectionName(request.Id, index));
            if (selection.Schema != Schema || selection.Id != request.Id || selection.Attempt != index || index > started || selection.Allowed is null ||
                selection.Allowed.Distinct().Count() != selection.Allowed.Length || selection.Allowed.Except(pending).Any()) throw new IOException("予約の選択履歴を確定できません。");
            pending = selection.Allowed;
        }
        return pending;
    }

    private bool IsTerminal(RebootCleanupRequest request) => LastResult(request)?.Terminal == true;
    private bool IsActive(RebootCleanupRequest request) => !IsTerminal(request) && EffectivePending(request).Length > 0;
    private void Finish(RebootCleanupRequest request, int attempt, bool success, string detail)
    {
        if (IsTerminal(request)) return;
        // 同番号の非terminal結果がある場合も、取消は専用0番へ固定して上書きしない。
        var number = _store.Exists(ResultName(request.Id, attempt)) ? 0 : attempt;
        Write(ResultName(request.Id, number), new RebootCleanupResult(Schema, request.Id, request.UserSid, number, true, success, detail));
    }
    private void RemoveTaskIfIdle(IEnumerable<RebootCleanupRequest> requests)
    {
        var latest = requests.Where(item => item.UserSid == _environment.CurrentSid).MaxBy(item => item.Generation);
        if (latest is null || !IsActive(latest)) _task.RemoveIfOwned(_environment.CurrentSid);
    }
    private T Read<T>(string name)
    {
        var json = _store.Read(name);
        if (Encoding.UTF8.GetByteCount(json) > 16384) throw new IOException("予約データが大きすぎます。");
        return JsonSerializer.Deserialize<T>(json, Json) ?? throw new IOException("予約データが空です。");
    }
    private void Write<T>(string name, T value) => _store.WriteNew(name, JsonSerializer.Serialize(value, Json));
    private static string JobName(string id) => $"job-{id}.json";
    private static string StartName(string id, int attempt) => $"start-{id}-{attempt}.json";
    private static string SelectionName(string id, int attempt) => $"selection-{id}-{attempt}.json";
    private static string ResultName(string id, int attempt) => $"result-{id}-{attempt}.json";
    private static string Diagnostic(string value) => CommandFailureDiagnostic.Sanitize(value);
    private static void LogResult(RebootCleanupResult result) => LoggerBootstrap.Log.Info($"reboot-cleanup: 予約結果 success={result.Success}: {result.Detail}");
    private static bool IsFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or SecurityException or JsonException or
        ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception or OverflowException;
}

public static class RebootCacheCleanup
{
    public static int RunBoot()
    {
        var environment = new RebootCleanupEnvironment();
        if (!environment.IsBootAccount || !environment.IsTrustedHelper ||
            !string.Equals(Environment.ProcessPath, environment.HelperPath, StringComparison.OrdinalIgnoreCase)) return 1;
        LoggerBootstrap.Initialize();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            return new RebootCacheCleanupService(new ProcessCommandExecutor()).RunBootAsync(deadline.Token).GetAwaiter().GetResult();
        }
        finally { LoggerBootstrap.Shutdown(); }
    }

    public static void ReportCurrentUserResults() => new RebootCacheCleanupService(new ProcessCommandExecutor()).ReportCurrentUserResults();
}
