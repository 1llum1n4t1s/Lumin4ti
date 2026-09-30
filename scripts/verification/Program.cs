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
        await Run("two-user-registry-on-on-off-off-retry", RegistryWorkflow);
        await Run("registry-reapply-failure-immediate-state-and-original", RegistryReapplyWorkflow);
        await Run("registry-initial-failure-ownership-and-cancellation", RegistryPreparationWorkflow);
        await Run("shared-atomic-replace-failure-original-preserved", SharedWriteFailureWorkflow);
        await Run("shared-two-owner-release-failure-consistency", SharedReleaseWorkflow);
        await Run("shared-completed-restoration-and-preparation-recovery", SharedRecoveryWorkflow);
        await Run("shared-new-user-preparation-failure-after-external-change", () => SharedExternalPreparationWorkflow(false));
        await Run("new-user-post-save-validation-failure-after-external-change", () => SharedExternalPreparationWorkflow(true));
        await Run("shutdown-cancellation-finally-gate", ShutdownWorkflow);
        await Run("ntp-stop-failure-compensation", NtpWorkflow);
        await Run("service-stop-scm-and-dependent-safety", ServiceWorkflow);
        await Run("cleanup-cancel-resume-failure-visible", CleanupWorkflow);
        await Run("mmagent-external-change-and-parent-state", StateWorkflow);
        await Run("mmagent-inflight-reset-coalescing", MmAgentInflightWorkflow);
        await Run("device-enumeration-failure-retry", DeviceWorkflow);
        await Run("uwp-sid-journal-isolation", UwpWorkflow);
        await Run("uwp-partial-pair-compensation-restart-and-external-change", UwpPairWorkflow);
        await Run("mmagent-failure-diagnostic-and-retry", MmAgentDiagnosticWorkflow);
        await Run("parser-diagnostic-and-effective-oem", ParserWorkflow);
        await Run("migration-download-body-timeout-and-cancel", DownloadWorkflow);
        await Run("scheduled-checklist-duplicate-off-selection", ScheduledSelectionWorkflow);
        await Run("clear-ignored-update-save-failure-boundary", IgnoredUpdateWorkflow);
        await Run("migration-pending-json-legacy-compatibility", PendingJsonWorkflow);
        await Run("actual-app-locale-version-vm-update-getters", LocaleWorkflow);
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
    private static RegistryToggle Toggle(string id, MemoryRegistry registry, MemoryBackups storage, string sid, RegistryToggleSpec[] specs) =>
        new(id, "検証", "検証", CommandCategory.System, specs, false, false, registry, new RegistryValueBackup(storage, registry, () => sid));
    private static async Task<string> RegistryReapplyWorkflow()
    {
        const string sid = "S-1-5-21-111-222-333-1001";
        RegistryToggleSpec[] specs = [new(RegistryHive.CurrentUser, "GOGO_CANARY", "User", RegistryValueKind.DWord, 1)];
        var registry = new MemoryRegistry(new(), sid); var storage = new MemoryBackups();
        registry.Write(specs[0], RegistryValueSnapshot.Dword(9));
        var toggle = Toggle("gogo-reapply", registry, storage, sid, specs);
        Require((await toggle.SetStateAsync(true)).Success, "initial ON failed");
        var original = storage.Files.Single().Value;
        registry.Write(specs[0], RegistryValueSnapshot.Dword(5));
        Require(await toggle.GetStateAsync() == false, "external change not visible");
        registry.FailNextWrite = true;
        Require(!(await toggle.SetStateAsync(true)).Success, "injected reapply failure hidden");
        Require(registry.Read(specs[0]).EquivalentTo(RegistryValueSnapshot.Dword(5)), "failed reapply restored historical original instead of immediate value5");
        Require(storage.Files.Single().Value == original, "failed reapply discarded/changed OFF original9");
        Require((await toggle.SetStateAsync(false)).Success && registry.Read(specs[0]).EquivalentTo(RegistryValueSnapshot.Dword(9)), "OFF retry did not restore historical original9");
        return "Actual ON -> external5 -> failed reON kept immediate5 and original9; OFF restored9";
    }
    private static async Task<string> RegistryPreparationWorkflow()
    {
        const string a = "S-1-5-21-111-222-333-1001", b = "S-1-5-21-111-222-333-1002";
        RegistryToggleSpec[] specs = [new(RegistryHive.LocalMachine, "GOGO_CANARY", "Machine", RegistryValueKind.DWord, 1), new(RegistryHive.CurrentUser, "GOGO_CANARY", "User", RegistryValueKind.DWord, 1)];
        var values = new Dictionary<string, RegistryValueSnapshot>();
        var ra = new MemoryRegistry(values, a); var rb = new MemoryRegistry(values, b); var storage = new MemoryBackups();
        ra.Write(specs[0], RegistryValueSnapshot.Dword(7)); ra.Write(specs[1], RegistryValueSnapshot.Dword(8)); rb.Write(specs[1], RegistryValueSnapshot.Dword(9));
        var ta = Toggle("gogo-owners", ra, storage, a, specs); var tb = Toggle("gogo-owners", rb, storage, b, specs);
        Require((await ta.SetStateAsync(true)).Success, "A ON failed");
        var original = storage.Files.ToDictionary(pair => pair.Key, pair => pair.Value);
        rb.FailAtWrite.Add(rb.WriteAttempts + 2);
        Require(!(await tb.SetStateAsync(true)).Success, "B partial apply failure hidden");
        Require(rb.Read(specs[0]).EquivalentTo(RegistryValueSnapshot.Dword(1)) && rb.Read(specs[1]).EquivalentTo(RegistryValueSnapshot.Dword(9)), "B failure changed immediate shared/user values");
        Require(storage.Files.Count == original.Count && original.All(pair => storage.Files.GetValueOrDefault(pair.Key) == pair.Value), "B failure retained new ownership or altered A original");
        Require((await ta.SetStateAsync(false)).Success && ra.Read(specs[0]).EquivalentTo(RegistryValueSnapshot.Dword(7)), "phantom B owner prevented A OFF");

        var fresh = new MemoryRegistry(new(), a); var freshStorage = new MemoryBackups();
        fresh.Write(specs[0], RegistryValueSnapshot.Dword(7)); fresh.Write(specs[1], RegistryValueSnapshot.Dword(8));
        var tf = Toggle("gogo-fresh", fresh, freshStorage, a, specs);
        fresh.FailAtWrite.Add(fresh.WriteAttempts + 2);
        Require(!(await tf.SetStateAsync(true)).Success && freshStorage.Files.Count == 0 && fresh.Read(specs[0]).EquivalentTo(RegistryValueSnapshot.Dword(7)), "first ON failure did not compensate/cancel fresh preparation");
        using var cancel = new CancellationTokenSource();
        freshStorage.AfterNewWrite = cancel.Cancel;
        var attempts = fresh.WriteAttempts;
        try { await tf.SetStateAsync(true, cancel.Token); throw new InvalidOperationException("cancel not propagated"); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        Require(fresh.WriteAttempts == attempts && freshStorage.Files.Count == 0, "cancellation after preparation wrote values or kept phantom ownership");
        freshStorage.AfterNewWrite = null;
        fresh.FailAtWrite.Add(fresh.WriteAttempts + 2); fresh.FailAtWrite.Add(fresh.WriteAttempts + 3);
        var failedCompensation = await tf.SetStateAsync(true);
        Require(!failedCompensation.Success && failedCompensation.Detail.Contains("補償にも失敗") && freshStorage.Files.Count == 2, "compensation failure lost originals or diagnostic");
        Require((await tf.SetStateAsync(false)).Success && fresh.Read(specs[0]).EquivalentTo(RegistryValueSnapshot.Dword(7)) && fresh.Read(specs[1]).EquivalentTo(RegistryValueSnapshot.Dword(8)), "OFF recovery after failed compensation lost original");
        return "Actual mixed partial apply canceled only B ownership; A OFF restored7; first apply and post-save cancellation reverted preparation; compensation failure retained originals and OFF retry recovered";
    }
    private static async Task<string> RegistryWorkflow()
    {
        const string a = "S-1-5-21-111-222-333-1001", b = "S-1-5-21-111-222-333-1002";
        RegistryToggleSpec[] specs = [new(RegistryHive.LocalMachine, "RERE_CANARY", "Machine", RegistryValueKind.DWord, 1), new(RegistryHive.CurrentUser, "RERE_CANARY", "User", RegistryValueKind.DWord, 1)];
        var values = new Dictionary<string, RegistryValueSnapshot>();
        var ra = new MemoryRegistry(values, a); var rb = new MemoryRegistry(values, b); var backups = new MemoryBackups();
        ra.Write(specs[0], RegistryValueSnapshot.Dword(7)); ra.Write(specs[1], RegistryValueSnapshot.Dword(8)); rb.Write(specs[1], RegistryValueSnapshot.Dword(9));
        var ta = Toggle("canary-mixed", ra, backups, a, specs); var tb = Toggle("canary-mixed", rb, backups, b, specs);
        var va = Vm(ta); var vb = Vm(tb);
        await ta.SetStateAsync(true); va.ApplyState(await ta.GetStateAsync());
        await tb.SetStateAsync(true); vb.ApplyState(await tb.GetStateAsync());
        Require(va.IsChecked && vb.IsChecked, "two ON states not observed");
        Require((await ta.SetStateAsync(false)).Success, "A OFF failed");
        Require(ra.Read(specs[0]).EquivalentTo(RegistryValueSnapshot.Dword(1)), "A OFF restored shared machine while B owns ON");
        Require(ra.Read(specs[1]).EquivalentTo(RegistryValueSnapshot.Dword(8)), "A HKCU original lost");
        rb.FailNextWrite = true;
        Require(!(await tb.SetStateAsync(false)).Success, "injected restore failure hidden");
        Require(backups.Files.Count > 0, "restore failure lost original");
        Require((await tb.SetStateAsync(false)).Success, "B OFF retry failed");
        Require(ra.Read(specs[0]).EquivalentTo(RegistryValueSnapshot.Dword(7)) && rb.Read(specs[1]).EquivalentTo(RegistryValueSnapshot.Dword(9)), "last owner retry did not restore original values");
        Require(backups.Files.Count == 1, "completed restore retained user journal or lost shared completion marker");
        using var shared = JsonDocument.Parse(backups.Files.Values.Single());
        Require(shared.RootElement.GetProperty("OwnerScopes").GetArrayLength() == 0, "completed restore retained active ownership");
        Require((await tb.SetStateAsync(false)).Success && ra.Read(specs[0]).EquivalentTo(RegistryValueSnapshot.Dword(7)), "repeated OFF changed restored shared original");
        return "Two real RegistryToggle instances and VM observations; shared HKLM=7, SID A HKCU=8, SID B HKCU=9 restored after injected failure/retry";
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
    private static async Task<string> SharedWriteFailureWorkflow()
    {
        const string a = "S-1-5-21-111-222-333-1001", b = "S-1-5-21-111-222-333-1002";
        RegistryToggleSpec[] specs = [new(RegistryHive.LocalMachine, "RERE_CANARY", "Machine", RegistryValueKind.DWord, 1), new(RegistryHive.CurrentUser, "RERE_CANARY", "User", RegistryValueKind.DWord, 1)];
        var values = new Dictionary<string, RegistryValueSnapshot>();
        var ra = new MemoryRegistry(values, a); var rb = new MemoryRegistry(values, b); var backups = new MemoryBackups();
        ra.Write(specs[0], RegistryValueSnapshot.Dword(7)); ra.Write(specs[1], RegistryValueSnapshot.Dword(8)); rb.Write(specs[1], RegistryValueSnapshot.Dword(9));
        var ta = Toggle("canary-atomic", ra, backups, a, specs); var tb = Toggle("canary-atomic", rb, backups, b, specs);
        Require((await ta.SetStateAsync(true)).Success, "initial A ON failed");
        backups.ThrowAfterNextSharedWrite = true;
        Require((await ta.SetStateAsync(false)).Success, "safely verified committed release not completed");
        Require(!backups.Files.ContainsKey(Path.Combine("registry", a, "canary-atomic.json")), "verified release retained user ownership");
        Require(ra.Read(specs[0]).EquivalentTo(RegistryValueSnapshot.Dword(7)), "committed OFF rolled registry back to ON");
        Require((await ta.SetStateAsync(false)).Success && ra.Read(specs[0]).EquivalentTo(RegistryValueSnapshot.Dword(7)), "repeat OFF changed true original");
        Require((await tb.SetStateAsync(true)).Success && (await tb.SetStateAsync(false)).Success && rb.Read(specs[0]).EquivalentTo(RegistryValueSnapshot.Dword(7)), "B could not safely capture/apply/restore after A retry");
        return "Post-replace exception verified against committed raw journal; OFF completed consistently without rolling values back to ON; repeated OFF and later B cycle preserved original7";
    }
    private static async Task<string> SharedReleaseWorkflow()
    {
        const string a = "S-1-5-21-111-222-333-1001", b = "S-1-5-21-111-222-333-1002";
        RegistryToggleSpec[] specs = [new(RegistryHive.LocalMachine, "GOGO_CANARY", "Machine", RegistryValueKind.DWord, 1), new(RegistryHive.CurrentUser, "GOGO_CANARY", "User", RegistryValueKind.DWord, 1)];
        foreach (var committed in new[] { true, false })
        {
            var values = new Dictionary<string, RegistryValueSnapshot>(); var storage = new MemoryBackups();
            var ra = new MemoryRegistry(values, a); var rb = new MemoryRegistry(values, b);
            ra.Write(specs[0], RegistryValueSnapshot.Dword(7)); ra.Write(specs[1], RegistryValueSnapshot.Dword(8)); rb.Write(specs[1], RegistryValueSnapshot.Dword(9));
            var ta = Toggle("gogo-release", ra, storage, a, specs); var tb = Toggle("gogo-release", rb, storage, b, specs);
            Require((await ta.SetStateAsync(true)).Success && (await tb.SetStateAsync(true)).Success, "two-owner setup failed");
            storage.ThrowAfterNextSharedWrite = committed; storage.ThrowBeforeNextSharedWrite = !committed;
            var result = await ta.SetStateAsync(false);
            Require(result.Success == committed && ra.Read(specs[1]).EquivalentTo(RegistryValueSnapshot.Dword(8)), "release failure rolled restored A user back to ON");
            Require((await tb.SetStateAsync(false)).Success && rb.Read(specs[1]).EquivalentTo(RegistryValueSnapshot.Dword(9)), "B OFF failed");
            Require(ra.Read(specs[0]).EquivalentTo(RegistryValueSnapshot.Dword(committed ? 7 : 1)), "B shared restore disagreed with retained/committed ownership");
            TaskCompletionSource<MaintenanceActionResult>? operation = null;
            var recovery = new CommandItemViewModel(ta, _ => Task.CompletedTask, async (vm, on) =>
            {
                try
                {
                    var applied = await ta.SetStateAsync(on);
                    vm.ResultText = applied.Detail; vm.ApplyResultStatus(applied.Status); vm.ApplyState(await ta.GetStateAsync());
                    operation!.SetResult(applied);
                }
                catch (Exception error) { operation!.SetException(error); }
            });
            recovery.ApplyState(await ta.GetStateAsync());
            Require(!recovery.IsChecked && recovery.CanToggle, "failed OFF did not expose expected screen state");
            if (!committed)
            {
                Require(result.Detail.Contains("ON に戻してから OFF"), "recovery text not usable from OFF screen");
                operation = new(TaskCreationOptions.RunContinuationsAsynchronously); recovery.IsChecked = true;
                Require((await operation.Task).Success && recovery.IsChecked, "screen ON recovery failed");
                operation = new(TaskCreationOptions.RunContinuationsAsynchronously); recovery.IsChecked = false;
                Require((await operation.Task).Success && !recovery.IsChecked, "screen OFF recovery failed");
            }
            Require(ra.Read(specs[0]).EquivalentTo(RegistryValueSnapshot.Dword(7)), "screen recovery did not complete retained ownership");
        }
        return "Two actual SID toggles: before/after replacement failures keep A user OFF; committed release restores shared7 on B OFF; uncommitted retains shared1 until actual VM OFF -> ON -> OFF recovery; final originals preserved";
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
    private static async Task<string> SharedRecoveryWorkflow()
    {
        const string a = "S-1-5-21-111-222-333-1001", b = "S-1-5-21-111-222-333-1002";
        RegistryToggleSpec[] specs = [new(RegistryHive.LocalMachine, "RERE_CANARY", "Machine", RegistryValueKind.DWord, 1), new(RegistryHive.CurrentUser, "RERE_CANARY", "User", RegistryValueKind.DWord, 1)];
        var values = new Dictionary<string, RegistryValueSnapshot>();
        var ra = new MemoryRegistry(values, a); var rb = new MemoryRegistry(values, b); var backups = new MemoryBackups();
        ra.Write(specs[0], RegistryValueSnapshot.Dword(7)); ra.Write(specs[1], RegistryValueSnapshot.Dword(8)); rb.Write(specs[1], RegistryValueSnapshot.Dword(9));
        var ta = Toggle("canary-recovery", ra, backups, a, specs); var tb = Toggle("canary-recovery", rb, backups, b, specs);
        Require((await ta.SetStateAsync(true)).Success, "A ON failed");
        var aPath = Path.Combine("registry", a, "canary-recovery.json");
        var aOriginal = backups.Files[aPath];
        backups.FailNextUserDelete = true;
        Require((await ta.SetStateAsync(false)).Success && await ta.GetStateAsync() == false, "restored values with failed original deletion did not show OFF");
        Require((await tb.SetStateAsync(true)).Success && backups.Files[aPath] == aOriginal, "completed restore blocked B ON or replaced A original");
        Require((await tb.SetStateAsync(false)).Success && ra.Read(specs[0]).EquivalentTo(RegistryValueSnapshot.Dword(7)), "B restored incorrect machine original after A deletion failure");
        Require((await ta.SetStateAsync(false)).Success, "A retained user original could not be retried");
        var preparation = new MemoryBackups { ThrowBeforeNextSharedWrite = true };
        var tp = Toggle("canary-preparation", ra, preparation, a, specs);
        Require(!(await tp.SetStateAsync(true)).Success && ra.Read(specs[0]).EquivalentTo(RegistryValueSnapshot.Dword(7)), "preparation failure wrote machine state");
        Require(preparation.Files.Count == 0, "initial missing shared write failure retained newly created user original");
        Require((await tp.SetStateAsync(true)).Success && (await tp.SetStateAsync(false)).Success && ra.Read(specs[0]).EquivalentTo(RegistryValueSnapshot.Dword(7)), "preparation retry lost original or remained blocked");
        return "Successful restore plus user-journal delete failure allowed B ON while preserving A original; initial shared-write failure recovered on ON retry";
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
    private static async Task<string> SharedExternalPreparationWorkflow(bool userPostSaveFault)
    {
        const string sid = "S-1-5-21-111-222-333-1001";
        RegistryToggleSpec[] specs = [new(RegistryHive.LocalMachine, "RERE_CANARY", "Machine", RegistryValueKind.DWord, 1), new(RegistryHive.CurrentUser, "RERE_CANARY", "User", RegistryValueKind.DWord, 1)];
        var registry = new MemoryRegistry(new Dictionary<string, RegistryValueSnapshot>(), sid);
        var backups = new MemoryBackups();
        registry.Write(specs[0], RegistryValueSnapshot.Dword(7)); registry.Write(specs[1], RegistryValueSnapshot.Dword(8));
        var toggle = Toggle("canary-external-preparation", registry, backups, sid, specs);
        Require((await toggle.SetStateAsync(true)).Success && (await toggle.SetStateAsync(false)).Success, "initial ON/OFF cycle failed");
        var sharedPath = Path.Combine("registry", "canary-external-preparation.json");
        var oldShared = backups.Files[sharedPath];
        registry.Write(specs[0], RegistryValueSnapshot.Dword(9));
        var attemptsBeforeFailure = registry.WriteAttempts;
        backups.ThrowBeforeNextSharedWrite = !userPostSaveFault;
        backups.ThrowAfterNextNewUserWrite = userPostSaveFault;
        Require(!(await toggle.SetStateAsync(true)).Success, "pre-replace shared write failure hidden");
        Require(registry.WriteAttempts == attemptsBeforeFailure, "failed preparation reached registry writes");
        Require(await toggle.GetStateAsync() == false && registry.Read(specs[0]).EquivalentTo(RegistryValueSnapshot.Dword(9)), "failed ON changed external machine value or UI state");
        Require(backups.Files[sharedPath] == oldShared, "failed pre-replace write modified old shared completion marker");
        Require(!backups.Files.ContainsKey(Path.Combine("registry", sid, "canary-external-preparation.json")), "failed preparation retained newly created user journal and blocked future ON");
        Require((await toggle.SetStateAsync(true)).Success && (await toggle.SetStateAsync(false)).Success, "ON retry remained blocked after unchanged shared write failure");
        Require(registry.Read(specs[0]).EquivalentTo(RegistryValueSnapshot.Dword(9)) && registry.Read(specs[1]).EquivalentTo(RegistryValueSnapshot.Dword(8)), "retry failed to capture latest external machine original");
        return "Completed cycle left shared=7/owner0; external value=9; " +
            (userPostSaveFault ? "post-save user validation failure" : "pre-replace shared write failure") +
            " canceled only new user journal, kept UI OFF/value9, retry ON/OFF restored latest value9";
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
        await vm.LoadDisconnectedDevicesAsync();
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
    private static Task<string> UwpWorkflow()
    {
        const string sid = "S-1-5-21-111-222-333-1001";
        var journal = new UwpBackgroundJournal(2, [UwpBackgroundJournalEntry.Create("RERE_CANARY_family", new(7, null), UwpBackgroundValues.Applied)], sid);
        var json = JsonSerializer.Serialize(journal);
        var own = UwpBackgroundJournalStore.LoadScoped(() => true, () => json, () => false, sid);
        var other = UwpBackgroundJournalStore.LoadScoped(() => true, () => json, () => false, "S-1-5-21-111-222-333-1002");
        var legacy = UwpBackgroundJournalStore.LoadScoped(() => false, () => throw new InvalidOperationException(), () => true, sid);
        Require(own.Status == UwpBackgroundJournalLoadStatus.Valid && other.Status == UwpBackgroundJournalLoadStatus.Invalid && legacy.Status == UwpBackgroundJournalLoadStatus.Invalid, "UWP ownership isolation failed");
        return Task.FromResult("Real scoped journal loader accepted own SID and rejected other SID/legacy ownerless original; in-memory data only");
    }
    private static async Task<string> UwpPairWorkflow()
    {
        const string sid = "S-1-5-21-111-222-333-1001", family = "GOGO_CANARY_A", other = "GOGO_CANARY_B";
        UwpBackgroundJournal? journal = null;
        var settings = new MemoryUwpSettings();
        UwpBackgroundToggle NewToggle() => new(() => new[] { family, other }, settings,
            () => journal is null ? new(UwpBackgroundJournalLoadStatus.Missing) : new(UwpBackgroundJournalLoadStatus.Valid, journal),
            saved => journal = saved with { UserScope = sid }, () => { journal = null; return true; });
        settings.Values[family] = new(null, 0); settings.Values[other] = new(0, 0);
        settings.FailPairs = 1;
        Require(!(await NewToggle().SetStateAsync(true)).Success && settings.Values[family] == new UwpBackgroundValues(null, 0) && journal is not null, "ON partial pair not compensated/journal lost");
        Require((await NewToggle().SetStateAsync(false)).Success && journal is null, "fresh OFF did not retire already compensated entries");
        Require((await NewToggle().SetStateAsync(true)).Success, "normal UWP ON failed");
        settings.FailPairs = 1;
        Require(!(await NewToggle().SetStateAsync(false)).Success && settings.Values[family] == UwpBackgroundValues.Applied && journal is not null, "OFF partial pair did not compensate to immediate ON");
        Require((await NewToggle().SetStateAsync(false)).Success && settings.Values[family] == new UwpBackgroundValues(null, 0), "OFF retry lost null/value original");
        settings.FailPairs = 2;
        Require(!(await NewToggle().SetStateAsync(true)).Success && settings.Values[family] == new UwpBackgroundValues(1, 0), "compensation failure did not retain partial pair");
        var original = JsonSerializer.Serialize(journal);
        Require(!(await NewToggle().SetStateAsync(true)).Success && JsonSerializer.Serialize(journal) == original, "fresh ON overwrote ambiguous original");
        settings.Values[other] = UwpBackgroundValues.Applied;
        Require(await NewToggle().GetStateAsync() == true, "other applied package could not select OFF");
        settings.FailPairs = 0; settings.FailBeforeFirstValue = false;
        Require(!(await NewToggle().SetStateAsync(false)).Success && journal?.Entries?.Count == 1 && settings.Values[other] == new UwpBackgroundValues(0, 0), "OFF did not restore safe other entry while retaining ambiguous one");
        Require(await NewToggle().GetStateAsync() is null && settings.Values[family] == new UwpBackgroundValues(1, 0), "ambiguous-only state overwritten or incorrectly known");
        settings.Values[family] = new(7, 8);
        Require((await NewToggle().SetStateAsync(false)).Success && settings.Values[family] == new UwpBackgroundValues(7, 8) && journal is null, "clear external change was overwritten");
        settings.FailPairs = 1; settings.ExternalOnFailure = new(7, 8);
        Require(!(await NewToggle().SetStateAsync(true)).Success && settings.Values[family] == new UwpBackgroundValues(7, 8) && journal is not null, "external change during write failure overwritten by compensation");
        return "Actual UWP ON/OFF with per-DWORD failures: immediate compensation, null original restoration, fresh-instance ambiguous journal retained, safe other entry restored, unknown state and external changes protected; schema2 in memory";
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
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var oem = (Encoding)typeof(ProcessCommandExecutor).GetField("OemEncoding", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var bytes = Enumerable.Range(128, 64).Select(value => new[] { (byte)value })
            .Concat(Enumerable.Range(128, 64).SelectMany(first => Enumerable.Range(0, 256).Select(second => new[] { (byte)first, (byte)second })))
            .First(candidate => oem.GetBytes(oem.GetString(candidate)).SequenceEqual(candidate));
        try { _ = new UTF8Encoding(false, true).GetString(bytes); throw new InvalidOperationException("OEM canary was valid UTF-8"); }
        catch (DecoderFallbackException) { }
        Require(ProcessCommandExecutor.DecodeConsoleOutput(bytes) == oem.GetString(bytes), "effective OEM output decoding changed");
        var diagnostic = CommandFailureDiagnostic.Format(MockExecutor.Result(false, "RERE_CANARY 診断", "token=RERE_CANARY_SECRET"));
        Require(diagnostic.Contains("exit=5") && diagnostic.Contains("RERE_CANARY 診断") && !diagnostic.Contains("RERE_CANARY_SECRET"), "failed-tool diagnosis lost stdout or exposed secret");
        return Task.FromResult($"Pure parser rejected wrapper/access denied; effective OEM {oem.CodePage} decoded; failure exit/stdout retained and synthetic secret redacted; no shell execution");
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
        foreach (var path in locales)
        {
            var keys = XDocument.Load(path).Descendants().Select(e => (string?)e.Attribute(x + "Key")).Where(k => k is not null).ToArray();
            Require(keys.Distinct().Count() == keys.Length && added.All(key => keys.Contains("Text." + key)), $"missing/duplicate locale keys: {Path.GetFileName(path)}");
        }
        var views = Directory.GetFiles(Path.Combine(_repo, "src/Lumin4ti.UI/Views"), "*.axaml").Select(File.ReadAllText).ToArray();
        Require(views.Any(text => text.Contains("AutomationProperties.Name")), "no accessibility names found");
        Require(File.ReadAllText(Path.Combine(_repo, "src/Lumin4ti.UI/Services/Lumin4tiUpdateStrings.cs")).Contains("Title => App.Text"), "cached update title remains");
        var actionButtons = XDocument.Load(Path.Combine(_repo, "src/Lumin4ti.UI/Views/CommandCategoryView.axaml"))
            .Descendants().Where(element => element.Name.LocalName == "Button" && element.Attributes().Any(attribute => attribute.Name.LocalName == "Command" && (attribute.Value == "{Binding RunCommand}" || attribute.Value == "{Binding CancelCommand}"))).ToArray();
        Require(actionButtons.Length == 4 && actionButtons.All(button => button.Attributes().Any(attribute => attribute.Name.LocalName == "AutomationProperties.Name" && attribute.Value == (button.Attribute("Command")!.Value.Contains("RunCommand") ? "{Binding RunAutomationName}" : "{Binding CancelAutomationName}"))), "parent/child run/cancel names not correctly bound");
        return Task.FromResult("Supplement only: 17 XML dictionaries parsed, 14 required keys/no duplicates, getter and automation-name source present; this is not GUI accessibility validation");
    }
    private static Task<string> LocaleWorkflow()
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
        var vm = new VersionViewModel(new UpdateService(settings.Current), settings, new MaintenanceOperationCoordinator(() => new NoopLease()));
        var setStatus = typeof(VersionViewModel).GetMethod("SetUpdateStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;
        setStatus.Invoke(vm, ["UpdateDialog.CheckFailed", "検証失敗: {0}", new object[] { "RERE_CANARY" }]);
        var instance = Lumin4tiUpdateStrings.Instance;
        var toggleExecutor = new MockExecutor((_, _, _) => MockExecutor.Result(stdout: "{}"));
        var genericToggle = Vm(new MmAgentFeatureToggle(toggleExecutor, new MmAgentStateProvider(toggleExecutor), "MemoryCompression", "gogo-locale", "検証", "検証"));
        var genericChoice = new CommandItemViewModel(new MmAgentOperationApiChoice(toggleExecutor, new MmAgentStateProvider(toggleExecutor)), _ => Task.CompletedTask, (_, _) => Task.CompletedTask);
        var specific = Vm(genericToggle.Item);
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
            Require(App.Text("RERE_CANARY_MISSING", "fallback {0}", "RERE_CANARY") == "fallback RERE_CANARY", "missing key fallback changed");
            const string unknownFallback = "状態を取得できませんでした (管理者権限がないか、この PC では利用できない機能です。デバッグ起動は昇格されないため、通常起動でお試しください)";
            Require(genericToggle.StateErrorText == expected[locale.Key].GetValueOrDefault("Text.Toggle.StateUnknown", unknownFallback) && genericChoice.StateErrorText == genericToggle.StateErrorText, "generic toggle/choice error remained in old language: " + locale.Key);
            Require(specific.StateErrorText == "GOGO_CANARY specific IOException", "locale switch replaced explicit diagnostic");
            Require(genericToggle.RunAutomationName.Contains(App.Text("Button.Run", "実行")) && genericToggle.CancelAutomationName.Contains(App.Text("Button.Cancel", "中断")) && genericToggle.RunAutomationName != genericToggle.CancelAutomationName, "action automation identities ambiguous/stale");
            Require(new[] { "StateErrorText", "RunAutomationName", "CancelAutomationName" }.All(notifications.Contains), "locale change missed property notification");
        }
        genericToggle.ApplyState(true); genericChoice.ApplySelectedValue("512");
        Require(genericToggle.StateErrorText.Length == 0 && genericChoice.StateErrorText.Length == 0 && genericToggle.ResultText == "GOGO_CANARY previous result", "known state recovery did not clear only diagnosis");
        setStatus.Invoke(vm, [null, "", Array.Empty<object>()]);
        Require(vm.UpdateStatusText.Length == 0, "status reset retained translated text");
        return Task.FromResult("Actual App.SetLocale via VersionVM.SelectedLocale: 17 switches saved to memory, same singleton 8 getters and retained failure status relocalized; XML-derived dictionaries, no GUI lifetime");
    }
}
