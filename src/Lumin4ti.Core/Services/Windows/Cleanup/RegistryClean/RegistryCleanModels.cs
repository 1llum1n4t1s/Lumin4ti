using System.Security.Cryptography;

namespace Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;

/// <summary>レジストリクリーナーのカテゴリー (設計: docs/design/registry-cleaner.md)。</summary>
internal enum RegistryCleanCategory
{
    AppPaths,
    Startup,
    Fonts,
    FileAssociations,
    Uninstall,
    SharedDlls,
}

/// <summary>走査・バックアップの対象ハイブ。HKCU は <c>HKEY_USERS\&lt;SID&gt;</c> として開く。</summary>
internal enum RegistryCleanHive
{
    LocalMachine,
    CurrentUser,

    /// <summary>
    /// 利用者の Classes (<c>HKEY_USERS\&lt;SID&gt;_Classes</c>)。<c>HKEY_USERS\&lt;SID&gt;\Software\Classes</c> はこのハイブへのリンクで、
    /// リンクは拒否するため、実体のハイブを直接開く。キーパスはハイブのルートからの相対 (例: <c>Applications\a.exe</c>)。
    /// SID は <c>_Classes</c> を外した利用者の SID で照合する。
    /// </summary>
    UserClasses,
}

/// <summary>利用者の Classes のハイブ名 (<c>&lt;SID&gt;_Classes</c>) の扱い。</summary>
internal static class RegistryUserClassesHive
{
    public const string Suffix = "_Classes";

    /// <summary>利用者ハイブ (HKCU・利用者の Classes) か。SID の照合が要る。</summary>
    public static bool IsUserHive(RegistryCleanHive hive) => hive is RegistryCleanHive.CurrentUser or RegistryCleanHive.UserClasses;
}

/// <summary>レジストリの 64 / 32 ビット表示 (KEY_WOW64_64KEY / KEY_WOW64_32KEY)。</summary>
internal enum RegistryCleanView
{
    Registry64,
    Registry32,
}

/// <summary>削除の単位。</summary>
internal enum RegistryCleanDeletionUnit
{
    /// <summary>サブキーを子から再帰的に削除する。</summary>
    Subkey,
    /// <summary>値 1 つ。</summary>
    Value,
    /// <summary>Run の値と対応する StartupApproved の値を 1 組で。</summary>
    ValuePair,
    /// <summary>値 (OpenWith 系) とサブキー (Classes\Applications) の両方がある。</summary>
    Mixed,
}

/// <summary>カテゴリーごとの扱い。</summary>
/// <param name="DefaultSelected">走査後に既定で選択する。</param>
/// <param name="IncludedInSelectAll">「すべて選択」に含める。</param>
/// <param name="RequiresIndividualApproval">確認画面で項目ごとの承認が要る。</param>
internal sealed record RegistryCleanCategoryDefinition(
    RegistryCleanCategory Category,
    bool DefaultSelected,
    bool IncludedInSelectAll,
    bool RequiresIndividualApproval,
    RegistryCleanDeletionUnit DeletionUnit);

/// <summary>カテゴリー定義の固定表。</summary>
internal static class RegistryCleanCategories
{
    public static IReadOnlyList<RegistryCleanCategoryDefinition> All { get; } =
    [
        new(RegistryCleanCategory.AppPaths, true, true, false, RegistryCleanDeletionUnit.Subkey),
        new(RegistryCleanCategory.Startup, true, true, false, RegistryCleanDeletionUnit.ValuePair),
        new(RegistryCleanCategory.Fonts, true, true, false, RegistryCleanDeletionUnit.Value),
        new(RegistryCleanCategory.FileAssociations, true, true, false, RegistryCleanDeletionUnit.Mixed),
        new(RegistryCleanCategory.Uninstall, false, false, true, RegistryCleanDeletionUnit.Subkey),
        new(RegistryCleanCategory.SharedDlls, false, false, false, RegistryCleanDeletionUnit.Value),
    ];

    public static RegistryCleanCategoryDefinition Get(RegistryCleanCategory category) =>
        All.First(definition => definition.Category == category);
}

/// <summary>キー・値の変更検出に使う内容ハッシュ (SHA-256 の小文字 16 進)。</summary>
internal static class RegistryContentHash
{
    public static string Of(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
}

/// <summary>走査で見つけた削除候補 1 件。<see cref="ValueName"/> が null ならキー単位。</summary>
/// <param name="ValueType">REG_* の型 (キー単位は null)。</param>
/// <param name="ContentHash">値のバイト列、またはサブツリーの正規化ハッシュ。</param>
/// <param name="KernelName">NtQueryKey(KeyNameInformation) の名前。同じ物理キーの重複判定に使う。</param>
/// <param name="Reason">候補にした理由の符号 (<c>CleanupMessage</c> 形式)。</param>
internal sealed record RegistryScanItem(
    RegistryCleanCategory Category,
    RegistryCleanHive Hive,
    RegistryCleanView View,
    string KeyPath,
    string? ValueName,
    uint? ValueType,
    string ContentHash,
    DateTime LastWriteTimeUtc,
    string KernelName,
    string Reason);

/// <summary>候補にしなかった理由の種別。</summary>
internal enum RegistryRetentionKind
{
    /// <summary>存在・欠損を確定できなかった。</summary>
    Unknown,
    /// <summary>OS 所有などで保護している。</summary>
    Protected,
}

/// <summary>「保持」として要約表示する項目。選択肢には出さない。</summary>
internal sealed record RegistryRetainedEntry(
    RegistryCleanCategory Category,
    RegistryRetentionKind Kind,
    string KeyPath,
    string? ValueName,
    string Reason);

/// <summary>項目ごとの結果。</summary>
internal enum RegistryItemState
{
    Deleted,
    /// <summary>走査後の変更・存在・不明のため意図して残した (成功扱い)。</summary>
    Retained,
    /// <summary>アクセス拒否・リンク・TrustedInstaller 所有などで残した (全体は Partial)。</summary>
    Skipped,
    Failed,
    Restored,
    AlreadyPresent,
    /// <summary>復元先に別の値があり、上書きしなかった (全体は Partial)。</summary>
    Conflict,
}

/// <summary>実行全体の結果。</summary>
internal enum RegistryOverallState
{
    Success,
    Partial,
    Failed,
    Canceled,
}

internal enum RegistryEvidenceTier
{
    /// <summary>Microsoft 公式文書。</summary>
    A,
    /// <summary>別ホストの独立した二次資料。</summary>
    B,
}

internal sealed record RegistryEvidenceSource(RegistryEvidenceTier Tier, string Url);

internal sealed record RegistryCategoryEvidence(RegistryCleanCategory Category, IReadOnlyList<RegistryEvidenceSource> Sources);

/// <summary>
/// カテゴリーごとの根拠の固定表。tier A は 1 件、tier B は別ホストの 2 件 (テストで固定)。
/// 規則データ JSON の検証器は HKCU\Software 限定のため使わない。
/// </summary>
internal static class RegistryCleanEvidenceTable
{
    private static RegistryCategoryEvidence A(RegistryCleanCategory category, string url) =>
        new(category, [new RegistryEvidenceSource(RegistryEvidenceTier.A, url)]);

    public static IReadOnlyList<RegistryCategoryEvidence> All { get; } =
    [
        A(RegistryCleanCategory.AppPaths, "https://learn.microsoft.com/en-us/windows/win32/shell/app-registration"),
        A(RegistryCleanCategory.Startup, "https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys"),
        // 値の形式は https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2003/cc787587(v=ws.10) も参照。
        A(RegistryCleanCategory.Fonts, "https://learn.microsoft.com/en-us/windows/win32/gdi/font-installation-and-deletion"),
        A(RegistryCleanCategory.FileAssociations, "https://learn.microsoft.com/en-us/windows/win32/shell/app-registration"),
        A(RegistryCleanCategory.Uninstall, "https://learn.microsoft.com/en-us/windows/win32/msi/uninstall-registry-key"),
        // SharedDLLs キーのパスと参照カウントの意味は、本文を開いて確認した 2 件 (Microsoft の文書にパスの記載はない)。
        // 候補の Revenera 製品ヘルプ (docs.revenera.com) は 403 で本文を確認できなかったため、同社ブログへ差し替えた。
        new(RegistryCleanCategory.SharedDlls,
        [
            new RegistryEvidenceSource(RegistryEvidenceTier.B, "https://www.revenera.com/blog/software-installation/cleaning-up-your-shared-dlls-registry-references-for-msis/"),
            new RegistryEvidenceSource(RegistryEvidenceTier.B, "https://jrsoftware.org/ishelp/topic_isxfunc_incrementsharedcount.htm"),
        ]),
    ];

    public static RegistryCategoryEvidence For(RegistryCleanCategory category) =>
        All.First(evidence => evidence.Category == category);
}
