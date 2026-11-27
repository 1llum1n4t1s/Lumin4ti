using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>
/// 埋め込み定義 JSON の source generator。未知キーは拒否し、深さを制限する
/// (定義は埋め込みだが、書き間違いを黙って無視しないため)。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    NumberHandling = JsonNumberHandling.Strict,
    MaxDepth = CleanupJsonContext.MaxJsonDepth)]
[JsonSerializable(typeof(CleanupRuleFile))]
internal sealed partial class CleanupJsonContext : JsonSerializerContext
{
    /// <summary>最上位 → rules → 規則 → targets → 対象 → iniKeys → 要素 まで足りる深さ。</summary>
    internal const int MaxJsonDepth = 12;
}
