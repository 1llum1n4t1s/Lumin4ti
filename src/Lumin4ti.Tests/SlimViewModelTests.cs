using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.Core.Services.Windows.Actions;
using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.Core.Services.Windows.Cleanup.Slim;
using Lumin4ti.UI.Services;
using Lumin4ti.UI.ViewModels;

namespace Lumin4ti.Tests;

/// <summary>スリム化タブの ViewModel。境界 (DISM・復元ポイント・登録情報) は偽物で、実機は変更しない。</summary>
[TestClass]
public sealed class SlimViewModelTests
{
    private const string AnalysisOutput = """
        Windows Explorer Reported Size of Component Store : 10.00 GB
        Actual Size of Component Store : 9.50 GB
            Shared with Windows : 6.00 GB
            Backups and Disabled Features : 2.00 GB
            Cache and Temporary Data : 512.00 MB

        Number of Reclaimable Packages : 3
        Component Store Cleanup Recommended : Yes

        The operation completed successfully.
        """;

    private string _root = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "Lumin4tiSlimVmTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Executor : ICommandExecutor
    {
        public List<string> Calls { get; } = [];

        public Action<string>? OnCall { get; set; }

        public Task<CommandExecutionResult> RunAsync(string fileName, string arguments, CancellationToken ct = default, IProgress<string>? onOutputLine = null, TimeSpan? timeout = null)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add($"{fileName} {arguments}");
            OnCall?.Invoke(arguments);
            var output = arguments.Contains("AnalyzeComponentStore", StringComparison.Ordinal) ? AnalysisOutput : "The operation completed successfully.";
            var ok = !arguments.Contains("Get-Capabilities", StringComparison.Ordinal);
            return Task.FromResult(new CommandExecutionResult(ok, "dism", ok ? 0 : 1, output, string.Empty));
        }

        public int CountOf(string text) => Calls.Count(c => c.Contains(text, StringComparison.Ordinal));
    }

    private sealed class NoRegistrations : IInstallerRegistrationSource
    {
        public InstallerRegistrationSnapshot Read(CancellationToken ct) => new(new HashSet<string>(), null);
    }

    private sealed class NoLanguages : IUserLanguageSource
    {
        public UserLanguageSnapshot Read() => new([], [], null);
    }

    private sealed class ProtectedProbe : IProtectedFileProbe
    {
        public FileProtectionState Probe(NativeCleanupEntry file) => FileProtectionState.WindowsResourceProtection;
    }

    private sealed class FakeRestorePoints : ISlimRestorePoints
    {
        public Queue<RestorePointResult> Results { get; } = new();

        public Task<RestorePointResult> CreateAsync(string description, CancellationToken ct) =>
            Task.FromResult(Results.Count > 0
                ? Results.Dequeue()
                : new RestorePointResult(RestorePointOutcome.Created, "ok", new RestorePointInfo(1, DateTimeOffset.UtcNow, "p")));

        public Task<RestorePointProtectionResult> EnableProtectionAsync(CancellationToken ct) =>
            Task.FromResult(new RestorePointProtectionResult(true, "enabled"));
    }

    private sealed class NoServices : ISlimServiceSuspender
    {
        public Task<ServiceSuspension> SuspendAsync(IReadOnlyList<string> serviceNames, IProgress<string>? progress, CancellationToken ct) =>
            throw new InvalidOperationException("サービスの停止は想定しない");
    }

    private sealed class Prompt(RestorePointDecision decision) : ISlimRestorePointPrompt
    {
        public int Asked { get; private set; }

        public string? Body { get; private set; }

        public Task<RestorePointDecision> AskAsync(string body, bool canEnableProtection, CancellationToken ct)
        {
            Asked++;
            Body = body;
            return Task.FromResult(decision);
        }
    }

    private sealed class Idle : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class Fixture
    {
        public Fixture(string root)
        {
            Directory.CreateDirectory(root);
            var paths = new SlimPaths(Path.Combine(root, "Windows"), Path.Combine(root, "Public"));
            Service = new SlimService(new SlimServiceParts(
                paths,
                new InstallerOrphanScanner(paths.InstallerDirectory, [new NoRegistrations()]),
                new ComponentStore(Executor),
                new LanguageCapabilityService(Executor, new NoLanguages()),
                new ProtectedSystemFiles(new ProtectedProbe()),
                Restore,
                new NoServices()));
            var settingsService = new SettingsService(root, Path.Combine(root, "settings.json"));
            ViewModel = new SlimViewModel(Service, new CleanupSettingsStore(settingsService), Coordinator);
        }

        public Executor Executor { get; } = new();

        public FakeRestorePoints Restore { get; } = new();

        public MaintenanceOperationCoordinator Coordinator { get; } = new(() => new Idle());

        public SlimService Service { get; }

        public SlimViewModel ViewModel { get; }

        public SlimItemRowViewModel Row(SlimItemId id) => ViewModel.Items.First(r => r.Definition.Id == id);

        public async Task ScanAsync() => await ViewModel.ScanCommand.ExecuteAsync(null);

        public async Task SelectRollbackAndRunAsync(bool acknowledge)
        {
            await ScanAsync();
            Row(SlimItemId.UpdateRollback).IsSelected = true;
            ViewModel.RequestRunCommand.Execute(null);
            ViewModel.Acknowledged = acknowledge;
            await ViewModel.ConfirmRunCommand.ExecuteAsync(null);
        }
    }

    [TestMethod]
    public async Task Scan_ShowsNoOverlay_AndNothingIsSelected()
    {
        var f = new Fixture(_root);
        var processingDuringScan = new List<bool>();
        f.Executor.OnCall = _ => processingDuringScan.Add(f.Coordinator.ProcessingState.IsProcessing);

        await f.ScanAsync();

        Assert.IsTrue(processingDuringScan.Count > 0);
        Assert.IsTrue(processingDuringScan.All(p => !p), "走査は処理中オーバーレイを出さない");
        Assert.IsFalse(f.ViewModel.IsRunning);
        Assert.IsTrue(f.ViewModel.HasScan);
        Assert.AreEqual(0, f.ViewModel.SelectedCount);
        Assert.IsTrue(f.Row(SlimItemId.UpdateRollback).CanSelect);
        Assert.IsFalse(f.Row(SlimItemId.UpdateRollback).IsSelected);
        Assert.IsTrue(f.Row(SlimItemId.UpdateRollback).HasWarning);

        // 実行できない項目 (Windows 保護で対象なし) は選べず、理由が出る。
        var help = f.Row(SlimItemId.Help);
        help.IsSelected = true;
        Assert.IsFalse(help.IsSelected);
        Assert.IsTrue(help.HasReason);
    }

    [TestMethod]
    public async Task LocaleChange_KeepsScanSelectionAndConfirmation()
    {
        var f = new Fixture(_root);
        await f.ScanAsync();
        f.Row(SlimItemId.UpdateRollback).IsSelected = true;
        f.ViewModel.RequestRunCommand.Execute(null);
        var status = f.ViewModel.StatusText;
        var confirm = f.ViewModel.ConfirmText;
        var helpReason = f.Row(SlimItemId.Help).ReasonText;

        f.ViewModel.OnLocaleChanged();

        Assert.IsTrue(f.ViewModel.HasScan);
        Assert.IsTrue(f.ViewModel.IsConfirming);
        Assert.AreEqual(1, f.ViewModel.SelectedCount);
        Assert.IsTrue(f.Row(SlimItemId.UpdateRollback).IsSelected);
        Assert.AreEqual(status, f.ViewModel.StatusText);
        Assert.AreEqual(confirm, f.ViewModel.ConfirmText);
        Assert.AreEqual(helpReason, f.Row(SlimItemId.Help).ReasonText);
    }

    [TestMethod]
    public async Task LocaleChange_KeepsRunResult_AndResultMessageIsTranslatedText()
    {
        var f = new Fixture(_root);
        await f.SelectRollbackAndRunAsync(acknowledge: true);
        var row = f.Row(SlimItemId.UpdateRollback);
        var message = row.ResultMessage;
        var summary = f.ViewModel.ResultSummary;
        Assert.IsTrue(row.HasResult);
        Assert.IsFalse(message.Contains("Cleanup.Msg.", StringComparison.Ordinal), "Core の文言の符号を画面へ出さない");
        StringAssert.Contains(message, "コンポーネントストアの整理");

        f.ViewModel.OnLocaleChanged();

        Assert.IsTrue(row.HasResult);
        Assert.AreEqual(message, row.ResultMessage);
        Assert.AreEqual(summary, f.ViewModel.ResultSummary);
        Assert.AreEqual(summary, f.ViewModel.StatusText);
    }

    [TestMethod]
    public async Task Run_NeedsSecondConfirmation_ForIrreversibleItem()
    {
        var f = new Fixture(_root);
        await f.ScanAsync();
        f.Row(SlimItemId.UpdateRollback).IsSelected = true;

        f.ViewModel.RequestRunCommand.Execute(null);
        Assert.IsTrue(f.ViewModel.IsConfirming);
        Assert.IsTrue(f.ViewModel.RequiresAcknowledgement);
        Assert.IsFalse(f.ViewModel.CanConfirm);

        await f.ViewModel.ConfirmRunCommand.ExecuteAsync(null);
        Assert.AreEqual(0, f.Executor.CountOf("ResetBase"), "二段階目の確認なしでは実行しない");

        f.ViewModel.Acknowledged = true;
        var processingDuringRun = new List<(bool Processing, bool CanCancel)>();
        f.Executor.OnCall = args =>
        {
            if (args.Contains("ResetBase", StringComparison.Ordinal))
            {
                processingDuringRun.Add((f.Coordinator.ProcessingState.IsProcessing, f.ViewModel.CanCancel));
            }
        };
        await f.ViewModel.ConfirmRunCommand.ExecuteAsync(null);

        Assert.AreEqual(1, f.Executor.CountOf("ResetBase"));
        Assert.AreEqual(1, processingDuringRun.Count);
        Assert.IsTrue(processingDuringRun[0].Processing, "実行は処理中オーバーレイを出す");
        Assert.IsTrue(processingDuringRun[0].CanCancel);
        Assert.IsTrue(f.Row(SlimItemId.UpdateRollback).HasResult);
        Assert.IsFalse(f.ViewModel.HasScan, "実行後は走査結果が失効し、再走査が要る");
        Assert.AreEqual(0, f.Coordinator.ActiveCount);
    }

    [TestMethod]
    public async Task Run_WhenRestorePointFails_AbortsWithoutPrompt_AndContinuesOnDecision()
    {
        var failed = new RestorePointResult(RestorePointOutcome.Failed, "boom", null);

        var aborted = new Fixture(_root);
        aborted.Restore.Results.Enqueue(failed);
        await aborted.SelectRollbackAndRunAsync(acknowledge: true);
        Assert.AreEqual(0, aborted.Executor.CountOf("ResetBase"), "窓口が無ければ安全側の中止");
        Assert.AreEqual("スキップ", aborted.Row(SlimItemId.UpdateRollback).ResultText);

        var asked = new Fixture(Path.Combine(_root, "second"));
        asked.Restore.Results.Enqueue(failed);
        var prompt = new Prompt(RestorePointDecision.ContinueWithout);
        asked.ViewModel.RestorePointPrompt = prompt;
        await asked.SelectRollbackAndRunAsync(acknowledge: true);
        Assert.AreEqual(1, prompt.Asked);
        StringAssert.Contains(prompt.Body, "boom");
        Assert.AreEqual(1, asked.Executor.CountOf("ResetBase"));
    }

    [TestMethod]
    public async Task Cancel_DuringRun_ReleasesLeaseOnlyAfterServiceFinishes()
    {
        var f = new Fixture(_root);
        await f.ScanAsync();
        f.Row(SlimItemId.UpdateRollback).IsSelected = true;
        f.ViewModel.RequestRunCommand.Execute(null);
        f.ViewModel.Acknowledged = true;
        var activeAtCancel = -1;
        f.Executor.OnCall = args =>
        {
            if (args.Contains("ResetBase", StringComparison.Ordinal))
            {
                f.ViewModel.CancelCommand.Execute(null);
                activeAtCancel = f.Coordinator.ActiveCount;
            }
        };

        await f.ViewModel.ConfirmRunCommand.ExecuteAsync(null);

        Assert.AreEqual(1, activeAtCancel, "中断を要求しても、補償が終わるまで lease は残る");
        Assert.IsFalse(f.ViewModel.IsRunning);
        Assert.IsFalse(f.ViewModel.CanCancel);
        Assert.AreEqual(0, f.Coordinator.ActiveCount);
    }

    [TestMethod]
    public async Task Scan_WhileAnotherOperationRuns_ReportsBusyAndKeepsState()
    {
        var f = new Fixture(_root);
        await f.ScanAsync();
        f.Row(SlimItemId.UpdateRollback).IsSelected = true;
        Assert.IsTrue(f.Coordinator.TryBegin(out var lease));
        using (lease)
        {
            await f.ScanAsync();
        }

        Assert.IsTrue(f.ViewModel.HasScan);
        Assert.AreEqual(1, f.ViewModel.SelectedCount);
        Assert.AreEqual(CleanupText.Get("Status.Busy"), f.ViewModel.StatusText);
    }
}
