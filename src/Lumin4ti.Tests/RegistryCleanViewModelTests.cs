using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;
using Lumin4ti.UI.Services;
using Lumin4ti.UI.ViewModels;

namespace Lumin4ti.Tests;

/// <summary>レジストリタブの ViewModel。Core の走査・削除・復元は偽物で、実レジストリは変更しない。</summary>
[TestClass]
public sealed class RegistryCleanViewModelTests
{
    private sealed class Idle : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class FakeClean : IRegistryCleanBackend
    {
        public RegistryScanResult Scan { get; set; } = Scan1();

        public RegistryCleanRunResult? Result { get; set; }

        public RegistryCleanRequest? LastRequest { get; private set; }

        public int Invalidations { get; private set; }

        public Action? OnClean { get; set; }

        public Task<RegistryScanResult> ScanAsync(CancellationToken ct) => Task.FromResult(Scan);

        public Task<RegistryCleanRunResult> CleanAsync(RegistryCleanRequest request, CancellationToken ct)
        {
            LastRequest = request;
            OnClean?.Invoke();
            return Task.FromResult(Result ?? new RegistryCleanRunResult(
                RegistryOverallState.Success,
                [.. request.Selected.Select(item => new RegistryCleanItemResult(item, RegistryItemState.Deleted, null))],
                "backup-1", null, null, false, true));
        }

        public void Invalidate() => Invalidations++;
    }

    private sealed class FakeRestore : IRegistryRestoreBackend
    {
        public List<RegistryBackupSummary> Backups { get; } = [new("20260101T000000000Z-aaaa", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 3, true)];

        public RegistryRestoreResult Result { get; set; } = new(RegistryOverallState.Success, [], null, true);

        public List<string> Deleted { get; } = [];

        public IReadOnlyList<RegistryBackupSummary> List() => [.. Backups];

        public RegistryRestoreResult Restore(string id, CancellationToken ct) => Result;

        public void Delete(string id) => Deleted.Add(id);
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            Clean = new RegistryCleanViewModel(Backend, Coordinator);
            Restore = new RegistryRestoreViewModel(RestoreBackend, Clean, Coordinator);
        }

        public FakeClean Backend { get; } = new();

        public FakeRestore RestoreBackend { get; } = new();

        public MaintenanceOperationCoordinator Coordinator { get; } = new(() => new Idle());

        public RegistryCleanViewModel Clean { get; }

        public RegistryRestoreViewModel Restore { get; }

        public RegistryCleanGroupViewModel Group(RegistryCleanCategory category) =>
            Clean.Groups.First(group => group.Definition.Category == category);

        public async Task ScanAsync() => await Clean.ScanCommand.ExecuteAsync(null);
    }

    private static RegistryScanItem Item(RegistryCleanCategory category, string key, RegistryCleanHive hive = RegistryCleanHive.LocalMachine) => new(
        category, hive, RegistryCleanView.Registry64, key, null, null, "hash", DateTime.UtcNow, key,
        CleanupMessage.Of(RegistryScanReasons.MissingFile));

    private static RegistryScanResult Scan1(RegistryUserScopeStatus? scope = null, params RegistryRetainedEntry[] retained) => new(
        "gen-1",
        DateTime.UtcNow,
        [
            Item(RegistryCleanCategory.AppPaths, "a1"),
            Item(RegistryCleanCategory.Startup, "s1"),
            Item(RegistryCleanCategory.Uninstall, "u1"),
            Item(RegistryCleanCategory.Uninstall, "u2"),
            Item(RegistryCleanCategory.SharedDlls, "d1"),
        ],
        retained,
        [],
        scope ?? new RegistryUserScopeStatus(true, "S-1-5-21-1", InteractiveScopeReason.None, null));

    [TestMethod]
    public async Task Scan_SelectsOnlyDefaultCategories_AndShowsNoOverlay()
    {
        var f = new Fixture();
        var overlay = new List<bool>();
        f.Backend.Scan = Scan1();
        f.Coordinator.StateChanged += (_, _) => overlay.Add(f.Coordinator.ProcessingState.IsProcessing);

        await f.ScanAsync();

        Assert.IsTrue(overlay.All(on => !on), "走査は処理中オーバーレイを出さない");
        Assert.IsTrue(f.Clean.HasScan);
        Assert.AreEqual(2, f.Clean.SelectedCount);
        Assert.AreEqual(1, f.Group(RegistryCleanCategory.AppPaths).SelectedCount);
        Assert.AreEqual(0, f.Group(RegistryCleanCategory.Uninstall).SelectedCount);
        Assert.AreEqual(0, f.Group(RegistryCleanCategory.SharedDlls).SelectedCount);
        Assert.AreEqual(2, f.Group(RegistryCleanCategory.Uninstall).Rows.Count);
    }

    [TestMethod]
    public async Task SelectAll_LeavesUninstallAndSharedDllsAlone()
    {
        var f = new Fixture();
        await f.ScanAsync();

        f.Clean.SelectAllCommand.Execute(null);

        Assert.AreEqual(2, f.Clean.SelectedCount);
        Assert.IsTrue(f.Group(RegistryCleanCategory.Uninstall).Rows.All(row => !row.IsSelected));
        Assert.IsTrue(f.Group(RegistryCleanCategory.SharedDlls).Rows.All(row => !row.IsSelected));

        f.Clean.ClearSelectionCommand.Execute(null);
        Assert.AreEqual(0, f.Clean.SelectedCount);
    }

    [TestMethod]
    public async Task Uninstall_CannotRunUntilEveryRowIsApproved()
    {
        var f = new Fixture();
        await f.ScanAsync();
        var uninstall = f.Group(RegistryCleanCategory.Uninstall).Rows;
        uninstall[0].IsSelected = true;
        uninstall[1].IsSelected = true;

        f.Clean.RequestRunCommand.Execute(null);

        Assert.IsTrue(f.Clean.IsConfirming);
        Assert.AreEqual(2, f.Clean.ApprovalRows.Count);
        Assert.IsFalse(f.Clean.CanConfirm);
        await f.Clean.ConfirmRunCommand.ExecuteAsync(null);
        Assert.IsNull(f.Backend.LastRequest, "承認が済むまで実行しない");

        uninstall[0].IsApproved = true;
        Assert.IsFalse(f.Clean.CanConfirm);
        uninstall[1].IsApproved = true;
        Assert.IsTrue(f.Clean.CanConfirm);
        await f.Clean.ConfirmRunCommand.ExecuteAsync(null);

        Assert.IsNotNull(f.Backend.LastRequest);
        Assert.AreEqual(4, f.Backend.LastRequest.Selected.Count);
    }

    [TestMethod]
    public async Task Run_UsesOverlayLease_AndRestorePointCheckResetsEachTime()
    {
        var f = new Fixture();
        var overlayDuringRun = false;
        f.Backend.OnClean = () => overlayDuringRun = f.Coordinator.ProcessingState.IsProcessing;
        await f.ScanAsync();

        f.Clean.RequestRunCommand.Execute(null);
        Assert.IsTrue(f.Clean.CreateRestorePoint);
        f.Clean.CreateRestorePoint = false;
        f.Clean.CancelConfirmCommand.Execute(null);
        f.Clean.RequestRunCommand.Execute(null);
        Assert.IsTrue(f.Clean.CreateRestorePoint, "復元ポイントの選択はその回だけ");
        await f.Clean.ConfirmRunCommand.ExecuteAsync(null);

        Assert.IsTrue(overlayDuringRun);
        Assert.IsTrue(f.Backend.LastRequest!.CreateRestorePoint);
        Assert.IsFalse(f.Clean.HasScan);
        Assert.IsTrue(f.Clean.NeedsRescan, "実行後は再走査を促す");
    }

    [TestMethod]
    public async Task Stale_AsksForRescan_AndShowsNoResult()
    {
        var f = new Fixture();
        f.Backend.Result = new(RegistryOverallState.Failed, [], null, null, CleanupMessage.Of(RegistryCleanReasons.Stale), true, false);
        await f.ScanAsync();
        f.Clean.RequestRunCommand.Execute(null);

        await f.Clean.ConfirmRunCommand.ExecuteAsync(null);

        Assert.IsTrue(f.Clean.NeedsRescan);
        Assert.IsFalse(f.Clean.HasScan);
        Assert.AreEqual(CleanupText.Get("Cleanup.Registry.Ui.Stale"), f.Clean.StatusText);
        Assert.AreEqual(string.Empty, f.Clean.ResultSummary);
    }

    [TestMethod]
    public Task Partial_IsNotShownAsSuccess_AndReasonsAreCappedAtEight() => AssertNonSuccessAsync(RegistryOverallState.Partial);

    [TestMethod]
    public Task Failed_IsNotShownAsSuccess_AndReasonsAreCappedAtEight() => AssertNonSuccessAsync(RegistryOverallState.Failed);

    private static async Task AssertNonSuccessAsync(RegistryOverallState overall)
    {
        var f = new Fixture();
        await f.ScanAsync();
        var reasons = Enumerable.Range(0, 10)
            .Select(i => new RegistryCleanItemResult(
                Item(RegistryCleanCategory.AppPaths, $"k{i}"), RegistryItemState.Skipped, CleanupMessage.Of($"RegistryTestReason{i}")))
            .ToList();
        var deleted = new RegistryCleanItemResult(Item(RegistryCleanCategory.Startup, "ok"), RegistryItemState.Deleted, null);
        f.Backend.Result = new(overall, [deleted, .. reasons], "backup-1", null, null, false, true);
        f.Clean.RequestRunCommand.Execute(null);

        await f.Clean.ConfirmRunCommand.ExecuteAsync(null);

        Assert.AreEqual(RegistryResultFormat.OverallText(overall, 1, 10), f.Clean.ResultSummary);
        Assert.AreNotEqual(RegistryResultFormat.OverallText(RegistryOverallState.Success, 1, 10), f.Clean.ResultSummary);
        Assert.AreEqual(RegistryResultFormat.MaxReasons, f.Clean.ResultReasons.Count);
        Assert.AreNotEqual(string.Empty, f.Clean.ResultMoreText);
    }

    [TestMethod]
    public async Task UnavailableUserScope_ShowsReason_AndRetainedSummaryStaysOutOfRows()
    {
        var f = new Fixture();
        var reason = CleanupMessage.Of(RegistryScanReasons.UserScopeUnavailable, InteractiveScopeReason.DifferentUser);
        var retained = new RegistryRetainedEntry(RegistryCleanCategory.AppPaths, RegistryRetentionKind.Unknown, "x", null, CleanupMessage.Of(RegistryScanReasons.UnknownPath));
        f.Backend.Scan = Scan1(new RegistryUserScopeStatus(false, null, InteractiveScopeReason.DifferentUser, reason), retained);

        await f.ScanAsync();

        Assert.IsTrue(f.Clean.HasUserScopeNote);
        var group = f.Group(RegistryCleanCategory.AppPaths);
        Assert.AreEqual(1, group.RetainedCount);
        Assert.AreEqual(1, group.Rows.Count, "保持した項目は選択肢に出さない");
        Assert.AreNotEqual(string.Empty, group.RetainedText);
    }

    [TestMethod]
    public async Task Restore_InvalidatesScan_AndSummarizesStates()
    {
        var f = new Fixture();
        await f.ScanAsync();
        var backupItem = new RegistryBackupItem(RegistryCleanCategory.AppPaths, RegistryCleanHive.LocalMachine, RegistryCleanView.Registry64, "k", null, null, null, null);
        f.RestoreBackend.Result = new(
            RegistryOverallState.Partial,
            [
                new(backupItem, RegistryItemState.Restored, null),
                new(backupItem, RegistryItemState.Conflict, CleanupMessage.Of(RegistryCleanReasons.RestoreConflict)),
            ],
            null,
            true);
        await f.Restore.RefreshCommand.ExecuteAsync(null);
        Assert.AreEqual(1, f.Restore.Backups.Count);

        await f.Restore.RestoreCommand.ExecuteAsync(f.Restore.Backups[0]);

        Assert.IsFalse(f.Clean.HasScan);
        Assert.IsTrue(f.Clean.NeedsRescan);
        Assert.AreEqual(CleanupText.Get("Cleanup.Registry.Ui.Restore.Summary", 1, 0, 1, 0, 0), f.Restore.ResultSummary);
        Assert.AreEqual(1, f.Restore.ResultReasons.Count);
    }

    [TestMethod]
    public async Task DeleteBackup_NeedsConfirmation()
    {
        var f = new Fixture();
        await f.Restore.RefreshCommand.ExecuteAsync(null);
        var row = f.Restore.Backups[0];

        f.Restore.RequestDeleteCommand.Execute(row);
        Assert.IsTrue(f.Restore.IsConfirmingDelete);
        Assert.AreEqual(0, f.RestoreBackend.Deleted.Count);
        f.Restore.CancelDeleteCommand.Execute(null);
        Assert.AreEqual(0, f.RestoreBackend.Deleted.Count);

        f.Restore.RequestDeleteCommand.Execute(row);
        f.RestoreBackend.Backups.Clear();
        await f.Restore.ConfirmDeleteCommand.ExecuteAsync(null);

        CollectionAssert.AreEqual(new[] { row.Id }, f.RestoreBackend.Deleted);
        Assert.AreEqual(0, f.Restore.Backups.Count);
    }
}
