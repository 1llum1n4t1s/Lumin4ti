using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.UI.Services;
using Lumin4ti.UI.ViewModels;

namespace Lumin4ti.Tests;

/// <summary>
/// 「通常」画面の ViewModel。走査・削除・場所を開く・実行中アプリの確認は偽物に差し替え、実機を変えずに
/// 選択の規則・確認の段階・結果の表示 (成功と偽らない) を確かめる。
/// </summary>
[TestClass]
public sealed class CommonCleanViewModelTests
{
    private string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "Lumin4ti.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class NoopLock : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class NoPrompts : ICleanupRunPrompts
    {
        public Task<CleanupProcessChoice> ChooseForRunningProcessesAsync(CleanupProcessPrompt prompt, CancellationToken ct) =>
            Task.FromResult(CleanupProcessChoice.Abort);

        public Task<bool> ConfirmForceTerminationAsync(CleanupProcessPrompt prompt, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class Harness
    {
        public MaintenanceOperationCoordinator Coordinator { get; } = new(() => new NoopLock());

        public CleanupScanSnapshot? Snapshot { get; set; }

        public bool Current { get; set; } = true;

        public Exception? ScanFailure { get; set; }

        public CleanupRunResult? RunResult { get; set; }

        public CleanupRunRequest? Request { get; private set; }

        public bool? ScanProcessing { get; private set; }

        public bool? RunProcessing { get; private set; }

        public CommonCleanViewModel Create(string directory)
        {
            var settings = new CleanupSettingsStore(new SettingsService(directory, Path.Combine(directory, "settings.json")));
            var services = new CommonCleanServices(
                (_, _) =>
                {
                    ScanProcessing = Coordinator.ProcessingState.IsProcessing;
                    return ScanFailure is null ? Task.FromResult(Snapshot!) : Task.FromException<CleanupScanSnapshot>(ScanFailure);
                },
                (request, _, _) =>
                {
                    RunProcessing = Coordinator.ProcessingState.IsProcessing;
                    Request = request;
                    return Task.FromResult(RunResult!);
                },
                _ => Current,
                _ => Task.CompletedTask,
                new NoPrompts(),
                action => action());
            return new CommonCleanViewModel(null!, null!, settings, null!, Coordinator, services);
        }
    }

    private static CleanupRuleInfo Rule(
        string id, CleanupRuleCategory category, CleanupRuleImpact impact, bool recommended, string[]? processes = null) =>
        new(id, id, null, CleanupRuleKind.Cache, category, impact, recommended, null, false, processes ?? [], [], false);

    private static CleanupScanItem Item(
        string id,
        CleanupRuleCategory category = CleanupRuleCategory.Windows,
        CleanupRuleImpact impact = CleanupRuleImpact.Junk,
        bool recommended = true,
        bool defaultSelected = true,
        CleanupItemState state = CleanupItemState.Found,
        string[]? representatives = null,
        string[]? processes = null) =>
        new(
            Rule(id, category, impact, recommended),
            state,
            CleanupUnavailableReason.None,
            3, 0, 0, 1024, 0, 0, 0,
            defaultSelected,
            representatives ?? [],
            processes ?? [],
            []);

    private static CleanupScanSnapshot Snapshot(params CleanupScanItem[] items) =>
        new(
            1,
            DateTime.UtcNow,
            CleanupScopeStatus.Available,
            items,
            new Dictionary<string, RulePlan>(),
            new CleanupScanContext(CleanupExclusionMatcher.Empty, [], false, "fp"));

    private static CleanupRuleRunResult RuleResult(CleanupScanItem item, CleanupRunStatus status) =>
        new(item.Rule, status, CleanupSkipReason.None, 3, 0, 0, 1024, 0, 0, 0, 0, []);

    private static CleanupRunResult RunResult(bool stale, bool canceled, params CleanupRuleRunResult[] items) =>
        new(items, stale, canceled, DateTimeOffset.Now, DateTimeOffset.Now);

    private static async Task<CommonCleanViewModel> ScannedAsync(Harness harness, string directory, params CleanupScanItem[] items)
    {
        harness.Snapshot = Snapshot(items);
        var viewModel = harness.Create(directory);
        await viewModel.ScanCommand.ExecuteAsync(null);
        return viewModel;
    }

    private static CommonCleanRowViewModel RowOf(CommonCleanViewModel viewModel, string key) =>
        viewModel.Rows.Single(r => r.Key == key);

    [TestMethod]
    public async Task 走査は処理中表示なしで始まり_既定は推奨のみ選び_利用者のファイルは選ばない()
    {
        var harness = new Harness();
        var viewModel = await ScannedAsync(
            harness,
            _directory,
            Item("a"),
            Item("b", recommended: false, defaultSelected: false),
            Item("u", impact: CleanupRuleImpact.UserFiles, recommended: false, defaultSelected: true),
            Item("gone", state: CleanupItemState.NotDetected));

        Assert.IsFalse(harness.ScanProcessing);
        Assert.IsFalse(viewModel.IsRunning);
        Assert.IsTrue(RowOf(viewModel, "a").IsChecked);
        Assert.IsFalse(RowOf(viewModel, "b").IsChecked);
        Assert.IsFalse(RowOf(viewModel, "u").IsChecked);
        Assert.IsFalse(viewModel.Rows.Any(r => r.Key == "gone"), "この PC に無い規則は出さない");
    }

    [TestMethod]
    public async Task 分類は3状態で_一括選択と分類のチェックは利用者のファイルを含めない()
    {
        var viewModel = await ScannedAsync(
            new Harness(),
            _directory,
            Item("a", defaultSelected: false),
            Item("b", defaultSelected: false),
            Item("u", impact: CleanupRuleImpact.UserFiles, recommended: false, defaultSelected: false));
        var category = viewModel.Rows.Single(r => r.IsCategory);

        viewModel.SelectAllCommand.Execute(null);
        Assert.IsTrue(category.IsChecked);
        Assert.IsFalse(RowOf(viewModel, "u").IsChecked);

        RowOf(viewModel, "a").IsChecked = false;
        Assert.IsNull(category.IsChecked);

        category.IsChecked = true;
        Assert.IsTrue(RowOf(viewModel, "a").IsChecked);
        Assert.IsFalse(RowOf(viewModel, "u").IsChecked);

        RowOf(viewModel, "u").IsChecked = true;
        Assert.IsTrue(RowOf(viewModel, "u").IsChecked, "利用者のファイルは個別には選べる");
        viewModel.SelectRecommendedCommand.Execute(null);
        Assert.IsFalse(RowOf(viewModel, "u").IsChecked);
    }

    [TestMethod]
    public void 規則の注意書きは理由_推奨外_実行中の製品を含む()
    {
        var item = Item("x", impact: CleanupRuleImpact.Signout, recommended: false, processes: ["app"]) with
        {
            Retained = 2,
            Unreadable = 1,
            SkippedLinks = 4,
            State = CleanupItemState.Unmeasurable,
        };

        var note = CommonCleanViewModel.BuildNote(item);

        Assert.HasCount(7, note);
        var unavailable = item with { State = CleanupItemState.Unavailable, Reason = CleanupUnavailableReason.TargetRejected, Detail = ["detail"] };
        Assert.Contains("detail", CommonCleanViewModel.BuildNote(unavailable));
        Assert.IsFalse(CommonCleanViewModel.BuildNote(Item("junk")).Any(), "推奨の不要ファイルには注意書きがない");
    }

    [TestMethod]
    public void 確認の注意書きは推奨外とサインアウトと利用者のファイルを数え_安全な削除を示す()
    {
        var chosen = new[]
        {
            Item("a"),
            Item("b", recommended: false),
            Item("c", impact: CleanupRuleImpact.Signout),
            Item("u", impact: CleanupRuleImpact.UserFiles, recommended: false, processes: ["app"]),
        };

        var plain = CommonCleanViewModel.BuildConfirmNotes([Item("a")], secureDelete: false);
        var notes = CommonCleanViewModel.BuildConfirmNotes(chosen, secureDelete: true);

        Assert.IsEmpty(plain);
        Assert.IsGreaterThanOrEqualTo(plain.Count + 6, notes.Count);
    }

    [TestMethod]
    public async Task 削除は確認の後に処理中表示ありで実行され_全成功のときだけ成功と表示する()
    {
        var harness = new Harness();
        var a = Item("a");
        var viewModel = await ScannedAsync(harness, _directory, a);
        harness.RunResult = RunResult(false, false, RuleResult(a, CleanupRunStatus.Succeeded));

        viewModel.RequestRunCommand.Execute(null);
        Assert.IsTrue(viewModel.IsConfirming);
        Assert.IsNull(harness.Request, "確認の前には実行しない");
        await viewModel.ConfirmRunCommand.ExecuteAsync(null);

        Assert.IsTrue(harness.RunProcessing);
        CollectionAssert.AreEqual(new[] { "a" }, harness.Request!.SelectedRuleIds.ToArray());
        Assert.IsTrue(viewModel.HasResult);
        Assert.IsTrue(viewModel.ResultIsSuccess);
        Assert.IsFalse(viewModel.HasRows, "実行で走査結果は使い切られ、再走査が必要");
        Assert.IsFalse(viewModel.CanRun);
    }

    [TestMethod]
    [DataRow(CleanupRunStatus.Partial, false, false)]
    [DataRow(CleanupRunStatus.Failed, false, false)]
    [DataRow(CleanupRunStatus.Skipped, false, false)]
    [DataRow(CleanupRunStatus.Succeeded, true, false)]
    [DataRow(CleanupRunStatus.Succeeded, false, true)]
    public async Task 部分失敗_取消_失効を成功と表示しない(CleanupRunStatus status, bool stale, bool canceled)
    {
        var harness = new Harness();
        var a = Item("a");
        var viewModel = await ScannedAsync(harness, _directory, a);
        harness.RunResult = RunResult(stale, canceled, RuleResult(a, status));

        viewModel.RequestRunCommand.Execute(null);
        await viewModel.ConfirmRunCommand.ExecuteAsync(null);

        Assert.IsTrue(viewModel.HasResult);
        Assert.IsFalse(viewModel.ResultIsSuccess);
    }

    [TestMethod]
    public async Task 走査結果が古ければ確認せず再走査を求める()
    {
        var harness = new Harness();
        var viewModel = await ScannedAsync(harness, _directory, Item("a"));
        harness.Current = false;

        viewModel.RequestRunCommand.Execute(null);

        Assert.IsFalse(viewModel.IsConfirming);
        Assert.IsTrue(viewModel.IsStale);
        Assert.IsFalse(viewModel.CanRun);
        Assert.IsNull(harness.Request);
    }

    [TestMethod]
    public async Task 確認の後に古くなったら実行しない()
    {
        var harness = new Harness();
        var viewModel = await ScannedAsync(harness, _directory, Item("a"));
        viewModel.RequestRunCommand.Execute(null);
        harness.Current = false;

        await viewModel.ConfirmRunCommand.ExecuteAsync(null);

        Assert.IsNull(harness.Request);
        Assert.IsTrue(viewModel.IsStale);
        Assert.IsFalse(viewModel.IsConfirming);
    }

    [TestMethod]
    public async Task 利用者のファイルは再確認で同意した規則だけを実行する()
    {
        var harness = new Harness();
        var u1 = Item("u1", impact: CleanupRuleImpact.UserFiles, recommended: false, defaultSelected: false);
        var u2 = Item("u2", impact: CleanupRuleImpact.UserFiles, recommended: false, defaultSelected: false);
        var viewModel = await ScannedAsync(harness, _directory, Item("a"), u1, u2);
        harness.RunResult = RunResult(false, false);
        RowOf(viewModel, "u1").IsChecked = true;
        RowOf(viewModel, "u2").IsChecked = true;

        viewModel.RequestRunCommand.Execute(null);
        await viewModel.ConfirmRunCommand.ExecuteAsync(null);
        Assert.IsTrue(viewModel.IsConfirmingUserFiles);
        Assert.IsNull(harness.Request, "再確認の前には実行しない");
        viewModel.UserFileEntries.Single(e => e.RuleId == "u1").IsConfirmed = true;
        await viewModel.ConfirmUserFilesCommand.ExecuteAsync(null);

        CollectionAssert.AreEquivalent(new[] { "a", "u1" }, harness.Request!.SelectedRuleIds.ToArray());
        CollectionAssert.AreEqual(new[] { "u1" }, harness.Request.ConfirmedUserFileRuleIds.ToArray());
    }

    [TestMethod]
    public async Task 走査の取消と失敗は結果を残さず成功と表示しない()
    {
        var harness = new Harness { Snapshot = Snapshot(Item("a")) };
        var viewModel = harness.Create(_directory);
        await viewModel.ScanCommand.ExecuteAsync(null);
        Assert.IsTrue(viewModel.HasRows);

        harness.ScanFailure = new OperationCanceledException();
        await viewModel.ScanCommand.ExecuteAsync(null);
        Assert.IsFalse(viewModel.HasRows, "再走査は前の結果を失効させるため、取消後は空にする");
        Assert.IsFalse(viewModel.IsRunning);

        harness.ScanFailure = new InvalidOperationException("x");
        await viewModel.ScanCommand.ExecuteAsync(null);
        Assert.IsFalse(viewModel.HasRows);
        Assert.IsFalse(viewModel.CanRun);
    }

    [TestMethod]
    public async Task 別の操作が実行中なら走査を始めない()
    {
        var harness = new Harness { Snapshot = Snapshot(Item("a")) };
        var viewModel = harness.Create(_directory);
        Assert.IsTrue(harness.Coordinator.TryBegin(out var other));
        using (other)
        {
            await viewModel.ScanCommand.ExecuteAsync(null);
        }

        Assert.IsFalse(viewModel.HasRows);
        Assert.IsNull(harness.ScanProcessing);
    }

    [TestMethod]
    public async Task 規則は展開すると代表パスの行が増え_絞り込みと空の非表示が効く()
    {
        var harness = new Harness();
        var viewModel = await ScannedAsync(
            harness,
            _directory,
            Item("alpha", representatives: [@"C:\x\one", @"C:\x\two"]),
            Item("beta", state: CleanupItemState.Empty));
        Assert.HasCount(3, viewModel.Rows);

        RowOf(viewModel, "alpha").IsExpanded = true;
        Assert.HasCount(5, viewModel.Rows);

        viewModel.HideEmpty = true;
        Assert.IsFalse(viewModel.Rows.Any(r => r.Key == "beta"));
        viewModel.FilterText = "zzz";
        Assert.IsFalse(viewModel.HasRows);
    }

    [TestMethod]
    public async Task 場所を開くは項目ではなく含まれるフォルダーだけを渡す()
    {
        var opened = new List<string>();
        var file = Path.Combine(_directory, "a.tmp");
        File.WriteAllText(file, "x");
        var settings = new CleanupSettingsStore(new SettingsService(_directory, Path.Combine(_directory, "settings.json")));
        var services = new CommonCleanServices(
            (_, _) => Task.FromResult(Snapshot()),
            (_, _, _) => Task.FromResult(RunResult(false, false)),
            _ => true,
            path =>
            {
                opened.Add(path);
                return Task.CompletedTask;
            },
            new NoPrompts(),
            action => action());
        var viewModel = new CommonCleanViewModel(null!, null!, settings, null!, new MaintenanceOperationCoordinator(() => new NoopLock()), services);

        await viewModel.OpenLocationAsync(file);
        await viewModel.OpenLocationAsync(Path.Combine(_directory, "missing", "b.tmp"));

        CollectionAssert.AreEqual(new[] { _directory }, opened);
    }

    [TestMethod]
    public async Task 除外へ追加すると保存され_走査結果は失効して再走査を求める()
    {
        var harness = new Harness();
        var viewModel = await ScannedAsync(harness, _directory, Item("a"));
        var folder = Path.Combine(_directory, "cache");
        Directory.CreateDirectory(folder);

        await viewModel.IgnorePathAsync(folder);

        Assert.IsTrue(viewModel.IsStale);
        Assert.IsFalse(viewModel.CanRun);
        CollectionAssert.Contains(viewModel.Settings.GetExclusions(CleanupExclusionKind.Folder).ToList(), folder);
        Assert.IsTrue(File.Exists(Path.Combine(_directory, "settings.json")));
    }
}
