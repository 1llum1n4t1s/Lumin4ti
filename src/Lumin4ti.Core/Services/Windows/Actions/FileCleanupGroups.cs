using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// 旧クリーンアップ画面に残している固定グループのカタログ (アイコン・サムネイル・フォントのキャッシュ再構築、先読み・検索インデックスの再構築)。
/// 一時ファイル・アプリ・パッケージ・ブラウザー・Windows Update のキャッシュは新しいクリーンアップ画面の規則へ移した。
/// 認証情報・鍵・アプリ設定を持つフォルダ (.gnupg / .aws / .config 等) は
/// 再生成できないため、意図的にどのグループにも含めていない。
/// </summary>
[SupportedOSPlatform("windows")]
public static class FileCleanupGroups
{
    /// <summary>カタログへ登録するグループ (この順に画面へ並ぶ)。</summary>
    public static IEnumerable<IMaintenanceItem> CreateAll(
        ICommandExecutor executor,
        ICleanupPreferences? preferences = null,
        IRebootCacheCleanupScheduler? rebootScheduler = null)
    {
        ArgumentNullException.ThrowIfNull(executor);

        return
        [
            CreateShellCache(executor, preferences, rebootScheduler),
            CreateOsIndex(executor, preferences),
        ];
    }

    // ═══ アイコン・サムネイル・フォントのキャッシュ ═══

    internal static readonly CleanupTarget[] ShellCacheTargets =
    [
        CleanupTarget.Files(@"%LOCALAPPDATA%", "IconCache.db"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Microsoft\Windows\Explorer"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Microsoft\FontCache"),
        CleanupTarget.Contents(@"%SystemRoot%\ServiceProfiles\LocalService\AppData\Local\FontCache"),
        CleanupTarget.Files(@"%SystemRoot%\System32", "FNTCACHE.DAT"),
    ];

    internal static string GetShellCacheGroupName(CleanupTarget target) =>
        target.RawPath.Contains(@"\Microsoft\Windows\Explorer", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(target.Pattern, "IconCache.db", StringComparison.OrdinalIgnoreCase)
            ? "Windows Explorer"
            : "Windows Font Cache";

    /// <summary>フォントキャッシュのファイルはフォントサービスが常時開いている。</summary>
    internal static readonly string[] ShellCacheServices = ["FontCache", "FontCache3.0.0.0"];

    internal static FileCleanupAction CreateShellCache(ICommandExecutor executor, ICleanupPreferences? preferences = null,
        IRebootCacheCleanupScheduler? rebootScheduler = null) => new(
        id: "cleanup-shell-cache",
        label: "アイコン・サムネイル・フォントのキャッシュを再構築",
        description:
            "エクスプローラーのアイコンキャッシュ、サムネイルキャッシュ、フォントキャッシュを削除して作り直させます。" +
            "アイコンが白紙・別アプリのものになる、サムネイルが古いまま更新されない、文字が正しく表示されない、といった表示の壊れに対する定番の修復です。" +
            "使用中の対象は署名済みの正規インストールで起動時の再列挙を予約します（最大3回・7日間）。" +
            "起動時もロック中のファイルは保持します。標準と異なるプロファイルなど予約できない対象は結果に表示します。キャッシュ再構築の間だけ表示が一時的に遅くなります。",
        targetProvider: () => ShellCacheTargets,
        executor: executor,
        servicesToStop: ShellCacheServices,
        requiresReboot: true,
        scheduleBlockedForReboot: true,
        preferences: preferences,
        checkListKeySelector: GetShellCacheGroupName,
        rebootScheduler: rebootScheduler ?? new RebootCacheCleanupService(executor));

    // ═══ 先読み・検索インデックス ═══

    internal static readonly CleanupTarget[] OsIndexTargets =
    [
        CleanupTarget.Contents(@"%SystemRoot%\Prefetch"),
        CleanupTarget.Contents(@"%ProgramData%\Microsoft\Search\Data\Applications\Windows"),
    ];

    internal static string GetOsIndexGroupName(CleanupTarget target) =>
        target.RawPath switch
        {
            @"%SystemRoot%\Prefetch" => "Prefetch",
            @"%ProgramData%\Microsoft\Search\Data\Applications\Windows" => "Windows Search",
            _ => FileCleanupAction.DescribeTarget(target),
        };

    internal static readonly string[] OsIndexServices = ["SysMain", "wsearch"];

    internal static FileCleanupAction CreateOsIndex(ICommandExecutor executor, ICleanupPreferences? preferences = null) => new(
        id: "cleanup-os-index",
        label: "先読みデータと検索インデックスを削除して再構築",
        description:
            "アプリ起動を速くするための先読みデータ (Prefetch) と、エクスプローラーの検索インデックスを削除し、Windows に作り直させます。" +
            "検索結果が出てこない・古いファイルが引っかかる、といったインデックス破損の修復に使います。" +
            "再構築が終わるまでの数十分〜数時間はアプリの起動と検索が遅くなり、その間バックグラウンドの CPU 使用率が上がります。",
        targetProvider: () => OsIndexTargets,
        executor: executor,
        servicesToStop: OsIndexServices,
        preferences: preferences,
        checkListKeySelector: GetOsIndexGroupName);
}
