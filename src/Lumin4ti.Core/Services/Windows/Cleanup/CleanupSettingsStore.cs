using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>除外の種類。</summary>
public enum CleanupExclusionKind
{
    /// <summary>ファイル (絶対パス)。</summary>
    File,

    /// <summary>フォルダー (絶対パス。配下も除外)。</summary>
    Folder,

    /// <summary>拡張子・ファイル名 (<c>*.log</c> の形式に正規化する)。</summary>
    Name,

    /// <summary>Cookie のドメイン (本体とサブドメイン)。</summary>
    CookieDomain,
}

/// <summary>追加の結果。</summary>
public enum CleanupAddResult
{
    Added,
    Duplicate,

    /// <summary>形式が不正で、保存しなかった。</summary>
    Invalid,

    /// <summary>保存できる件数の上限に達した。</summary>
    LimitReached,
}

/// <summary>インポートを拒否した理由。</summary>
public enum CleanupImportFailure
{
    None = 0,
    TooLarge,
    Unreadable,
    InvalidFormat,
    UnsupportedSchema,
    TooManyEntries,
}

/// <summary>インポートの結果。失敗時は設定を一切変えない。</summary>
/// <param name="Added">新しく加えた件数。</param>
/// <param name="Duplicates">既にあった件数。</param>
/// <param name="Rejected">形式不正・上限超過で取り込まなかった件数。</param>
public sealed record CleanupImportResult(
    CleanupImportFailure Failure, int Added, int Duplicates, int Rejected)
{
    public bool Succeeded => Failure == CleanupImportFailure.None;

    internal static CleanupImportResult Fail(CleanupImportFailure failure) => new(failure, 0, 0, 0);
}

/// <summary>除外のインポート・エクスポート用文書。</summary>
internal sealed class CleanupExclusionDocument
{
    public int Schema { get; set; }

    public List<string?>? Files { get; set; }

    public List<string?>? Folders { get; set; }

    public List<string?>? Names { get; set; }

    public List<string?>? CookieDomains { get; set; }
}

/// <summary>インポート文書の source generator。未知キー・コメント・末尾カンマを拒否し、深さを制限する。</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    NumberHandling = JsonNumberHandling.Strict,
    MaxDepth = 4)]
[JsonSerializable(typeof(CleanupExclusionDocument))]
internal sealed partial class CleanupSettingsJsonContext : JsonSerializerContext;

/// <summary>
/// <see cref="CleanupSettings"/> の読み書き。排他は <see cref="ISettingsService.SyncRoot"/>、保存は
/// <see cref="ISettingsService.SaveAsync"/> (一時ファイル→置換) に従い、読込に失敗した設定は上書きしない。
/// 画面から来る値は、保存前に必ずここで正規化・検査する。
/// </summary>
public sealed class CleanupSettingsStore(ISettingsService settings)
{
    /// <summary>1 つのリストに保存できる最大件数。インポート文書もこの件数を超えると拒否する。</summary>
    public const int MaxEntriesPerList = 10_000;

    /// <summary>インポートできるファイルの最大バイト数。</summary>
    public const int MaxImportBytes = 1024 * 1024;

    private const int ExportFormatVersion = 1;

    private readonly object _lock = settings.SyncRoot;

    private CleanupSettings Data => settings.Current.Cleanup ??= new();

    // ----- 規則の選択 -----

    /// <summary>規則の上書き。無ければ null (規則の既定に従う)。</summary>
    public bool? GetRuleOverride(string ruleId)
    {
        lock (_lock)
        {
            return Data.RuleOverrides.TryGetValue(ruleId, out var value) ? value : null;
        }
    }

    /// <summary>規則の選択を上書きする。<c>null</c> なら上書きを消して既定へ戻す。</summary>
    public void SetRuleOverride(string ruleId, bool? enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        lock (_lock)
        {
            if (enabled is { } value) Data.RuleOverrides[ruleId] = value;
            else Data.RuleOverrides.Remove(ruleId);
        }
    }

    // ----- 除外 -----

    /// <summary>除外の一覧 (正規化済み)。</summary>
    public IReadOnlyList<string> GetExclusions(CleanupExclusionKind kind)
    {
        lock (_lock)
        {
            return [.. ListFor(Data, kind)];
        }
    }

    /// <summary>除外を 1 件追加する。形式が不正なら保存しない。</summary>
    public CleanupAddResult AddExclusion(CleanupExclusionKind kind, string? value)
    {
        var normalized = CleanupInput.Normalize(kind, value);
        if (normalized is null) return CleanupAddResult.Invalid;
        lock (_lock)
        {
            return AddTo(ListFor(Data, kind), normalized);
        }
    }

    /// <summary>除外を 1 件外す。外したか。</summary>
    public bool RemoveExclusion(CleanupExclusionKind kind, string? value)
    {
        var normalized = CleanupInput.Normalize(kind, value);
        if (normalized is null) return false;
        lock (_lock)
        {
            return ListFor(Data, kind).RemoveAll(e => string.Equals(e, normalized, StringComparison.OrdinalIgnoreCase)) > 0;
        }
    }

    /// <summary>走査と削除が共有する、除外の一致器 (ファイル・フォルダー・拡張子/名前)。</summary>
    public CleanupExclusionMatcher CreateExclusionMatcher()
    {
        lock (_lock)
        {
            return new CleanupExclusionMatcher(
                [.. Data.ExcludedFiles], [.. Data.ExcludedFolders], [.. Data.ExcludedNames]);
        }
    }

    // ----- カスタムフォルダー -----

    /// <summary>カスタムフォルダーを追加する。拒否したときは理由を返し、保存しない。</summary>
    public CustomFolderCheck AddCustomFolder(string? path, CustomFolderGuardContext? context = null)
    {
        var check = CustomFolderGuard.Check(path, context ?? CustomFolderGuardContext.FromEnvironment());
        if (!check.IsAllowed) return check;
        lock (_lock)
        {
            return AddTo(Data.CustomFolders, check.FullPath!) == CleanupAddResult.LimitReached
                ? new(check.FullPath, CustomFolderRejection.Invalid)
                : check;
        }
    }

    /// <summary>カスタムフォルダーを外す。</summary>
    public bool RemoveCustomFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var full = CleanupInput.NormalizeFolder(path);
        lock (_lock)
        {
            return Data.CustomFolders.RemoveAll(f =>
                string.Equals(f, path.Trim(), StringComparison.OrdinalIgnoreCase) ||
                (full is not null && string.Equals(f, full, StringComparison.OrdinalIgnoreCase))) > 0;
        }
    }

    /// <summary>
    /// 保存されているカスタムフォルダーを、実行時の検査結果つきで返す。
    /// 設定ファイルは非昇格でも書けるため、削除の直前にも呼ぶ。拒否されたものは既定未選択・削除対象外にする。
    /// </summary>
    public IReadOnlyList<CustomFolderCheck> GetCustomFolders(CustomFolderGuardContext? context = null)
    {
        string[] folders;
        lock (_lock)
        {
            folders = [.. Data.CustomFolders];
        }

        var guard = context ?? CustomFolderGuardContext.FromEnvironment();
        return [.. folders.Select(f => CustomFolderGuard.Check(f, guard))];
    }

    // ----- パターン -----

    /// <summary>高度なクリーンのパターンの選択を保存する。全ワイルドカードや不正な形式は拒否する。</summary>
    public CleanupAddResult SetPattern(string? pattern, bool enabled)
    {
        var normalized = CleanupInput.NormalizePattern(pattern);
        if (normalized is null) return CleanupAddResult.Invalid;
        lock (_lock)
        {
            var existing = Data.Patterns.Keys.FirstOrDefault(k => string.Equals(k, normalized, StringComparison.OrdinalIgnoreCase));
            if (existing is null && Data.Patterns.Count >= MaxEntriesPerList) return CleanupAddResult.LimitReached;
            Data.Patterns[existing ?? normalized] = enabled;
            return existing is null ? CleanupAddResult.Added : CleanupAddResult.Duplicate;
        }
    }

    /// <summary>パターンの保存を消して既定へ戻す。</summary>
    public bool RemovePattern(string? pattern)
    {
        var normalized = CleanupInput.NormalizePattern(pattern);
        if (normalized is null) return false;
        lock (_lock)
        {
            var existing = Data.Patterns.Keys.FirstOrDefault(k => string.Equals(k, normalized, StringComparison.OrdinalIgnoreCase));
            return existing is not null && Data.Patterns.Remove(existing);
        }
    }

    /// <summary>保存されているパターンの選択。</summary>
    public IReadOnlyDictionary<string, bool> GetPatterns()
    {
        lock (_lock)
        {
            return new Dictionary<string, bool>(Data.Patterns, StringComparer.OrdinalIgnoreCase);
        }
    }

    // ----- 動作設定・実績 -----

    /// <summary>安全な削除 (既定オフ)。</summary>
    public bool SecureDelete
    {
        get { lock (_lock) { return Data.SecureDelete; } }
        set { lock (_lock) { Data.SecureDelete = value; } }
    }

    /// <summary>スリム化前の復元ポイント作成 (既定オン)。</summary>
    public bool CreateRestorePoint
    {
        get { lock (_lock) { return Data.CreateRestorePoint; } }
        set { lock (_lock) { Data.CreateRestorePoint = value; } }
    }

    /// <summary>実行の実績を累計と前回へ記録する。負数は 0、累計は上限で止める。</summary>
    public void RecordRun(long filesDeleted, long bytesFreed, long filesSkipped, bool partial, DateTimeOffset completedAt)
    {
        filesDeleted = Math.Max(0, filesDeleted);
        bytesFreed = Math.Max(0, bytesFreed);
        filesSkipped = Math.Max(0, filesSkipped);
        lock (_lock)
        {
            var totals = Data.Totals ??= new();
            totals.Runs = SaturatingAdd(totals.Runs, 1);
            totals.FilesDeleted = SaturatingAdd(totals.FilesDeleted, filesDeleted);
            totals.BytesFreed = SaturatingAdd(totals.BytesFreed, bytesFreed);
            Data.LastRun = new CleanupLastRun
            {
                CompletedAt = completedAt,
                FilesDeleted = filesDeleted,
                BytesFreed = bytesFreed,
                FilesSkipped = filesSkipped,
                Partial = partial,
            };
        }
    }

    /// <summary>累計の実績 (コピー)。</summary>
    public CleanupTotals GetTotals()
    {
        lock (_lock)
        {
            var t = Data.Totals ??= new();
            return new CleanupTotals { Runs = t.Runs, FilesDeleted = t.FilesDeleted, BytesFreed = t.BytesFreed };
        }
    }

    /// <summary>前回の実績 (コピー)。まだ実行していなければ null。</summary>
    public CleanupLastRun? GetLastRun()
    {
        lock (_lock)
        {
            return Data.LastRun is { } r
                ? new CleanupLastRun
                {
                    CompletedAt = r.CompletedAt,
                    FilesDeleted = r.FilesDeleted,
                    BytesFreed = r.BytesFreed,
                    FilesSkipped = r.FilesSkipped,
                    Partial = r.Partial,
                }
                : null;
        }
    }

    // ----- インポート・エクスポート -----

    /// <summary>除外 4 種を JSON 文書にする。</summary>
    public string ExportExclusionsJson()
    {
        CleanupExclusionDocument document;
        lock (_lock)
        {
            document = new CleanupExclusionDocument
            {
                Schema = ExportFormatVersion,
                Files = [.. Data.ExcludedFiles],
                Folders = [.. Data.ExcludedFolders],
                Names = [.. Data.ExcludedNames],
                CookieDomains = [.. Data.ExcludedCookieDomains],
            };
        }

        return JsonSerializer.Serialize(document, CleanupSettingsJsonContext.Default.CleanupExclusionDocument);
    }

    /// <summary>除外 4 種をファイルへ書く (同じフォルダーの一時ファイル→置換。既存ファイルは書き終えてから置き換える)。</summary>
    public void ExportExclusions(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath))
        {
            throw new ArgumentException("書き出し先は絶対パスで指定してください。", nameof(filePath));
        }

        var full = Path.GetFullPath(filePath);
        var tempPath = full + ".tmp";
        try
        {
            File.WriteAllText(tempPath, ExportExclusionsJson(), new UTF8Encoding(false));
            File.Move(tempPath, full, overwrite: true);
        }
        catch
        {
            // 書きかけの一時ファイルを残さない。元の書き出し先は置換前なので無傷。
            try { File.Delete(tempPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>JSON 文書から除外を取り込む (追加のみ。既存は消さない)。文書が不正なら何も変えない。</summary>
    public CleanupImportResult ImportExclusionsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return CleanupImportResult.Fail(CleanupImportFailure.InvalidFormat);
        if (Encoding.UTF8.GetByteCount(json) > MaxImportBytes) return CleanupImportResult.Fail(CleanupImportFailure.TooLarge);

        CleanupExclusionDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json, CleanupSettingsJsonContext.Default.CleanupExclusionDocument);
        }
        catch (JsonException)
        {
            return CleanupImportResult.Fail(CleanupImportFailure.InvalidFormat);
        }

        if (document is null) return CleanupImportResult.Fail(CleanupImportFailure.InvalidFormat);
        if (document.Schema != ExportFormatVersion) return CleanupImportResult.Fail(CleanupImportFailure.UnsupportedSchema);

        var incoming = new (CleanupExclusionKind Kind, List<string?>? Values)[]
        {
            (CleanupExclusionKind.File, document.Files),
            (CleanupExclusionKind.Folder, document.Folders),
            (CleanupExclusionKind.Name, document.Names),
            (CleanupExclusionKind.CookieDomain, document.CookieDomains),
        };
        if (incoming.Any(static i => i.Values is { Count: > MaxEntriesPerList }))
        {
            return CleanupImportResult.Fail(CleanupImportFailure.TooManyEntries);
        }

        return Merge(incoming);
    }

    /// <summary>ファイルから除外を取り込む。大きすぎる・読めない・不正な文書は何も変えない。</summary>
    public CleanupImportResult ImportExclusions(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath))
        {
            return CleanupImportResult.Fail(CleanupImportFailure.Unreadable);
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxImportBytes) return CleanupImportResult.Fail(CleanupImportFailure.TooLarge);
            var buffer = new byte[(int)stream.Length];
            stream.ReadExactly(buffer);
            return ImportExclusionsJson(new UTF8Encoding(false, true).GetString(buffer).TrimStart('\uFEFF'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return CleanupImportResult.Fail(CleanupImportFailure.Unreadable);
        }
    }

    private CleanupImportResult Merge((CleanupExclusionKind Kind, List<string?>? Values)[] incoming)
    {
        int added = 0, duplicates = 0, rejected = 0;
        lock (_lock)
        {
            foreach (var (kind, values) in incoming)
            {
                var list = ListFor(Data, kind);
                foreach (var value in values ?? [])
                {
                    var normalized = CleanupInput.Normalize(kind, value);
                    var result = normalized is null ? CleanupAddResult.Invalid : AddTo(list, normalized);
                    switch (result)
                    {
                        case CleanupAddResult.Added: added++; break;
                        case CleanupAddResult.Duplicate: duplicates++; break;
                        default: rejected++; break;
                    }
                }
            }
        }

        return new CleanupImportResult(CleanupImportFailure.None, added, duplicates, rejected);
    }

    // ----- 保存 -----

    /// <summary>
    /// 設定ファイルを読み込めず、原本を守るために保存しない状態か。変更はこの起動中だけ有効で、
    /// <see cref="SaveAsync"/> は <see cref="InvalidOperationException"/> を投げる。画面は変更の前から表示できる。
    /// </summary>
    public bool IsSaveSuppressed => settings.LoadStatus == SettingsLoadStatus.Failed;

    /// <summary>
    /// 設定を保存する。読込に失敗した設定ファイルは原本を保護して保存しない (<see cref="InvalidOperationException"/>)。
    /// </summary>
    public Task SaveAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (IsSaveSuppressed)
        {
            // 画面は例外の文言を表示せず辞書の SaveSuppressed を使う (開発者向けの診断文)。
            throw new InvalidOperationException("設定ファイルを読み込めないため、原本を保護してクリーンアップ設定を保存していません。変更はこの起動中だけ有効です。");
        }

        return settings.SaveAsync(ct);
    }

    private static List<string> ListFor(CleanupSettings data, CleanupExclusionKind kind) => kind switch
    {
        CleanupExclusionKind.File => data.ExcludedFiles,
        CleanupExclusionKind.Folder => data.ExcludedFolders,
        CleanupExclusionKind.Name => data.ExcludedNames,
        CleanupExclusionKind.CookieDomain => data.ExcludedCookieDomains,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static CleanupAddResult AddTo(List<string> list, string normalized)
    {
        if (list.Exists(e => string.Equals(e, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return CleanupAddResult.Duplicate;
        }

        if (list.Count >= MaxEntriesPerList) return CleanupAddResult.LimitReached;
        list.Add(normalized);
        return CleanupAddResult.Added;
    }

    private static long SaturatingAdd(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
}

/// <summary>画面・インポートから来る除外・パターンの正規化と検査。不正なら <c>null</c>。</summary>
internal static class CleanupInput
{
    private const int MaxLength = 1024;

    private static readonly char[] InvalidPathChars = ['*', '?', '<', '>', '|', '"', '\0'];

    private static readonly char[] InvalidNameChars = ['\\', '/', ':', '\0', '<', '>', '|', '"'];

    public static string? Normalize(CleanupExclusionKind kind, string? value) => kind switch
    {
        CleanupExclusionKind.File => NormalizeFile(value),
        CleanupExclusionKind.Folder => NormalizeFolder(value),
        CleanupExclusionKind.Name => NormalizeName(value),
        CleanupExclusionKind.CookieDomain => NormalizeDomain(value),
        _ => null,
    };

    public static string? NormalizeFile(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text[^1] is '\\' or '/') return null;
        var full = NormalizePath(text);
        return full is not null && !string.IsNullOrEmpty(Path.GetFileName(full)) ? full : null;
    }

    public static string? NormalizeFolder(string? value) => NormalizePath(value);

    private static string? NormalizePath(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength ||
            text.StartsWith(@"\\?\", StringComparison.Ordinal) || text.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            text.IndexOfAny(InvalidPathChars) >= 0 || !Path.IsPathFullyQualified(text) ||
            text.IndexOf(':', 2) >= 0)
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(text);
            return full.Length > 3 ? Path.TrimEndingDirectorySeparator(full) : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>拡張子またはファイル名。<c>.log</c> は <c>*.log</c> に揃える (<see cref="CleanupExclusionMatcher"/> と同じ)。</summary>
    public static string? NormalizeName(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 255 || text.IndexOfAny(InvalidNameChars) >= 0) return null;
        return text[0] == '.' && text.AsSpan().IndexOfAny('*', '?') < 0 ? "*" + text : text;
    }

    /// <summary>削除側のパターン。全てを選ぶ形 (<c>*</c>・<c>*.*</c>・<c>??</c> など) は拒否する。</summary>
    public static string? NormalizePattern(string? value)
    {
        var name = NormalizeName(value);
        return name is not null && name.Any(static c => c is not ('*' or '?' or '.')) ? name : null;
    }

    /// <summary>Cookie のドメイン。小文字の ASCII (IDN は Punycode) にし、先頭の <c>.</c>・<c>*.</c> は外す。</summary>
    public static string? NormalizeDomain(string? value)
    {
        var text = value?.Trim().TrimEnd('.');
        if (text is null) return null;
        if (text.StartsWith("*.", StringComparison.Ordinal)) text = text[2..];
        text = text.TrimStart('.');
        if (text.Length == 0 || text.Length > 253) return null;

        try { text = new IdnMapping().GetAscii(text).ToLower(CultureInfo.InvariantCulture); }
        catch (ArgumentException) { return null; }

        return text.Length <= 253 && text.Split('.').All(IsDomainLabel) ? text : null;
    }

    private static bool IsDomainLabel(string label) =>
        label.Length is >= 1 and <= 63 &&
        label[0] != '-' && label[^1] != '-' &&
        label.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-');
}
