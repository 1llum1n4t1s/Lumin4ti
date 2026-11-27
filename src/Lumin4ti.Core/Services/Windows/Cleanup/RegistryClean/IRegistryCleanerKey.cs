namespace Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;

/// <summary>キーを開くときに要求する権限。</summary>
internal enum RegistryCleanAccess
{
    /// <summary>列挙・値の読み取り・情報の取得 (走査用)。</summary>
    Read,
    /// <summary>値の設定・削除。</summary>
    ModifyValues,
    /// <summary>値の設定・削除と、サブキーの作成、キー自体の削除。</summary>
    DeleteTree,
}

/// <summary>値の生データ。型は REG_* の数値、データは保存形式のままのバイト列。</summary>
internal sealed record RegistryRawValue(uint Type, byte[] Data);

/// <summary>キーの情報。</summary>
internal sealed record RegistryKeyInfo(DateTime LastWriteTimeUtc, int SubKeyCount, int ValueCount);

/// <summary>開こうとしたキーがレジストリのシンボリックリンクだった。リンク自身もリンク先も触らない。</summary>
internal sealed class RegistryCleanLinkException(string name)
    : IOException(CleanupMessage.Of("RegistrySymlink", name));

/// <summary>
/// 開いたキー 1 つ分のハンドル。各階層は REG_OPTION_OPEN_LINK で 1 階層ずつ開き、リンクなら
/// <see cref="RegistryCleanLinkException"/> を投げる。値・キーの削除は、そのキーを開いた同じハンドルで行う。
/// テストでは偽の実装に差し替える。
/// </summary>
internal interface IRegistryCleanerKey : IDisposable
{
    /// <summary>直下の子を 1 階層だけ開く。無ければ null。名前に <c>\</c> を含めてはならない。</summary>
    IRegistryCleanerKey? OpenChild(string name, RegistryCleanAccess access, RegistryCleanView view);

    /// <summary>直下の子を作る (既にあれば開く)。このキーを <see cref="RegistryCleanAccess.DeleteTree"/> で開いている必要がある。</summary>
    IRegistryCleanerKey CreateChild(string name, RegistryCleanView view);

    /// <summary>値の生データを返す。無ければ null。</summary>
    RegistryRawValue? GetValue(string name);

    void SetValue(string name, RegistryRawValue value);

    IReadOnlyList<string> GetValueNames();

    IReadOnlyList<string> GetSubKeyNames();

    /// <summary>値を削除する (既に無ければ何もしない)。</summary>
    void DeleteValue(string name);

    /// <summary>
    /// このキー自体を、子から再帰的に削除する (既定値を含む)。子がリンクなら <see cref="RegistryCleanLinkException"/> で拒否する。
    /// <see cref="RegistryCleanAccess.DeleteTree"/> で開いたキーだけが対象。
    /// </summary>
    void DeleteSelf();

    RegistryKeyInfo QueryInfo();

    /// <summary>NtQueryKey(KeyNameInformation) のカーネル名 (<c>\REGISTRY\MACHINE\...</c>)。同じ物理キーの判定に使う。</summary>
    string KernelName();

    /// <summary>キーの所有者の SID (文字列形式)。</summary>
    string OwnerSid();
}

/// <summary>ルートを開く工場。</summary>
internal interface IRegistryCleanerKeyFactory
{
    /// <summary>HKEY_LOCAL_MACHINE を指定の表示で開く。</summary>
    IRegistryCleanerKey OpenLocalMachine(RegistryCleanView view);

    /// <summary><c>HKEY_USERS\&lt;sid&gt;</c> (その利用者の HKCU) を開く。</summary>
    IRegistryCleanerKey OpenUserHive(string sid, RegistryCleanView view);
}
