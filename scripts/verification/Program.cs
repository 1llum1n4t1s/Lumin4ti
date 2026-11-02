using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.Core.Services.Windows.Actions;
using Lumin4ti.UI.Services;
using Lumin4ti.UI.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SuperLightLogger;
using Avalonia;
using Avalonia.Controls;
using Lumin4ti.UI;

namespace Lumin4ti.Verification;

internal static class Program
{
    private static readonly List<object> Cases = [];
    private static int _failures;
    private static string _repo = "";
    private static bool _simulateMissingDownloadCancellation;
    private static readonly MemoryLogProvider Logs = new();
    private static void Require(bool condition, string observation)
    {
        if (!condition) throw new InvalidOperationException(observation);
    }
    private static async Task Run(string name, Func<Task<string>> workflow)
    {
        try
        {
            // 製品側のキャンセル伝播が壊れても、mock の待機を有限にして成果物へ失敗を残す。
            var evidence = await workflow().WaitAsync(TimeSpan.FromSeconds(15));
            Cases.Add(new { name, passed = true, evidence });
            Console.WriteLine($"PASS {name}: {evidence}");
        }
        catch (Exception ex)
        {
            _failures++;
            Cases.Add(new { name, passed = false, error = ex.ToString() });
            Console.WriteLine($"FAIL {name}: {ex.Message}");
        }
    }
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--preview-state-diagnostics")
            return StateDiagnosticsPreview.Run();
        if (args.Length is < 2 or > 3 || args.Length == 3 && args[2] != "--simulate-missing-download-cancellation") return 2;
        _simulateMissingDownloadCancellation = args.Length == 3;
        _repo = Path.GetFullPath(args[0]);
        var artifact = Path.GetFullPath(args[1]);
        Require(artifact.StartsWith(Path.Combine(_repo, "local-release") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "artifact must stay within local-release");
        // 製品の Initialize/Program は呼ばず、利用者ログへの書込みを遮断する。
        LogManager.Configure(builder => builder.ClearProviders().AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        await Run("minimal-runtime-and-vm", () =>
        {
            var executor = new MockExecutor((_, _, _) => MockExecutor.Result(stdout: "{\"MemoryCompression\":true}"));
            var vm = Vm(new MmAgentFeatureToggle(executor, new MmAgentStateProvider(executor), "MemoryCompression", "canary", "検証", "検証"));
            vm.ApplyState(true);
            Require(vm.CanToggle, "minimal actual VM could not accept known state");
            return Task.FromResult("Actual VM constructed; no Avalonia lifetime, elevation or OS write invoked");
        });
        await Run("two-user-registry-explicit-shared-release", RegistryWorkflow);
        await Run("registry-reapply-failure-immediate-state-and-default", RegistryReapplyWorkflow);
        await Run("registry-memory-compensation-cancellation-and-defaults", RegistryPreparationWorkflow);
        await Run("shutdown-cancellation-finally-gate", ShutdownWorkflow);
        await Run("ntp-stop-failure-compensation", NtpWorkflow);
        await Run("service-stop-scm-and-dependent-safety", ServiceWorkflow);
        await Run("cleanup-cancel-resume-failure-visible", CleanupWorkflow);
        await Run("mmagent-external-change-and-parent-state", StateWorkflow);
        await Run("mmagent-inflight-reset-coalescing", MmAgentInflightWorkflow);
        await Run("device-enumeration-failure-retry", DeviceWorkflow);
        await Run("uwp-batch-repeat-current-targets", UwpWorkflow);
        await Run("uwp-pair-memory-compensation-and-external-change", UwpPairWorkflow);
        await Run("vbs-default-release-and-memory-compensation", VbsDefaultsWorkflow);
        await Run("toggle-persistent-backup-boundary", TogglePersistenceBoundaryWorkflow);
        await Run("mmagent-failure-diagnostic-and-retry", MmAgentDiagnosticWorkflow);
        await Run("parser-and-diagnostic", ParserWorkflow);
        await Run("migration-download-body-timeout-and-cancel", DownloadWorkflow);
        await Run("scheduled-checklist-duplicate-off-selection", ScheduledSelectionWorkflow);
        await Run("clear-ignored-update-save-failure-boundary", IgnoredUpdateWorkflow);
        await Run("migration-pending-json-legacy-compatibility", PendingJsonWorkflow);
        await Run("item-operation-failure-partial-busy-log-causes", OperationFailureLogWorkflow);
        await Run("actual-app-locale-version-vm-update-getters", LocaleWorkflow);
        await Run("retained-operation-and-device-results-locale", RetainedResultsLocaleWorkflow);
        await Run("item-state-null-exception-timeout-log-causes", StateFailureLogWorkflow);
        await Run("locale-and-accessibility-static-supplement", StaticWorkflow);
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        var assemblies = new[] { typeof(Program).Assembly, typeof(RegistryToggle).Assembly, typeof(CommandItemViewModel).Assembly }
            .Select(a => new { name = a.GetName().Name, version = a.GetName().Version?.ToString(), path = a.Location, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) });
        await File.WriteAllTextAsync(artifact, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, generatedUtc = DateTimeOffset.UtcNow,
            verificationKind = "OS boundary mock operation workflow verification; NOT real GUI E2E",
            safety = "No real registry/service/process execution/network/install/user-data deletion. Logger uses in-memory provider only. No temporary directory.",
            runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), repository = _repo, assemblies,
            success = _failures == 0, failures = _failures, cases = Cases,
        }, new JsonSerializerOptions { WriteIndented = true }));
        LogManager.Shutdown();
        Console.WriteLine($"Artifact: {artifact}");
        return _failures == 0 ? 0 : 1;
    }
    private static CommandItemViewModel Vm(IMaintenanceItem item) => new(item, _ => Task.CompletedTask,
        async (vm, on) => { var result = await ((IMaintenanceToggle)item).SetStateAsync(on); vm.ResultText = result.Detail; vm.ApplyResultStatus(result.Status); });

    private const string LogCanaryCause = "GOGO_LOG_CAUSE";
    private const string LogCanarySecret = "GOGO_LOG_SECRET_123";
    private const string LogCanaryDetail = LogCanaryCause + " token=" + LogCanarySecret;

    private static void RequireItemCauseLog(int start, IMaintenanceItem item, string cause, bool redacted = false)
    {
        var messages = Logs.Messages.Skip(start).ToArray();
        Require(messages.Any(message => message.Contains(item.Id, StringComparison.Ordinal) &&
            message.Contains(item.Label, StringComparison.Ordinal) &&
            message.Contains(cause, StringComparison.Ordinal)),
            $"item name/id/cause missing for {item.Id}: " + string.Join(" | ", messages));
        Require(messages.All(message => !message.Contains(LogCanarySecret, StringComparison.Ordinal)),
            "secret leaked into failure log for " + item.Id);
        if (redacted)
            Require(messages.Any(message => message.Contains("[redacted]", StringComparison.Ordinal)),
                "redaction marker missing for " + item.Id);
    }

    private static async Task<string> StateFailureLogWorkflow()
    {
        var diagnostic = new LogProbeToggle("state-diagnostic")
        {
            Diagnostic = new("Gogo.LogDiagnostic", LogCanaryDetail),
            Query = _ => Task.FromResult<bool?>(null),
        };
        var before = Logs.Messages.Count;
        Require(await CommandCategoryViewModel.QueryStateAsync<bool?>(diagnostic.GetStateAsync, diagnostic) is null,
            "diagnostic null state became known");
        RequireItemCauseLog(before, diagnostic, LogCanaryCause, redacted: true);

        var technical = new LogProbeToggle("state-technical")
        {
            StateFailureReason = LogCanaryDetail,
            Query = _ => Task.FromResult<bool?>(null),
        };
        before = Logs.Messages.Count;
        Require(await CommandCategoryViewModel.QueryStateAsync<bool?>(technical.GetStateAsync, technical) is null,
            "technical failure became known");
        RequireItemCauseLog(before, technical, LogCanaryCause, redacted: true);

        var unknown = new LogProbeToggle("state-null") { Query = _ => Task.FromResult<bool?>(null) };
        before = Logs.Messages.Count;
        Require(await CommandCategoryViewModel.QueryStateAsync<bool?>(unknown.GetStateAsync, unknown) is null,
            "unexplained null state became known");
        RequireItemCauseLog(before, unknown, "詳細な理由が返されませんでした");

        var choice = new LogProbeChoice("choice-state-null") { Query = _ => Task.FromResult<string?>(null) };
        before = Logs.Messages.Count;
        Require(await CommandCategoryViewModel.QueryStateAsync<string?>(choice.GetSelectedValueAsync, choice) is null,
            "null choice state became known");
        RequireItemCauseLog(before, choice, "詳細な理由が返されませんでした");

        var catalog = new MaintenanceActionCatalog(new MockExecutor((_, _, _) =>
            throw new InvalidOperationException("メタデータ検証でOSコマンドを実行しない")));
        foreach (var item in catalog.Items.Where(item => item is IMaintenanceToggle or IMaintenanceChoice))
        {
            before = Logs.Messages.Count;
            await CommandCategoryViewModel.QueryStateAsync<bool?>(_ => Task.FromResult<bool?>(null), item);
            RequireItemCauseLog(before, item, "詳細な理由が返されませんでした");
        }

        var exception = new LogProbeToggle("state-exception")
        {
            Query = _ => Task.FromException<bool?>(new IOException(LogCanaryDetail)),
        };
        before = Logs.Messages.Count;
        Require(await CommandCategoryViewModel.QueryStateAsync<bool?>(exception.GetStateAsync, exception) is null,
            "exception escaped state query boundary");
        RequireItemCauseLog(before, exception, LogCanaryCause, redacted: true);

        Require(HibernateToggle.IsHiberFilePresent(() => FileAttributes.System)
            && !HibernateToggle.IsHiberFilePresent(() => FileAttributes.Directory)
            && !HibernateToggle.IsHiberFilePresent(() => throw new FileNotFoundException())
            && !HibernateToggle.IsHiberFilePresent(() => throw new DirectoryNotFoundException()),
            "hiberfile present/missing classification changed");
        foreach (var readError in new Exception[] { new IOException(LogCanaryDetail), new UnauthorizedAccessException(LogCanaryDetail) })
        {
            var hibernate = new HibernateToggle(new MockExecutor((_, _, _) =>
                throw new InvalidOperationException("状態照会でpowercfgを実行しない")),
                () => HibernateToggle.IsHiberFilePresent(() => throw readError));
            before = Logs.Messages.Count;
            Require(await CommandCategoryViewModel.QueryStateAsync<bool?>(hibernate.GetStateAsync, hibernate) is null,
                "hiberfile read failure became optimization ON");
            RequireItemCauseLog(before, hibernate, LogCanaryCause, redacted: true);
        }

        var timeout = new LogProbeToggle("state-timeout")
        {
            Query = async ct => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return false; },
        };
        before = Logs.Messages.Count;
        Require(await CommandCategoryViewModel.QueryStateAsync<bool?>(timeout.GetStateAsync, timeout,
            timeout: TimeSpan.FromMilliseconds(30)) is null, "timeout state became known");
        RequireItemCauseLog(before, timeout, "タイムアウト");

        before = Logs.Messages.Count;
        var known = new LogProbeToggle("state-known");
        Require(await CommandCategoryViewModel.QueryStateAsync<bool?>(known.GetStateAsync, known) == false &&
            Logs.Messages.Count == before, "known OFF state logged a failure");
        return "Actual item-aware VM state query: null with/without diagnostic, null choice, IOException and bounded cancellation timeout retained item name/id/cause; token redacted; known OFF emitted no failure";
    }

    private static Task<string> OperationFailureLogWorkflow()
    {
        // 同期 fake の操作を同じ thread で完了し、選択状態反映だけ Dispatcher へ流す。
        foreach (var partial in new[] { false, true })
        {
            var result = partial ? MaintenanceActionResult.Partial(LogCanaryDetail) : MaintenanceActionResult.Fail(LogCanaryDetail);
            foreach (var kind in new[] { "toggle", "choice", "action", "batch-on", "batch-off" })
            {
                LogProbeItem item = kind switch
                {
                    "toggle" => new LogProbeToggle(kind + "-" + result.Status) { Result = result },
                    "choice" => new LogProbeChoice(kind + "-" + result.Status) { Result = result },
                    "batch-on" or "batch-off" => new LogProbeBatch(kind + "-" + result.Status) { Result = result },
                    _ => new LogProbeAction(kind + "-" + result.Status) { Result = result },
                };
                var coordinator = new MaintenanceOperationCoordinator(() => new NoopLease());
                var category = new CommandCategoryViewModel(new MaintenanceActionCatalog(new IMaintenanceItem[] { item }),
                    coordinator, CommandCategory.System, "検証", "検証");
                var vm = category.Items.Single();
                var before = Logs.Messages.Count;
                TriggerLogProbe(vm, kind);
                RequireItemCauseLog(before, item, LogCanaryCause, redacted: true);
                Require(item.OperationCalls == 1 && coordinator.ActiveCount == 0 &&
                    (partial ? vm.LastRunWarning : vm.LastRunFailed), "operation result or lease state lost for " + item.Id);
            }
        }

        foreach (var kind in new[] { "toggle", "choice", "action", "batch-on", "batch-off" })
        {
            LogProbeItem item = kind switch
            {
                "toggle" => new LogProbeToggle(kind + "-busy"),
                "choice" => new LogProbeChoice(kind + "-busy"),
                "batch-on" or "batch-off" => new LogProbeBatch(kind + "-busy"),
                _ => new LogProbeAction(kind + "-busy"),
            };
            var category = new CommandCategoryViewModel(new MaintenanceActionCatalog(new IMaintenanceItem[] { item }),
                new MaintenanceOperationCoordinator(() => null), CommandCategory.System, "検証", "検証");
            var before = Logs.Messages.Count;
            TriggerLogProbe(category.Items.Single(), kind);
            RequireItemCauseLog(before, item, "別のメンテナンス操作");
            Require(item.OperationCalls == 0, "busy operation reached OS fake for " + item.Id);
        }
        foreach (var enabled in new[] { false, true })
        {
            var item = new LogProbeBatch("batch-canceled") { Result = MaintenanceActionResult.Canceled() };
            var category = new CommandCategoryViewModel(new MaintenanceActionCatalog(new IMaintenanceItem[] { item }),
                new MaintenanceOperationCoordinator(() => new NoopLease()), CommandCategory.System, "検証", "検証");
            var before = Logs.Messages.Count;
            var vm = category.Items.Single();
            TriggerLogProbe(vm, enabled ? "batch-on" : "batch-off");
            var name = enabled ? App.Text("Button.BatchOn", "一括ON") : App.Text("Button.BatchOff", "一括OFF");
            Require(Logs.Messages.Skip(before).Any(line => line.Contains(item.Id, StringComparison.Ordinal) &&
                line.Contains(name, StringComparison.Ordinal)), "batch terminal log omitted ON/OFF operation");
            Require(vm.ResultText.Contains(name, StringComparison.Ordinal) && !vm.IsRunning &&
                vm.ApplyBatchOnCommand.CanExecute(null) && vm.ApplyBatchOffCommand.CanExecute(null),
                "batch canceled result or command reset lost");
        }
        var scheduledFailed = new LogProbeAction("scheduled-failed") { Result = MaintenanceActionResult.Fail(LogCanaryDetail) };
        var scheduledPartial = new LogProbeAction("scheduled-partial") { Result = MaintenanceActionResult.Partial(LogCanaryDetail) };
        var scheduledException = new LogProbeAction("scheduled-exception") { Failure = new IOException(LogCanaryDetail) };
        var scheduledLast = new LogProbeAction("scheduled-last-success");
        var scheduledStart = Logs.Messages.Count;
        Require(ScheduledTempCleanup.RunAsync([scheduledFailed, scheduledPartial, scheduledException, scheduledLast])
            .GetAwaiter().GetResult() == 1, "scheduled failure exit code lost");
        foreach (var action in new[] { scheduledFailed, scheduledPartial, scheduledException })
            RequireItemCauseLog(scheduledStart, action, LogCanaryCause, redacted: true);
        Require(scheduledLast.OperationCalls == 1, "scheduled sequence stopped after earlier failure");
        return Task.FromResult("Actual category/card VM toggle, choice and action: Failed/Partial retained name/id/sanitized cause; Busy rejected without fake calls; scheduled fake batch retained Failed/Partial/exception causes and continued through final success");
    }

    private static void TriggerLogProbe(CommandItemViewModel vm, string kind)
    {
        Task? action = null;
        switch (kind)
        {
            case "toggle": vm.ApplyState(false); vm.IsChecked = true; break;
            case "batch-on": action = vm.ApplyBatchOnCommand.ExecuteAsync(null); break;
            case "batch-off": action = vm.ApplyBatchOffCommand.ExecuteAsync(null); break;
            case "choice": vm.ApplySelectedValue("off"); vm.SelectedChoice = vm.ChoiceOptions.Single(option => option.Value == "on"); break;
            default: action = vm.RunCommand.ExecuteAsync(null); break;
        }
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (vm.IsRunning || action is { IsCompleted: false })
        {
            Require(deadline.Elapsed < TimeSpan.FromSeconds(3), "fake VM operation did not finish");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        action?.GetAwaiter().GetResult();
    }

    private abstract class LogProbeItem(string id) : IMaintenanceItem
    {
        public string Id => "gogo-log-" + id;
        public string Label => "ログ検証項目 " + id;
        public string Description => "メモリ内の検証専用項目";
        public CommandCategory Category => CommandCategory.System;
        public bool RequiresReboot => false;
        public string? StateFailureReason { get; init; }
        public MaintenanceActionResult Result { get; init; } = MaintenanceActionResult.Ok();
        public int OperationCalls { get; protected set; }
    }

    private sealed class LogProbeToggle(string id) : LogProbeItem(id), IMaintenanceToggle
    {
        public bool? DefaultState { get; init; }
        public MaintenanceStateDiagnostic? Diagnostic { get; init; }
        public MaintenanceStateDiagnostic? StateDiagnostic => Diagnostic;
        public bool? RequestedState { get; private set; }
        public Func<CancellationToken, Task<bool?>> Query { get; init; } = _ => Task.FromResult<bool?>(false);
        public Task<bool?> GetStateAsync(CancellationToken ct = default) => Query(ct);
        public Task<MaintenanceActionResult> SetStateAsync(bool on, CancellationToken ct = default)
        { OperationCalls++; RequestedState = on; return Task.FromResult(Result); }
    }

    private sealed class LogProbeBatch(string id) : LogProbeItem(id), IMaintenanceBatchSetting
    {
        public Task<MaintenanceActionResult> ApplyAsync(bool enabled, CancellationToken ct = default)
        { OperationCalls++; return Task.FromResult(Result); }
    }

    private sealed class LogProbeChoice(string id) : LogProbeItem(id), IMaintenanceChoice
    {
        public IReadOnlyList<MaintenanceChoiceOption> Options { get; } = [new("off", "OFF"), new("on", "ON")];
        public Func<CancellationToken, Task<string?>> Query { get; init; } = _ => Task.FromResult<string?>("off");
        public Task<string?> GetSelectedValueAsync(CancellationToken ct = default) => Query(ct);
        public Task<MaintenanceActionResult> SetSelectedValueAsync(string value, CancellationToken ct = default)
        { OperationCalls++; return Task.FromResult(Result); }
    }

    private sealed class LogProbeAction(string id) : LogProbeItem(id), IMaintenanceAction
    {
        public Exception? Failure { get; init; }
        public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default)
        {
            OperationCalls++;
            return Failure is null ? Task.FromResult(Result) : Task.FromException<MaintenanceActionResult>(Failure);
        }
    }
    private static RegistryToggle Toggle(string id, MemoryRegistry registry, RegistryToggleSpec[] specs) =>
        new(id, "検証", "検証", CommandCategory.System, specs, false, false, registry);
    private static async Task<string> RegistryReapplyWorkflow()
    {
        RegistryToggleSpec[] specs = [new(RegistryHive.CurrentUser, "GOGO_CANARY", "User", RegistryValueKind.DWord, 1)];
        var registry = new MemoryRegistry(new(), "user-a");
        registry.Write(specs[0], RegistryValueSnapshot.Dword(9));
        var toggle = Toggle("gogo-reapply", registry, specs);
        Require((await toggle.SetStateAsync(true)).Success, "initial ON failed");
        registry.Write(specs[0], RegistryValueSnapshot.Dword(5));
        Require(await toggle.GetStateAsync() == false, "external change not visible");
        registry.FailNextWrite = true;
        Require(!(await toggle.SetStateAsync(true)).Success && registry.Read(specs[0]).EquivalentTo(RegistryValueSnapshot.Dword(5)),
            "failed reapply lost immediate value5");
        Require((await toggle.SetStateAsync(false)).Success && !registry.Read(specs[0]).Exists,
            "OFF restored historical value instead of removing explicit setting");
        return "Actual ON -> external5 -> failed reON compensated immediate5; OFF removed explicit value instead of restoring historical9";
    }
    private static async Task<string> RegistryPreparationWorkflow()
    {
        RegistryToggleSpec[] specs =
        [
            new(RegistryHive.LocalMachine, "GOGO_CANARY", "Machine", RegistryValueKind.DWord, 1),
            new(RegistryHive.CurrentUser, "GOGO_CANARY", "User", RegistryValueKind.DWord, 1, 0),
        ];
        var registry = new MemoryRegistry(new(), "user-a");
        registry.Write(specs[0], RegistryValueSnapshot.Dword(7));
        registry.Write(specs[1], RegistryValueSnapshot.Dword(8));
        var toggle = Toggle("gogo-transaction", registry, specs);
        registry.FailAtWrite.Add(registry.WriteAttempts + 2);
        Require(!(await toggle.SetStateAsync(true)).Success &&
            registry.Read(specs[0]).EquivalentTo(RegistryValueSnapshot.Dword(7)) &&
            registry.Read(specs[1]).EquivalentTo(RegistryValueSnapshot.Dword(8)), "partial ON did not compensate all immediate values");
        using var cancellation = new CancellationTokenSource();
        registry.AfterWrite = () => { registry.AfterWrite = null; cancellation.Cancel(); };
        try { await toggle.SetStateAsync(true, cancellation.Token); throw new InvalidOperationException("cancellation ignored"); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Require(registry.Read(specs[0]).EquivalentTo(RegistryValueSnapshot.Dword(7)) &&
            registry.Read(specs[1]).EquivalentTo(RegistryValueSnapshot.Dword(8)), "cancellation returned before memory compensation");
        registry.FailAtWrite.Add(registry.WriteAttempts + 2);
        registry.FailAtWrite.Add(registry.WriteAttempts + 3);
        var failure = await toggle.SetStateAsync(true);
        Require(failure.Status == MaintenanceActionStatus.Failed && failure.Detail.Contains("補償"), "compensation failure hidden");
        Require((await toggle.SetStateAsync(false)).Success && !registry.Read(specs[0]).Exists &&
            registry.Read(specs[1]).EquivalentTo(RegistryValueSnapshot.Dword(0)), "OFF deletion/explicit default contract failed");
        return "Actual mixed HKLM/HKCU transaction: partial write and cancellation compensated in memory; compensation failure remained Failed; OFF removed absent-default value and wrote explicit default0";
    }
    private static async Task<string> RegistryWorkflow()
    {
        RegistryToggleSpec[] specs = [new(RegistryHive.LocalMachine, "RERE_CANARY", "Machine", RegistryValueKind.DWord, 1), new(RegistryHive.CurrentUser, "RERE_CANARY", "User", RegistryValueKind.DWord, 1)];
        var values = new Dictionary<string, RegistryValueSnapshot>();
        var ra = new MemoryRegistry(values, "user-a"); var rb = new MemoryRegistry(values, "user-b");
        ra.Write(specs[0], RegistryValueSnapshot.Dword(7)); ra.Write(specs[1], RegistryValueSnapshot.Dword(8)); rb.Write(specs[1], RegistryValueSnapshot.Dword(9));
        var ta = Toggle("canary-mixed", ra, specs); var tb = Toggle("canary-mixed", rb, specs);
        Require((await ta.SetStateAsync(true)).Success && (await tb.SetStateAsync(true)).Success, "two-user ON failed");
        Require(await ta.GetStateAsync() == true && await tb.GetStateAsync() == true, "two ON states not observed");
        Require((await ta.SetStateAsync(false)).Success, "A OFF failed");
        Require(!ra.Read(specs[0]).Exists && !ra.Read(specs[1]).Exists &&
            rb.Read(specs[1]).EquivalentTo(RegistryValueSnapshot.Dword(1)), "A OFF failed to release shared HKLM or changed B HKCU");
        Require(await tb.GetStateAsync() == false, "B state hid shared machine setting removal");
        Require((await tb.SetStateAsync(false)).Success && !rb.Read(specs[1]).Exists, "B OFF failed to remove B HKCU");
        Require((await ta.SetStateAsync(false)).Success && !ra.Read(specs[0]).Exists, "repeated OFF was not idempotent");
        return "Two actual RegistryToggle instances: A ON/B ON/A OFF removed shared HKLM and A HKCU immediately while preserving B HKCU; B observed OFF and explicitly cleared its HKCU; no ownership tracking";
    }
    private static async Task<string> ShutdownWorkflow()
    {
        var coordinator = new MaintenanceOperationCoordinator(() => new NoopLease());
        Require(coordinator.TryBegin(out var lease), "initial lease refused");
        coordinator.RequestCancellation();
        Require(lease!.Token.IsCancellationRequested && coordinator.IsStopping, "cancel/gate missing");
        var idle = coordinator.WaitForIdleAsync();
        Require(!idle.IsCompleted, "idle completed before finally");
        var finallyDone = false;
        try { await Task.Yield(); } finally { finallyDone = true; lease.Dispose(); }
        await idle;
        Require(finallyDone && !coordinator.TryBegin(out _), "shutdown reopened after old lease release");
        return "Cancellation observed; idle waited for finally; later lease refused even after old lease disposal";
    }


    private static async Task<string> NtpWorkflow()
    {
        var writes = 0; var queries = 0; var restarted = false;
        var executor = new MockExecutor((_, arguments, _) => { if (arguments.StartsWith("start")) restarted = true; return MockExecutor.Result(false, "RERE_CANARY stop rejected"); });
        var action = new NtpConfigAction(executor, () => true, () => writes++,
            _ => restarted ? WindowsServiceState.Running : ++queries == 1 ? WindowsServiceState.Running : queries == 2 ? WindowsServiceState.Transitioning : WindowsServiceState.Stopped,
            _ => Task.CompletedTask, _ => true);
        var result = await action.ExecuteAsync();
        Require(!result.Success && writes == 0 && executor.Calls.Any(c => c.Arguments.StartsWith("start")), "NTP stop failure failed to compensate safely");
        return "Rejected stop did not write configuration; compensation mock start called";
    }

    private static async Task<string> ServiceWorkflow()
    {
        var states = new Queue<WindowsServiceState?>([WindowsServiceState.Running, WindowsServiceState.Transitioning, WindowsServiceState.Running]);
        var executor = new MockExecutor((_, _, _) => MockExecutor.Result());
        var suspension = await WindowsServiceControl.SuspendAsync(executor, ["RERE_CANARY"], null, default, _ => states.Dequeue(), _ => Task.CompletedTask, _ => true);
        Require(suspension.FailedToStop.Contains("RERE_CANARY"), "transition state treated as stopped");
        Require(executor.Calls.Single().Arguments == "stop \"RERE_CANARY\"", "stop must not authorize dependent-service cascade");
        await suspension.ResumeAsync();
        foreach (var unknown in new WindowsServiceState?[] { null, WindowsServiceState.Transitioning })
        {
            var before = executor.Calls.Count;
            var refusal = await WindowsServiceControl.SuspendAsync(executor, ["RERE_CANARY"], null, default, _ => unknown, _ => Task.CompletedTask, _ => true);
            Require(refusal.FailedToStop.Count == 1 && executor.Calls.Count == before, "unknown initial SCM allowed stop");
        }
        var callsBefore = executor.Calls.Count;
        var dependent = await WindowsServiceControl.SuspendAsync(executor, ["RERE_CANARY"], null, default, _ => WindowsServiceState.Running, _ => Task.CompletedTask, _ => false);
        Require(dependent.FailedToStop.Count == 1 && executor.Calls.Count == callsBefore, "active dependents allowed stop");
        var unqueryable = await WindowsServiceControl.SuspendAsync(executor, ["RERE_CANARY"], null, default, _ => WindowsServiceState.Running, _ => Task.CompletedTask, _ => throw new IOException("RERE_CANARY SCM denied"));
        Require(unqueryable.FailedToStop.Count == 1 && executor.Calls.Count == callsBefore, "unqueryable dependents allowed stop");
        return "Plain net stop recorded; post-stop SCM transition rejected; unknown/transition initial state and dependent/unknown dependent inspection issued no command";
    }

    private static async Task<string> CleanupWorkflow()
    {
        using var cancellation = new CancellationTokenSource();
        var executor = new MockExecutor((_, _, _) => MockExecutor.Result(false, "RERE_CANARY cannot resume"));
        var action = new FileCleanupAction("canary-cleanup", "検証", "検証", () => [CleanupTarget.Contents("RERE_CANARY")], executor, ["RERE_CANARY"],
            suspendServices: (_, _, _, _) => { cancellation.Cancel(); return Task.FromResult(new ServiceSuspension(executor, ["RERE_CANARY"], [], _ => WindowsServiceState.Stopped, _ => Task.CompletedTask)); });
        var result = await action.ExecuteAsync(cancellation.Token);
        Require(!result.Success && result.Detail.Contains("RERE_CANARY") && result.Detail.Contains("キャンセル"), "cancel hid service resume failure");
        Require(executor.Calls.All(c => !c.Cancelable), "compensation received canceled token");
        return "Non-operational relative target; cancellation before cleanup engine plus failed mock resume retained service name in failed result";
    }
    private static async Task<string> StateWorkflow()
    {
        var external = true;
        var executor = new MockExecutor((_, _, _) => MockExecutor.Result(stdout: $"{{\"MemoryCompression\":{external.ToString().ToLowerInvariant()}}}"));
        var provider = new MmAgentStateProvider(executor);
        var toggle = new MmAgentFeatureToggle(executor, provider, "MemoryCompression", "canary", "検証", "検証");
        var parent = Vm(toggle); var child = Vm(toggle); parent.AttachChildren([child]);
        child.ApplyState(true); parent.ApplyState(await toggle.GetStateAsync()); Require(child.CanToggle, "known ON parent blocked child");
        parent.ResultText = "RERE_CANARY previous action result"; parent.ApplyState(null);
        Require(!child.CanToggle && parent.StateErrorText.Length > 0 && parent.ResultText.Contains("previous"), "unknown parent/result handling wrong");
        parent.ApplyState(true); Require(child.CanToggle && parent.StateErrorText.Length == 0, "known recovery retained error/disabled child");
        external = false; provider.ResetSnapshot(); parent.ApplyState(await toggle.GetStateAsync());
        Require(!parent.IsChecked && !child.CanToggle && executor.Calls.Count == 2, "reload retained stale MMAgent snapshot");
        return "Real VM parent unknown/recovery and separate state error observed; fresh external false propagated after ResetSnapshot";
    }
    private static async Task<string> DeviceWorkflow()
    {
        var service = new MockDevices();
        var vm = new DeviceCleanupViewModel(service, new MaintenanceOperationCoordinator(() => new NoopLease()), action => { action(); return Task.CompletedTask; });
        var logStart = Logs.Messages.Count;
        await vm.LoadDisconnectedDevicesAsync();
        Require(Logs.Messages.Skip(logStart).Any(message => message.Contains("disconnected-devices")
            && message.Contains("未接続デバイス") && message.Contains("RERE_CANARY enumeration failure")),
            "device loading failure lost name or cause");
        Require(!vm.IsEmpty && !vm.HasSuccessfulLoad && vm.CanRefresh && vm.StatusText.Length > 0, "failed enumeration looked like successful empty");
        service.FailEnumeration = false; await vm.LoadDisconnectedDevicesAsync();
        Require(vm.IsEmpty && vm.HasSuccessfulLoad && service.EnumerationCalls == 2, "retry empty success not observed");
        return "Enumeration exception displayed failure, not empty success; retry empty result succeeded; no removal";
    }
    private static async Task<string> MmAgentInflightWorkflow()
    {
        var pending = new TaskCompletionSource<CommandExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new AsyncMockExecutor(call => call == 1 ? pending.Task : Task.FromResult(MockExecutor.Result(stdout: "{\"MemoryCompression\":false}")));
        var provider = new MmAgentStateProvider(executor);
        var first = provider.GetAsync("MemoryCompression");
        provider.ResetSnapshot();
        var second = provider.GetAsync("MemoryCompression");
        provider.ResetSnapshot();
        var third = provider.GetAsync("MemoryCompression");
        Require(executor.Calls == 1, "reset started multiple pending OS queries");
        pending.SetResult(MockExecutor.Result(stdout: "{\"MemoryCompression\":true}"));
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(2));
        provider.ResetSnapshot();
        Require(await provider.GetAsync("MemoryCompression") == false && executor.Calls == 2, "completed reset failed to reload updated snapshot");
        return "Three callers/reset bursts shared one incomplete mock query; completed reset loaded updated false with exactly second query";
    }
    private static async Task<string> UwpWorkflow()
    {
        var targets = new List<string> { "GOGO_CANARY_A", "GOGO_CANARY_B" };
        var settings = new MemoryUwpSettings();
        settings.Values[targets[0]] = new(7, null); settings.Values[targets[1]] = new(0, 0);
        var batch = new UwpBackgroundBatchSetting(() => targets, settings);
        Require((await batch.ApplyAsync(false)).Success &&
            settings.Values.Values.All(value => value == UwpBackgroundValues.DisabledValues), "UWP bulk OFF did not disable both values");
        targets.Add("GOGO_CANARY_NEW"); settings.Values[targets[2]] = default;
        Require((await batch.ApplyAsync(false)).Success &&
            settings.Values.Values.All(value => value == UwpBackgroundValues.DisabledValues), "repeated bulk OFF omitted the newly installed app");
        Require((await batch.ApplyAsync(true)).Success &&
            settings.Values.Values.All(value => value == UwpBackgroundValues.Enabled), "bulk ON did not explicitly permit all current apps");
        targets.Add("GOGO_CANARY_NEW_ON"); settings.Values[targets[3]] = new(1, 1);
        Require((await batch.ApplyAsync(true)).Success &&
            settings.Values.Values.All(value => value == UwpBackgroundValues.Enabled), "repeated bulk ON omitted the newly installed app");
        return "Actual UWP batch re-enumerated installed families on repeated OFF and ON, set both DWORDs to 1 or 0 respectively, and included newly installed apps without an opposite operation";
    }
    private static async Task<string> UwpPairWorkflow()
    {
        const string family = "GOGO_CANARY_A", other = "GOGO_CANARY_B";
        var settings = new MemoryUwpSettings();
        UwpBackgroundBatchSetting NewBatch() => new(() => [family, other], settings);
        settings.Values[family] = new(null, 0); settings.Values[other] = new(0, 0);
        settings.FailPairs = 1;
        Require(!(await NewBatch().ApplyAsync(false)).Success && settings.Values[family] == new UwpBackgroundValues(null, 0),
            "bulk bulk ON partial pair not compensated to immediate state");
        Require((await NewBatch().ApplyAsync(false)).Success, "normal bulk OFF failed");
        settings.FailPairs = 1;
        Require(!(await NewBatch().ApplyAsync(true)).Success && settings.Values[family] == UwpBackgroundValues.DisabledValues,
            "bulk ON partial pair did not compensate to immediate disabled values");
        Require((await NewBatch().ApplyAsync(true)).Success && settings.Values.Values.All(value => value == UwpBackgroundValues.Enabled),
            "bulk ON retry did not permit all apps");
        settings.Values[family] = new(null, 0);
        settings.FailPairs = 2;
        var result = await NewBatch().ApplyAsync(false);
        Require(result.Status == MaintenanceActionStatus.Failed && result.Detail.Contains("補償"), "pair compensation failure hidden");
        settings.FailPairs = 0; settings.FailBeforeFirstValue = false;
        Require((await NewBatch().ApplyAsync(true)).Success && settings.Values.Values.All(value => value == UwpBackgroundValues.Enabled),
            "bulk ON was blocked by old partial state");
        settings.Values[family] = new(null, 0);
        settings.FailPairs = 1; settings.ExternalOnFailure = new(7, 8);
        Require(!(await NewBatch().ApplyAsync(false)).Success && settings.Values[family] == new UwpBackgroundValues(7, 8),
            "failure compensation overwrote external change");
        return "Actual UWP bulk OFF/ON pair failures compensated immediate values in memory, compensation failure remained Failed, repeat ON repaired partial state, external changes during failure preserved";
    }

    private static async Task<string> VbsDefaultsWorkflow()
    {
        var custom = VirtualizationSecuritySnapshot.Default() with
        {
            HypervisorLaunchType = BcdValueSnapshot.Present("Auto"),
            Hvci = RegistryValueSnapshot.Dword(2),
        };
        var platform = new MemoryVirtualizationPlatform(custom);
        var toggle = new VirtualizationSecurityToggle(platform);
        Require(await toggle.GetStateAsync() == false && (await toggle.SetStateAsync(true)).Success && platform.State.IsFullyApplied,
            "VBS custom state could not be applied");
        Require((await new VirtualizationSecurityToggle(platform).SetStateAsync(false)).Success &&
            platform.State.EquivalentTo(VirtualizationSecuritySnapshot.Default()), "VBS OFF restored custom values instead of deleting overrides");
        platform.State = custom; platform.FailAfterComponent = VirtualizationSecurityComponent.Hvci;
        Require(!(await new VirtualizationSecurityToggle(platform).SetStateAsync(true)).Success && platform.State.EquivalentTo(custom),
            "VBS partial failure did not compensate immediate snapshot");
        return "Actual VBS fake platform: ON applied all components; fresh OFF removed BCD/three registry overrides; injected partial failure compensated current custom values in memory";
    }

    private static Task<string> TogglePersistenceBoundaryWorkflow()
    {
        foreach (var file in new[] { "RegistryToggle.cs", "UwpBackgroundBatchSetting.cs", "VirtualizationSecurityToggle.cs" })
        {
            var source = File.ReadAllText(Path.Combine(_repo, "src/Lumin4ti.Core/Services/Windows/Actions", file));
            Require(!new[] { "RegistryValueBackup", "UwpBackgroundJournal", "VirtualizationSecurityBackupStore", "ProtectedBackupStorage", "File.Read", "File.Write" }
                .Any(source.Contains), "toggle still references persistent backup: " + file);
        }
        return Task.FromResult("Static supplement: setting implementations do not persist or read legacy state backups; UWP reads only installed package manifests for classification; PATH/QuickAccess data backups excluded");
    }
    private static async Task<string> MmAgentDiagnosticWorkflow()
    {
        foreach (var output in new[] { "", "{", "[]", "null", "{}" })
        {
            var before = Logs.Messages.Count;
            var executor = new MockExecutor((_, _, _) => MockExecutor.Result(stdout: output));
            var provider = new MmAgentStateProvider(executor);
            Require(await provider.GetAsync("MemoryCompression") is null && Logs.Messages.Count == before + 1, "shared query failure diagnosis missing/duplicated: " + output);
        }
        var gate = new TaskCompletionSource<CommandExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shared = new AsyncMockExecutor(_ => gate.Task); var sharedProvider = new MmAgentStateProvider(shared);
        var logStart = Logs.Messages.Count;
        var first = sharedProvider.GetAsync("MemoryCompression"); var second = sharedProvider.GetAsync("OperationAPI");
        gate.SetResult(MockExecutor.Result(false, "GOGO_CANARY output", "token=GOGO_CANARY_SECRET"));
        Require(await first is null && await second is null && shared.Calls == 1 && Logs.Messages.Count == logStart + 1, "failure query/log not coalesced");
        var message = Logs.Messages.Last();
        Require(message.Contains("exit=5") && message.Contains("GOGO_CANARY output") && !message.Contains("GOGO_CANARY_SECRET"), "shared failure diagnostic lost exit/stdout or exposed secret");
        sharedProvider.ResetSnapshot();
        Require(await sharedProvider.GetAsync("MemoryCompression") is null && shared.Calls == 2, "unknown state retry remained stale");
        foreach (var output in new[] { "", "{", "null", "[]", "\"512\"", "2147483648", "1.5", "512" })
        {
            var executor = new MockExecutor((_, _, _) => MockExecutor.Result(stdout: output));
            var choice = new MmAgentOperationApiChoice(executor, new MmAgentStateProvider(executor));
            var before = Logs.Messages.Count; var value = await choice.GetSelectedValueAsync();
            Require(value == (output == "512" ? "512" : null) && Logs.Messages.Count == before + (output == "512" ? 0 : 1), "numeric query result/diagnosis mismatch: " + output);
        }
        return "Actual shared and numeric providers rejected failed/empty/malformed/non-object/overflow responses, preserved null/retry, coalesced two callers into one query/log, and logged bounded exit/stdout with synthetic secret redacted";
    }
    private static Task<string> ParserWorkflow()
    {
        Require(StartupCommandParser.TryResolveExecutable("cmd.exe /c RERE_CANARY") is null, "wrapper not rejected");
        Require(StartupCommandParser.TryResolveExecutable("RERE_CANARY.exe-helper") is null, "internal .exe prefix treated as executable suffix");
        foreach (var command in new[] { @"C:\Tools.exe Suite\app.exe /quiet", @"C:\Tools.exe Suite\app /quiet", @"C:\Tool.exe Extra", @"C:\Tool.exe -flag", @"C:\Program Files\Tool\app.exe /quiet", @"%ProgramFiles%\Tool\app.exe /quiet", @"C:\Tool.exe /launch C:\Other\app.exe" })
            Require(StartupCommandParser.TryResolveExecutable(command) is null, "ambiguous unquoted path accepted: " + command);
        Require(StartupCommandParser.TryResolveExecutable("\"C:\\Tools.exe Suite\\app.exe\" /quiet") == @"C:\Tools.exe Suite\app.exe", "quoted path rejected");
        Require(StartupCommandParser.TryResolveExecutable(@"C:\Tool\app.exe /quiet") == @"C:\Tool\app.exe", "unambiguous path plus switches rejected");
        Require(StartupCommandParser.TryResolveExecutable("C:\\Tool\\app.exe \"argument\"") == @"C:\Tool\app.exe", "unambiguous path plus quoted argument rejected");
        Require(!StartupCommandParser.IsConfirmedMissing(@"C:\RERE_CANARY\missing.exe", _ => (DriveType.Fixed, true), _ => throw new UnauthorizedAccessException()), "access denial treated as missing");
        var diagnostic = CommandFailureDiagnostic.Format(MockExecutor.Result(false, "RERE_CANARY 診断", "token=RERE_CANARY_SECRET"));
        Require(diagnostic.Contains("exit=5") && diagnostic.Contains("RERE_CANARY 診断") && !diagnostic.Contains("RERE_CANARY_SECRET"), "failed-tool diagnosis lost stdout or exposed secret");
        return Task.FromResult("Pure parser rejected wrapper/access denied; failure exit/stdout retained and synthetic secret redacted; no shell execution");
    }
    private static async Task<string> DownloadWorkflow()
    {
        using var normal = new HttpClient(new MockHttpHandler(false));
        using var output = new MemoryStream();
        await WindowsPerMachineMigration.DownloadMsiAsync(normal, new Uri("https://RERE_CANARY.invalid/file"), output, TimeSpan.FromSeconds(1), default);
        Require(Encoding.UTF8.GetString(output.ToArray()) == "RERE_CANARY", "normal body copy failed");
        using var blocked = new HttpClient(new MockHttpHandler(true, _simulateMissingDownloadCancellation));
        using var timeoutOutput = new MemoryStream();
        var timedOut = false;
        try { await WindowsPerMachineMigration.DownloadMsiAsync(blocked, new Uri("https://RERE_CANARY.invalid/file"), timeoutOutput, TimeSpan.FromMilliseconds(30), default); }
        catch (TimeoutException) { timedOut = true; }
        Require(timedOut, "slow response body escaped download timeout");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        var userCanceled = false;
        try { await WindowsPerMachineMigration.DownloadMsiAsync(blocked, new Uri("https://RERE_CANARY.invalid/file"), timeoutOutput, TimeSpan.FromSeconds(10), cancel.Token); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { userCanceled = true; }
        Require(userCanceled, "user cancellation incorrectly converted to timeout");
        return "Mock handler only: normal body copied, blocked body timed out, user cancellation remained OCE; no network";
    }
    private static async Task<string> ScheduledSelectionWorkflow()
    {
        var settings = new MemorySettings();
        settings.Current.ScheduledCleanupGroupIds = ["cleanup-user-temp", "CLEANUP-USER-TEMP", "cleanup-system-temp"];
        var preferences = new CleanupPreferences(settings);
        var executor = new MockExecutor((_, _, _) => throw new InvalidOperationException("Must not register or run a task"));
        var toggle = new ScheduledTempCleanupToggle(executor, preferences);
        IMaintenanceAction[] actions = [
            new FileCleanupAction("cleanup-user-temp", "検証", "検証", () => throw new InvalidOperationException("Must not resolve user cleanup targets")),
            new FileCleanupAction("cleanup-system-temp", "検証", "検証", () => throw new InvalidOperationException("Must not resolve system cleanup targets"))];
        Require(ScheduledTempCleanup.SelectActions(actions, preferences.ScheduledGroupIds).Count == 2, "duplicate setup not selected");
        await toggle.SetCheckListEntrySelectedAsync("cleanup-user-temp", false);
        Require(preferences.ScheduledGroupIds.SequenceEqual(new[] { "cleanup-system-temp" }), "OFF retained a duplicate or removed unrelated ID");
        Require(ScheduledTempCleanup.SelectActions(actions, preferences.ScheduledGroupIds).Single().Id == "cleanup-system-temp", "OFF item retained by actual scheduled selection");
        await toggle.SetCheckListEntrySelectedAsync("cleanup-user-temp", false);
        await toggle.SetCheckListEntrySelectedAsync("cleanup-user-temp", true);
        await toggle.SetCheckListEntrySelectedAsync("CLEANUP-USER-TEMP", true);
        Require(preferences.ScheduledGroupIds.Count == 2 && settings.SaveCount == 4 && executor.Calls.Count == 0, "idempotent checklist edits changed unrelated selections or executed OS task");
        return "Actual checklist save -> preferences -> scheduled selection removed both case-variant duplicates; unrelated selection/order kept; repeated OFF/ON idempotent; no target resolution or execution";
    }
    private static async Task<string> IgnoredUpdateWorkflow()
    {
        foreach (var fail in new[] { false, true })
        {
            var settings = new MemorySettings { FailSave = fail };
            settings.Current.IgnoreUpdateTag = "OPOP_CANARY";
            var vm = new VersionViewModel(new UpdateService(settings.Current), settings, new MaintenanceOperationCoordinator(() => new NoopLease()));
            var initialSaves = settings.SaveCount;
            await vm.ClearIgnoredUpdateTagCommand.ExecuteAsync(null);
            Require(settings.SaveCount == initialSaves + 1 && settings.Current.IgnoreUpdateTag is null && !vm.HasIgnoredUpdateTag, "skip clear did not follow save boundary or clear VM/current setting");
            await vm.ClearIgnoredUpdateTagCommand.ExecuteAsync(null);
            Require(settings.SaveCount == initialSaves + 1, "repeated empty skip clear attempted persistence");
        }
        return "Actual generated command handled normal persistence and injected IOException without propagating; cleared UI/current tag; repeat command performed no save";
    }
    private static Task<string> PendingJsonWorkflow()
    {
        foreach (var executable in new string?[] { null, @"C:\Program Files\Lumin4ti\current\Lumin4ti.UI.exe" })
        {
            var pending = new WindowsPerMachineMigration.PendingMigration(@"C:\Users\検証\AppData\Local\Lumin4ti", 123, executable);
            var legacy = JsonSerializer.Serialize(pending, new JsonSerializerOptions { WriteIndented = true });
            var current = Lumin4tiJson.Serialize(pending);
            Require(current == legacy && Lumin4tiJson.Deserialize<WindowsPerMachineMigration.PendingMigration>(legacy) == pending, "legacy migration JSON format/read compatibility changed");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(pending, Lumin4tiJsonContext.Default.PendingMigration);
            Require(Encoding.UTF8.GetString(bytes) == legacy, "protected UTF-8 pending format differs");
            using var document = JsonDocument.Parse(current);
            Require(document.RootElement.EnumerateObject().Select(p => p.Name).SequenceEqual(new[] { "LegacyRoot", "ParentProcessId", "InstalledExecutable" }), "pending properties/order changed");
        }
        return Task.FromResult("Old reflection JSON and new generated metadata are text/UTF-8 equivalent; legacy read, Japanese path, null/non-null installed executable and property order preserved; no storage/registry/install invoked");
    }
    private static Task<string> StaticWorkflow()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var locales = Directory.GetFiles(Path.Combine(_repo, "src/Lumin4ti.UI/Resources/Locales"), "*.axaml");
        Require(locales.Length == 17, "locale count differs from 17");
        string[] added = ["UpdateDialog.Title", "UpdateDialog.AvailableHeader", "UpdateDialog.DownloadAndInstall", "UpdateDialog.IgnoreThisVersion", "UpdateDialog.UpToDateMessage", "UpdateDialog.ErrorHeader", "UpdateDialog.Close", "UpdateDialog.CheckingMessage", "UpdateDialog.DevelopmentBuild", "UpdateDialog.CheckFailed", "Status.Applying", "Status.Applied", "Status.ApplyFailed", "Shutdown.BlockReason"];
        added = [.. added, "Setting.DefaultFormat", "Setting.DefaultEnvironment",
            "Toggle.RecallQueryFailed", "Toggle.RecallStateMissing", "Toggle.RecallStateUnsupported",
            "Toggle.ScheduledTaskOtherUser", "Toggle.ScheduledTaskOwnershipUnknown"];
        foreach (var path in locales)
        {
            var keys = XDocument.Load(path).Descendants().Select(e => (string?)e.Attribute(x + "Key")).Where(k => k is not null).ToArray();
            Require(keys.Distinct().Count() == keys.Length && added.All(key => keys.Contains("Text." + key)), $"missing/duplicate locale keys: {Path.GetFileName(path)}");
        }
        var views = Directory.GetFiles(Path.Combine(_repo, "src/Lumin4ti.UI/Views"), "*.axaml").Select(File.ReadAllText).ToArray();
        Require(views.Any(text => text.Contains("AutomationProperties.Name")), "no accessibility names found");
        Require(File.ReadAllText(Path.Combine(_repo, "src/Lumin4ti.UI/Services/Lumin4tiUpdateStrings.cs")).Contains("Title => App.Text"), "cached update title remains");
        var expectedNames = new Dictionary<string, string>
        {
            ["{Binding RunCommand}"] = "{Binding RunAutomationName}",
            ["{Binding CancelCommand}"] = "{Binding CancelAutomationName}",
            ["{Binding ApplyBatchOnCommand}"] = "{Binding BatchOnAutomationName}",
            ["{Binding ApplyBatchOffCommand}"] = "{Binding BatchOffAutomationName}",
        };
        var actionButtons = XDocument.Load(Path.Combine(_repo, "src/Lumin4ti.UI/Views/CommandCategoryView.axaml"))
            .Descendants().Where(element => element.Name.LocalName == "Button" &&
                expectedNames.ContainsKey((string?)element.Attribute("Command") ?? "")).ToArray();
        Require(actionButtons.Length == 8 && expectedNames.Keys.All(command => actionButtons.Count(button =>
            (string?)button.Attribute("Command") == command) == 2) && actionButtons.All(button => button.Attributes().Any(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name" && attribute.Value == expectedNames[button.Attribute("Command")!.Value])),
            "parent/child run/cancel names not correctly bound");
        return Task.FromResult($"Supplement only: 17 XML dictionaries parsed, {added.Length} required keys/no duplicates, getter and automation-name source present; this is not GUI accessibility validation");
    }
    private sealed class LocaleDevices : IDisconnectedDeviceService
    {
        private bool _removed;
        public Task<IReadOnlyList<DisconnectedDevice>> GetDisconnectedDevicesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DisconnectedDevice>>(_removed ? [] : [new("OPOP_FAKE_DEVICE", "Memory device", "Test", "Test")]);
        public Task<DisconnectedDeviceRemovalResult> RemoveDisconnectedDeviceAsync(string instanceId, CancellationToken ct = default)
        {
            Require(instanceId == "OPOP_FAKE_DEVICE", "unexpected fake removal target");
            _removed = true;
            return Task.FromResult(new DisconnectedDeviceRemovalResult(DisconnectedDeviceRemovalStatus.Removed, RequiresRestart: true));
        }
    }

    private static Task<string> RetainedResultsLocaleWorkflow()
    {
        const string detail = "OPOP_CANARY 技術明細";
        var results = new[] { MaintenanceActionResult.Ok(detail), MaintenanceActionResult.Partial(detail),
            MaintenanceActionResult.Fail(detail), MaintenanceActionResult.Canceled(detail) };
        foreach (var result in results)
        foreach (var kind in new[] { "action", "toggle", "choice", "batch-on", "batch-off" })
        {
            App.SetLocale("ja_JP");
            LogProbeItem item = kind switch
            {
                "toggle" => new LogProbeToggle("locale") { Result = result },
                "choice" => new LogProbeChoice("locale") { Result = result },
                "batch-on" or "batch-off" => new LogProbeBatch("locale") { Result = result },
                _ => new LogProbeAction("locale") { Result = result },
            };
            var category = new CommandCategoryViewModel(new MaintenanceActionCatalog(new IMaintenanceItem[] { item }),
                new MaintenanceOperationCoordinator(() => new NoopLease()), CommandCategory.System, "検証", "検証");
            var card = category.Items.Single();
            TriggerLogProbe(card, kind);
            var japaneseResult = card.ResultText;
            var japaneseStatus = category.StatusText;
            App.SetLocale("en_US");
            Require(card.ResultText != japaneseResult && category.StatusText != japaneseStatus,
                $"retained {kind}/{result.Status} result or status did not relocalize");
            Require(card.ResultText.Contains(detail), "technical result detail was changed");
            if (kind.StartsWith("batch-"))
                Require(card.ResultText.StartsWith(App.Text(kind == "batch-on" ? "Button.BatchOn" : "Button.BatchOff", "")),
                    "batch operation label retained old locale");
            App.SetLocale("ja_JP");
            Require(card.ResultText == japaneseResult && category.StatusText == japaneseStatus,
                "retained result did not return to Japanese");
        }

        var device = new DeviceCleanupViewModel(new LocaleDevices(), new MaintenanceOperationCoordinator(() => new NoopLease()),
            action => { action(); return Task.CompletedTask; });
        device.LoadDisconnectedDevicesAsync().GetAwaiter().GetResult();
        var loaded = device.StatusText;
        App.SetLocale("en_US");
        Require(device.StatusText != loaded && device.StatusText.Contains('1'), "device count did not relocalize");
        App.SetLocale("ja_JP");
        device.SelectAllCommand.Execute(null);
        device.RequestRemovalCommand.Execute(null);
        device.ConfirmRemovalCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        var removed = device.StatusText;
        App.SetLocale("en_US");
        Require(device.StatusText != removed && device.StatusText.Contains('1'), "device removal summary did not relocalize");
        App.SetLocale("ja_JP");
        Require(device.StatusText == removed, "device removal summary did not return to Japanese");
        foreach (var succeeded in new[] { true, false })
        {
            var logResult = succeeded ? MaintenanceActionResult.Ok(detail + "\n  - エクスプローラーを再起動しました")
                : CommandCategoryViewModel.MarkExplorerRestartFailed(MaintenanceActionResult.Ok(detail),
                    "  - エクスプローラーの再起動に失敗しました (手動で再起動してください)");
            var japanese = CommandCategoryViewModel.RenderResultDetail(logResult, succeeded, detail);
            App.SetLocale("en_US");
            var english = CommandCategoryViewModel.RenderResultDetail(logResult, succeeded, detail);
            Require(english != japanese && english.Contains(detail)
                && english.EndsWith(App.Text(succeeded ? "Result.ExplorerRestarted" : "Result.ExplorerRestartFailed", "")),
                "Explorer restart instruction retained previous locale or lost technical detail");
            App.SetLocale("ja_JP");
            Require(CommandCategoryViewModel.RenderResultDetail(logResult, succeeded, detail) == japanese,
                "Explorer restart instruction did not return to Japanese");
        }
        return Task.FromResult("Success/Partial/Failed/Canceled action/toggle/choice/batch results, device counts/removal and Explorer restart instructions followed ja→en→ja; technical details preserved; fake OS only");
    }

    private static async Task<string> LocaleWorkflow()
    {
        var app = new App();
        // OS platform、ApplicationLifetime、製品 Initialize は起動しない。
        // Avalonia 12 は mutable locator を参照 assembly に公開しないため、検証専用で runtime API を照合する。
        var locatorType = typeof(AvaloniaLocator);
        var locator = locatorType.GetProperty("CurrentMutable", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null);
        var bind = locatorType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method => method.Name == "Bind" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0);
        var registration = bind.MakeGenericMethod(typeof(Application)).Invoke(locator, null)!;
        var toConstant = registration.GetType().GetMethod("ToConstant")!;
        if (toConstant.IsGenericMethodDefinition) toConstant = toConstant.MakeGenericMethod(typeof(App));
        toConstant.Invoke(registration, [app]);
        app.RegisterServices();
        Require(ReferenceEquals(Application.Current, app), "actual App was not bound as current application");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var expected = new Dictionary<string, Dictionary<string, string>>();
        foreach (var locale in App.SupportedLocales)
        {
            var xml = XDocument.Load(Path.Combine(_repo, "src/Lumin4ti.UI/Resources/Locales", locale.Key + ".axaml"));
            var values = xml.Descendants().Where(e => e.Attribute(x + "Key") is not null && e.Name.LocalName == "String")
                .ToDictionary(e => (string)e.Attribute(x + "Key")!, e => e.Value);
            expected[locale.Key] = values;
            var dictionary = new ResourceDictionary();
            foreach (var (key, value) in values) dictionary[key] = value;
            app.Resources[locale.Key] = dictionary;
        }
        App.SetLocale("en_US");
        var settings = new MemorySettings();
        var developmentUpdate = new UpdateService(() => null);
        var vm = new VersionViewModel(developmentUpdate, settings, new MaintenanceOperationCoordinator(() => new NoopLease()));
        var failedUpdate = new UpdateService(() => throw new IOException("OPOP_CANARY manager creation failure"));
        var failedVersion = new VersionViewModel(failedUpdate, new MemorySettings(), new MaintenanceOperationCoordinator(() => new NoopLease()));
        var threwManagerFailure = false;
        try { failedUpdate.TryCreateInstalledManager(); }
        catch (IOException) { threwManagerFailure = true; }
        Require(threwManagerFailure, "manager initialization failure was hidden as an uninstalled build");
        var unsavedSettings = new MemorySettings { FailSave = true };
        var unsaved = new VersionViewModel(developmentUpdate, unsavedSettings, new MaintenanceOperationCoordinator(() => new NoopLease()));
        var protectedSettings = new MemorySettings { LoadStatus = SettingsLoadStatus.Failed };
        var protectedVm = new VersionViewModel(developmentUpdate, protectedSettings, new MaintenanceOperationCoordinator(() => new NoopLease()));
        Require(protectedVm.SettingsSaveStatusText.Length > 0, "unreadable settings warning required an attempted change");
        var saveSettings = typeof(VersionViewModel).GetMethod("SaveSettingsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)saveSettings.Invoke(unsaved, null)!;
        await (Task)saveSettings.Invoke(protectedVm, null)!;
        Require(protectedSettings.SaveCount == 0, "unreadable settings were sent to the persistence boundary");
        var setStatus = typeof(VersionViewModel).GetMethod("SetUpdateStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;
        setStatus.Invoke(vm, ["UpdateDialog.CheckFailed", "検証失敗: {0}", new object[] { "RERE_CANARY" }]);
        var instance = Lumin4tiUpdateStrings.Instance;
        var toggleExecutor = new MockExecutor((_, _, _) => MockExecutor.Result(stdout: "{}"));
        var genericToggle = Vm(new MmAgentFeatureToggle(toggleExecutor, new MmAgentStateProvider(toggleExecutor), "MemoryCompression", "gogo-locale", "検証", "検証"));
        var genericChoice = new CommandItemViewModel(new MmAgentOperationApiChoice(toggleExecutor, new MmAgentStateProvider(toggleExecutor)), _ => Task.CompletedTask, (_, _) => Task.CompletedTask);
        var specific = Vm(genericToggle.Item);
        var diagnosticItem = new LogProbeToggle("locale-recall-diagnostic")
        {
            Diagnostic = new("Toggle.RecallStateMissing", "Recall の状態が返されませんでした。"),
            Query = _ => Task.FromResult<bool?>(null),
        };
        var stateDiagnostic = Vm(diagnosticItem);
        var defaultOn = Vm(new LogProbeToggle("locale-default-on") { DefaultState = true });
        var defaultOff = Vm(new LogProbeToggle("locale-default-off") { DefaultState = false });
        stateDiagnostic.ApplyState(null);
        stateDiagnostic.ResultText = "GOGO_CANARY previous diagnostic result";
        genericToggle.ApplyState(null); genericChoice.ApplySelectedValue(null); specific.ApplyState(null);
        specific.StateErrorText = "GOGO_CANARY specific IOException";
        genericToggle.ResultText = "GOGO_CANARY previous result";
        var notifications = new HashSet<string?>();
        genericToggle.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        foreach (var locale in vm.Locales)
        {
            notifications.Clear();
            var before = settings.SaveCount;
            vm.SelectedLocale = locale;
            Require(settings.Current.Locale == locale.Key && App.CurrentLocaleKey == locale.Key && settings.SaveCount == before + 1, "locale selection/save route failed: " + locale.Key);
            foreach (var property in typeof(Lumin4tiUpdateStrings).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                var value = (string)property.GetValue(instance)!;
                Require(value == expected[locale.Key]["Text.UpdateDialog." + property.Name], "same singleton getter stale: " + locale.Key + "/" + property.Name);
            }
            Require(vm.UpdateStatusText == string.Format(expected[locale.Key]["Text.UpdateDialog.CheckFailed"], "RERE_CANARY"), "persisted status did not relocalize: " + locale.Key);
            Require(vm.VersionText == expected[locale.Key]["Text.Version.DevelopmentBuild"], "development version remained in old language: " + locale.Key);
            Require(failedVersion.VersionText == expected[locale.Key]["Text.Version.Unavailable"], "version read failure was hidden or stale: " + locale.Key);
            Require(unsaved.SettingsSaveStatusText == expected[locale.Key]["Text.Settings.SaveFailed"]
                && protectedVm.SettingsSaveStatusText == expected[locale.Key]["Text.Settings.SaveSuppressed"], "unsaved/temporary settings status remained in old language: " + locale.Key);
            Require(App.Text("RERE_CANARY_MISSING", "fallback {0}", "RERE_CANARY") == "fallback RERE_CANARY", "missing key fallback changed");
            const string unknownFallback = "状態を取得できませんでした (管理者権限がないか、この PC では利用できない機能です。デバッグ起動は昇格されないため、通常起動でお試しください)";
            Require(genericToggle.StateErrorText == expected[locale.Key].GetValueOrDefault("Text.Toggle.StateUnknown", unknownFallback) && genericChoice.StateErrorText == genericToggle.StateErrorText, "generic toggle/choice error remained in old language: " + locale.Key);
            Require(specific.StateErrorText == "GOGO_CANARY specific IOException", "locale switch replaced explicit diagnostic");
            Require(stateDiagnostic.StateErrorText == expected[locale.Key]["Text.Toggle.RecallStateMissing"]
                && !stateDiagnostic.CanToggle, "state diagnostic remained generic, stale or enabled: " + locale.Key);
            Require(genericToggle.RunAutomationName.Contains(App.Text("Button.Run", "実行")) && genericToggle.CancelAutomationName.Contains(App.Text("Button.Cancel", "中断")) && genericToggle.RunAutomationName != genericToggle.CancelAutomationName, "action automation identities ambiguous/stale");
            var defaultFormat = expected[locale.Key]["Text.Setting.DefaultFormat"];
            Require(defaultOn.DefaultStateText == string.Format(defaultFormat, "ON")
                && defaultOff.DefaultStateText == string.Format(defaultFormat, "OFF")
                && stateDiagnostic.DefaultStateText == string.Format(defaultFormat, expected[locale.Key]["Text.Setting.DefaultEnvironment"]),
                "default ON/OFF/environment text remained in old language: " + locale.Key);
            var defaultOption = ((IMaintenanceChoice)genericChoice.Item).Options.Single(option => option.IsDefault);
            var defaultLabel = defaultOption.LabelKey is { } key
                ? expected[locale.Key].GetValueOrDefault("Text." + key, defaultOption.Label) : defaultOption.Label;
            Require(genericChoice.DefaultStateText == string.Format(defaultFormat, defaultLabel), "choice default text remained in old language: " + locale.Key);
            Require(new[] { "StateErrorText", "RunAutomationName", "CancelAutomationName", "DefaultStateText" }.All(notifications.Contains), "locale change missed property notification");
        }
        genericToggle.ApplyState(true); genericChoice.ApplySelectedValue("512");
        stateDiagnostic.ApplyState(false);
        Require(stateDiagnostic.StateErrorText.Length == 0 && stateDiagnostic.CanToggle
            && stateDiagnostic.ResultText == "GOGO_CANARY previous diagnostic result", "state recovery erased result or retained diagnostic");
        Require(genericToggle.StateErrorText.Length == 0 && genericChoice.StateErrorText.Length == 0 && genericToggle.ResultText == "GOGO_CANARY previous result", "known state recovery did not clear only diagnosis");
        setStatus.Invoke(vm, [null, "", Array.Empty<object>()]);
        Require(vm.UpdateStatusText.Length == 0, "status reset retained translated text");
        unsavedSettings.FailSave = false;
        await (Task)saveSettings.Invoke(unsaved, null)!;
        Require(unsaved.SettingsSaveStatusText.Length == 0, "successful retry retained save failure status");
        return "Actual App.SetLocale via VersionVM.SelectedLocale: 17 switches saved to memory, update getters/version/save warnings relocalized; manager creation failure propagated and unreadable settings protected; XML-derived dictionaries, no GUI lifetime";
    }
}
