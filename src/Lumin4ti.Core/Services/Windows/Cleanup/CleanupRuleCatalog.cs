using System.Reflection;
using System.Text.Json;
using Lumin4ti.Core.Services;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>読み込めなかった定義ファイル。</summary>
internal sealed record CleanupFileError(string File, string Message);

/// <summary>カタログの読み込み結果。</summary>
internal sealed record CleanupCatalogResult(
    IReadOnlyList<CleanupRule> Rules,
    IReadOnlyList<CleanupViolation> Violations,
    IReadOnlyList<CleanupFileError> FileErrors);

/// <summary>
/// 画面へ公開する規則の一覧 (読み取り専用)。走査の前でも、設定画面が全規則の「既定の選択」を出せるようにする。
/// 規則の定義 (対象パスなど) は公開せず、表示に使う <see cref="CleanupRuleInfo"/> だけを返す。
/// カスタムフォルダーは実行時の設定から作られるため含まない。
/// </summary>
public static class CleanupRuleDirectory
{
    private static readonly Lazy<IReadOnlyList<CleanupRuleInfo>> Cached = new(static () =>
        [.. CleanupRuleSource.All().Select(static rule => CleanupRuleInfo.From(rule, [.. rule.Services]))]);

    /// <summary>使える全規則の情報。違反して無効になった規則は含めない。</summary>
    public static IReadOnlyList<CleanupRuleInfo> All() => Cached.Value;
}

/// <summary>
/// 埋め込み定義 (<c>Definitions/*.json</c>) だけから規則を読み込む。外部ファイルの読み込み・更新経路は作らない。
/// 違反した規則だけを無効にしてログへ残し、ほかの規則は使う。構文・スキーマ不正のファイルは丸ごと読まない。
/// </summary>
internal static class CleanupRuleCatalog
{
    /// <summary>csproj の glob が付ける埋め込みリソース名の接頭辞。</summary>
    internal const string ResourcePrefix = "Lumin4ti.Cleanup.Definitions.";

    internal const int SupportedSchema = 1;

    private static readonly Lazy<CleanupCatalogResult> Embedded = new(() => LoadEmbeddedCore(typeof(CleanupRuleCatalog).Assembly));

    /// <summary>埋め込み定義の読み込み結果 (初回に1度だけ読む)。</summary>
    public static CleanupCatalogResult Load() => Embedded.Value;

    internal static CleanupCatalogResult LoadEmbeddedCore(Assembly assembly)
    {
        var sources = new List<(string Name, string Json)>();
        var errors = new List<CleanupFileError>();
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                sources.Add((name[ResourcePrefix.Length..], reader.ReadToEnd()));
            }
            catch (IOException ex)
            {
                errors.Add(new(name, ex.Message));
            }
        }

        var loaded = LoadFrom(sources);
        return loaded with { FileErrors = [.. errors, .. loaded.FileErrors] };
    }

    /// <summary>JSON 文字列の列から読み込む。埋め込み読込とテストが共有する。</summary>
    internal static CleanupCatalogResult LoadFrom(IEnumerable<(string Name, string Json)> sources)
    {
        var rules = new List<CleanupRule>();
        var errors = new List<CleanupFileError>();
        foreach (var (name, json) in sources)
        {
            var parsed = Parse(name, json, errors);
            if (parsed is not null)
            {
                rules.AddRange(parsed.Rules);
            }
        }

        var validated = CleanupRuleValidator.ValidateAll(rules);
        foreach (var violation in validated.Violations)
        {
            LoggerBootstrap.Log.Warn($"クリーンアップ規則 {violation.RuleId} を無効にしました ({violation.Code}): {violation.Message}");
        }

        return new(validated.Valid, validated.Violations, errors);
    }

    private static CleanupRuleFile? Parse(string name, string json, List<CleanupFileError> errors)
    {
        try
        {
            var file = JsonSerializer.Deserialize(json, CleanupJsonContext.Default.CleanupRuleFile);
            if (file is null || file.Schema != SupportedSchema || file.Rules is null)
            {
                return Reject(name, "schema が未対応、または rules がありません", errors);
            }

            return file;
        }
        catch (JsonException ex)
        {
            return Reject(name, ex.Message, errors);
        }
    }

    private static CleanupRuleFile? Reject(string name, string message, List<CleanupFileError> errors)
    {
        errors.Add(new(name, message));
        LoggerBootstrap.Log.Warn($"クリーンアップ定義 {name} を読み込めませんでした: {message}");
        return null;
    }
}
