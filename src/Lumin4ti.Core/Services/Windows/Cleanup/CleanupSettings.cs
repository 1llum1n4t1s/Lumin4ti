using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>これまでの実行の累計。</summary>
public sealed class CleanupTotals
{
    public long Runs { get; set; }

    public long FilesDeleted { get; set; }

    public long BytesFreed { get; set; }
}

/// <summary>前回の実行の実績。</summary>
public sealed class CleanupLastRun
{
    public DateTimeOffset CompletedAt { get; set; }

    public long FilesDeleted { get; set; }

    public long BytesFreed { get; set; }

    /// <summary>削除できなかった (保持・使用中・権限不足を含む) 件数。</summary>
    public long FilesSkipped { get; set; }

    /// <summary>一部失敗・中断を含み、完全には成功しなかったか。</summary>
    public bool Partial { get; set; }
}

/// <summary>
/// 新クリーンアップの設定 (<c>settings.json</c> の <c>Cleanup</c>)。「有効なもの」ではなく
/// 「外したもの」だけを保存し、更新で規則が増えても既存の設定が黙って無効化しない。
/// 旧版が読んでも未知キーとして無視され、<c>CleanupExclusions</c> は別に残る。
/// </summary>
public sealed class CleanupSettings
{
    /// <summary>現在の設定形式。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>0 = 旧版 (この設定がない) から未移行、1 = 移行済み。</summary>
    public int SchemaVersion { get; set; }

    /// <summary>規則 Id ごとの選択の上書き (false = 外した)。無いものは規則の既定に従う。</summary>
    public Dictionary<string, bool> RuleOverrides { get; set; } = [];

    /// <summary>除外するファイル (絶対パス)。</summary>
    public List<string> ExcludedFiles { get; set; } = [];

    /// <summary>除外するフォルダー (絶対パス。配下も除外)。</summary>
    public List<string> ExcludedFolders { get; set; } = [];

    /// <summary>除外する拡張子・ファイル名 (<c>*.log</c>・<c>thumbs.db</c> の形式)。</summary>
    public List<string> ExcludedNames { get; set; } = [];

    /// <summary>残す Cookie のドメイン (本体とサブドメイン)。</summary>
    public List<string> ExcludedCookieDomains { get; set; } = [];

    /// <summary>利用者が足したカスタムフォルダー (中身だけを空にする)。</summary>
    public List<string> CustomFolders { get; set; } = [];

    /// <summary>高度なクリーンのパターンの選択 (キー = パターン、値 = 選択)。無いものは既定に従う。</summary>
    public Dictionary<string, bool> Patterns { get; set; } = [];

    /// <summary>安全な削除 (1パス上書き)。既定オフ。</summary>
    public bool SecureDelete { get; set; }

    /// <summary>スリム化の前に復元ポイントを作る。既定オン。</summary>
    public bool CreateRestorePoint { get; set; } = true;

    public CleanupTotals Totals { get; set; } = new();

    public CleanupLastRun? LastRun { get; set; }

    /// <summary>JSON が <c>null</c> を持ち込んだ項目を空へ戻す。</summary>
    internal void Normalize()
    {
        RuleOverrides ??= [];
        ExcludedFiles ??= [];
        ExcludedFolders ??= [];
        ExcludedNames ??= [];
        ExcludedCookieDomains ??= [];
        CustomFolders ??= [];
        Patterns ??= [];
        Totals ??= new();
        RemoveNulls(ExcludedFiles);
        RemoveNulls(ExcludedFolders);
        RemoveNulls(ExcludedNames);
        RemoveNulls(ExcludedCookieDomains);
        RemoveNulls(CustomFolders);
    }

    private static void RemoveNulls(List<string> list) => list.RemoveAll(static s => s is null);
}

/// <summary>旧 <c>CleanupExclusions</c> から <see cref="CleanupSettings"/> への移行。</summary>
internal static class CleanupSettingsMigration
{
    /// <summary>
    /// 設定を現在の形式へ移す (読み込み時に1回)。旧除外に当たる規則を override=false にする。
    /// <c>CleanupExclusions</c> 自体は、旧カード (cleanup-shell-cache) と旧版への戻しのため残す。
    /// </summary>
    /// <returns>移行を実行したか。</returns>
    public static bool Apply(AppSettings settings, IEnumerable<CleanupRule> rules)
    {
        var cleanup = settings.Cleanup ??= new CleanupSettings();
        cleanup.Normalize();
        if (cleanup.SchemaVersion >= CleanupSettings.CurrentSchemaVersion) return false;

        var exclusions = settings.CleanupExclusions;
        if (exclusions is { Count: > 0 })
        {
            var rawPaths = exclusions.Values
                .Where(static list => list is not null)
                .SelectMany(static list => list)
                .Where(static p => !string.IsNullOrWhiteSpace(p))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in rules.Where(r => IsExcluded(r, exclusions, rawPaths)))
            {
                cleanup.RuleOverrides[rule.Id] = false;
            }
        }

        cleanup.SchemaVersion = CleanupSettings.CurrentSchemaVersion;
        return true;
    }

    private static bool IsExcluded(
        CleanupRule rule, Dictionary<string, List<string>> exclusions, HashSet<string> rawPaths) =>
        (rule.Legacy ?? []).Any(key => HasLegacyKey(exclusions, key)) ||
        (rule.Targets ?? []).Any(t => t.Path is { } path && rawPaths.Contains(path));

    /// <summary><c>{旧項目Id}|{旧キー}</c> が旧除外にあるか。</summary>
    private static bool HasLegacyKey(Dictionary<string, List<string>> exclusions, string legacy)
    {
        var bar = legacy.IndexOf('|');
        if (bar <= 0 || bar == legacy.Length - 1) return false;
        return exclusions.TryGetValue(legacy[..bar], out var list) &&
               list is not null &&
               list.Contains(legacy[(bar + 1)..], StringComparer.OrdinalIgnoreCase);
    }
}
