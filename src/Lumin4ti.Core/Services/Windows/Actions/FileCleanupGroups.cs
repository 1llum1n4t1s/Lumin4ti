using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// AegisOverhaul バッチが行っていたファイル削除を、用途ごとのグループに分けたカタログ。
/// グループ単位でボタンになるため、利用者は消したいものだけを選べる。
/// 認証情報・鍵・アプリ設定を持つフォルダ (.gnupg / .aws / .config 等) は
/// 再生成できないため、意図的にどのグループにも含めていない。
/// </summary>
[SupportedOSPlatform("windows")]
public static class FileCleanupGroups
{
    /// <summary>カタログへ登録するグループ (この順に画面へ並ぶ)。</summary>
    public static IEnumerable<IMaintenanceItem> CreateAll(
        ICommandExecutor executor,
        ICleanupPreferences? preferences = null) =>
        CreateCleanupActions(executor, preferences);

    /// <summary>
    /// 削除グループの実体。画面のカタログとサインイン時の定期実行が同じ生成経路を使い、
    /// 「画面で見えている項目 = 定期実行で選べる項目 = 実際に走る処理」を一致させる。
    /// </summary>
    public static IReadOnlyList<IMaintenanceAction> CreateCleanupActions(
        ICommandExecutor executor,
        ICleanupPreferences? preferences = null)
    {
        ArgumentNullException.ThrowIfNull(executor);

        return
        [
            CreateUserTemp(preferences),
            CreateSystemTemp(executor, preferences),
            CreateAppCache(preferences),
            CreatePackageCache(preferences),
            CreateBrowserCache(preferences),
            CreateShellCache(executor, preferences),
            CreateWindowsUpdateCache(executor, preferences),
            CreateOsIndex(executor, preferences),
            CreateDriveRootLeftovers(preferences),
            CreateOutlookOfflineCache(preferences),
            CreateNulFiles(preferences),
            new RecycleBinCleanupAction(),
        ];
    }

    // ═══ ユーザーの一時ファイル ═══

    internal static readonly CleanupTarget[] UserTempTargets =
    [
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Temp"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\CrashDumps"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\D3DSCache"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Microsoft\Windows\INetCache"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Microsoft\Windows\Temporary Internet Files"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Microsoft\Windows\WebCache"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Microsoft\Windows\AppCache"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Microsoft\Internet Explorer"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Microsoft\IME\15.0\IMEJP\Cache"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Microsoft\IME\15.0\IMEJP\Watson"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Microsoft\Office\16.0\Wef"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Microsoft\Office\SolutionPackages"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Microsoft\Outlook\HubAppFileCache"),
        CleanupTarget.Contents(@"%APPDATA%\Microsoft\Office\Recent"),

        // 最近使ったファイルの履歴。%USERPROFILE%\Recent は実体ではなく Windows 標準の
        // ジャンクションで、リンク先の実体を消さないガードに毎回弾かれて一度も消えていなかったため、
        // 実体のパスを直接指定する。ただし中身ごとは消さない: 同じフォルダの AutomaticDestinations には
        // クイックアクセスとタスクバーのピン留め (QuickAccessSortAction が扱う
        // f01b4d95cf55d32a.automaticDestinations-ms) が入っていて、再生成できない利用者データのため。
        // 説明文が約束している「最近使ったファイルの履歴」は直下の .lnk なので、それだけを対象にする。
        CleanupTarget.Files(@"%APPDATA%\Microsoft\Windows\Recent", "*.lnk"),
        CleanupTarget.Contents(@"%USERPROFILE%\AppData\LocalLow\Microsoft\CryptnetUrlCache"),
        CleanupTarget.Contents(@"%USERPROFILE%\AppData\LocalLow\webviewdata"),
        CleanupTarget.Contents(@"%USERPROFILE%\AppData\LocalLow\Intel"),
    ];

    internal static FileCleanupAction CreateUserTemp(ICleanupPreferences? preferences = null) => new(
        id: "cleanup-user-temp",
        label: "ユーザーの一時ファイルとキャッシュを削除",
        description:
            "サインイン中のユーザー用の一時フォルダ (%LOCALAPPDATA%\\Temp)、クラッシュダンプ、シェーダーキャッシュ、Internet Explorer / WebView 系のキャッシュ、" +
            "最近使ったファイルの履歴、IME の変換キャッシュなどをまとめて削除します。数 GB 単位の空き容量になることがあります。" +
            "アプリが使用中のファイルは自動的にスキップされるため、作業中でも安全に実行できます。",
        targetProvider: () => UserTempTargets,
        preferences: preferences);

    // ═══ システムの一時ファイル・ログ ═══

    internal static readonly CleanupTarget[] SystemTempTargets =
    [
        CleanupTarget.Contents(@"%SystemRoot%\Temp"),
        CleanupTarget.Contents(@"%SystemRoot%\SystemTemp"),
        CleanupTarget.Contents(@"%SystemRoot%\Logs"),
        CleanupTarget.Contents(@"%SystemRoot%\System32\LogFiles"),
        CleanupTarget.Contents(@"%ProgramData%\USOShared\Logs"),
        CleanupTarget.Contents(@"%ProgramData%\Microsoft\EdgeUpdate\Log"),
        CleanupTarget.Contents(@"%ProgramData%\Microsoft\Network\Downloader"),
        CleanupTarget.Contents(@"%SystemRoot%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"),
        CleanupTarget.Contents(@"%ProgramData%\Microsoft\Windows Defender\Definition Updates\Backup"),
        CleanupTarget.Contents(@"%ProgramData%\Microsoft\Windows Defender\Scans\History\Results\Resource"),
        CleanupTarget.Contents(@"%ProgramData%\Microsoft\Windows Defender\Support"),
    ];

    /// <summary>BITS の転送キューと配信最適化キャッシュはサービスが握っているため停止してから消す。</summary>
    internal static readonly string[] SystemTempServices = ["bits", "DoSvc"];

    internal static FileCleanupAction CreateSystemTemp(ICommandExecutor executor, ICleanupPreferences? preferences = null) => new(
        id: "cleanup-system-temp",
        label: "システムの一時ファイルとログを削除",
        description:
            "Windows 本体の一時フォルダ、セットアップ・サービスの各種ログ、BITS の転送キュー、配信最適化 (他 PC への更新配布) のキャッシュ、" +
            "Windows Defender の古い定義バックアップとスキャン履歴を削除します。実行中は BITS と配信最適化サービスを一時停止し、完了後に元の状態へ戻します。" +
            "過去のトラブル調査に使うログも消えるため、不具合を調査中の場合は実行を控えてください。",
        targetProvider: () => SystemTempTargets,
        executor: executor,
        servicesToStop: SystemTempServices,
        preferences: preferences);

    // ═══ アプリのキャッシュ ═══

    internal static readonly CleanupTarget[] AppCacheTargets =
    [
        CleanupTarget.Contents(@"%APPDATA%\Antigravity\Cache"),
        CleanupTarget.Contents(@"%APPDATA%\Antigravity\CachedData"),
        CleanupTarget.Contents(@"%APPDATA%\Antigravity\Crashpad"),
        CleanupTarget.Contents(@"%APPDATA%\Antigravity\WebStorage"),
        CleanupTarget.Contents(@"%APPDATA%\Aqua Voice\Cache"),
        CleanupTarget.Contents(@"%APPDATA%\Claude\Cache"),
        CleanupTarget.Contents(@"%APPDATA%\Claude\Code Cache"),
        CleanupTarget.Contents(@"%APPDATA%\Claude\logs"),
        CleanupTarget.Contents(@"%APPDATA%\Claude\vm_bundles"),
        CleanupTarget.Contents(@"%APPDATA%\Cursor\Cache"),
        CleanupTarget.Contents(@"%APPDATA%\Cursor\CachedData"),
        CleanupTarget.Contents(@"%APPDATA%\Cursor\GPUCache"),
        CleanupTarget.Contents(@"%APPDATA%\discord\Cache"),
        CleanupTarget.Contents(@"%APPDATA%\discord\Code Cache"),
        CleanupTarget.Contents(@"%APPDATA%\discord\GPUCache"),
        CleanupTarget.Contents(@"%APPDATA%\discord\VideoDecodeStats"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\AMD\DxCache"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\AMD\DxcCache"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\AMD\VkCache"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Ati\GLCache"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\NVIDIA\DXCache"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\UnrealEngine"),
        CleanupTarget.Contents(@"%ProgramData%\LGHUB\cache"),
        CleanupTarget.Contents(@"%USERPROFILE%\AppData\LocalLow\NVIDIA\PerDriverVersion\DXCache"),
        CleanupTarget.Contents(@"%USERPROFILE%\.claude\debug"),
        CleanupTarget.Contents(@"%USERPROFILE%\.claude\telemetry"),
        CleanupTarget.Contents(@"%USERPROFILE%\.claude-mem\logs"),
    ];

    internal static FileCleanupAction CreateAppCache(ICleanupPreferences? preferences = null) => new(
        id: "cleanup-app-cache",
        label: "アプリのキャッシュとログを削除",
        description:
            "Electron 系アプリ (Claude / Cursor / Discord / Antigravity 等) のキャッシュ、GPU のシェーダーキャッシュ (NVIDIA DXCache / AMD)、" +
            "Unreal Engine の派生データ、常駐ツールのログを削除します。設定やログイン状態は消えず、各アプリが次の起動で作り直します。" +
            "対象のアプリが入っていない環境では、その分だけスキップされます。",
        targetProvider: () => AppCacheTargets,
        preferences: preferences);

    // ═══ 開発ツールのパッケージキャッシュ ═══

    /// <summary>
    /// 内容アドレス方式のストア型キャッシュ。実体は venv や node_modules へハードリンクで配られるため、
    /// ストアを消しても展開済みの環境は壊れず、次回インストール時に再取得されるだけで済む。
    /// </summary>
    internal static readonly CleanupTarget[] StoreCacheTargets =
    [
        CleanupTarget.Contents(@"%LOCALAPPDATA%\npm-cache"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\pnpm\store"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\uv\cache"),
        CleanupTarget.Contents(@"%USERPROFILE%\.cargo\registry"),
    ];

    internal static readonly CleanupTarget[] PackageCacheTargets =
    [
        CleanupTarget.Contents(@"%LOCALAPPDATA%\pip\Cache"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Yarn\Cache"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\NuGet"),
        CleanupTarget.Contents(@"%APPDATA%\uv\python"),
        CleanupTarget.Contents(@"%USERPROFILE%\.npm"),
        CleanupTarget.Contents(@"%USERPROFILE%\.nuget"),
        CleanupTarget.Contents(@"%USERPROFILE%\.gradle\caches"),
        CleanupTarget.Contents(@"%USERPROFILE%\.m2\repository"),
        CleanupTarget.Contents(@"%USERPROFILE%\.bun\install\cache"),
        CleanupTarget.Contents(@"%USERPROFILE%\.cache"),
        CleanupTarget.Contents(@"%USERPROFILE%\.matplotlib"),
        CleanupTarget.Contents(@"%USERPROFILE%\.templateengine"),
        CleanupTarget.Contents(@"%USERPROFILE%\.omnisharp"),
        CleanupTarget.Contents(@"%USERPROFILE%\.crossnote"),
        .. StoreCacheTargets,
    ];

    internal static FileCleanupAction CreatePackageCache(ICleanupPreferences? preferences = null) => new(
        id: "cleanup-package-cache",
        label: "開発ツールのパッケージキャッシュを削除",
        description:
            "npm / pnpm / NuGet / pip / Yarn / Gradle / Maven / Bun / uv / Cargo がダウンロード済みパッケージを溜め込んでいるキャッシュを削除します。数十 GB 単位で空くことがあります。" +
            "認証情報や設定ファイル (.gnupg / .aws / .config 等) は対象に含みません。" +
            "ホームディレクトリの .cache フォルダ全体も対象のため、huggingface のモデルなど数十 GB 規模の再ダウンロードが必要なキャッシュが含まれることがあります。" +
            "削除後、各プロジェクトの次回ビルドや復元でパッケージが再ダウンロードされるため、その 1 回だけ時間と通信量がかかります。",
        targetProvider: () => PackageCacheTargets,
        preferences: preferences);

    // ═══ ブラウザキャッシュ ═══

    /// <summary>Chromium 系ブラウザのデータ基点 (User Data の親)。</summary>
    internal static readonly string[] BrowserRoots =
    [
        @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser",
        @"%LOCALAPPDATA%\Microsoft\Edge",
        @"%LOCALAPPDATA%\Google\Chrome",
        @"%LOCALAPPDATA%\Google\Chrome Beta",
        @"%LOCALAPPDATA%\Google\Chrome Dev",
        @"%LOCALAPPDATA%\Vivaldi",
        @"%LOCALAPPDATA%\Perplexity\Comet",
    ];

    /// <summary>ブラウザ基点の直下にある、プロファイル共通のキャッシュ。</summary>
    private static readonly string[] BrowserSharedCaches =
    [
        "Temp",
        @"User Data\Safe Browsing",
        @"User Data\CertificateRevocation",
        @"User Data\optimization_guide_model_store",
        @"User Data\BrowserMetrics",
        @"User Data\component_crx_cache",
        @"User Data\Crashpad",
        @"User Data\extensions_crx_cache",
        @"User Data\GraphiteDawnCache",
        @"User Data\GrShaderCache",
        @"User Data\ShaderCache",
        @"User Data\WidevineCdm",
    ];

    /// <summary>
    /// プロファイル (Default / Profile 1 …) ごとのキャッシュ。File System / IndexedDB / WebStorage /
    /// Extension State は Cookie 非依存 (localStorage 等) でログイン状態を保持するサイト・拡張機能の
    /// 永続データであり、「キャッシュ」ではなく実質サイトデータのため、消えないと説明している
    /// ログイン状態を壊さないよう対象から除外する。
    /// </summary>
    private static readonly string[] BrowserProfileCaches =
    [
        "Cache",
        "Code Cache",
        "Service Worker",
        "GPUCache",
        "JumpListIconsRecentClosed",
        "JumpListIconsTopSites",
        "DawnGraphiteCache",
        "DawnWebGPUCache",
        "Shared Dictionary",
        "screen_ai",
        "CRXTelemetry",
        "VideoDecodeStats",
        "blob_storage",
        "Media Cache",
        "Reporting and NEL",
        "CertificateTransparency",
        "DawnCache",
    ];

    /// <summary>
    /// インストール済みブラウザとそのプロファイルを走査して対象を組み立てる。
    /// プロファイル数は環境ごとに違うため、実行時に列挙する。
    /// </summary>
    internal static IEnumerable<CleanupTarget> EnumerateBrowserTargets()
    {
        foreach (var rawRoot in BrowserRoots)
        {
            if (!FileCleanupEngine.TryResolve(rawRoot, out var root, out _) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var relative in BrowserSharedCaches)
            {
                yield return CleanupTarget.Contents(Path.Combine(root, relative));
            }

            var userData = Path.Combine(root, "User Data");
            string[] profiles;
            try
            {
                profiles = Directory.Exists(userData) ? Directory.GetDirectories(userData) : [];
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var profile in profiles)
            {
                foreach (var relative in BrowserProfileCaches)
                {
                    yield return CleanupTarget.Contents(Path.Combine(profile, relative));
                }
            }
        }
    }

    /// <summary>
    /// チェックリスト用に、ブラウザ配下の対象を「ブラウザ 1 つ」へ畳む。
    /// プロファイル数 × キャッシュ種別で対象は数千件になりうるが、利用者が選びたい単位は
    /// 「このブラウザは消す / 消さない」なので、そこまで階層を上げる。
    /// 返すのは未展開のルート (%LOCALAPPDATA%\...) で、保存した設定がユーザー名に依存しないようにする。
    /// </summary>
    internal static string DescribeBrowserTarget(CleanupTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        foreach (var rawRoot in BrowserRoots)
        {
            if (FileCleanupEngine.TryResolve(rawRoot, out var root, out _) &&
                target.RawPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return rawRoot;
            }
        }

        return FileCleanupAction.DescribeTarget(target);
    }

    internal static FileCleanupAction CreateBrowserCache(ICleanupPreferences? preferences = null) => new(
        id: "cleanup-browser-cache",
        label: "ブラウザのキャッシュを削除",
        description:
            "インストール済みの Chromium 系ブラウザ (Edge / Chrome / Brave / Vivaldi / Comet) を検出し、全プロファイルの Web キャッシュ、" +
            "GPU シェーダーキャッシュ、Service Worker、クラッシュレポート、拡張機能の一時データを削除します。数 GB 単位で空くことが多い項目です。" +
            "ブックマーク・パスワード・ログイン状態は消えません。実行前にブラウザを終了しておくと、より確実に削除できます。",
        targetProvider: () => EnumerateBrowserTargets(),
        preferences: preferences,
        checkListKeySelector: DescribeBrowserTarget);

    // ═══ アイコン・サムネイル・フォントのキャッシュ ═══

    internal static readonly CleanupTarget[] ShellCacheTargets =
    [
        CleanupTarget.Files(@"%LOCALAPPDATA%", "IconCache.db"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Microsoft\Windows\Explorer"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Microsoft\FontCache"),
        CleanupTarget.Contents(@"%SystemRoot%\ServiceProfiles\LocalService\AppData\Local\FontCache"),
        CleanupTarget.Files(@"%SystemRoot%\System32", "FNTCACHE.DAT"),
    ];

    /// <summary>フォントキャッシュのファイルはフォントサービスが常時開いている。</summary>
    internal static readonly string[] ShellCacheServices = ["FontCache", "FontCache3.0.0.0"];

    internal static FileCleanupAction CreateShellCache(ICommandExecutor executor, ICleanupPreferences? preferences = null) => new(
        id: "cleanup-shell-cache",
        label: "アイコン・サムネイル・フォントのキャッシュを再構築",
        description:
            "エクスプローラーのアイコンキャッシュ、サムネイルキャッシュ、フォントキャッシュを削除して作り直させます。" +
            "アイコンが白紙・別アプリのものになる、サムネイルが古いまま更新されない、文字が正しく表示されない、といった表示の壊れに対する定番の修復です。" +
            "実行後はエクスプローラーを再起動し、シェルが開いたままで消せなかったファイルは次回の再起動時に削除されます。キャッシュ再構築の間だけ表示が一時的に遅くなります。",
        targetProvider: () => ShellCacheTargets,
        executor: executor,
        servicesToStop: ShellCacheServices,
        requiresReboot: true,
        affectsExplorer: true,
        scheduleBlockedForReboot: true,
        preferences: preferences);

    // ═══ Windows Update のキャッシュ ═══

    internal static readonly CleanupTarget[] WindowsUpdateCacheTargets =
    [
        CleanupTarget.Contents(@"%SystemRoot%\SoftwareDistribution\Download"),
        CleanupTarget.Contents(@"%SystemRoot%\SoftwareDistribution\DataStore"),
        CleanupTarget.Contents(@"%SystemRoot%\System32\catroot2"),
    ];

    internal static readonly string[] WindowsUpdateCacheServices = ["wuauserv", "bits", "usosvc", "cryptsvc"];

    internal static FileCleanupAction CreateWindowsUpdateCache(ICommandExecutor executor, ICleanupPreferences? preferences = null) => new(
        id: "cleanup-windows-update-cache",
        label: "Windows Update のダウンロードキャッシュを削除",
        description:
            "適用済みの更新プログラムのダウンロード済みファイル (SoftwareDistribution)、更新履歴のデータストア、署名カタログのキャッシュ (catroot2) を削除します。" +
            "更新が途中で止まる・同じ更新が繰り返し失敗するといった不調の定番対処で、数 GB の空き容量にもなります。" +
            "関連サービスを一時停止してから削除し、完了後に元の状態へ戻します。Windows Update の更新履歴の表示は消えます。",
        targetProvider: () => WindowsUpdateCacheTargets,
        executor: executor,
        servicesToStop: WindowsUpdateCacheServices,
        preferences: preferences);

    // ═══ 先読み・検索インデックス ═══

    internal static readonly CleanupTarget[] OsIndexTargets =
    [
        CleanupTarget.Contents(@"%SystemRoot%\Prefetch"),
        CleanupTarget.Contents(@"%ProgramData%\Microsoft\Search\Data\Applications\Windows"),
    ];

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
        preferences: preferences);

    // ═══ ドライブ直下の残骸 ═══

    /// <summary>
    /// ドライブ直下に残る残骸フォルダ。フォルダごと消すため、作った主体を名前で断定できるものだけを載せる
    /// (%SystemDrive%\log のような汎用名は、利用者やアプリが正規の置き場として作っている可能性があるので入れない)。
    /// 説明文に列挙した名前と一致させること (ドライブ直下の残骸フォルダは説明に列挙した名前だけを消す)。
    /// </summary>
    internal static readonly CleanupTarget[] DriveRootLeftoverTargets =
    [
        CleanupTarget.Remove(@"%SystemDrive%\$SysReset"),
        CleanupTarget.Remove(@"%SystemDrive%\AMD"),
        CleanupTarget.Remove(@"%SystemDrive%\Intel"),
        CleanupTarget.Remove(@"%SystemDrive%\OneDriveTemp"),
        CleanupTarget.Remove(@"%SystemDrive%\PerfLogs"),
        CleanupTarget.Remove(@"%SystemDrive%\SWSetup"),
        CleanupTarget.Remove(@"%SystemDrive%\Windows.old"),
    ];

    internal static FileCleanupAction CreateDriveRootLeftovers(ICleanupPreferences? preferences = null) => new(
        id: "cleanup-drive-root-leftovers",
        label: "ドライブ直下に残った残骸フォルダを削除",
        description:
            "ドライバのインストーラーや OS のアップグレードがシステムドライブの直下に残していくフォルダ (AMD / Intel / SWSetup / PerfLogs / OneDriveTemp / $SysReset / Windows.old) を削除します。" +
            "特に Windows.old は数十 GB になることがあり、最大の空き容量になります。" +
            "注意: Windows.old を消すと、大型アップデート後に「以前のバージョンに戻す」ことができなくなります。アップグレード直後は実行しないでください。",
        targetProvider: () => DriveRootLeftoverTargets,
        preferences: preferences);

    // ═══ Outlook のオフラインキャッシュ ═══

    internal static readonly CleanupTarget[] OutlookOfflineCacheTargets =
    [
        CleanupTarget.Files(@"%LOCALAPPDATA%\Microsoft\Outlook", "*.ost"),
        CleanupTarget.Files(@"%LOCALAPPDATA%\Microsoft\Outlook", "*.nst"),
    ];

    internal static FileCleanupAction CreateOutlookOfflineCache(ICleanupPreferences? preferences = null) => new(
        id: "cleanup-outlook-offline-cache",
        label: "Outlook のオフラインキャッシュを削除",
        description:
            "Outlook がサーバーのメールを手元に複製しているオフラインデータ (.ost) と検索用データ (.nst) を削除します。1 つで数 GB〜数十 GB になることがあります。" +
            "サーバー上のメールは消えず、次回 Outlook を起動したときに再同期されます。" +
            "注意: 再同期が終わるまで過去のメールが表示されず、回線によっては数時間かかります。ローカルにしかない .pst のデータは対象外なので消えません。",
        targetProvider: () => OutlookOfflineCacheTargets,
        preferences: preferences);

    // ═══ 迷子の nul ファイル ═══

    internal static readonly CleanupTarget[] NulFileTargets =
    [
        CleanupTarget.RecursiveFiles(@"%SystemDrive%\", "nul"),
    ];

    internal static FileCleanupAction CreateNulFiles(ICleanupPreferences? preferences = null) => new(
        id: "cleanup-nul-files",
        label: "迷子の nul ファイルを削除",
        description:
            "Git Bash や一部の開発ツールがコマンドのリダイレクト (> nul) をファイル名としてそのまま作成してしまうことで、" +
            "システムドライブのあちこちに残る「nul」という名前の空ファイルを、フォルダを問わず検索して削除します。" +
            "Windows の予約デバイス名と同じ名前のため通常の方法では削除できず、専用の処理で安全に削除します。" +
            "ドライブ全体を検索するため、ファイル数の多い環境では完了まで数分以上かかることがあります。",
        targetProvider: () => NulFileTargets,
        preferences: preferences);
}
