using System.Text;
using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;
using Lumin4ti.Core.Services.Windows.Cleanup.Slim;
using Lumin4ti.UI.ViewModels;

namespace Lumin4ti.Verification;

/// <summary>
/// レジストリタブのプレビュー専用の偽の境界。合成データを返し、実レジストリは読まず・書かない。
/// 削除と復元は同じバックアップ一覧と走査の世代を共有する (本番の <c>RegistryCleanBackends</c> と同じ形)。
/// </summary>
internal sealed class PreviewRegistryState(PreviewToggles toggles)
{
    private readonly object _gate = new();
    private readonly List<RegistryBackupSummary> _backups =
    [
        new("20260301T093000123Z-a1b2c3d4", new DateTime(2026, 3, 1, 9, 30, 0, DateTimeKind.Utc), 12, true),
        new("20260215T210501456Z-e5f6a7b8", new DateTime(2026, 2, 15, 21, 5, 1, DateTimeKind.Utc), null, false),
    ];

    private string? _currentGeneration;
    private int _serial;

    public PreviewToggles Toggles { get; } = toggles;

    public IReadOnlyList<RegistryBackupSummary> List()
    {
        lock (_gate) return [.. _backups];
    }

    public void Add(RegistryBackupSummary summary)
    {
        lock (_gate) _backups.Insert(0, summary);
    }

    public void Remove(string id)
    {
        lock (_gate) _backups.RemoveAll(b => b.Id == id);
    }

    public string NextBackupId()
    {
        var serial = Interlocked.Increment(ref _serial);
        return $"{DateTime.UtcNow:yyyyMMddTHHmmssfff}Z-pv{serial:000000}";
    }

    public void Register(string generation)
    {
        lock (_gate) _currentGeneration = generation;
    }

    public bool TryConsume(string generation)
    {
        lock (_gate)
        {
            if (!string.Equals(_currentGeneration, generation, StringComparison.Ordinal)) return false;
            _currentGeneration = null;
            return true;
        }
    }

    public void Invalidate()
    {
        lock (_gate) _currentGeneration = null;
    }
}

internal sealed class PreviewRegistryCleanBackend(PreviewRegistryState state) : IRegistryCleanBackend
{
    private const uint RegSz = 1;
    private const uint RegDword = 4;

    public async Task<RegistryScanResult> ScanAsync(CancellationToken ct)
    {
        await Task.Delay(900, ct).ConfigureAwait(false);
        var hkcu = state.Toggles.HkcuAvailable;
        var items = new List<RegistryScanItem>();
        var companions = new List<RegistryScanCompanion>();

        for (var i = 1; i <= 6; i++)
        {
            items.Add(Item(RegistryCleanCategory.AppPaths, RegistryCleanHive.LocalMachine, RegistryCleanView.Registry64,
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\contoso-tool{i}.exe", null, null,
                CleanupMessage.Of(RegistryScanReasons.MissingFile, $@"C:\Program Files\Contoso\Tool{i}\contoso-tool{i}.exe"), 30 * i));
        }

        if (hkcu)
        {
            for (var i = 1; i <= 4; i++)
            {
                var run = Item(RegistryCleanCategory.Startup, RegistryCleanHive.CurrentUser, RegistryCleanView.Registry64,
                    @"Software\Microsoft\Windows\CurrentVersion\Run", $"ContosoUpdater{i}", RegSz,
                    CleanupMessage.Of(RegistryScanReasons.MissingFile, $@"C:\Users\preview\AppData\Local\Contoso\updater{i}.exe"), 20 * i);
                items.Add(run);
                if (i <= 2)
                {
                    companions.Add(new RegistryScanCompanion(
                        run,
                        RegistryCompanionKind.StartupApprovedValue,
                        Item(RegistryCleanCategory.Startup, RegistryCleanHive.CurrentUser, RegistryCleanView.Registry64,
                            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", $"ContosoUpdater{i}", 3,
                            CleanupMessage.Of(RegistryScanReasons.MissingFile, "StartupApproved"), 20 * i),
                        []));
                }
            }

            for (var i = 1; i <= 3; i++)
            {
                items.Add(Item(RegistryCleanCategory.FileAssociations, RegistryCleanHive.CurrentUser, RegistryCleanView.Registry64,
                    $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.cts{i}\OpenWithList", ((char)('a' + i)).ToString(), RegSz,
                    CleanupMessage.Of(RegistryScanReasons.MissingFile, $@"C:\Program Files\Contoso\viewer{i}.exe"), 15 * i));
            }

            items.Add(Item(RegistryCleanCategory.FileAssociations, RegistryCleanHive.UserClasses, RegistryCleanView.Registry64,
                @"Applications\contoso-viewer.exe", null, null,
                CleanupMessage.Of(RegistryScanReasons.MissingFile, @"C:\Program Files\Contoso\contoso-viewer.exe"), 200));
        }

        items.Add(Item(RegistryCleanCategory.FileAssociations, RegistryCleanHive.LocalMachine, RegistryCleanView.Registry64,
            @"SOFTWARE\Classes\.ctsx", null, null, CleanupMessage.Of(RegistryScanReasons.ProgIdMissing, "Contoso.Document"), 400));

        // アンインストール: 個別承認が必要。32 ビット表示 (WOW6432Node) の行を含む。
        for (var i = 1; i <= 8; i++)
        {
            var view = i % 3 == 0 ? RegistryCleanView.Registry32 : RegistryCleanView.Registry64;
            var root = view == RegistryCleanView.Registry32
                ? @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                : @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
            items.Add(Item(RegistryCleanCategory.Uninstall, RegistryCleanHive.LocalMachine, view,
                $@"{root}\Contoso Suite {i}", null, null,
                CleanupMessage.Of(RegistryScanReasons.MissingFile, $@"C:\Program Files\Contoso Suite {i}\uninstall.exe"), 90 * i));
        }

        // 共有 DLL: 数百行 (一覧の仮想化の確認用)。
        for (var i = 1; i <= 320; i++)
        {
            items.Add(Item(RegistryCleanCategory.SharedDlls, RegistryCleanHive.LocalMachine, RegistryCleanView.Registry64,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs", $@"C:\Program Files\Legacy\Component{i:000}\shared{i:000}.dll", RegDword,
                CleanupMessage.Of(RegistryScanReasons.MissingFile, $@"C:\Program Files\Legacy\Component{i:000}\shared{i:000}.dll"), 365 + i));
        }

        // フォント: 候補なし (保持だけを表示する)。
        var retained = new List<RegistryRetainedEntry>
        {
            new(RegistryCleanCategory.Fonts, RegistryRetentionKind.Protected, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", "Segoe UI (TrueType)",
                CleanupMessage.Of(RegistryScanReasons.ProtectedPath, @"C:\Windows\Fonts\segoeui.ttf")),
            new(RegistryCleanCategory.Fonts, RegistryRetentionKind.Unknown, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", "Contoso Sans (TrueType)",
                CleanupMessage.Of(RegistryScanReasons.UnknownPath, @"C:\Program Files\Contoso\Fonts\contoso-sans.ttf")),
            new(RegistryCleanCategory.AppPaths, RegistryRetentionKind.Unknown, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\fabrikam.exe", null,
                CleanupMessage.Of(RegistryScanReasons.Unresolved)),
            new(RegistryCleanCategory.Uninstall, RegistryRetentionKind.Protected, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Microsoft Edge", null,
                CleanupMessage.Of(RegistryScanReasons.ProtectedPath, @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")),
        };

        var scope = hkcu
            ? new RegistryUserScopeStatus(true, "S-1-5-21-1000-2000-3000-1001", InteractiveScopeReason.None, null)
            : new RegistryUserScopeStatus(false, null, InteractiveScopeReason.DifferentUser,
                CleanupMessage.Of(RegistryScanReasons.UserScopeUnavailable, InteractiveScopeReason.DifferentUser));
        var generation = Guid.NewGuid().ToString("N");
        state.Register(generation);
        return new RegistryScanResult(generation, DateTime.UtcNow, items, retained, companions, scope);
    }

    public async Task<RegistryCleanRunResult> CleanAsync(RegistryCleanRequest request, CancellationToken ct)
    {
        if (!state.TryConsume(request.Scan.GenerationId))
        {
            return new(RegistryOverallState.Failed, [], null, null, CleanupMessage.Of(RegistryCleanReasons.Stale), true, false);
        }

        RestorePointResult? point = null;
        if (request.CreateRestorePoint)
        {
            point = await RestorePointAsync(request, ct).ConfigureAwait(false);
            if (!point.IsUsable)
            {
                return new(RegistryOverallState.Failed, [], null, point, CleanupMessage.Of(RegistryCleanReasons.RestorePointUnavailable, point.Message), false, false);
            }
        }

        var results = new List<RegistryCleanItemResult>();
        var index = 0;
        foreach (var item in request.Selected)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(12, ct).ConfigureAwait(false);
            index++;
            results.Add(index % 7 == 3
                ? new(item, RegistryItemState.Skipped, CleanupMessage.Of(RegistryCleanReasons.AccessDenied))
                : index % 11 == 5
                    ? new(item, RegistryItemState.Retained, CleanupMessage.Of(RegistryCleanReasons.Changed))
                    : new(item, RegistryItemState.Deleted, null));
        }

        var id = state.NextBackupId();
        state.Add(new RegistryBackupSummary(id, DateTime.UtcNow, results.Count, true));
        var overall = results.Any(r => r.State is RegistryItemState.Skipped or RegistryItemState.Failed)
            ? RegistryOverallState.Partial
            : RegistryOverallState.Success;
        return new(overall, results, id, point, null, false, true);
    }

    public void Invalidate() => state.Invalidate();

    /// <summary>復元ポイントの偽。作成可・保護無効・失敗をツールバーで切り替える (実際には何も作らない)。</summary>
    private async Task<RestorePointResult> RestorePointAsync(RegistryCleanRequest request, CancellationToken ct)
    {
        await Task.Delay(700, ct).ConfigureAwait(false);
        var point = PreviewRestorePoints.Result(state.Toggles);
        while (!point.IsUsable)
        {
            if (request.OnRestorePointUnavailable is null) return point;
            var decision = await request.OnRestorePointUnavailable(point, ct).ConfigureAwait(false);
            if (decision == RestorePointDecision.ContinueWithout) return new RestorePointResult(RestorePointOutcome.Created, "preview: continued without", null);
            if (decision != RestorePointDecision.EnableProtectionAndRetry || point.Outcome != RestorePointOutcome.ProtectionDisabled) return point;
            state.Toggles.RestoreMode = PreviewRestoreMode.Created;
            point = PreviewRestorePoints.Result(state.Toggles);
        }

        return point;
    }

    private static RegistryScanItem Item(
        RegistryCleanCategory category, RegistryCleanHive hive, RegistryCleanView view,
        string keyPath, string? valueName, uint? valueType, string reason, int ageDays) =>
        new(category, hive, view, keyPath, valueName, valueType,
            RegistryContentHash.Of(Encoding.UTF8.GetBytes(hive + keyPath + valueName)),
            DateTime.UtcNow.AddDays(-ageDays), @"\REGISTRY\PREVIEW\" + keyPath, reason);
}

internal sealed class PreviewRegistryRestoreBackend(PreviewRegistryState state) : IRegistryRestoreBackend
{
    public IReadOnlyList<RegistryBackupSummary> List() => state.List();

    public RegistryRestoreResult Restore(string id, CancellationToken ct)
    {
        if (state.List().All(b => b.Id != id))
        {
            return new(RegistryOverallState.Failed, [], CleanupMessage.Of(RegistryCleanReasons.RestoreNotFound), false);
        }

        Thread.Sleep(600);
        state.Invalidate();
        var first = id.StartsWith("20260301", StringComparison.Ordinal);
        RegistryRestoreItemResult Row(string key, string? value, RegistryItemState result, string? reason) => new(
            new RegistryBackupItem(RegistryCleanCategory.AppPaths, RegistryCleanHive.LocalMachine, RegistryCleanView.Registry64,
                key, value, value is null ? null : 1u, value is null ? null : Convert.ToBase64String("x"u8.ToArray()),
                value is null ? new RegistryBackupKeyNode([], []) : null),
            result, reason);
        var rows = new List<RegistryRestoreItemResult>
        {
            Row(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\contoso-tool1.exe", null, RegistryItemState.Restored, null),
            Row(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\contoso-tool2.exe", null, RegistryItemState.AlreadyPresent, null),
        };
        if (first)
        {
            rows.Add(Row(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "ContosoUpdater1", RegistryItemState.Conflict, CleanupMessage.Of(RegistryCleanReasons.RestoreConflict)));
            rows.Add(Row(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "ContosoUpdater2", RegistryItemState.Failed, CleanupMessage.Of(RegistryCleanReasons.RestoreVerifyFailed)));
        }

        var overall = first ? RegistryOverallState.Partial : RegistryOverallState.Success;
        return new(overall, rows, null, true);
    }

    public void Delete(string id) => state.Remove(id);
}
