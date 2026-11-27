using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;

/// <summary>
/// レジストリクリーナーのバックアップ文書 (型付き JSON)。reg.exe export は値単位で戻せないため使わない。
/// ACL は保存しない。キー単位の項目はサブツリー全体 (既定値を含む) を <see cref="RegistryBackupKeyNode"/> で持つ。
/// </summary>
/// <param name="FormatVersion">形式の版 (<see cref="RegistryBackupDocument.CurrentVersion"/>)。</param>
/// <param name="CreatedUtc">作成時刻 (UTC)。</param>
/// <param name="UserSid">HKCU 項目の持ち主 (走査時の昇格利用者) の SID。</param>
/// <param name="Items">バックアップ項目。</param>
internal sealed record RegistryBackupDocument(
    int FormatVersion,
    DateTime CreatedUtc,
    string UserSid,
    RegistryBackupItem[] Items)
{
    public const int CurrentVersion = 1;
}

/// <summary>
/// 項目 1 件。<see cref="ValueName"/> が null ならキー単位 (<see cref="Subtree"/> 必須、値の型・データは不可)、
/// 非 null なら値単位 (<see cref="ValueType"/> と <see cref="DataBase64"/> 必須、<see cref="Subtree"/> は不可)。
/// </summary>
internal sealed record RegistryBackupItem(
    RegistryCleanCategory Category,
    RegistryCleanHive Hive,
    RegistryCleanView View,
    string KeyPath,
    string? ValueName,
    uint? ValueType,
    string? DataBase64,
    RegistryBackupKeyNode? Subtree);

/// <summary>レジストリの値 1 つ。既定値の名前は空文字列。</summary>
internal sealed record RegistryBackupValue(string Name, uint Type, string DataBase64);

/// <summary>キーの内容 (値と子)。</summary>
internal sealed record RegistryBackupKeyNode(RegistryBackupValue[] Values, RegistryBackupSubKey[] SubKeys);

internal sealed record RegistryBackupSubKey(string Name, RegistryBackupKeyNode Node);

/// <summary>項目ごとの結果 (結果ファイル用)。</summary>
internal sealed record RegistryBackupResultItem(
    RegistryCleanCategory Category,
    RegistryCleanHive Hive,
    RegistryCleanView View,
    string KeyPath,
    string? ValueName,
    RegistryItemState State,
    string? Reason);

/// <summary>実行結果ファイル <c>&lt;id&gt;.result.json</c> の中身。</summary>
internal sealed record RegistryBackupResultDocument(
    int FormatVersion,
    string BackupId,
    DateTime CompletedUtc,
    RegistryOverallState Overall,
    RegistryBackupResultItem[] Items);

/// <summary>バックアップ文書の source generator。未知プロパティは拒否する (改ざん・版違いを黙って読まない)。</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    AllowTrailingCommas = false,
    NumberHandling = JsonNumberHandling.Strict,
    MaxDepth = RegistryBackupLimits.JsonMaxDepth)]
[JsonSerializable(typeof(RegistryBackupDocument))]
[JsonSerializable(typeof(RegistryBackupResultDocument))]
internal sealed partial class RegistryCleanJsonContext : JsonSerializerContext;

/// <summary>開いているキーからサブツリーを取り込む。</summary>
internal static class RegistryBackupCapture
{
    /// <summary>
    /// <paramref name="key"/> のサブツリー全体 (既定値を含む) を取り込む。深さ・件数・サイズが上限を超える、
    /// または列挙中に消えたら <see cref="RegistryBackupException"/> (バックアップできないものは削除しない)。
    /// </summary>
    public static RegistryBackupKeyNode CaptureKey(IRegistryCleanerKey key, RegistryCleanView view)
    {
        ArgumentNullException.ThrowIfNull(key);
        var budget = new Budget();
        return Capture(key, view, 1, budget);
    }

    /// <summary>値 1 つの項目の元データ。無ければ null。</summary>
    public static RegistryBackupValue? CaptureValue(IRegistryCleanerKey key, string name)
    {
        ArgumentNullException.ThrowIfNull(key);
        var value = key.GetValue(name);
        if (value is null)
        {
            return null;
        }

        if (value.Data.Length > RegistryBackupLimits.MaxValueBytes)
        {
            throw new RegistryBackupException("値が大きすぎるためバックアップできません。");
        }

        return new RegistryBackupValue(name, value.Type, Convert.ToBase64String(value.Data));
    }

    private static RegistryBackupKeyNode Capture(IRegistryCleanerKey key, RegistryCleanView view, int depth, Budget budget)
    {
        if (depth > RegistryBackupLimits.MaxKeyDepth)
        {
            throw new RegistryBackupException("キーの階層が深すぎるためバックアップできません。");
        }

        var values = new List<RegistryBackupValue>();
        foreach (var name in key.GetValueNames())
        {
            var value = CaptureValue(key, name)
                ?? throw new RegistryBackupException("バックアップ中に値が消えました。");
            budget.Add(1, value.DataBase64.Length * 3L / 4);
            values.Add(value);
        }

        var subKeys = new List<RegistryBackupSubKey>();
        foreach (var name in key.GetSubKeyNames())
        {
            budget.Add(1, 0);
            using var child = key.OpenChild(name, RegistryCleanAccess.Read, view)
                ?? throw new RegistryBackupException("バックアップ中にキーが消えました。");
            subKeys.Add(new RegistryBackupSubKey(name, Capture(child, view, depth + 1, budget)));
        }

        return new RegistryBackupKeyNode([.. values], [.. subKeys]);
    }

    private sealed class Budget
    {
        private int _nodes;
        private long _bytes;

        public void Add(int nodes, long bytes)
        {
            _nodes += nodes;
            _bytes += bytes;
            if (_nodes > RegistryBackupLimits.MaxNodes || _bytes > RegistryBackupLimits.MaxTotalBytes)
            {
                throw new RegistryBackupException("バックアップの件数またはサイズが上限を超えました。");
            }
        }
    }
}
