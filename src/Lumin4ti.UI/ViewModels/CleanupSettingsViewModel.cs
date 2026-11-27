using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.Core.Services.Windows.Cleanup.Slim;
using Lumin4ti.UI.Services;

namespace Lumin4ti.UI.ViewModels;

/// <summary>
/// 「設定」タブ。除外 4 種 (ファイル・フォルダー・拡張子/ファイル名・Cookie ドメイン) とインポート・エクスポート、
/// 安全な削除、復元ポイント、カスタムフォルダー、ユーザー定義パターン、項目ごとの既定の選択、実績。
/// 設定を変えたら、走査結果を失効させるため <see cref="Scan"/>・<see cref="Ledger"/>・<see cref="Slim"/> の
/// <c>Invalidate</c> を呼ぶ。<see cref="Executor"/> は「システムの復元を開く」(rstrui.exe) の起動に使う。
/// 走査・削除などの操作 (lease) の実行中は変更できない。保存に失敗しても画面は止めず、状態を表示する。
/// </summary>
public sealed partial class CleanupSettingsViewModel : CleanupOperationViewModel
{
    private static readonly CleanupExclusionKind[] ExclusionKinds =
    [
        CleanupExclusionKind.File,
        CleanupExclusionKind.Folder,
        CleanupExclusionKind.Name,
        CleanupExclusionKind.CookieDomain,
    ];

    private List<CleanupRuleInfo> _rules = [];
    private bool _loading;
    private Func<string>? _status;

    public CleanupSettingsViewModel(
        CleanupSettingsStore settings,
        CleanupScanService scan,
        AdvancedScanLedger ledger,
        SlimService slim,
        ICommandExecutor executor,
        MaintenanceOperationCoordinator coordinator)
    {
        Settings = settings;
        Scan = scan;
        Ledger = ledger;
        Slim = slim;
        Executor = executor;
        Coordinator = coordinator;
        ExclusionGroups = [.. ExclusionKinds.Select(k => new CleanupSettingsExclusionGroupViewModel(k, this))];
        AddCustomFolderCommand = new AsyncRelayCommand(() => AddCustomFolderAsync(NewCustomFolder));
        AddPatternCommand = new AsyncRelayCommand(AddPatternAsync);
        ResetRulesCommand = new AsyncRelayCommand(ResetRulesAsync);
        OpenSystemRestoreCommand = new AsyncRelayCommand(OpenSystemRestoreAsync, () => !IsOpeningSystemRestore);
        coordinator.StateChanged += OnCoordinatorStateChanged;
        SetRules(CleanupRuleDirectory.All());
        Reload();
        App.LocaleChanged += () => PostToUi(OnLocaleChanged);
    }

    internal CleanupSettingsStore Settings { get; }

    internal CleanupScanService Scan { get; }

    internal AdvancedScanLedger Ledger { get; }

    internal SlimService Slim { get; }

    internal ICommandExecutor Executor { get; }

    internal MaintenanceOperationCoordinator Coordinator { get; }

    /// <summary>カスタムフォルダーの検査に使う環境。null なら実環境 (テストで差し替える)。</summary>
    internal CustomFolderGuardContext? GuardContext { get; set; }

    public IReadOnlyList<CleanupSettingsExclusionGroupViewModel> ExclusionGroups { get; }

    public ObservableCollection<CleanupSettingsEntryViewModel> CustomFolders { get; } = [];

    public ObservableCollection<CleanupSettingsPatternViewModel> Patterns { get; } = [];

    public ObservableCollection<CleanupSettingsRuleViewModel> Rules { get; } = [];

    /// <summary>項目ごとの既定の選択の選択肢 (言語を切り替えたら引き直す)。</summary>
    public IReadOnlyList<string> RuleChoices =>
    [
        CleanupText.Get("Cleanup.Settings.Rule.Default"),
        CleanupText.Get("Cleanup.Settings.Rule.On"),
        CleanupText.Get("Cleanup.Settings.Rule.Off"),
    ];

    public IAsyncRelayCommand AddCustomFolderCommand { get; }

    public IAsyncRelayCommand AddPatternCommand { get; }

    public IAsyncRelayCommand ResetRulesCommand { get; }

    public IAsyncRelayCommand OpenSystemRestoreCommand { get; }

    /// <summary>走査・削除などの操作がない間だけ設定を変えられる。</summary>
    public bool IsEditable => Coordinator.ActiveCount == 0;

    public bool IsBusy => !IsEditable;

    public bool HasRules => Rules.Count > 0;

    public string SaveSuppressedText => CleanupText.Get("Cleanup.Settings.SaveSuppressed");

    /// <summary>ユーザー定義パターン入力欄の例 (透かし文字)。</summary>
    public string PatternHint => CleanupText.Get("Cleanup.Settings.Patterns.Hint");

    [ObservableProperty]
    private string newCustomFolder = string.Empty;

    [ObservableProperty]
    private string newPattern = string.Empty;

    [ObservableProperty]
    private bool secureDelete;

    [ObservableProperty]
    private bool createRestorePoint;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private bool isStatusError;

    /// <summary>設定ファイルを読めず、原本を守るために保存しない状態。変更はこの起動中だけ有効。</summary>
    [ObservableProperty]
    private bool isSaveSuppressed;

    [ObservableProperty]
    private string totalsText = string.Empty;

    [ObservableProperty]
    private string lastRunText = string.Empty;

    [NotifyCanExecuteChangedFor(nameof(OpenSystemRestoreCommand))]
    [ObservableProperty]
    private bool isOpeningSystemRestore;

    /// <summary>
    /// 項目ごとの既定の選択の一覧を差し替える。通常は構築時に Core の全規則 (<see cref="CleanupRuleDirectory"/>) を使うため、
    /// 走査の前でも全規則が並ぶ。テストが一部の規則だけで確かめるときにも使う。
    /// </summary>
    public void LoadRules(IEnumerable<CleanupRuleInfo> rules)
    {
        SetRules(rules);
        RefreshRules();
    }

    private void SetRules(IEnumerable<CleanupRuleInfo> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules =
        [
            .. rules.GroupBy(r => r.Id, StringComparer.Ordinal).Select(g => g.First())
                .OrderBy(r => r.Category).ThenBy(r => CleanupText.RuleName(r), StringComparer.CurrentCultureIgnoreCase),
        ];
    }

    /// <summary>保存されている設定から画面を作り直す (タブを開いたとき・実行後の実績の更新に使う)。</summary>
    public void Reload()
    {
        _loading = true;
        try
        {
            foreach (var group in ExclusionGroups) group.Refresh(Settings.GetExclusions(group.Kind));
            RefreshCustomFolders();
            RefreshPatterns();
            RefreshRules();
            SecureDelete = Settings.SecureDelete;
            CreateRestorePoint = Settings.CreateRestorePoint;
            IsSaveSuppressed = Settings.IsSaveSuppressed;
            RefreshTotals();
        }
        finally
        {
            _loading = false;
        }
    }

    // ----- 除外 -----

    internal async Task AddExclusionAsync(CleanupSettingsExclusionGroupViewModel group)
    {
        if (!TryBeginChange()) return;
        var result = Settings.AddExclusion(group.Kind, group.NewValue);
        if (result != CleanupAddResult.Added)
        {
            SetStatus(() => AddResultText(result), isError: true);
            return;
        }

        group.NewValue = string.Empty;
        group.Refresh(Settings.GetExclusions(group.Kind));
        await CommitAsync(() => AddResultText(result));
    }

    internal async Task RemoveExclusionAsync(CleanupExclusionKind kind, string value)
    {
        if (!TryBeginChange()) return;
        if (!Settings.RemoveExclusion(kind, value)) return;
        ExclusionGroups.First(g => g.Kind == kind).Refresh(Settings.GetExclusions(kind));
        await CommitAsync(() => CleanupText.Get("Cleanup.Settings.Saved"));
    }

    /// <summary>ファイルから除外を取り込む。</summary>
    public async Task ImportExclusionsAsync(string path)
    {
        if (!TryBeginChange()) return;
        var result = await Task.Run(() => Settings.ImportExclusions(path));
        if (!result.Succeeded)
        {
            SetStatus(() => CleanupText.Get($"Cleanup.Exclusion.Import.Failure.{result.Failure}"), isError: true);
            return;
        }

        foreach (var group in ExclusionGroups) group.Refresh(Settings.GetExclusions(group.Kind));
        string Message() => CleanupText.Get("Cleanup.Exclusion.Import.Done", result.Added, result.Duplicates, result.Rejected);
        if (result.Added > 0) await CommitAsync(Message);
        else SetStatus(Message, isError: false);
    }

    /// <summary>除外をファイルへ書き出す (設定は変えない)。</summary>
    public async Task ExportExclusionsAsync(string path)
    {
        try
        {
            await Task.Run(() => Settings.ExportExclusions(path));
            SetStatus(() => CleanupText.Get("Cleanup.Exclusion.Export.Done", path), isError: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            SetStatus(() => CleanupText.Get("Cleanup.Exclusion.Export.Failed", CleanupText.Detail(ex.Message)), isError: true);
        }
    }

    // ----- カスタムフォルダー -----

    /// <summary>カスタムフォルダーを追加する。拒否したときは <see cref="CustomFolderGuard"/> の理由を表示する。</summary>
    public async Task AddCustomFolderAsync(string? path)
    {
        if (!TryBeginChange()) return;
        var before = Settings.GetCustomFolders(GuardContext).Select(c => c.FullPath).ToList();
        var check = Settings.AddCustomFolder(path, GuardContext);
        if (check is { Rejection: CustomFolderRejection.Invalid, FullPath: not null })
        {
            // 検査は通ったが、件数の上限で追加できなかった (ストアはこの形で返す)。
            SetStatus(() => CleanupText.Get("Cleanup.Exclusion.Add.LimitReached", CleanupSettingsStore.MaxEntriesPerList), isError: true);
            return;
        }

        if (!check.IsAllowed)
        {
            SetStatus(() => CleanupText.Get($"Cleanup.CustomFolder.Reject.{check.Rejection}"), isError: true);
            return;
        }

        if (before.Any(p => IsSamePath(p, check.FullPath)))
        {
            SetStatus(() => CleanupText.Get("Cleanup.Exclusion.Add.Duplicate"), isError: true);
            return;
        }

        NewCustomFolder = string.Empty;
        RefreshCustomFolders();
        await CommitAsync(() => CleanupText.Get("Cleanup.CustomFolder.Added"));
    }

    private async Task RemoveCustomFolderAsync(string path)
    {
        if (!TryBeginChange()) return;
        if (!Settings.RemoveCustomFolder(path)) return;
        RefreshCustomFolders();
        await CommitAsync(() => CleanupText.Get("Cleanup.Settings.Saved"));
    }

    // ----- パターン -----

    private async Task AddPatternAsync()
    {
        if (!TryBeginChange()) return;
        var result = Settings.SetPattern(NewPattern, enabled: true);
        if (result is CleanupAddResult.Invalid or CleanupAddResult.LimitReached)
        {
            SetStatus(() => AddResultText(result), isError: true);
            return;
        }

        NewPattern = string.Empty;
        RefreshPatterns();
        await CommitAsync(() => AddResultText(result));
    }

    internal async Task SetPatternAsync(CleanupSettingsPatternViewModel row, bool enabled)
    {
        if (_loading) return;
        if (!IsEditable)
        {
            row.RevertTo(!enabled);
            SetStatus(() => CleanupText.Get("Status.Busy"), isError: true);
            return;
        }

        // 組み込みの規則を既定と同じ値に戻したときは、保存を消して既定へ戻す。
        var isBuiltInDefault = row.Group switch
        {
            AdvancedPatternGroup.Default => enabled,
            AdvancedPatternGroup.Additional => !enabled,
            _ => false,
        };
        if (isBuiltInDefault) Settings.RemovePattern(row.Pattern);
        else Settings.SetPattern(row.Pattern, enabled);
        await CommitAsync(() => CleanupText.Get("Cleanup.Settings.Saved"));
    }

    internal async Task RemovePatternAsync(CleanupSettingsPatternViewModel row)
    {
        if (!TryBeginChange()) return;
        if (!Settings.RemovePattern(row.Pattern)) return;
        RefreshPatterns();
        await CommitAsync(() => CleanupText.Get("Cleanup.Settings.Saved"));
    }

    // ----- 項目ごとの既定の選択 -----

    internal async Task SetRuleOverrideAsync(CleanupSettingsRuleViewModel row)
    {
        if (_loading) return;
        if (!IsEditable)
        {
            row.RevertTo(Settings.GetRuleOverride(row.RuleId));
            SetStatus(() => CleanupText.Get("Status.Busy"), isError: true);
            return;
        }

        Settings.SetRuleOverride(row.RuleId, row.Override);
        await CommitAsync(() => CleanupText.Get("Cleanup.Settings.Saved"));
    }

    private async Task ResetRulesAsync()
    {
        if (!TryBeginChange()) return;
        foreach (var rule in _rules) Settings.SetRuleOverride(rule.Id, null);
        RefreshRules();
        await CommitAsync(() => CleanupText.Get("Cleanup.Settings.Saved"));
    }

    // ----- 動作設定 -----

    partial void OnSecureDeleteChanged(bool value) => _ = ApplyOptionAsync(
        value, Settings.SecureDelete, v => Settings.SecureDelete = v, v => SecureDelete = v);

    partial void OnCreateRestorePointChanged(bool value) => _ = ApplyOptionAsync(
        value, Settings.CreateRestorePoint, v => Settings.CreateRestorePoint = v, v => CreateRestorePoint = v);

    private async Task ApplyOptionAsync(bool value, bool saved, Action<bool> apply, Action<bool> revert)
    {
        if (_loading || value == saved) return;
        if (!IsEditable)
        {
            _loading = true;
            try { revert(saved); }
            finally { _loading = false; }
            SetStatus(() => CleanupText.Get("Status.Busy"), isError: true);
            return;
        }

        apply(value);
        await CommitAsync(() => CleanupText.Get("Cleanup.Settings.Saved"));
    }

    /// <summary>システムの復元 (rstrui.exe) を開く。開いている間はもう一度開けない。</summary>
    private async Task OpenSystemRestoreAsync()
    {
        IsOpeningSystemRestore = true;
        try
        {
            var result = await Executor.RunAsync("rstrui.exe", string.Empty, timeout: TimeSpan.FromHours(12));
            if (!result.Success) SetStatus(() => CleanupText.Get("Cleanup.Settings.SystemRestore.Failed"), isError: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetStatus(() => CleanupText.Get("Cleanup.Settings.SystemRestore.Failed"), isError: true);
        }
        finally
        {
            IsOpeningSystemRestore = false;
        }
    }

    // ----- 共通 -----

    /// <summary>変更を始めてよいか。操作の実行中は拒否して、その旨を表示する。</summary>
    private bool TryBeginChange()
    {
        if (IsEditable) return true;
        SetStatus(() => CleanupText.Get("Status.Busy"), isError: true);
        return false;
    }

    /// <summary>
    /// 変更済みの設定で走査結果を失効させてから保存する。保存に失敗しても (読込失敗による保存抑止を含む)
    /// 変更はこの起動中は有効なので、状態を表示して続ける。
    /// </summary>
    private async Task CommitAsync(Func<string> successMessage)
    {
        Scan.Invalidate();
        Ledger.Invalidate();
        Slim.Invalidate();
        try
        {
            await Settings.SaveAsync();
            SetStatus(successMessage, isError: false);
        }
        catch (InvalidOperationException)
        {
            IsSaveSuppressed = true;
            SetStatus(() => SaveSuppressedText, isError: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 理由は SettingsService がログへ残している。
            SetStatus(() => CleanupText.Get("Cleanup.Settings.SaveFailed"), isError: true);
        }
    }

    /// <summary>状態の文言を設定する。言語を切り替えたときに引き直せるよう、文言は生成関数で受ける。</summary>
    private void SetStatus(Func<string> text, bool isError)
    {
        _status = text;
        StatusText = text();
        IsStatusError = isError;
    }

    private static string AddResultText(CleanupAddResult result) => result switch
    {
        CleanupAddResult.Added => CleanupText.Get("Cleanup.Exclusion.Add.Added"),
        CleanupAddResult.Duplicate => CleanupText.Get("Cleanup.Exclusion.Add.Duplicate"),
        CleanupAddResult.LimitReached => CleanupText.Get("Cleanup.Exclusion.Add.LimitReached", CleanupSettingsStore.MaxEntriesPerList),
        _ => CleanupText.Get("Cleanup.Exclusion.Add.Invalid"),
    };

    private static bool IsSamePath(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void RefreshCustomFolders()
    {
        CustomFolders.Clear();
        foreach (var check in Settings.GetCustomFolders(GuardContext))
        {
            // 正規化できなかった保存値は、外す手段を持たないため一覧に出さない。
            if (check.FullPath is not { } path) continue;
            var reason = check.IsAllowed ? null : CleanupText.Get($"Cleanup.CustomFolder.Reject.{check.Rejection}");
            CustomFolders.Add(new CleanupSettingsEntryViewModel(path, reason, () => RemoveCustomFolderAsync(path)));
        }
    }

    private void RefreshPatterns()
    {
        var saved = Settings.GetPatterns();
        Patterns.Clear();
        AddBuiltIn(AdvancedPatterns.DefaultPatterns, AdvancedPatternGroup.Default, saved, defaultEnabled: true);
        AddBuiltIn(AdvancedPatterns.AdditionalPatterns, AdvancedPatternGroup.Additional, saved, defaultEnabled: false);
        var builtIn = AdvancedPatterns.DefaultPatterns.Concat(AdvancedPatterns.AdditionalPatterns)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (pattern, enabled) in saved.Where(p => !builtIn.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            Patterns.Add(new CleanupSettingsPatternViewModel(pattern, AdvancedPatternGroup.UserDefined, enabled, this));
        }
    }

    private void AddBuiltIn(
        IReadOnlyList<string> patterns, AdvancedPatternGroup group, IReadOnlyDictionary<string, bool> saved, bool defaultEnabled)
    {
        foreach (var pattern in patterns)
        {
            Patterns.Add(new CleanupSettingsPatternViewModel(
                pattern, group, saved.TryGetValue(pattern, out var enabled) ? enabled : defaultEnabled, this));
        }
    }

    private void RefreshRules()
    {
        Rules.Clear();
        foreach (var rule in _rules) Rules.Add(new CleanupSettingsRuleViewModel(rule, Settings.GetRuleOverride(rule.Id), this));
        OnPropertyChanged(nameof(HasRules));
    }

    private void RefreshTotals()
    {
        var totals = Settings.GetTotals();
        var last = Settings.GetLastRun();
        TotalsText = totals.Runs == 0 && last is null
            ? CleanupText.Get("Cleanup.Settings.Totals.None")
            : CleanupText.Get("Cleanup.Totals.Total", totals.FilesDeleted, CleanupText.FormatBytes(totals.BytesFreed));
        LastRunText = last is null
            ? string.Empty
            : CleanupText.Get(
                "Cleanup.Totals.Last",
                last.FilesDeleted,
                CleanupText.FormatBytes(last.BytesFreed),
                last.CompletedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
    }

    private void OnCoordinatorStateChanged(object? sender, EventArgs e) => PostToUi(NotifyEditableChanged);

    private static void PostToUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    /// <summary>
    /// 言語を切り替えたとき、表示済みの文言を引き直す。保存済みの設定から行を作り直すだけで、
    /// 入力中の値・走査結果・選択は失わない。
    /// </summary>
    private void OnLocaleChanged()
    {
        SetRules(_rules);
        foreach (var group in ExclusionGroups) group.RefreshText();
        Reload();
        OnPropertyChanged(nameof(RuleChoices));
        OnPropertyChanged(nameof(SaveSuppressedText));
        OnPropertyChanged(nameof(PatternHint));
        if (_status is { } status) StatusText = status();
    }

    private void NotifyEditableChanged()
    {
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(IsBusy));
    }
}
