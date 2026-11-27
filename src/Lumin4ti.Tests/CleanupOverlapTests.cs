using Lumin4ti.Core.Services.Windows.Cleanup;

namespace Lumin4ti.Tests;

/// <summary>包含関係にある対象 (全体とその一部、全体とその中の 1 ファイル) の二重計上と二重失敗を防ぐ。テスト専用の一時領域だけを使う。</summary>
[TestClass]
public sealed class CleanupOverlapTests
{
    private const string Temp = @"%LOCALAPPDATA%\Test\Temp";

    private CleanupHarness _h = null!;

    [TestInitialize]
    public void Setup()
    {
        _h = new CleanupHarness();
        _h.Write(@"Local\Test\Temp\ACC.log", "123");
        _h.Write(@"Local\Test\Temp\other.tmp", "12345");
        _h.Write(@"Local\Test\Temp\Nero\n1.dat", "1234567");
        _h.Write(@"Local\Test\Temp\Nero\sub\n2.dat", "12345678901");
    }

    [TestCleanup]
    public void Cleanup() => _h.Dispose();

    private static CleanupRule All() => CleanupHarness.MakeRule("tmp.all", [CleanupHarness.Contents(Temp)]);

    private static CleanupRule Nero() => CleanupHarness.MakeRule("tmp.nero", [CleanupHarness.Contents(Temp + @"\Nero")]);

    private static CleanupRule Acc() => CleanupHarness.MakeRule(
        "tmp.acc", [new CleanupRuleTarget { Type = CleanupTargetType.Files, Path = Temp, Pattern = "ACC.log" }]);

    private async Task<(CleanupScanService Scan, CleanupScanSnapshot Snapshot)> ScanAsync(params CleanupRule[] rules)
    {
        _h.Rules.AddRange(rules);
        Thread.Sleep(150); // 走査の直前に作られたものは「走査後に作られた」として残すため。
        var scan = _h.NewScan();
        return (scan, await scan.ScanAsync(null, null, CancellationToken.None));
    }

    private static Task<CleanupRunResult> RunAsync(CleanupScanService scan, CleanupScanSnapshot snapshot, params string[] ids) =>
        new CleanupRunService(scan, new RunningProcessGuard(new FakeProcessControl()), new FakeServiceHost().Suspend, () => DateTimeOffset.UtcNow)
            .RunAsync(new CleanupRunRequest(snapshot, ids), null, CancellationToken.None);

    [TestMethod]
    public async Task 規則別の表示はそのままで合計は同じファイルを1回だけ数える()
    {
        var (_, snapshot) = await ScanAsync(All(), Nero(), Acc());

        Assert.AreEqual(4, snapshot.Items.Single(i => i.Rule.Id == "tmp.all").Files);
        Assert.AreEqual(2, snapshot.Items.Single(i => i.Rule.Id == "tmp.nero").Files);
        Assert.AreEqual(18, snapshot.Items.Single(i => i.Rule.Id == "tmp.nero").Bytes);
        Assert.AreEqual(1, snapshot.Items.Single(i => i.Rule.Id == "tmp.acc").Files);
        Assert.AreEqual((4L, 26L), (snapshot.TotalFiles, snapshot.TotalBytes));
        Assert.AreEqual((2L, 18L), Pair(snapshot, "tmp.nero"), "先の規則を選ばなければ一部だけの分");
        Assert.AreEqual((1L, 3L), Pair(snapshot, "tmp.acc"));
        Assert.AreEqual((3L, 21L), Pair(snapshot, "tmp.nero", "tmp.acc"), "互いに重ならない 2 つ");
        Assert.AreEqual((4L, 26L), Pair(snapshot, "tmp.all", "tmp.nero"));
    }

    [TestMethod]
    public async Task 一部を先に走査した場合も全体との合計は1回だけ数える()
    {
        var (_, snapshot) = await ScanAsync(Nero(), All());

        Assert.AreEqual(4, snapshot.TotalFiles);
        Assert.AreEqual(26, snapshot.TotalBytes);
        Assert.AreEqual((4L, 26L), Pair(snapshot, "tmp.all"), "後の規則だけを選べば全体の分");
        Assert.AreEqual(2, snapshot.Measure(["tmp.all", "tmp.nero"]).Directories, "フォルダーも1回だけ数える");
    }

    [TestMethod]
    public async Task 先の規則が消したものは後の規則で失敗にも二重計上にもしない()
    {
        var (scan, snapshot) = await ScanAsync(All(), Nero(), Acc());

        var result = await RunAsync(scan, snapshot, "tmp.all", "tmp.nero", "tmp.acc");

        Assert.AreEqual(CleanupRunStatus.Succeeded, result.Items.Single(i => i.Rule.Id == "tmp.all").Status);
        Assert.IsTrue(result.Items.Where(i => i.Rule.Id != "tmp.all").All(i => i.Status == CleanupRunStatus.NothingToDo), "先の規則が消して無いものを失敗にした");
        Assert.AreEqual((4L, 26L), (result.FilesDeleted, result.BytesFreed));
        Assert.IsFalse(File.Exists(_h.Full(@"Local\Test\Temp\ACC.log")));
    }

    [TestMethod]
    public async Task 先の規則が消せなかった失敗を後の規則でもう一度数えない()
    {
        var (scan, snapshot) = await ScanAsync(All(), Nero());
        var locked = _h.Full(@"Local\Test\Temp\Nero\n1.dat");

        CleanupRunResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await RunAsync(scan, snapshot, "tmp.all", "tmp.nero");
        }

        Assert.IsTrue(File.Exists(locked), "使用中のファイルを消した");
        Assert.AreEqual(1, result.Items.Single(i => i.Rule.Id == "tmp.all").Blocked);
        var nero = result.Items.Single(i => i.Rule.Id == "tmp.nero");
        Assert.AreEqual((0L, 0L, CleanupRunStatus.NothingToDo), (nero.Blocked, nero.Bytes, nero.Status), "先の規則が報告した失敗を重ねて数えた");
        Assert.AreEqual(3, result.FilesDeleted);
    }

    private static (long Files, long Bytes) Pair(CleanupScanSnapshot snapshot, params string[] ids)
    {
        var totals = snapshot.Measure(ids);
        return (totals.Files, totals.Bytes);
    }
}
