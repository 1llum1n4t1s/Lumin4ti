using System.Text.Json;
using System.Text.Json.Serialization;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Core.Services;

/// <summary>実行時リフレクションへ依存しない共有 JSON 型情報。</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(EnvPathBackupDocument))]
[JsonSerializable(typeof(RegistryValueBackupDocument))]
// 高速生成経路は byte[] の null を空 Base64 として書くため、型付き復元値はメタデータ経路に固定する。
[JsonSerializable(typeof(RegistryValueSnapshot), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(UwpBackgroundJournal))]
[JsonSerializable(typeof(VirtualizationSecuritySnapshot))]
[JsonSerializable(typeof(QuickAccessScriptResult))]
internal sealed partial class Lumin4tiJsonContext : JsonSerializerContext;

internal static class Lumin4tiJson
{
    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, typeof(T), Lumin4tiJsonContext.Default);

    public static void Serialize<T>(Stream stream, T value) =>
        JsonSerializer.Serialize(stream, value, typeof(T), Lumin4tiJsonContext.Default);

    public static T? Deserialize<T>(string json) =>
        (T?)JsonSerializer.Deserialize(json, typeof(T), Lumin4tiJsonContext.Default);
}
