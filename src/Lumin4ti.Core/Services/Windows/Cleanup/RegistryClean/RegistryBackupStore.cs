using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;

/// <summary>バックアップの保存・読み込みに失敗した (書けない・読み戻しが一致しない・上限超過など)。</summary>
internal class RegistryBackupException(string message, Exception? inner = null)
    : IOException(message, inner);

/// <summary>バックアップと結果ファイルの保存先。実装は <see cref="RegistryBackupStore"/>。</summary>
internal interface IRegistryBackupStore
{
    /// <summary>検証してから新規保存し、読み戻して一致を確かめる。失敗したら書いたファイルを消して例外。戻り値は ID。</summary>
    string Save(RegistryBackupDocument document);

    /// <summary>ID の一覧 (時刻順)。<see cref="RegistryBackupLimits.MaxBackups"/> を超えたら例外 (fail closed)。</summary>
    IReadOnlyList<string> List();

    /// <summary>読み込んで検証する。無ければ null。破損・違反は <see cref="RegistryBackupRejectedException"/>。</summary>
    RegistryBackupDocument? Load(string id);

    /// <summary>バックアップと結果ファイルを消す (自動では呼ばない)。</summary>
    void Delete(string id);

    /// <summary>結果ファイル <c>&lt;id&gt;.result.json</c> を新規に書く。</summary>
    void WriteResult(string id, RegistryBackupResultDocument result);

    /// <summary>結果ファイルを読む。無ければ null。</summary>
    RegistryBackupResultDocument? ReadResult(string id);
}

/// <summary>ファイル操作の抽象 (本番は <see cref="ProtectedBackupStorage"/>、テストは偽物)。パスは保存根からの相対。</summary>
internal interface IRegistryBackupFiles
{
    bool Exists(string relativePath);

    long Length(string relativePath);

    string ReadAllText(string relativePath);

    void WriteNew(string relativePath, byte[] content);

    void Delete(string relativePath);

    IReadOnlyList<string> EnumerateNames(string relativeDirectory, int limit);
}

[SupportedOSPlatform("windows")]
internal sealed class ProtectedRegistryBackupFiles(ProtectedBackupStorage storage) : IRegistryBackupFiles
{
    public bool Exists(string relativePath) => storage.FileExists(relativePath);

    public long Length(string relativePath) => new FileInfo(storage.GetFullPath(relativePath)).Length;

    public string ReadAllText(string relativePath) => storage.ReadAllText(relativePath);

    public void WriteNew(string relativePath, byte[] content) =>
        storage.WriteNewAtomically(relativePath, stream => stream.Write(content));

    public void Delete(string relativePath) => storage.Delete(relativePath);

    public IReadOnlyList<string> EnumerateNames(string relativeDirectory, int limit) =>
        storage.EnumerateFileNames(relativeDirectory, limit);
}

/// <summary>
/// <c>%ProgramData%\Lumin4ti\backups\registry\&lt;UTC時刻&gt;-&lt;GUID&gt;.json</c> への保存。保護 ACL (Administrators と SYSTEM)・
/// WriteThrough・原子的な新規作成は <see cref="ProtectedBackupStorage"/> が担う。自動削除しない。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RegistryBackupStore : IRegistryBackupStore
{
    private const string Folder = "registry";
    private const string IdTimeFormat = "yyyyMMddTHHmmssfff'Z'";
    private const string ResultSuffix = ".result.json";
    /// <summary>バックアップと結果ファイルの組と、書き込み途中の一時ファイルを含む列挙の上限。</summary>
    private const int MaxEntries = RegistryBackupLimits.MaxBackups * 4;

    private readonly IRegistryBackupFiles _files;

    public RegistryBackupStore(ProtectedBackupStorage storage)
        : this(new ProtectedRegistryBackupFiles(storage))
    {
    }

    internal RegistryBackupStore(IRegistryBackupFiles files) => _files = files;

    public static RegistryBackupStore CreateDefault() => new(ProtectedBackupStorage.Default);

    public string Save(RegistryBackupDocument document)
    {
        RegistryBackupValidator.ThrowIfInvalid(document);
        var json = JsonSerializer.Serialize(document, RegistryCleanJsonContext.Default.RegistryBackupDocument);
        var id = $"{DateTime.UtcNow.ToString(IdTimeFormat, CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}";
        WriteAndVerify(BackupPath(id), json);
        return id;
    }

    public IReadOnlyList<string> List()
    {
        IReadOnlyList<string> names;
        try
        {
            names = _files.EnumerateNames(Folder, MaxEntries);
        }
        catch (IOException ex)
        {
            throw new RegistryBackupException("バックアップの一覧を安全に取得できません (件数の上限超過を含む)。", ex);
        }

        var ids = names
            .Where(name => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                           !name.EndsWith(ResultSuffix, StringComparison.OrdinalIgnoreCase))
            .Select(name => name[..^".json".Length])
            .Where(IsValidId)
            .Order(StringComparer.Ordinal)
            .ToList();
        return ids.Count > RegistryBackupLimits.MaxBackups
            ? throw new RegistryBackupException($"バックアップが {RegistryBackupLimits.MaxBackups} 件を超えています。")
            : ids;
    }

    public RegistryBackupDocument? Load(string id)
    {
        var json = ReadText(BackupPath(id));
        if (json is null)
        {
            return null;
        }

        var document = Deserialize(json, RegistryCleanJsonContext.Default.RegistryBackupDocument);
        RegistryBackupValidator.ThrowIfInvalid(document);
        return document;
    }

    public void Delete(string id)
    {
        _files.Delete(BackupPath(id));
        _files.Delete(ResultPath(id));
    }

    public void WriteResult(string id, RegistryBackupResultDocument result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.FormatVersion != RegistryBackupDocument.CurrentVersion || result.Items is null ||
            result.Items.Length > RegistryBackupLimits.MaxItems)
        {
            throw new RegistryBackupRejectedException("結果ファイルの版または件数が不正です。");
        }

        WriteAndVerify(ResultPath(id), JsonSerializer.Serialize(result, RegistryCleanJsonContext.Default.RegistryBackupResultDocument));
    }

    public RegistryBackupResultDocument? ReadResult(string id)
    {
        var json = ReadText(ResultPath(id));
        if (json is null)
        {
            return null;
        }

        var result = Deserialize(json, RegistryCleanJsonContext.Default.RegistryBackupResultDocument);
        return result.FormatVersion != RegistryBackupDocument.CurrentVersion || result.Items is null ||
               result.Items.Length > RegistryBackupLimits.MaxItems
            ? throw new RegistryBackupRejectedException("結果ファイルの版または件数が不正です。")
            : result;
    }

    /// <summary>ID は <c>yyyyMMddTHHmmssfffZ-&lt;GUID(N)&gt;</c> の形だけ許す (パスの混入を防ぐ)。</summary>
    internal static bool IsValidId(string? id)
    {
        var separator = id?.IndexOf('-') ?? -1;
        return id is not null && separator > 0 &&
               DateTime.TryParseExact(id[..separator], IdTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) &&
               Guid.TryParseExact(id[(separator + 1)..], "N", out _);
    }

    private static string BackupPath(string id) => Path.Combine(Folder, RequireId(id) + ".json");

    private static string ResultPath(string id) => Path.Combine(Folder, RequireId(id) + ResultSuffix);

    private static string RequireId(string id) =>
        IsValidId(id) ? id : throw new ArgumentException("バックアップ ID の形式が不正です。", nameof(id));

    private void WriteAndVerify(string path, string json)
    {
        try
        {
            _files.WriteNew(path, new UTF8Encoding(false).GetBytes(json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new RegistryBackupException("バックアップを保存できませんでした。", ex);
        }

        string? readBack = null;
        Exception? readFailure = null;
        try
        {
            readBack = _files.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            readFailure = ex;
        }

        if (string.Equals(readBack, json, StringComparison.Ordinal))
        {
            return;
        }

        TryDelete(path);
        throw new RegistryBackupException("バックアップを読み戻して検証できなかったため破棄しました。", readFailure);
    }

    private void TryDelete(string path)
    {
        try
        {
            _files.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 呼び出し側は保存の失敗として扱い削除へ進まない。残ったファイルは次回の一覧で見える。
        }
    }

    private string? ReadText(string path)
    {
        if (!_files.Exists(path))
        {
            return null;
        }

        if (_files.Length(path) > RegistryBackupLimits.MaxFileBytes)
        {
            throw new RegistryBackupRejectedException("ファイルが大きすぎます。");
        }

        return _files.ReadAllText(path);
    }

    private static T Deserialize<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(json, info) ?? throw new RegistryBackupRejectedException("内容が空です。");
        }
        catch (JsonException ex)
        {
            throw new RegistryBackupRejectedException("JSON を解釈できません: " + ex.Message);
        }
    }
}
