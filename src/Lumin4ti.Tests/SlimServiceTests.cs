using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.Core.Services.Windows.Actions;
using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.Core.Services.Windows.Cleanup.Slim;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class SlimServiceTests
{
    private const string AnalysisOutput = """
        Deployment Image Servicing and Management tool
        Component Store (WinSxS) information:

        Windows Explorer Reported Size of Component Store : 10.00 GB
        Actual Size of Component Store : 9.50 GB
            Shared with Windows : 6.00 GB
            Backups and Disabled Features : 2.00 GB
            Cache and Temporary Data : 512.00 MB

        Date of Last Cleanup : 2026-11-01 10:00:00

        Number of Reclaimable Packages : 3
        Component Store Cleanup Recommended : Yes

        The operation completed successfully.
        """;

    private const string CapabilitiesOutput = """
        Capability Identity : Language.Basic~~~ja-JP~0.0.1.0
        State : Installed

        Capability Identity : Language.Basic~~~ko-KR~0.0.1.0
        State : Not Present

        Capability Identity : Language.Basic~~~zh-CN~0.0.1.0
        State : Installed

        Capability Identity : Language.Basic~~~..\evil~0.0.1.0
        State : Installed

        The operation completed successfully.
        """;

    private string _root = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "Lumin4tiSlimTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ───────────── 境界の偽物 ─────────────

    private static CommandExecutionResult Ok(string output) => new(true, "dism", 0, output, string.Empty);

    private static CommandExecutionResult Exit(int code, string output = "") => new(code == 0, "dism", code, output, "error text");

    private sealed class ScriptedExecutor : ICommandExecutor
    {
        public List<string> Calls { get; } = [];

        public Func<string, string, CommandExecutionResult?>? Override { get; set; }

        public bool NetStartSucceeds { get; set; } = true;

        public Task<CommandExecutionResult> RunAsync(string fileName, string arguments, CancellationToken ct = default, IProgress<string>? onOutputLine = null, TimeSpan? timeout = null)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add($"{fileName} {arguments}");
            if (Override?.Invoke(fileName, arguments) is { } custom) return Task.FromResult(custom);
            if (fileName == "net.exe") return Task.FromResult(NetStartSucceeds ? Ok(string.Empty) : Exit(2));
            var result = arguments switch
            {
                _ when arguments.Contains("AnalyzeComponentStore") => Ok(AnalysisOutput),
                _ when arguments.Contains("Get-Capabilities") => Ok(CapabilitiesOutput),
                _ when arguments.Contains("StartComponentCleanup") => Ok("The operation completed successfully."),
                _ when arguments.Contains("Remove-Capability") => Ok("The operation completed successfully."),
                _ => Exit(1),
            };
            return Task.FromResult(result);
        }

        public int CountOf(string text) => Calls.Count(c => c.Contains(text, StringComparison.Ordinal));
    }

    private sealed class FakeRegistrations(HashSet<string> paths) : IInstallerRegistrationSource
    {
        public InstallerRegistrationSnapshot Read(CancellationToken ct) => new(paths, null);
    }

    private sealed class FakeLanguages : IUserLanguageSource
    {
        public UserLanguageSnapshot Snapshot { get; set; } = new([], [], null);

        public UserLanguageSnapshot Read() => Snapshot;
    }

    private sealed class FakeProbe : IProtectedFileProbe
    {
        public Dictionary<string, FileProtectionState> States { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FileProtectionState Probe(NativeCleanupEntry file) =>
            States.GetValueOrDefault(Path.GetFileName(file.Path), FileProtectionState.WindowsResourceProtection);
    }

    private sealed class FakeRestorePoints : ISlimRestorePoints
    {
        public Queue<RestorePointResult> Results { get; } = new();

        public int Creates { get; private set; }

        public int Enables { get; private set; }

        public bool EnableSucceeds { get; set; } = true;

        public Task<RestorePointResult> CreateAsync(string description, CancellationToken ct)
        {
            Creates++;
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : Point(RestorePointOutcome.Created));
        }

        public Task<RestorePointProtectionResult> EnableProtectionAsync(CancellationToken ct)
        {
            Enables++;
            return Task.FromResult(new RestorePointProtectionResult(EnableSucceeds, "enable"));
        }
    }

    private static RestorePointResult Point(RestorePointOutcome outcome) =>
        new(outcome, outcome.ToString(), outcome is RestorePointOutcome.Created or RestorePointOutcome.ReusedExisting
            ? new RestorePointInfo(1, DateTimeOffset.UtcNow, "point")
            : null);

    private sealed class FakeSuspender(ScriptedExecutor executor) : ISlimServiceSuspender
    {
        public List<string[]> Requests { get; } = [];

        public string[] FailToStop { get; set; } = [];

        public WindowsServiceState ResumeState { get; set; } = WindowsServiceState.Running;

        public Action? AfterSuspend { get; set; }

        public Task<ServiceSuspension> SuspendAsync(IReadOnlyList<string> serviceNames, IProgress<string>? progress, CancellationToken ct)
        {
            Requests.Add([.. serviceNames]);
            AfterSuspend?.Invoke();
            string[] stopped = FailToStop.Length == 0 ? [.. serviceNames] : [];
            return Task.FromResult(new ServiceSuspension(executor, stopped, FailToStop, _ => ResumeState, _ => Task.CompletedTask));
        }
    }

    private sealed class Fixture
    {
        public Fixture(string root)
        {
            Root = root;
            Paths = new SlimPaths(Path.Combine(root, "Windows"), Path.Combine(root, "Public"));
            Suspender = new FakeSuspender(Executor);
            Service = new SlimService(new SlimServiceParts(
                Paths,
                new InstallerOrphanScanner(Paths.InstallerDirectory, [new FakeRegistrations(Registered)]),
                new ComponentStore(Executor),
                new LanguageCapabilityService(Executor, Languages),
                new ProtectedSystemFiles(Probe),
                Restore,
                Suspender));
        }

        public string Root { get; }

        public SlimPaths Paths { get; }

        public ScriptedExecutor Executor { get; } = new();

        public HashSet<string> Registered { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FakeLanguages Languages { get; } = new();

        public FakeProbe Probe { get; } = new();

        public FakeRestorePoints Restore { get; } = new();

        public FakeSuspender Suspender { get; }

        public SlimService Service { get; }

        public string Write(string relative, string content = "data", int ageDays = 0)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            if (ageDays > 0)
            {
                File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
            }

            return path;
        }

        // 直前に作ったファイルが「走査後に作られた」と見なされないよう、作成時刻の余裕を過ぎてから走査する。
        public async Task<SlimScanResult> ScanAsync()
        {
            await Task.Delay(120);
            return await Service.ScanAsync(null, CancellationToken.None);
        }

        public Task<SlimRunResult> RunAsync(SlimScanResult scan, SlimRunRequest? request = null, params SlimItemId[] items) =>
            Service.RunAsync(request ?? new SlimRunRequest(scan, items, CreateRestorePoint: false), null, CancellationToken.None);
    }

    private Fixture NewFixture() => new(_root);

    // ───────────── ComponentStore ─────────────

    [TestMethod]
    public void 解析の出力から容量と推定の空きを読む()
    {
        var parsed = ComponentStore.ParseAnalysis(Ok(AnalysisOutput));

        Assert.IsNull(parsed.Error);
        var analysis = parsed.Analysis!;
        Assert.AreEqual(2L * 1024 * 1024 * 1024, analysis.BackupsAndDisabledFeaturesBytes);
        Assert.AreEqual(512L * 1024 * 1024, analysis.CacheAndTemporaryDataBytes);
        Assert.AreEqual(3, analysis.ReclaimablePackages);
        Assert.IsTrue(analysis.CleanupRecommended);
        Assert.AreEqual(analysis.BackupsAndDisabledFeaturesBytes + analysis.CacheAndTemporaryDataBytes, analysis.EstimatedReclaimableBytes);
    }

    [TestMethod]
    public void 解析が非0終了_成功の出力なし_項目欠落_容量不明はどれも解釈不能にする()
    {
        Assert.IsNotNull(ComponentStore.ParseAnalysis(Exit(1, AnalysisOutput)).Error);
        Assert.IsNotNull(ComponentStore.ParseAnalysis(Ok(AnalysisOutput.Replace("The operation completed successfully.", string.Empty))).Error);
        Assert.IsNotNull(ComponentStore.ParseAnalysis(Ok(AnalysisOutput.Replace("Cache and Temporary Data", "Cache"))).Error);
        Assert.IsNotNull(ComponentStore.ParseAnalysis(Ok(AnalysisOutput.Replace("512.00 MB", "512.00 XB"))).Error);
        Assert.IsNotNull(ComponentStore.ParseAnalysis(Ok(AnalysisOutput.Replace(" : 3", " : many"))).Error);
    }

    [TestMethod]
    public void 容量の表記を読み取り不正な値は拒否する()
    {
        Assert.IsTrue(ComponentStore.TryParseSize("7.12 GB", out var gb));
        Assert.AreEqual((long)Math.Round(7.12 * 1024 * 1024 * 1024), gb);
        Assert.IsTrue(ComponentStore.TryParseSize("0 bytes", out var zero));
        Assert.AreEqual(0, zero);
        Assert.IsTrue(ComponentStore.TryParseSize("1.50 KB", out var kb));
        Assert.AreEqual(1536, kb);
        Assert.IsFalse(ComponentStore.TryParseSize("1,5 GB", out _));
        Assert.IsFalse(ComponentStore.TryParseSize("-1 GB", out _));
        Assert.IsFalse(ComponentStore.TryParseSize("1 PB", out _));
        Assert.IsFalse(ComponentStore.TryParseSize("1 GB extra", out _));
    }

    [TestMethod]
    public async Task コンポーネントストアの整理は非0と解釈不能を失敗にし3010は成功と再起動にする()
    {
        var executor = new ScriptedExecutor();
        var store = new ComponentStore(executor);

        executor.Override = (_, _) => Exit(5);
        Assert.IsFalse((await store.CleanupAsync(false, null, CancellationToken.None)).Success);

        executor.Override = (_, _) => Ok("something else");
        Assert.IsFalse((await store.CleanupAsync(false, null, CancellationToken.None)).Success);

        executor.Override = (_, _) => Exit(3010, "The operation completed successfully.");
        var reboot = await store.CleanupAsync(true, null, CancellationToken.None);
        Assert.IsTrue(reboot.Success);
        Assert.IsTrue(reboot.RebootRequired);

        executor.Override = (_, _) => Exit(3010, "no marker");
        Assert.IsFalse((await store.CleanupAsync(true, null, CancellationToken.None)).Success);

        Assert.IsTrue(executor.Calls[^2].Contains("/ResetBase") && executor.Calls[^2].Contains("/English"));
        Assert.IsFalse(executor.Calls[0].Contains("/ResetBase"));
    }

    // ───────────── LanguageCapability ─────────────

    [TestMethod]
    public void 機能の一覧から対象言語のインストール済みだけを集め不正な名前は無視する()
    {
        var installed = LanguageCapabilityService.ParseInstalledIdentities(Ok(CapabilitiesOutput))!;

        CollectionAssert.AreEquivalent(new[] { "ja-JP", "zh-CN" }, installed.Keys.ToArray());
        Assert.AreEqual("Language.Basic~~~ja-JP~0.0.1.0", installed["ja-JP"]);
        Assert.IsNull(LanguageCapabilityService.ParseInstalledIdentities(Exit(1, CapabilitiesOutput)));
        Assert.IsNull(LanguageCapabilityService.ParseInstalledIdentities(Ok("The operation completed successfully.")));
        Assert.IsNull(LanguageCapabilityService.ParseInstalledIdentities(Ok(CapabilitiesOutput.Replace("The operation completed successfully.", string.Empty))));
    }

    [TestMethod]
    public void 言語タグとキーボード配列を削除対象の言語へ対応付ける()
    {
        var used = LanguageCapabilityService.UsedTags(new UserLanguageSnapshot(
            ["en-US", "zh-Hans-CN", "ja", "ko-KR-u-x", "jaxx", "zh-Hant-HK"],
            [0x0409, 0x0404, unchecked((int)0xE0010412)],
            null));

        CollectionAssert.AreEquivalent(new[] { "zh-CN", "ja-JP", "ko-KR", "zh-TW" }, used.ToArray());
    }

    [TestMethod]
    public async Task 未ロードの利用者ハイブなど読み切れないときは言語の機能を触らず利用不可にする()
    {
        var executor = new ScriptedExecutor();
        var languages = new FakeLanguages { Snapshot = new([], [], "未ロードの利用者ハイブがあります") };
        var service = new LanguageCapabilityService(executor, languages);

        var states = await service.ScanAsync(null, CancellationToken.None);

        Assert.IsTrue(states.All(s => s.Status == LanguageCapabilityStatus.Unavailable && s.UserSettingsUnreadable));
        Assert.AreEqual(0, executor.Calls.Count);
        var removal = await service.RemoveAsync("ja-JP", null, CancellationToken.None);
        Assert.IsFalse(removal.Success);
        Assert.AreEqual(0, executor.CountOf("Remove-Capability"));
    }

    [TestMethod]
    public async Task 使われていないインストール済みの言語だけが削除可能で_削除直前に使用状況を取り直す()
    {
        var executor = new ScriptedExecutor();
        var languages = new FakeLanguages { Snapshot = new(["zh-Hans-CN"], [], null) };
        var service = new LanguageCapabilityService(executor, languages);

        var states = (await service.ScanAsync(null, CancellationToken.None)).ToDictionary(s => s.Tag);
        Assert.AreEqual(LanguageCapabilityStatus.Removable, states["ja-JP"].Status);
        Assert.AreEqual(LanguageCapabilityStatus.NotInstalled, states["ko-KR"].Status);
        Assert.AreEqual(LanguageCapabilityStatus.InUse, states["zh-CN"].Status);
        Assert.AreEqual(LanguageCapabilityStatus.NotInstalled, states["zh-TW"].Status);

        languages.Snapshot = new(["ja-JP"], [], null);
        var blocked = await service.RemoveAsync("ja-JP", null, CancellationToken.None);
        Assert.IsFalse(blocked.Success);
        Assert.AreEqual(0, executor.CountOf("Remove-Capability"));

        languages.Snapshot = new([], [], null);
        var removed = await service.RemoveAsync("ja-JP", null, CancellationToken.None);
        Assert.IsTrue(removed.Success);
        Assert.AreEqual(1, executor.CountOf("/CapabilityName:Language.Basic~~~ja-JP~0.0.1.0"));
        Assert.IsFalse((await service.RemoveAsync("en-US", null, CancellationToken.None)).Success);
    }

    // ───────────── ProtectedSystemFiles ─────────────

    [TestMethod]
    public void 保護されていないファイルだけが候補で_TrustedInstaller所有と判定不能は対象外にする()
    {
        var fixture = NewFixture();
        fixture.Write(@"help\free.txt", "free");
        fixture.Write(@"help\sub\wrp.chm");
        fixture.Write(@"help\sub\owned.chm");
        fixture.Write(@"help\unknown.chm");
        fixture.Probe.States["free.txt"] = FileProtectionState.Unprotected;
        fixture.Probe.States["owned.chm"] = FileProtectionState.TrustedInstallerOwned;
        fixture.Probe.States["unknown.chm"] = FileProtectionState.Unknown;
        var before = File.GetLastWriteTimeUtc(Path.Combine(_root, @"help\free.txt"));

        var scan = new ProtectedSystemFiles(fixture.Probe).Scan([Path.Combine(_root, "help"), Path.Combine(_root, "missing")], CancellationToken.None);

        Assert.AreEqual(1, scan.Candidates.Count);
        Assert.AreEqual("free.txt", Path.GetFileName(scan.Candidates[0].Path));
        Assert.AreEqual(1, scan.ProtectedCount);
        Assert.AreEqual(1, scan.TrustedInstallerCount);
        Assert.AreEqual(1, scan.UnknownCount);
        Assert.AreEqual(1, scan.MissingRoots);
        Assert.AreEqual(before, File.GetLastWriteTimeUtc(Path.Combine(_root, @"help\free.txt")));
        Assert.IsTrue(File.Exists(Path.Combine(_root, @"help\sub\wrp.chm")));
    }

    [TestMethod]
    public void 実行直前に保護状態が変わった候補は削除せず残す()
    {
        var fixture = NewFixture();
        var kept = fixture.Write(@"help\kept.txt");
        var gone = fixture.Write(@"help\gone.txt");
        fixture.Probe.States["kept.txt"] = FileProtectionState.Unprotected;
        fixture.Probe.States["gone.txt"] = FileProtectionState.Unprotected;
        var files = new ProtectedSystemFiles(fixture.Probe);
        var scan = files.Scan([Path.Combine(_root, "help")], CancellationToken.None);
        fixture.Probe.States["kept.txt"] = FileProtectionState.WindowsResourceProtection;

        var outcome = files.Run(scan.Candidates, null, CancellationToken.None);

        Assert.IsTrue(File.Exists(kept));
        Assert.IsFalse(File.Exists(gone));
        Assert.AreEqual(1, outcome.DeletedFiles);
        Assert.AreEqual(1, outcome.Retained);
    }

    // ───────────── SlimService: 走査 ─────────────

    [TestMethod]
    public async Task 走査は15項目を返し何も変更せず既定では選択しない()
    {
        var fixture = NewFixture();
        var orphan = fixture.Write(@"Windows\Installer\orphan.msi", ageDays: 100);
        var cache = fixture.Write(@"Windows\SoftwareDistribution\Download\update.cab", "cab-data");
        var patch = fixture.Write(@"Windows\Installer\$PatchCache$\Managed\x.bin");
        var known = fixture.Write(@"Public\Music\Sample Music\Kalimba.mp3");
        var mine = fixture.Write(@"Public\Music\Sample Music\mine.mp3");
        fixture.Write(@"Windows\Help\a.chm");
        fixture.Languages.Snapshot = new(["zh-CN"], [], null);

        var scan = await fixture.ScanAsync();

        Assert.AreEqual(15, scan.Items.Count);
        Assert.AreEqual(15, SlimService.Definitions.Count);
        Assert.IsFalse(SlimService.Definitions.Any(d => d.DefaultSelected));
        Assert.AreEqual(SlimScanStatus.Available, scan.Item(SlimItemId.InstallerOrphans).Status);
        Assert.AreEqual(SlimScanStatus.Available, scan.Item(SlimItemId.UpdateCache).Status);
        Assert.AreEqual(8, scan.Item(SlimItemId.UpdateCache).Bytes);
        Assert.AreEqual(SlimScanStatus.Available, scan.Item(SlimItemId.PatchCache).Status);
        Assert.AreEqual(1, scan.Item(SlimItemId.SampleMusic).Files);
        Assert.AreEqual(SlimScanStatus.NothingToClean, scan.Item(SlimItemId.SamplePictures).Status);
        Assert.AreEqual(SlimReason.ProtectedByWindows, scan.Item(SlimItemId.Help).Reason);
        Assert.AreEqual(SlimScanStatus.NothingToClean, scan.Item(SlimItemId.DefaultWallpapers).Status);
        Assert.AreEqual(SlimScanStatus.Available, scan.Item(SlimItemId.UpdateRollback).Status);
        Assert.IsNull(scan.Item(SlimItemId.UpdateRollback).Bytes);
        Assert.IsTrue(scan.Item(SlimItemId.UpdateBackup).BytesIsEstimate);
        Assert.AreEqual(SlimScanStatus.Available, scan.Item(SlimItemId.LanguageJa).Status);
        Assert.AreEqual(SlimReason.LanguageInUse, scan.Item(SlimItemId.LanguageZhCn).Reason);
        Assert.AreEqual(SlimReason.NotPresent, scan.Item(SlimItemId.LanguageKo).Reason);
        Assert.IsTrue(new[] { orphan, cache, patch, known, mine }.All(File.Exists));
        Assert.AreEqual(0, fixture.Executor.CountOf("StartComponentCleanup") + fixture.Executor.CountOf("Remove-Capability"));
        Assert.AreEqual(0, fixture.Restore.Creates);
    }

    [TestMethod]
    public async Task DISM解析を解釈できなければ整理の2項目と言語は利用不可になる()
    {
        var fixture = NewFixture();
        fixture.Executor.Override = (_, args) => args.Contains("AnalyzeComponentStore") ? Exit(1) : null;

        var scan = await fixture.ScanAsync();

        Assert.AreEqual(SlimReason.ToolFailed, scan.Item(SlimItemId.UpdateRollback).Reason);
        Assert.AreEqual(SlimScanStatus.Unavailable, scan.Item(SlimItemId.UpdateBackup).Status);
    }

    [TestMethod]
    public async Task 登録の列挙失敗はInstaller残骸を利用不可にし_未ロードハイブは言語を利用不可にする()
    {
        var fixture = new Fixture(_root);
        fixture.Languages.Snapshot = new([], [], "未ロードの利用者ハイブがあります");
        var failing = new SlimService(new SlimServiceParts(
            fixture.Paths,
            new InstallerOrphanScanner(fixture.Paths.InstallerDirectory, [new FailingRegistrations()]),
            new ComponentStore(fixture.Executor),
            new LanguageCapabilityService(fixture.Executor, fixture.Languages),
            new ProtectedSystemFiles(fixture.Probe),
            fixture.Restore,
            fixture.Suspender));

        var scan = await failing.ScanAsync(null, CancellationToken.None);

        Assert.AreEqual(SlimScanStatus.Unavailable, scan.Item(SlimItemId.InstallerOrphans).Status);
        Assert.AreEqual(SlimReason.EnumerationFailed, scan.Item(SlimItemId.InstallerOrphans).Reason);
        Assert.AreEqual(SlimReason.UserHiveNotLoaded, scan.Item(SlimItemId.LanguageJa).Reason);
    }

    private sealed class FailingRegistrations : IInstallerRegistrationSource
    {
        public InstallerRegistrationSnapshot Read(CancellationToken ct) => InstallerRegistrationSnapshot.Failed("列挙失敗");
    }

    // ───────────── SlimService: 実行 ─────────────

    [TestMethod]
    public async Task 古い走査結果では何も実行しない()
    {
        var fixture = NewFixture();
        var cache = fixture.Write(@"Windows\SoftwareDistribution\Download\update.cab");
        var first = await fixture.ScanAsync();
        await fixture.ScanAsync();

        var result = await fixture.RunAsync(first, null, SlimItemId.UpdateCache);

        Assert.IsNotNull(result.Rejected);
        Assert.IsTrue(File.Exists(cache));
        Assert.AreEqual(0, fixture.Suspender.Requests.Count);
    }

    [TestMethod]
    public async Task 実行を始めると同じ走査結果での再実行は拒否される()
    {
        var fixture = NewFixture();
        fixture.Write(@"Windows\SoftwareDistribution\Download\update.cab");
        var scan = await fixture.ScanAsync();

        var first = await fixture.RunAsync(scan, null, SlimItemId.UpdateCache);
        var second = await fixture.RunAsync(scan, null, SlimItemId.UpdateCache);

        Assert.IsTrue(first.AllSucceeded);
        Assert.IsNotNull(second.Rejected);
    }

    [TestMethod]
    public async Task Updateキャッシュはサービスを止めて削除し元へ戻す()
    {
        var fixture = NewFixture();
        var cache = fixture.Write(@"Windows\SoftwareDistribution\Download\sub\update.cab", "12345");
        fixture.Suspender.ResumeState = WindowsServiceState.Stopped;
        var scan = await fixture.ScanAsync();

        var result = await fixture.RunAsync(scan, null, SlimItemId.UpdateCache);

        Assert.IsTrue(result.AllSucceeded, result.Items[0].Message);
        Assert.IsFalse(File.Exists(cache));
        CollectionAssert.AreEqual(new[] { "wuauserv", "bits" }, fixture.Suspender.Requests[0]);
        Assert.AreEqual(1, fixture.Executor.CountOf("net.exe start \"wuauserv\""));
        Assert.AreEqual(5, result.Items[0].FreedBytes);
    }

    [TestMethod]
    public async Task サービスを止められなければ何も削除せず失敗にする()
    {
        var fixture = NewFixture();
        var cache = fixture.Write(@"Windows\SoftwareDistribution\Download\update.cab");
        fixture.Suspender.FailToStop = ["bits"];
        var scan = await fixture.ScanAsync();

        var result = await fixture.RunAsync(scan, null, SlimItemId.UpdateCache);

        Assert.AreEqual(SlimItemStatus.Failed, result.Items[0].Status);
        Assert.IsTrue(File.Exists(cache));
        Assert.IsFalse(result.AllSucceeded);
    }

    [TestMethod]
    public async Task 削除に成功してもサービスを元へ戻せなければ失敗にする()
    {
        var fixture = NewFixture();
        var cache = fixture.Write(@"Windows\Installer\$PatchCache$\Managed\x.bin");
        fixture.Suspender.ResumeState = WindowsServiceState.NotInstalled;
        fixture.Executor.NetStartSucceeds = false;
        var scan = await fixture.ScanAsync();

        var result = await fixture.RunAsync(scan, null, SlimItemId.PatchCache);

        Assert.IsFalse(File.Exists(cache));
        Assert.AreEqual(SlimItemStatus.Failed, result.Items[0].Status);
        CollectionAssert.AreEqual(new[] { "msiserver" }, fixture.Suspender.Requests[0]);
    }

    [TestMethod]
    public async Task 取消で中断してもサービスを元へ戻せなければ復帰の失敗を結果に出す()
    {
        var fixture = NewFixture();
        var cache = fixture.Write(@"Windows\SoftwareDistribution\Download\update.cab");
        fixture.Suspender.ResumeState = WindowsServiceState.NotInstalled;
        fixture.Executor.NetStartSucceeds = false;
        var scan = await fixture.ScanAsync();
        using var cts = new CancellationTokenSource();
        fixture.Suspender.AfterSuspend = cts.Cancel;

        var result = await fixture.Service.RunAsync(new SlimRunRequest(scan, [SlimItemId.UpdateCache], CreateRestorePoint: false), null, cts.Token);

        var item = result.Items.Single();
        Assert.AreEqual(SlimItemStatus.Failed, item.Status);
        Assert.AreEqual("Cleanup.Msg.SlimServiceResumeFailed", CleanupMessage.KeyOf(item.Message));
        Assert.IsTrue(File.Exists(cache));
    }

    [TestMethod]
    public async Task 使用中のファイルが残れば一部成功として成功にしない()
    {
        var fixture = NewFixture();
        var locked = fixture.Write(@"Windows\SoftwareDistribution\Download\locked.cab");
        var free = fixture.Write(@"Windows\SoftwareDistribution\Download\free.cab");
        var scan = await fixture.ScanAsync();

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await fixture.RunAsync(scan, null, SlimItemId.UpdateCache);

            Assert.AreEqual(SlimItemStatus.Partial, result.Items[0].Status);
            Assert.IsFalse(result.AllSucceeded);
        }

        Assert.IsTrue(File.Exists(locked));
        Assert.IsFalse(File.Exists(free));
    }

    [TestMethod]
    public async Task サンプルは既知のファイル名だけ削除し利用者のファイルは残す()
    {
        var fixture = NewFixture();
        var known = fixture.Write(@"Public\Pictures\Sample Pictures\Koala.jpg");
        var mine = fixture.Write(@"Public\Pictures\Sample Pictures\holiday.jpg");
        var scan = await fixture.ScanAsync();

        var result = await fixture.RunAsync(scan, null, SlimItemId.SamplePictures);

        Assert.IsTrue(result.AllSucceeded);
        Assert.IsFalse(File.Exists(known));
        Assert.IsTrue(File.Exists(mine));
    }

    [TestMethod]
    public async Task 走査後に作られたファイルは残し_走査前のファイルだけ削除する()
    {
        var fixture = NewFixture();
        var before = fixture.Write(@"Windows\SoftwareDistribution\Download\before.cab");
        var scan = await fixture.ScanAsync();
        await Task.Delay(150);
        var after = fixture.Write(@"Windows\SoftwareDistribution\Download\after.cab");

        var result = await fixture.RunAsync(scan, null, SlimItemId.UpdateCache);

        Assert.IsFalse(File.Exists(before));
        Assert.IsTrue(File.Exists(after));
        Assert.AreEqual(SlimItemStatus.Succeeded, result.Items[0].Status);
    }

    [TestMethod]
    public async Task 更新のロールバックは二段階確認がなければ実行せず_確認後はバックアップと一度にまとめて実行する()
    {
        var fixture = NewFixture();
        var scan = await fixture.ScanAsync();

        var unconfirmed = await fixture.RunAsync(scan, null, SlimItemId.UpdateRollback);
        Assert.AreEqual(SlimItemStatus.Skipped, unconfirmed.Items[0].Status);
        Assert.AreEqual(0, fixture.Executor.CountOf("StartComponentCleanup"));
        Assert.AreEqual(SlimWarningLevel.Irreversible, SlimService.Definitions.Single(d => d.Id == SlimItemId.UpdateRollback).Warning);

        var scan2 = await fixture.ScanAsync();
        var confirmed = await fixture.RunAsync(
            scan2,
            new SlimRunRequest(scan2, [SlimItemId.UpdateRollback, SlimItemId.UpdateBackup], CreateRestorePoint: false, ConfirmedIrreversible: true));

        Assert.IsTrue(confirmed.AllSucceeded);
        Assert.AreEqual(1, fixture.Executor.CountOf("StartComponentCleanup"));
        Assert.AreEqual(1, fixture.Executor.CountOf("/ResetBase"));
    }

    [TestMethod]
    public async Task DISMが失敗すれば成功にせず_3010なら再起動要求を返す()
    {
        var fixture = NewFixture();
        var scan = await fixture.ScanAsync();
        fixture.Executor.Override = (_, args) => args.Contains("StartComponentCleanup") ? Exit(87, "error") : null;
        var failed = await fixture.RunAsync(scan, null, SlimItemId.UpdateBackup);
        Assert.AreEqual(SlimItemStatus.Failed, failed.Items[0].Status);
        Assert.IsFalse(failed.RebootRequired);

        var scan2 = await fixture.ScanAsync();
        fixture.Executor.Override = (_, args) => args.Contains("StartComponentCleanup") ? Exit(3010, "The operation completed successfully.") : null;
        var reboot = await fixture.RunAsync(scan2, null, SlimItemId.UpdateBackup);
        Assert.AreEqual(SlimItemStatus.Succeeded, reboot.Items[0].Status);
        Assert.IsTrue(reboot.RebootRequired);
    }

    [TestMethod]
    public async Task 言語の削除は直前に使用状況を取り直し使われていれば実行しない()
    {
        var fixture = NewFixture();
        var scan = await fixture.ScanAsync();
        fixture.Languages.Snapshot = new(["ja-JP"], [], null);

        var result = await fixture.RunAsync(scan, null, SlimItemId.LanguageJa);

        Assert.AreEqual(SlimItemStatus.Failed, result.Items[0].Status);
        Assert.AreEqual(0, fixture.Executor.CountOf("Remove-Capability"));
    }

    [TestMethod]
    public async Task 実行中のキャンセルは取消として返し以降の項目を実行しない()
    {
        var fixture = NewFixture();
        var cache = fixture.Write(@"Windows\SoftwareDistribution\Download\update.cab");
        var scan = await fixture.ScanAsync();
        fixture.Executor.Override = (_, args) => args.Contains("StartComponentCleanup") ? throw new OperationCanceledException() : null;

        var result = await fixture.RunAsync(scan, null, SlimItemId.UpdateCache, SlimItemId.UpdateBackup, SlimItemId.LanguageJa);

        Assert.AreEqual(SlimItemStatus.Succeeded, result.Items.Single(i => i.Id == SlimItemId.UpdateCache).Status);
        Assert.AreEqual(SlimItemStatus.Cancelled, result.Items.Single(i => i.Id == SlimItemId.UpdateBackup).Status);
        Assert.AreEqual(SlimItemStatus.Skipped, result.Items.Single(i => i.Id == SlimItemId.LanguageJa).Status);
        Assert.IsFalse(File.Exists(cache));
        Assert.IsFalse(result.AllSucceeded);
    }

    [TestMethod]
    public async Task 利用できない項目と選択なしは実行しない()
    {
        var fixture = NewFixture();
        var scan = await fixture.ScanAsync();

        var none = await fixture.RunAsync(scan, new SlimRunRequest(scan, [], CreateRestorePoint: false));
        Assert.IsNotNull(none.Rejected);

        var scan2 = await fixture.ScanAsync();
        var unavailable = await fixture.RunAsync(scan2, null, SlimItemId.Help);
        Assert.AreEqual(SlimItemStatus.Skipped, unavailable.Items[0].Status);
        Assert.IsNull(unavailable.Rejected);
    }

    // ───────────── SlimService: 復元ポイント ─────────────

    [TestMethod]
    public async Task 復元ポイントを利用できず判断も得られなければ対象項目を実行しない()
    {
        var fixture = NewFixture();
        var installer = fixture.Write(@"Windows\Installer\orphan.msi", ageDays: 100);
        fixture.Restore.Results.Enqueue(Point(RestorePointOutcome.Failed));
        var scan = await fixture.ScanAsync();

        var result = await fixture.Service.RunAsync(new SlimRunRequest(scan, [SlimItemId.InstallerOrphans]), null, CancellationToken.None);

        Assert.AreEqual(SlimItemStatus.Skipped, result.Items[0].Status);
        Assert.AreEqual(RestorePointOutcome.Failed, result.RestorePoint!.Outcome);
        Assert.IsTrue(File.Exists(installer));
    }

    [TestMethod]
    public async Task 復元ポイントなしで続行を選べば実行し_中止を選べば実行しない()
    {
        var fixture = NewFixture();
        var installer = fixture.Write(@"Windows\Installer\orphan.msi", ageDays: 100);
        fixture.Restore.Results.Enqueue(Point(RestorePointOutcome.Failed));
        var scan = await fixture.ScanAsync();

        var abort = await fixture.Service.RunAsync(
            new SlimRunRequest(scan, [SlimItemId.InstallerOrphans], OnRestorePointUnavailable: (_, _) => Task.FromResult(RestorePointDecision.Abort)),
            null,
            CancellationToken.None);
        Assert.AreEqual(SlimItemStatus.Skipped, abort.Items[0].Status);
        Assert.IsTrue(File.Exists(installer));

        var scan2 = await fixture.ScanAsync();
        fixture.Restore.Results.Enqueue(Point(RestorePointOutcome.Failed));
        var proceed = await fixture.Service.RunAsync(
            new SlimRunRequest(scan2, [SlimItemId.InstallerOrphans], OnRestorePointUnavailable: (_, _) => Task.FromResult(RestorePointDecision.ContinueWithout)),
            null,
            CancellationToken.None);
        Assert.AreEqual(SlimItemStatus.Succeeded, proceed.Items[0].Status);
        Assert.IsFalse(File.Exists(installer));
    }

    [TestMethod]
    public async Task システムの保護を有効化して作り直せる_有効化できなければ中止する()
    {
        var fixture = NewFixture();
        var installer = fixture.Write(@"Windows\Installer\orphan.msi", ageDays: 100);
        fixture.Restore.Results.Enqueue(Point(RestorePointOutcome.ProtectionDisabled));
        var scan = await fixture.ScanAsync();
        var asked = 0;

        var result = await fixture.Service.RunAsync(
            new SlimRunRequest(scan, [SlimItemId.InstallerOrphans], OnRestorePointUnavailable: (_, _) =>
            {
                asked++;
                return Task.FromResult(RestorePointDecision.EnableProtectionAndRetry);
            }),
            null,
            CancellationToken.None);

        Assert.AreEqual(1, asked);
        Assert.AreEqual(1, fixture.Restore.Enables);
        Assert.AreEqual(2, fixture.Restore.Creates);
        Assert.AreEqual(RestorePointOutcome.Created, result.RestorePoint!.Outcome);
        Assert.IsFalse(File.Exists(installer));

        var failing = NewFixtureIn("second");
        var kept = failing.Write(@"Windows\Installer\orphan.msi", ageDays: 100);
        failing.Restore.Results.Enqueue(Point(RestorePointOutcome.ProtectionDisabled));
        failing.Restore.EnableSucceeds = false;
        var scan2 = await failing.ScanAsync();
        var aborted = await failing.Service.RunAsync(
            new SlimRunRequest(scan2, [SlimItemId.InstallerOrphans], OnRestorePointUnavailable: (_, _) => Task.FromResult(RestorePointDecision.EnableProtectionAndRetry)),
            null,
            CancellationToken.None);
        Assert.AreEqual(SlimItemStatus.Skipped, aborted.Items[0].Status);
        Assert.IsTrue(File.Exists(kept));
    }

    [TestMethod]
    public async Task 復元ポイントの作成が取り消されたら対象項目を実行しない()
    {
        var fixture = NewFixture();
        var installer = fixture.Write(@"Windows\Installer\orphan.msi", ageDays: 100);
        fixture.Restore.Results.Enqueue(Point(RestorePointOutcome.Cancelled));
        var scan = await fixture.ScanAsync();

        var result = await fixture.Service.RunAsync(
            new SlimRunRequest(scan, [SlimItemId.InstallerOrphans], OnRestorePointUnavailable: (_, _) => Task.FromResult(RestorePointDecision.ContinueWithout)),
            null,
            CancellationToken.None);

        Assert.AreEqual(SlimItemStatus.Skipped, result.Items[0].Status);
        Assert.IsTrue(File.Exists(installer));
    }

    [TestMethod]
    public async Task 復元ポイントが不要な項目だけなら作成しない()
    {
        var fixture = NewFixture();
        fixture.Write(@"Windows\SoftwareDistribution\Download\update.cab");
        var scan = await fixture.ScanAsync();

        var result = await fixture.Service.RunAsync(new SlimRunRequest(scan, [SlimItemId.UpdateCache]), null, CancellationToken.None);

        Assert.IsTrue(result.AllSucceeded);
        Assert.AreEqual(0, fixture.Restore.Creates);
    }

    private Fixture NewFixtureIn(string name) => new(Path.Combine(_root, name));
}
