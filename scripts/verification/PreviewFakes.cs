using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.Core.Services.Windows.Actions;
using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.Core.Services.Windows.Cleanup.Slim;
using Lumin4ti.UI.Services;
using Lumin4ti.UI.ViewModels;
using Lumin4ti.UI.Views;

namespace Lumin4ti.Verification;

internal enum PreviewRestoreMode
{
    Created,
    ProtectionDisabled,
    Failed,
}

/// <summary>プレビューのツールバーで切り替える偽の状態 (実機の状態には触れない)。</summary>
internal sealed class PreviewToggles
{
    /// <summary>false にすると、レジストリの走査が HKCU を走査できない注記を返す。</summary>
    public bool HkcuAvailable { get; set; } = true;

    /// <summary>復元ポイントの偽の結果 (作成可・保護無効・失敗)。</summary>
    public PreviewRestoreMode RestoreMode { get; set; } = PreviewRestoreMode.Created;
}

internal static class PreviewRestorePoints
{
    public static RestorePointResult Result(PreviewToggles toggles) => toggles.RestoreMode switch
    {
        PreviewRestoreMode.Created => new(RestorePointOutcome.Created, "preview: 復元ポイントは作成していません (偽)", null),
        PreviewRestoreMode.ProtectionDisabled => new(RestorePointOutcome.ProtectionDisabled, "システムの保護が無効です (プレビューの偽の結果)", null),
        _ => new(RestorePointOutcome.Failed, "復元ポイントを作成できませんでした (プレビューの偽の結果)", null),
    };
}

/// <summary>
/// 外部プロセスの偽の境界。DISM の解析・整理・言語の削除は合成の出力を返し、rstrui は何も起動せずに成功とする。
/// それ以外はプロセスを起動せず例外にする (想定外の経路を見つけるため)。
/// </summary>
internal sealed class PreviewExecutor : ICommandExecutor
{
    private const string Done = "The operation completed successfully.";

    public async Task<CommandExecutionResult> RunAsync(
        string fileName, string arguments, CancellationToken ct = default, IProgress<string>? onOutputLine = null, TimeSpan? timeout = null)
    {
        var name = Path.GetFileName(fileName);
        if (name.Equals("dism.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Contains("/AnalyzeComponentStore", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(900, ct).ConfigureAwait(false);
                return Ok("""
                    Component Store (WinSxS) information:
                    Windows Explorer Reported Size of Component Store : 9.12 GB
                    Actual Size of Component Store : 7.40 GB
                        Shared with Windows : 5.10 GB
                        Backups and Disabled Features : 1.80 GB
                        Cache and Temporary Data : 512.00 MB
                    Date of Last Cleanup : 2026-01-15 10:00:00
                    Number of Reclaimable Packages : 3
                    Component Store Cleanup Recommended : Yes
                    """);
            }

            if (arguments.Contains("/Get-Capabilities", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                return Ok("""
                    Capability Identity : Language.Basic~~~ja-JP~0.0.1.0
                    State : Installed
                    Capability Identity : Language.Basic~~~ko-KR~0.0.1.0
                    State : Installed
                    Capability Identity : Language.Basic~~~zh-TW~0.0.1.0
                    State : Not Present
                    Capability Identity : Language.Basic~~~zh-CN~0.0.1.0
                    State : Installed
                    """);
            }

            if (arguments.Contains("/StartComponentCleanup", StringComparison.OrdinalIgnoreCase) ||
                arguments.Contains("/Remove-Capability", StringComparison.OrdinalIgnoreCase))
            {
                for (var i = 1; i <= 4; i++)
                {
                    onOutputLine?.Report($"[{i * 25}%] preview: DISM を模擬しています (実行していません)");
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }

                return Ok(string.Empty);
            }
        }

        if (name.Equals("rstrui.exe", StringComparison.OrdinalIgnoreCase))
        {
            return new CommandExecutionResult(true, "preview: rstrui (not started)", 0, string.Empty, string.Empty);
        }

        throw new InvalidOperationException($"preview does not run processes: {name}");
    }

    private static CommandExecutionResult Ok(string output) =>
        new(true, "preview: simulated", 0, output + Environment.NewLine + Done + Environment.NewLine, string.Empty);
}

/// <summary>スリム化の偽の部品 (登録・利用者の言語・保護状態・復元ポイント・サービス停止)。</summary>
internal sealed class PreviewInstallerRegistrations(string registeredFile) : IInstallerRegistrationSource
{
    public InstallerRegistrationSnapshot Read(CancellationToken ct) =>
        new(new HashSet<string>([registeredFile], StringComparer.OrdinalIgnoreCase), null);
}

internal sealed class PreviewUserLanguages : IUserLanguageSource
{
    public UserLanguageSnapshot Read() => new(["ja-JP", "en-US"], [0x0411], null);
}

internal sealed class PreviewProtectedFileProbe : IProtectedFileProbe
{
    public FileProtectionState Probe(NativeCleanupEntry file) => FileProtectionState.Unprotected;
}

internal sealed class PreviewSlimRestorePoints(PreviewToggles toggles) : ISlimRestorePoints
{
    public async Task<RestorePointResult> CreateAsync(string description, CancellationToken ct)
    {
        await Task.Delay(700, ct).ConfigureAwait(false);
        return PreviewRestorePoints.Result(toggles);
    }

    public Task<RestorePointProtectionResult> EnableProtectionAsync(CancellationToken ct)
    {
        toggles.RestoreMode = PreviewRestoreMode.Created;
        return Task.FromResult(new RestorePointProtectionResult(true, "preview: システムの保護を有効にしていません (偽)"));
    }
}

internal sealed class PreviewServiceSuspender(ICommandExecutor executor) : ISlimServiceSuspender
{
    public async Task<ServiceSuspension> SuspendAsync(IReadOnlyList<string> serviceNames, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("  - preview: サービスは停止していません (偽)");
        await Task.Delay(300, ct).ConfigureAwait(false);
        return new ServiceSuspension(executor, [], []);
    }
}

/// <summary>
/// 設定の保存先を包み、読まれるたびにカスタムフォルダーの一覧から、合成フォルダーの中でないものを取り除く
/// (<see cref="PreviewSandbox.IsInside"/>)。高度タブの走査の起点も、削除の対象も、この一覧から決まる。
/// 設定はすべてここを通して読むため、外のパスは走査・削除のどちらにも届かない。
/// </summary>
internal sealed class SandboxedSettings(ISettingsService inner, PreviewSandbox sandbox) : ISettingsService
{
    private readonly List<string> _removed = [];

    /// <summary>取り除いたパス (自己検査の報告用)。</summary>
    public IReadOnlyList<string> Removed
    {
        get { lock (inner.SyncRoot) return [.. _removed]; }
    }

    public AppSettings Current
    {
        get
        {
            lock (inner.SyncRoot)
            {
                if (inner.Current.Cleanup is { } cleanup)
                {
                    foreach (var path in cleanup.CustomFolders.Where(p => !sandbox.IsInside(p)).ToList())
                    {
                        _removed.Add(path);
                        cleanup.CustomFolders.Remove(path);
                    }
                }

                return inner.Current;
            }
        }
    }

    public SettingsLoadStatus LoadStatus => inner.LoadStatus;

    public object SyncRoot => inner.SyncRoot;

    public Task SaveAsync(CancellationToken ct = default) => inner.SaveAsync(ct);

    public Task FlushAsync(CancellationToken ct = default) => inner.FlushAsync(ct);
}

/// <summary>起動中の製品の確認 (モーダルの窓) を出さず、その規則を飛ばす・強制終了に同意しない。窓を前面に出さないための偽。</summary>
internal sealed class PreviewBackgroundPrompts : ICleanupRunPrompts
{
    public Task<CleanupProcessChoice> ChooseForRunningProcessesAsync(CleanupProcessPrompt prompt, CancellationToken ct) =>
        Task.FromResult(CleanupProcessChoice.SkipRule);

    public Task<bool> ConfirmForceTerminationAsync(CleanupProcessPrompt prompt, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>通常タブの偽の走査と実行。合成データを返し、実ファイルには触れない。</summary>
internal static class PreviewCommonServices
{
    private const long Mb = 1024 * 1024;

    /// <param name="generation">世代の管理だけに使う (走査も削除もしない)。</param>
    /// <param name="background">true なら、起動中の製品の確認を窓 (モーダル) で出さず、その規則を飛ばす (前面に出さない)。</param>
    public static CommonCleanServices Create(CleanupScanService generation, PreviewSandbox sandbox, bool background) => new(
        (progress, ct) => ScanAsync(generation, sandbox, progress, ct),
        (request, progress, ct) => RunAsync(generation, request, progress, ct),
        snapshot => snapshot.Generation == generation.Generation,
        _ => Task.CompletedTask,
        background ? new PreviewBackgroundPrompts() : new CommonCleanProcessPrompts(),
        action => Avalonia.Threading.Dispatcher.UIThread.Post(action));

    private static CleanupRuleInfo Rule(
        string id, string name, CleanupRuleKind kind, CleanupRuleCategory category, CleanupRuleImpact impact, bool recommended,
        string[]? processes = null, bool custom = false) => new(
        id, name, null, kind, category, impact, recommended, null, custom, processes ?? [], [], category is CleanupRuleCategory.BrowserCache or CleanupRuleCategory.Cookies);

    private static CleanupScanItem Found(
        CleanupRuleInfo rule, long files, long dirs, long bytes, string[] representatives, string[]? running = null,
        long retained = 0, long unreadable = 0, long links = 0, long items = 0, string[]? detail = null) => new(
        rule, CleanupItemState.Found, CleanupUnavailableReason.None, files, dirs, items, bytes, retained, unreadable, links,
        rule.Recommended && rule.Impact != CleanupRuleImpact.UserFiles && !rule.IsCustom,
        representatives, running ?? [], detail ?? []);

    private static CleanupScanItem Other(CleanupRuleInfo rule, CleanupItemState state, CleanupUnavailableReason reason, string[]? detail = null, long items = 0) => new(
        rule, state, reason, 0, 0, items, 0, 0, 0, 0, false, [], [], detail ?? []);

    private static async Task<CleanupScanSnapshot> ScanAsync(
        CleanupScanService generation, PreviewSandbox sandbox, IProgress<CleanupScanProgress> progress, CancellationToken ct)
    {
        generation.Invalidate();
        var id = generation.Generation;
        var items = new List<CleanupScanItem>
        {
            Found(Rule("win-temp", "Windows", CleanupRuleKind.Temp, CleanupRuleCategory.Windows, CleanupRuleImpact.Junk, true),
                1240, 85, 640 * Mb, [@"C:\Users\preview\AppData\Local\Temp\~DF1A2B.tmp", @"C:\Windows\Temp\MpCmdRun.log", @"C:\Users\preview\AppData\Local\Temp\setup-0042.exe"],
                retained: 6, unreadable: 2),
            Found(Rule("win-thumb", "Windows", CleanupRuleKind.Thumbnail, CleanupRuleCategory.Windows, CleanupRuleImpact.Rebuild, true),
                36, 0, 212 * Mb, [@"C:\Users\preview\AppData\Local\Microsoft\Windows\Explorer\thumbcache_256.db"]),
            Found(Rule("win-dumps", "Windows", CleanupRuleKind.Dump, CleanupRuleCategory.Windows, CleanupRuleImpact.Junk, false),
                9, 1, 1_850 * Mb, [@"C:\Windows\MEMORY.DMP", @"C:\Users\preview\AppData\Local\CrashDumps\app.exe.4242.dmp"]),
            Found(Rule("win-logs", "Windows", CleanupRuleKind.Log, CleanupRuleCategory.Windows, CleanupRuleImpact.Junk, true),
                402, 31, 96 * Mb, [@"C:\Windows\Logs\CBS\CBS.log", @"C:\Windows\Logs\DISM\dism.log"], links: 3),
            Other(Rule("win-reports", "Windows", CleanupRuleKind.Report, CleanupRuleCategory.Windows, CleanupRuleImpact.Junk, true), CleanupItemState.Empty, CleanupUnavailableReason.None),
            Other(Rule("win-dns", "DNS", CleanupRuleKind.Cache, CleanupRuleCategory.Windows, CleanupRuleImpact.Rebuild, true), CleanupItemState.Unmeasurable, CleanupUnavailableReason.None, items: 0),
            Found(Rule("store-cache", "Microsoft Store", CleanupRuleKind.Cache, CleanupRuleCategory.StoreApps, CleanupRuleImpact.Rebuild, true),
                58, 4, 41 * Mb, [@"C:\Users\preview\AppData\Local\Packages\Microsoft.WindowsStore_8wekyb3d8bbwe\LocalCache"]),
            Found(Rule("contoso-cache", "Contoso Browser", CleanupRuleKind.Cache, CleanupRuleCategory.BrowserCache, CleanupRuleImpact.Junk, true, ["contosobrowser", "contosoupdate"]),
                8_320, 640, 1_240 * Mb, [@"C:\Users\preview\AppData\Local\Contoso\Browser\Cache\f_0001a2", @"C:\Users\preview\AppData\Local\Contoso\Browser\Code Cache\js\index"],
                running: ["contosobrowser"]),
            Found(Rule("fabrikam-cache", "Fabrikam Browser", CleanupRuleKind.Cache, CleanupRuleCategory.BrowserCache, CleanupRuleImpact.Junk, true, ["fabrikam"]),
                1_204, 120, 318 * Mb, [@"C:\Users\preview\AppData\Local\Fabrikam\Cache\data_1"]),
            Found(Rule("contoso-history", "Contoso Browser", CleanupRuleKind.History, CleanupRuleCategory.BrowserTrace, CleanupRuleImpact.Trace, false, ["contosobrowser"]),
                3, 0, 4 * Mb, [@"C:\Users\preview\AppData\Local\Contoso\Browser\User Data\Default\History"], running: ["contosobrowser"], items: 5_420),
            Found(Rule("contoso-cookies", "Contoso Browser", CleanupRuleKind.Cookies, CleanupRuleCategory.Cookies, CleanupRuleImpact.Signout, false, ["contosobrowser"]),
                2, 0, 2 * Mb, [@"C:\Users\preview\AppData\Local\Contoso\Browser\User Data\Default\Network\Cookies"], running: ["contosobrowser"], items: 812),
            Found(Rule("recent-files", "Windows", CleanupRuleKind.Recent, CleanupRuleCategory.Traces, CleanupRuleImpact.Trace, false),
                214, 0, 1 * Mb, [@"C:\Users\preview\AppData\Roaming\Microsoft\Windows\Recent\report.docx.lnk"]),
            Found(Rule("shader-cache", "DirectX", CleanupRuleKind.Shader, CleanupRuleCategory.Components, CleanupRuleImpact.Rebuild, true),
                77, 3, 530 * Mb, [@"C:\Users\preview\AppData\Local\D3DSCache\1a2b3c\cache.bin"]),
            Other(Rule("fabrikam-logs", "Fabrikam Sync", CleanupRuleKind.Log, CleanupRuleCategory.Apps, CleanupRuleImpact.Junk, true),
                CleanupItemState.Unavailable, CleanupUnavailableReason.TargetRejected, ["保護された場所のため対象にしませんでした (プレビューの合成データ)"]),
            Found(Rule("downloads-old", "ダウンロード", CleanupRuleKind.Downloads, CleanupRuleCategory.Apps, CleanupRuleImpact.UserFiles, false),
                48, 6, 2_300 * Mb, [@"C:\Users\preview\Downloads\installer-1.2.3.exe", @"C:\Users\preview\Downloads\photos.zip"]),
            Found(Rule("custom-preview", sandbox.CustomFolder, CleanupRuleKind.Other, CleanupRuleCategory.Custom, CleanupRuleImpact.UserFiles, false, custom: true),
                6, 0, 126 * 1024, [Path.Combine(sandbox.CustomFolder, "custom-file-01.dat")]),
        };

        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new CleanupScanProgress(i + 1, items.Count, items[i].Rule.Id, items[i].Rule.Name));
            await Task.Delay(220, ct).ConfigureAwait(false);
        }

        return new CleanupScanSnapshot(id, DateTime.UtcNow, CleanupScopeStatus.Available, items, new Dictionary<string, RulePlan>(), null!);
    }

    private static readonly HashSet<string> PartialIds = new(["win-temp", "contoso-cache"], StringComparer.OrdinalIgnoreCase);

    private static async Task<CleanupRunResult> RunAsync(
        CleanupScanService generation, CleanupRunRequest request, IProgress<string> progress, CancellationToken ct)
    {
        var started = DateTimeOffset.Now;
        if (request.Snapshot.Generation != generation.Generation)
        {
            return new CleanupRunResult([], true, false, started, DateTimeOffset.Now);
        }

        generation.Invalidate();
        var selected = request.Snapshot.Items
            .Where(i => request.SelectedRuleIds.Contains(i.Rule.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var results = new List<CleanupRuleRunResult>();
        var stopped = false;
        var canceled = false;
        foreach (var item in selected)
        {
            if (stopped)
            {
                results.Add(Result(item, CleanupRunStatus.NotRun, CleanupSkipReason.None, 0, 0, 0, 0, []));
                continue;
            }

            var rule = item.Rule;
            if (!item.CanSelect)
            {
                results.Add(Result(item, CleanupRunStatus.Skipped, CleanupSkipReason.Unavailable, 0, 0, 0, 0, []));
                continue;
            }

            if (rule.Impact == CleanupRuleImpact.UserFiles && !request.ConfirmedUserFileRuleIds.Contains(rule.Id, StringComparer.OrdinalIgnoreCase))
            {
                results.Add(Result(item, CleanupRunStatus.Skipped, CleanupSkipReason.UserFilesNotConfirmed, 0, 0, 0, 0, []));
                continue;
            }

            if (item.RunningProcesses.Count > 0 && request.Prompts is { } prompts)
            {
                var choice = await prompts.ChooseForRunningProcessesAsync(new CleanupProcessPrompt(rule.Id, rule.Name, item.RunningProcesses), ct).ConfigureAwait(false);
                if (choice == CleanupProcessChoice.SkipRule)
                {
                    results.Add(Result(item, CleanupRunStatus.Skipped, CleanupSkipReason.ProcessRunningDeclined, 0, 0, 0, 0, []));
                    continue;
                }

                if (choice == CleanupProcessChoice.Abort)
                {
                    results.Add(Result(item, CleanupRunStatus.NotRun, CleanupSkipReason.None, 0, 0, 0, 0, []));
                    stopped = true;
                    continue;
                }
            }

            progress.Report($"  - {rule.Name}: {item.Files:N0} files (preview)");
            try
            {
                await Task.Delay(900, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                results.Add(Result(item, CleanupRunStatus.Canceled, CleanupSkipReason.None, item.Files / 3, 0, item.Items, item.Bytes / 3, ["取消されました (プレビューの偽の結果。実ファイルは削除していません)"]));
                stopped = true;
                canceled = true;
                continue;
            }

            results.Add(PartialIds.Contains(rule.Id)
                ? Result(item, CleanupRunStatus.Partial, CleanupSkipReason.None, item.Files * 9 / 10, 0, item.Items, item.Bytes * 9 / 10,
                    [$"権限不足で削除できなかったもの: {item.Files / 10:N0} 件 (例: {item.Representatives.FirstOrDefault()})"], blocked: item.Files / 10)
                : Result(item, CleanupRunStatus.Succeeded, CleanupSkipReason.None, item.Files, item.Directories, item.Items, item.Bytes, []));
        }

        return new CleanupRunResult(results, false, canceled, started, DateTimeOffset.Now);
    }

    private static CleanupRuleRunResult Result(
        CleanupScanItem item, CleanupRunStatus status, CleanupSkipReason skip, long files, long dirs, long items, long bytes,
        string[] details, long blocked = 0) => new(
        item.Rule, status, skip, files, dirs, items, bytes, item.Retained, blocked, 0, 0, details);
}

/// <summary>クリーンアップ画面の偽の組み立て。実ファイル・実レジストリ・復元ポイント・サービスに届く経路を持たない。</summary>
internal static class PreviewCleanupFactory
{
    /// <param name="background">自己検査・書き出しのとき true。モーダルのダイアログを出す経路を、窓を出さない偽に替える。</param>
    public static CleanupViewModel Create(
        MaintenanceActionCatalog catalog, ISettingsService settings, MaintenanceOperationCoordinator coordinator,
        PreviewSandbox sandbox, PreviewToggles toggles, bool background = false)
    {
        var executor = new PreviewExecutor();
        // 高度タブの走査の起点・削除の対象になるカスタムフォルダーは、保存先の段階で合成フォルダーの外を必ず除く
        // (設定タブで足した値も、settings.json を直接書き換えられた値も、読まれる前に外れる)。
        var store = new CleanupSettingsStore(new SandboxedSettings(settings, sandbox));

        // 通常タブ: 世代の管理にだけ使う (走査・削除は偽)。
        var scan = new CleanupScanService(store, executor);
        var common = new CommonCleanViewModel(scan, null!, store, executor, coordinator, PreviewCommonServices.Create(scan, sandbox, background));

        // 高度タブ: 走査の起点も、カスタムフォルダーの許可も、プレビュー専用の一時フォルダーだけ。
        // 検査の層は 3 つ: (1) 保存先の除去 (SandboxedSettings)、(2) ドライブ一覧の絞り込み、
        // (3) 製品の検査 (全ドライブの直下を「他の利用者のプロファイル」として拒否し、合成フォルダーだけを現在のプロファイルにする)。
        var guard = new CustomFolderGuardContext(null, [], null, [.. PreviewSandbox.AllDriveRoots(sandbox)], sandbox.Root);
        var ledger = new AdvancedScanLedger();
        // 枝刈りは名前だけの規則 (.git・Credentials など)。実環境のフォルダーは渡さない。
        var protection = new AdvancedProtectedPaths(new AdvancedProtectionContext(new CustomFolderGuardContext(null, [], null, [], null), []));
        var advanced = new AdvancedCleanViewModel(new AdvancedCleanScanner(ledger), new AdvancedCleaner(ledger), ledger, store, executor, coordinator)
        {
            DriveProvider = () => sandbox.IsInside(sandbox.AdvancedRoot)
                ? [new AdvancedDrive(sandbox.AdvancedRoot, "プレビュー用の合成フォルダー", DriveType.Fixed, true)]
                : [],
            OpenFolder = _ => Task.CompletedTask,
            Protection = protection,
            GuardContext = guard,
        };

        // スリム化: 場所は一時フォルダー内の偽の %SystemRoot%、外部の操作はすべて偽。
        var slim = new SlimService(new SlimServiceParts(
            new SlimPaths(sandbox.SlimWindows, sandbox.SlimPublic),
            new InstallerOrphanScanner(sandbox.InstallerDirectory, [new PreviewInstallerRegistrations(sandbox.RegisteredInstallerFile)]),
            new ComponentStore(executor),
            new LanguageCapabilityService(executor, new PreviewUserLanguages()),
            new ProtectedSystemFiles(new PreviewProtectedFileProbe()),
            new PreviewSlimRestorePoints(toggles),
            new PreviewServiceSuspender(executor)));

        // レジストリ: 実レジストリもバックアップ置き場も使わない偽。
        var state = new PreviewRegistryState(toggles);
        var registry = new RegistryCleanViewModel(new PreviewRegistryCleanBackend(state), coordinator);
        var restore = new RegistryRestoreViewModel(new PreviewRegistryRestoreBackend(state), registry, coordinator);

        var cleanupSettings = new CleanupSettingsViewModel(store, scan, ledger, slim, executor, coordinator) { GuardContext = guard };
        return new CleanupViewModel(catalog, coordinator, common, advanced, new SlimViewModel(slim, store, coordinator), registry, restore, cleanupSettings);
    }
}
