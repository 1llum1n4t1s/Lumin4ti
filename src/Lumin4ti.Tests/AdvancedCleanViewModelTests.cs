using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.UI.Services;
using Lumin4ti.UI.ViewModels;

namespace Lumin4ti.Tests;

/// <summary>高度なクリーン画面の ViewModel。実機は触らず、テスト専用の一時フォルダーを仮想のドライブとして走査・削除する。</summary>
[TestClass]
public sealed class AdvancedCleanViewModelTests
{
    private string _parent = string.Empty;
    private string _root = string.Empty;

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public SettingsLoadStatus LoadStatus { get; init; } = SettingsLoadStatus.Loaded;

        public object SyncRoot { get; } = new();

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopLock : IDisposable
    {
        public void Dispose() { }
    }

    private sealed record Harness(
        AdvancedCleanViewModel Vm,
        CleanupSettingsStore Store,
        AdvancedScanLedger Ledger,
        MaintenanceOperationCoordinator Coordinator,
        List<bool> Processing,
        List<string> Opened);

    [TestInitialize]
    public void Setup()
    {
        _parent = Path.Combine(Path.GetTempPath(), "Lumin4tiAdvancedVm", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_parent, "root");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (!Directory.Exists(_parent)) return;
        foreach (var entry in new DirectoryInfo(_parent).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            entry.Attributes = FileAttributes.Normal;
        }

        Directory.Delete(_parent, recursive: true);
    }

    private CustomFolderGuardContext Guard() => new(
        Path.Combine(_root, "Windows"),
        [Path.Combine(_root, "Program Files")],
        Path.Combine(_root, "ProgramData"),
        [Path.Combine(_root, "Users")],
        Path.Combine(_root, "Users", "me"));

    private Harness NewHarness(bool saveSuppressed = false)
    {
        var ledger = new AdvancedScanLedger();
        var store = new CleanupSettingsStore(new FakeSettingsService
        {
            LoadStatus = saveSuppressed ? SettingsLoadStatus.Failed : SettingsLoadStatus.Loaded,
        });
        var coordinator = new MaintenanceOperationCoordinator(() => new NoopLock());
        var processing = new List<bool>();
        coordinator.StateChanged += (_, _) => processing.Add(coordinator.ProcessingState.IsProcessing);
        var opened = new List<string>();
        var vm = new AdvancedCleanViewModel(new AdvancedCleanScanner(ledger), new AdvancedCleaner(ledger), ledger, store, null!, coordinator)
        {
            DriveProvider = () => [new AdvancedDrive(_root, "Test", DriveType.Fixed, true)],
            Protection = new AdvancedProtectedPaths(new AdvancedProtectionContext(Guard(), [Path.Combine(_root, "Self")])),
            GuardContext = Guard(),
            OpenFolder = folder =>
            {
                opened.Add(folder);
                return Task.CompletedTask;
            },
        };
        return new Harness(vm, store, ledger, coordinator, processing, opened);
    }

    private string CreateFile(string relative, string content = "12345", int ageDays = 3)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        if (ageDays > 0) File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddDays(-ageDays));
        return full;
    }

    private static AdvancedCleanRowViewModel Row(AdvancedCleanViewModel vm, string name) =>
        vm.VisibleRows.Single(row => row.Name == name);

    [TestMethod]
    public async Task 走査は進捗表示なしの操作で行い_既定の選択は設計どおりになる()
    {
        var safe = CreateFile("a.tmp");
        var recent = CreateFile("recent.tmp", ageDays: 0);
        CreateFile("data.chk");
        CreateFile("keep.txt");
        var h = NewHarness();

        await h.Vm.ScanCommand.ExecuteAsync(null);

        Assert.IsTrue(h.Vm.HasScan);
        Assert.IsFalse(h.Processing.Contains(true), "走査は showProcessing:false で行う。");
        Assert.HasCount(3, h.Vm.VisibleRows);
        Assert.IsTrue(Row(h.Vm, "a.tmp").IsSelected);
        Assert.IsFalse(Row(h.Vm, "recent.tmp").IsSelected, "24 時間以内に更新されたものは未選択。");
        Assert.IsFalse(Row(h.Vm, "data.chk").IsSelected, "FOUND 以外の .chk は判定不能で未選択。");
        Assert.AreEqual(1, h.Vm.SelectedFileCount);
        Assert.IsTrue(File.Exists(safe), "走査は何も削除しない。");
        Assert.AreEqual(2, h.Vm.Groups.Count);
    }

    [TestMethod]
    public async Task 削除の確認を経て選択だけを削除し_実行中だけ進捗表示になる()
    {
        var selected = CreateFile("a.tmp");
        var unselected = CreateFile("recent.tmp", ageDays: 0);
        var h = NewHarness();
        await h.Vm.ScanCommand.ExecuteAsync(null);

        h.Vm.RequestDeleteCommand.Execute(null);
        Assert.IsTrue(h.Vm.IsConfirming);
        Assert.IsNotEmpty(h.Vm.ConfirmLines);
        Assert.IsTrue(File.Exists(selected), "確認の前は削除しない。");

        await h.Vm.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.IsFalse(File.Exists(selected));
        Assert.IsTrue(File.Exists(unselected));
        Assert.IsTrue(h.Processing.Contains(true), "削除は showProcessing:true で行う。");
        Assert.AreEqual(1, h.Vm.LastOutcome!.DeletedFiles);
        Assert.IsFalse(h.Vm.HasScan, "削除後の結果は失効しているため、再走査まで削除できない。");
        Assert.IsFalse(h.Vm.CanDelete);
    }

    [TestMethod]
    public async Task 確認を戻すと何も削除しない()
    {
        var file = CreateFile("a.tmp");
        var h = NewHarness();
        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Vm.RequestDeleteCommand.Execute(null);

        h.Vm.CancelDeleteCommand.Execute(null);
        await h.Vm.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.IsFalse(h.Vm.IsConfirming);
        Assert.IsTrue(File.Exists(file));
    }

    [TestMethod]
    public async Task 世代が失効した結果は削除に使えず_確認の後に失効しても何も削除しない()
    {
        var file = CreateFile("a.tmp");
        var h = NewHarness();
        await h.Vm.ScanCommand.ExecuteAsync(null);

        h.Ledger.Invalidate();
        h.Vm.RequestDeleteCommand.Execute(null);
        Assert.IsFalse(h.Vm.IsConfirming);
        Assert.IsTrue(h.Vm.IsStale);
        Assert.IsFalse(h.Vm.CanDelete);

        await h.Vm.ScanCommand.ExecuteAsync(null);
        h.Vm.RequestDeleteCommand.Execute(null);
        Assert.IsTrue(h.Vm.IsConfirming);
        h.Ledger.Invalidate();
        await h.Vm.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.IsTrue(h.Vm.LastOutcome!.Expired);
        Assert.IsTrue(File.Exists(file));
        Assert.AreEqual(Ja("Cleanup.Advanced.Result.Expired"), h.Vm.StatusText);
    }

    [TestMethod]
    public async Task カスタムフォルダーは未選択で走査され_選ぶと中身だけを空にする()
    {
        var custom = Path.Combine(_root, "Users", "me", "Junk");
        var inner = CreateFile(Path.Combine("Users", "me", "Junk", "x.dat"));
        File.SetCreationTimeUtc(inner, DateTime.UtcNow.AddDays(-1)); // 走査の直前に作られたものは残す仕様のため、十分前に作られたことにする。
        var h = NewHarness();
        h.Vm.DriveProvider = () => [];
        Assert.IsTrue(h.Store.AddCustomFolder(custom, Guard()).IsAllowed);

        await h.Vm.ScanCommand.ExecuteAsync(null);

        Assert.HasCount(1, h.Vm.CustomFolders);
        var folder = h.Vm.CustomFolders[0];
        Assert.IsTrue(folder.IsSelectable);
        Assert.IsFalse(folder.IsSelected, "カスタムフォルダーは既定で未選択。");
        Assert.IsFalse(h.Vm.CanDelete);

        folder.IsSelected = true;
        Assert.IsTrue(h.Vm.CanDelete);
        h.Vm.RequestDeleteCommand.Execute(null);
        await h.Vm.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.IsFalse(File.Exists(inner));
        Assert.IsTrue(Directory.Exists(custom), "フォルダー自体は残す。");
    }

    [TestMethod]
    public async Task 無視した行は一覧と選択から外れ_削除されない()
    {
        var ignored = CreateFile("a.tmp");
        var other = CreateFile("b.tmp");
        var h = NewHarness();
        await h.Vm.ScanCommand.ExecuteAsync(null);

        await h.Vm.IgnoreFileCommand.ExecuteAsync(Row(h.Vm, "a.tmp"));

        Assert.HasCount(1, h.Vm.VisibleRows);
        Assert.AreEqual(1, h.Vm.SelectedFileCount);
        Assert.Contains(ignored, h.Store.GetExclusions(CleanupExclusionKind.File), StringComparer.OrdinalIgnoreCase);

        h.Vm.RequestDeleteCommand.Execute(null);
        await h.Vm.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.IsTrue(File.Exists(ignored));
        Assert.IsFalse(File.Exists(other));
    }

    [TestMethod]
    public async Task 設定を読めず保存を抑止していても無視は例外にならず行を外す()
    {
        var ignored = CreateFile("a.tmp");
        CreateFile("b.tmp");
        var h = NewHarness(saveSuppressed: true);
        await h.Vm.ScanCommand.ExecuteAsync(null);

        await h.Vm.IgnoreFileCommand.ExecuteAsync(Row(h.Vm, "a.tmp"));

        Assert.HasCount(1, h.Vm.VisibleRows);
        Assert.Contains(ignored, h.Store.GetExclusions(CleanupExclusionKind.File), StringComparer.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task 言語を切り替えても走査結果と選択と状態を失わない()
    {
        CreateFile("a.tmp");
        CreateFile("recent.tmp", ageDays: 0);
        var h = NewHarness();
        await h.Vm.ScanCommand.ExecuteAsync(null);
        Row(h.Vm, "recent.tmp").IsSelected = true;
        h.Vm.RequestDeleteCommand.Execute(null);
        var status = h.Vm.StatusText;
        var notices = h.Vm.Notices.ToList();
        var confirm = h.Vm.ConfirmLines.ToList();

        h.Vm.OnLocaleChanged();

        Assert.IsTrue(h.Vm.HasScan);
        Assert.IsTrue(h.Vm.IsConfirming);
        Assert.HasCount(2, h.Vm.VisibleRows);
        Assert.AreEqual(2, h.Vm.SelectedFileCount);
        Assert.IsTrue(Row(h.Vm, "recent.tmp").IsSelected);
        Assert.AreEqual(status, h.Vm.StatusText);
        CollectionAssert.AreEqual(notices, h.Vm.Notices.ToList());
        CollectionAssert.AreEqual(confirm, h.Vm.ConfirmLines.ToList());
    }

    [TestMethod]
    public async Task 場所を開くは行のフォルダーを開く()
    {
        CreateFile(Path.Combine("sub", "a.tmp"));
        var h = NewHarness();
        await h.Vm.ScanCommand.ExecuteAsync(null);

        await h.Vm.OpenLocationCommand.ExecuteAsync(Row(h.Vm, "a.tmp"));

        CollectionAssert.AreEqual(new[] { Path.Combine(_root, "sub") }, h.Opened);
    }

    [TestMethod]
    public async Task 別の操作が実行中なら走査を始めない()
    {
        CreateFile("a.tmp");
        var h = NewHarness();
        Assert.IsTrue(h.Coordinator.TryBegin(out var other));
        using (other)
        {
            await h.Vm.ScanCommand.ExecuteAsync(null);
        }

        Assert.IsFalse(h.Vm.HasScan);
        Assert.IsFalse(h.Vm.IsRunning);
        Assert.IsNotEmpty(h.Vm.StatusText);
    }

    [TestMethod]
    public async Task 一括の選択は表示中の行に効き_集計と並べ替えと絞り込みが働く()
    {
        CreateFile("b.tmp", "1234567890");
        CreateFile("a.tmp", "1");
        CreateFile("c.chk", "123");
        var h = NewHarness();
        await h.Vm.ScanCommand.ExecuteAsync(null);

        h.Vm.SelectNoneCommand.Execute(null);
        Assert.AreEqual(0, h.Vm.SelectedFileCount);
        Assert.IsFalse(h.Vm.CanDelete);
        h.Vm.SelectAllCommand.Execute(null);
        Assert.AreEqual(3, h.Vm.SelectedFileCount);
        Assert.AreEqual(14, h.Vm.SelectedBytes);
        h.Vm.SelectSafeCommand.Execute(null);
        Assert.AreEqual(2, h.Vm.SelectedFileCount);

        var rows = h.Vm.VisibleRows;
        var bySize = AdvancedCleanViewModel.Arrange(rows, null, null, AdvancedCleanSortColumn.Size, descending: true);
        CollectionAssert.AreEqual(new[] { "b.tmp", "c.chk", "a.tmp" }, bySize.Select(r => r.Name).ToArray());
        var onlyChk = AdvancedCleanViewModel.Arrange(rows, h.Vm.Groups.Single(g => g.Title == "*.chk").Key, null, AdvancedCleanSortColumn.None, false);
        CollectionAssert.AreEqual(new[] { "c.chk" }, onlyChk.Select(r => r.Name).ToArray());
        var byText = AdvancedCleanViewModel.Arrange(rows, null, "A.T", AdvancedCleanSortColumn.None, false);
        CollectionAssert.AreEqual(new[] { "a.tmp" }, byText.Select(r => r.Name).ToArray());
    }

    [TestMethod]
    public void 削除の結果は部分失敗と残した数を成功と区別して示す()
    {
        var done = new AdvancedCleanOutcome { DeletedFiles = 3, FreedBytes = 2048 };
        var partial = new AdvancedCleanOutcome { DeletedFiles = 2, Blocked = 4, Retained = 1, Gone = 2, CoveredByFolders = 1 };
        partial.Rejected.Add("x");
        partial.AddBlocked(["C:\\a.tmp (使用中)"]);
        var expired = new AdvancedCleanOutcome { Expired = true };
        var cancelled = new AdvancedCleanOutcome { Cancelled = true, DeletedFiles = 1 };

        Assert.AreEqual(Ja("Cleanup.Advanced.Result.Done", "3", "0", "2.0 KB"), AdvancedCleanViewModel.DescribeOutcome(done));
        Assert.AreEqual(Ja("Cleanup.Advanced.Result.Partial", "2", "0", "0 B"), AdvancedCleanViewModel.DescribeOutcome(partial));
        Assert.AreEqual(Ja("Cleanup.Advanced.Result.Expired"), AdvancedCleanViewModel.DescribeOutcome(expired));
        Assert.AreEqual(Ja("Cleanup.Advanced.Result.Cancelled", "1", "0 B"), AdvancedCleanViewModel.DescribeOutcome(cancelled));
        Assert.IsEmpty(AdvancedCleanViewModel.BuildOutcomeNotices(done));
        var notices = AdvancedCleanViewModel.BuildOutcomeNotices(partial);
        Assert.HasCount(6, notices);
        Assert.Contains(Ja("Cleanup.Advanced.Result.Blocked", "4"), notices);
        Assert.Contains(Ja("Cleanup.Advanced.Result.Rejected", "1"), notices);
    }

    /// <summary>テストでは辞書が無いため、日本語マスターの文言になる。</summary>
    private static string Ja(string key, params string[] args) =>
        string.Format(Lumin4ti.UI.ViewModels.CleanupText.JapaneseOf(key), args);
}
