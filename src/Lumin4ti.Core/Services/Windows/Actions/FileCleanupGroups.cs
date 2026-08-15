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
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Microsoft\Windows\AppCache"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Microsoft\IME\15.0\IMEJP\Cache"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Microsoft\IME\15.0\IMEJP\Watson"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Microsoft\Office\16.0\Wef"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Microsoft\Office\SolutionPackages"),
        CleanupTarget.Contents(@"%LOCALAPPDATA%\Microsoft\Outlook\HubAppFileCache"),
        CleanupTarget.Contents(@"%USERPROFILE%\AppData\LocalLow\Microsoft\CryptnetUrlCache"),
    ];

    internal static string GetUserTempGroupName(CleanupTarget target)
    {
        var path = target.RawPath;
        return path switch
        {
            @"%LOCALAPPDATA%\Temp" => "Windows Temp",
            @"%LOCALAPPDATA%\CrashDumps" => "CrashDumps",
            @"%LOCALAPPDATA%\D3DSCache" => "Direct3D Shader Cache",
            _ when path.Contains(@"\Microsoft\Windows\INetCache", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains(@"\Microsoft\Windows\Temporary Internet Files", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains(@"\Microsoft\Windows\AppCache", StringComparison.OrdinalIgnoreCase) => "Internet Cache",
            _ when path.Contains(@"\Microsoft\IME\", StringComparison.OrdinalIgnoreCase) => "Microsoft IME",
            _ when path.Contains(@"\Microsoft\Office\", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains(@"\Microsoft\Outlook\", StringComparison.OrdinalIgnoreCase) => "Microsoft Office / Outlook",
            _ when path.EndsWith(@"\CryptnetUrlCache", StringComparison.OrdinalIgnoreCase) => "CryptnetUrlCache",
            _ => FileCleanupAction.DescribeTarget(target),
        };
    }

    internal static FileCleanupAction CreateUserTemp(ICleanupPreferences? preferences = null) => new(
        id: "cleanup-user-temp",
        label: "ユーザーの一時ファイルとキャッシュを削除",
        description:
            "サインイン中のユーザー用の一時フォルダ (%LOCALAPPDATA%\\Temp)、クラッシュダンプ、シェーダーキャッシュ、Internet Explorer / WebView 系のキャッシュ、" +
            "IME・Office・Outlook の再生成可能なキャッシュをまとめて削除します。閲覧履歴や最近使ったファイル、アプリ設定は削除しません。数 GB 単位の空き容量になることがあります。" +
            "アプリが使用中のファイルは自動的にスキップされるため、作業中でも安全に実行できます。",
        targetProvider: () => UserTempTargets,
        preferences: preferences,
        checkListKeySelector: GetUserTempGroupName);

    // ═══ システムの一時ファイル・ログ ═══

    internal static readonly CleanupTarget[] SystemTempTargets =
    [
        CleanupTarget.Contents(@"%SystemRoot%\Temp"),
        CleanupTarget.Contents(@"%SystemRoot%\SystemTemp"),
        CleanupTarget.Contents(@"%ProgramData%\USOShared\Logs"),
        CleanupTarget.Contents(@"%ProgramData%\Microsoft\EdgeUpdate\Log"),
        CleanupTarget.Contents(@"%SystemRoot%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"),
        CleanupTarget.Contents(@"%ProgramData%\Microsoft\Windows Defender\Support"),
    ];

    /// <summary>
    /// Windows が ETL トレースログを置く既知のログ基点。ドライブ全体は走査せず、
    /// この配下にある *.etl だけを実行時に列挙する。
    /// </summary>
    internal static readonly string[] SystemEtlRoots =
    [
        @"%SystemRoot%\Logs",
        @"%SystemRoot%\System32\LogFiles",
        @"%SystemRoot%\Panther",
        @"%ProgramData%\Microsoft\Diagnosis\ETLLogs",
    ];

    internal static IEnumerable<CleanupTarget> EnumerateSystemTempTargets()
    {
        foreach (var target in SystemTempTargets)
        {
            yield return target;
        }

        foreach (var root in SystemEtlRoots)
        {
            foreach (var target in EnumerateKnownTreeEtlTargets(root))
            {
                yield return target;
            }
        }
    }

    /// <summary>
    /// 許可済みの基点配下をリンク非追従で列挙し、各フォルダ直下のパターン一致だけを
    /// FileCleanupEngine に渡す。汎用の再帰削除機能を復活させないため、対象作成側で
    /// フォルダを固定して展開する。
    /// </summary>
    internal static IReadOnlyList<CleanupTarget> EnumerateKnownTreeEtlTargets(
        string rawRoot,
        int maxDepth = 32)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawRoot);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);

        const string etlPattern = "*.etl";
        var targets = new List<CleanupTarget> { CleanupTarget.Files(rawRoot, etlPattern) };
        if (!FileCleanupEngine.TryResolve(rawRoot, out var fullRoot, out _) || !Directory.Exists(fullRoot))
        {
            return targets;
        }

        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((new DirectoryInfo(fullRoot), 0));

        while (pending.TryPop(out var entry))
        {
            if (entry.Depth >= maxDepth)
            {
                continue;
            }

            DirectoryInfo[] children;
            try
            {
                children = entry.Directory.GetDirectories();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var child in children)
            {
                try
                {
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                targets.Add(CleanupTarget.Files(child.FullName, etlPattern));
                pending.Push((child, entry.Depth + 1));
            }
        }

        return targets;
    }

    internal static string GetSystemTempGroupName(CleanupTarget target)
    {
        var path = target.RawPath;
        return path switch
        {
            _ when string.Equals(target.Pattern, "*.etl", StringComparison.OrdinalIgnoreCase) => "ETL Trace Logs",
            @"%SystemRoot%\Temp" or @"%SystemRoot%\SystemTemp" => "Windows Temp",
            @"%ProgramData%\USOShared\Logs" => "Windows Update Logs",
            @"%ProgramData%\Microsoft\EdgeUpdate\Log" => "Microsoft Edge Update",
            _ when path.Contains(@"\DeliveryOptimization\Cache", StringComparison.OrdinalIgnoreCase) => "Delivery Optimization",
            _ when path.Contains(@"\Microsoft\Windows Defender\", StringComparison.OrdinalIgnoreCase) => "Microsoft Defender",
            _ => FileCleanupAction.DescribeTarget(target),
        };
    }

    /// <summary>配信最適化キャッシュはサービスが握っているため停止してから消す。</summary>
    internal static readonly string[] SystemTempServices = ["DoSvc"];

    internal static FileCleanupAction CreateSystemTemp(ICommandExecutor executor, ICleanupPreferences? preferences = null) => new(
        id: "cleanup-system-temp",
        label: "システムの一時ファイルとログを削除",
        description:
            "Windows 本体の一時フォルダ、既知の Windows ログ領域にある ETL トレースログ、セットアップ・サービスの各種ログ、配信最適化 (他 PC への更新配布) のキャッシュ、" +
            "Windows Defender の診断ログを削除します。検出・対処履歴は削除しません。実行中は配信最適化サービスを一時停止し、完了後に元の状態へ戻します。" +
            "ETL はドライブ全体を検索せず、Windows Logs・LogFiles・Panther・Diagnosis の配下にある *.etl だけを対象にします。過去のトラブル調査に使うログも消えるため、不具合を調査中の場合は実行を控えてください。",
        targetProvider: EnumerateSystemTempTargets,
        executor: executor,
        servicesToStop: SystemTempServices,
        preferences: preferences,
        checkListKeySelector: GetSystemTempGroupName);

    // ═══ アプリのキャッシュ ═══

    internal static readonly CleanupTarget[] AppCacheTargets =
    [
        CleanupTarget.Contents(@"%APPDATA%\Antigravity\Cache"),
        CleanupTarget.Contents(@"%APPDATA%\Antigravity\CachedData"),
        CleanupTarget.Contents(@"%APPDATA%\Antigravity\Crashpad"),
        CleanupTarget.Contents(@"%APPDATA%\Aqua Voice\Cache"),
        CleanupTarget.Contents(@"%APPDATA%\Claude\Cache"),
        CleanupTarget.Contents(@"%APPDATA%\Claude\Code Cache"),
        CleanupTarget.Contents(@"%APPDATA%\Claude\logs"),
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
        CleanupTarget.Contents(@"%ProgramData%\LGHUB\cache"),
        CleanupTarget.Contents(@"%USERPROFILE%\AppData\LocalLow\NVIDIA\PerDriverVersion\DXCache"),
        CleanupTarget.Contents(@"%USERPROFILE%\.claude\debug"),
        CleanupTarget.Contents(@"%USERPROFILE%\.claude\telemetry"),
        CleanupTarget.Contents(@"%USERPROFILE%\.claude-mem\logs"),
    ];

    /// <summary>
    /// アプリキャッシュの各パスを、画面で選択するアプリ名へ畳む。
    /// 保存キーもこの名前になるため、パスが増えても同じアプリの選択状態を引き継げる。
    /// </summary>
    internal static string GetAppCacheGroupName(CleanupTarget target)
    {
        var path = target.RawPath;
        return path switch
        {
            _ when path.StartsWith(@"%APPDATA%\Antigravity\", StringComparison.OrdinalIgnoreCase) => "Antigravity",
            _ when path.StartsWith(@"%APPDATA%\Aqua Voice\", StringComparison.OrdinalIgnoreCase) => "Aqua Voice",
            _ when path.StartsWith(@"%APPDATA%\Claude\", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(@"%USERPROFILE%\.claude\", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(@"%USERPROFILE%\.claude-mem\", StringComparison.OrdinalIgnoreCase) => "Claude",
            _ when path.StartsWith(@"%APPDATA%\Cursor\", StringComparison.OrdinalIgnoreCase) => "Cursor",
            _ when path.StartsWith(@"%APPDATA%\discord\", StringComparison.OrdinalIgnoreCase) => "Discord",
            _ when path.StartsWith(@"%LOCALAPPDATA%\AMD\", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(@"%LOCALAPPDATA%\Ati\", StringComparison.OrdinalIgnoreCase) => "AMD",
            _ when path.StartsWith(@"%LOCALAPPDATA%\NVIDIA\", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(@"%USERPROFILE%\AppData\LocalLow\NVIDIA\", StringComparison.OrdinalIgnoreCase) => "NVIDIA",
            _ when path.StartsWith(@"%ProgramData%\LGHUB\", StringComparison.OrdinalIgnoreCase) => "Logitech G HUB",
            _ => FileCleanupAction.DescribeTarget(target),
        };
    }

    internal static FileCleanupAction CreateAppCache(ICleanupPreferences? preferences = null) => new(
        id: "cleanup-app-cache",
        label: "アプリのキャッシュとログを削除",
        description:
            "Electron 系アプリ (Claude / Cursor / Discord / Antigravity 等) のキャッシュ、GPU のシェーダーキャッシュ (NVIDIA DXCache / AMD)、" +
            "クラッシュレポートや常駐ツールのログを削除します。WebStorage、仮想マシン本体、アプリ設定やログイン状態は削除しません。各アプリが必要なキャッシュを次の起動で作り直します。" +
            "対象のアプリが入っていない環境では、その分だけスキップされます。",
        targetProvider: () => AppCacheTargets,
        preferences: preferences,
        checkListKeySelector: GetAppCacheGroupName);

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
        CleanupTarget.Contents(@"%LOCALAPPDATA%\NuGet\v3-cache"),
        CleanupTarget.Contents(@"%USERPROFILE%\.npm\_cacache"),
        CleanupTarget.Contents(@"%USERPROFILE%\.npm\_logs"),
        CleanupTarget.Contents(@"%USERPROFILE%\.npm\_npx"),
        CleanupTarget.Contents(@"%USERPROFILE%\.nuget\packages"),
        CleanupTarget.Contents(@"%USERPROFILE%\.gradle\caches"),
        CleanupTarget.Contents(@"%USERPROFILE%\.bun\install\cache"),
        .. StoreCacheTargets,
    ];

    internal static string GetPackageCacheGroupName(CleanupTarget target)
    {
        var path = target.RawPath;
        return path switch
        {
            _ when path.Contains(@"\pip\", StringComparison.OrdinalIgnoreCase) => "pip",
            _ when path.Contains(@"\Yarn\", StringComparison.OrdinalIgnoreCase) => "Yarn",
            _ when path.Contains(@"\NuGet\", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains(@"\.nuget\", StringComparison.OrdinalIgnoreCase) => "NuGet",
            _ when path.Contains(@"\uv\", StringComparison.OrdinalIgnoreCase) => "uv",
            _ when path.EndsWith(@"\npm-cache", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains(@"\.npm\", StringComparison.OrdinalIgnoreCase) => "npm",
            _ when path.Contains(@"\pnpm\", StringComparison.OrdinalIgnoreCase) => "pnpm",
            _ when path.Contains(@"\.cargo\", StringComparison.OrdinalIgnoreCase) => "Cargo",
            _ when path.Contains(@"\.gradle\", StringComparison.OrdinalIgnoreCase) => "Gradle",
            _ when path.Contains(@"\.bun\", StringComparison.OrdinalIgnoreCase) => "Bun",
            _ => FileCleanupAction.DescribeTarget(target),
        };
    }

    internal static FileCleanupAction CreatePackageCache(ICleanupPreferences? preferences = null) => new(
        id: "cleanup-package-cache",
        label: "開発ツールのパッケージキャッシュを削除",
        description:
            "npm / pnpm / NuGet / pip / Yarn / Gradle / Bun / uv / Cargo がダウンロード済みパッケージを溜め込んでいる、再取得可能なキャッシュだけを削除します。数十 GB 単位で空くことがあります。" +
            "uv 管理 Python、Maven のローカル成果物、認証情報、設定ファイル、汎用の .cache フォルダは対象に含みません。" +
            "削除後、各プロジェクトの次回ビルドや復元でパッケージが再ダウンロードされるため、その 1 回だけ時間と通信量がかかります。",
        targetProvider: () => PackageCacheTargets,
        preferences: preferences,
        checkListKeySelector: GetPackageCacheGroupName);

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
    internal static readonly string[] BrowserSharedCaches =
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
    ];

    /// <summary>
    /// プロファイル (Default / Profile 1 …) ごとのキャッシュ。File System / IndexedDB / WebStorage /
    /// Extension State は Cookie 非依存 (localStorage 等) でログイン状態を保持するサイト・拡張機能の
    /// 永続データであり、「キャッシュ」ではなく実質サイトデータのため、消えないと説明している
    /// ログイン状態を壊さないよう対象から除外する。
    /// </summary>
    internal static readonly string[] BrowserProfileCaches =
    [
        "Cache",
        "Code Cache",
        @"Service Worker\CacheStorage",
        @"Service Worker\ScriptCache",
        "GPUCache",
        "JumpListIconsRecentClosed",
        "JumpListIconsTopSites",
        "DawnGraphiteCache",
        "DawnWebGPUCache",
        "Shared Dictionary",
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

    internal static string GetBrowserDisplayName(CleanupTarget target) =>
        DescribeBrowserTarget(target) switch
        {
            @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser" => "Brave",
            @"%LOCALAPPDATA%\Microsoft\Edge" => "Microsoft Edge",
            @"%LOCALAPPDATA%\Google\Chrome" => "Google Chrome",
            @"%LOCALAPPDATA%\Google\Chrome Beta" => "Google Chrome Beta",
            @"%LOCALAPPDATA%\Google\Chrome Dev" => "Google Chrome Dev",
            @"%LOCALAPPDATA%\Vivaldi" => "Vivaldi",
            @"%LOCALAPPDATA%\Perplexity\Comet" => "Comet",
            var fallback => fallback,
        };

    internal static FileCleanupAction CreateBrowserCache(ICleanupPreferences? preferences = null) => new(
        id: "cleanup-browser-cache",
        label: "ブラウザのキャッシュを削除",
        description:
            "インストール済みの Chromium 系ブラウザ (Edge / Chrome / Brave / Vivaldi / Comet) を検出し、全プロファイルの Web キャッシュ、" +
            "GPU シェーダーキャッシュ、Service Worker のキャッシュ領域、クラッシュレポート、拡張機能の一時データを削除します。数 GB 単位で空くことが多い項目です。" +
            "Service Worker の登録 DB、DRM コンポーネント、ブックマーク・パスワード・ログイン状態は削除しません。実行前にブラウザを終了しておくと、より確実に削除できます。",
        targetProvider: () => EnumerateBrowserTargets(),
        preferences: preferences,
        checkListKeySelector: DescribeBrowserTarget,
        checkListLabelSelector: GetBrowserDisplayName);

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
        preferences: preferences,
        checkListKeySelector: GetShellCacheGroupName);

    // ═══ Windows Update のキャッシュ ═══

    internal static readonly CleanupTarget[] WindowsUpdateCacheTargets =
    [
        CleanupTarget.Contents(@"%SystemRoot%\SoftwareDistribution\Download"),
    ];

    internal static string GetWindowsUpdateCacheGroupName(CleanupTarget target) =>
        target.RawPath switch
        {
            @"%SystemRoot%\SoftwareDistribution\Download" => "Update Downloads",
            _ => FileCleanupAction.DescribeTarget(target),
        };

    internal static readonly string[] WindowsUpdateCacheServices = ["wuauserv", "bits"];

    internal static FileCleanupAction CreateWindowsUpdateCache(ICommandExecutor executor, ICleanupPreferences? preferences = null) => new(
        id: "cleanup-windows-update-cache",
        label: "Windows Update のダウンロードキャッシュを削除",
        description:
            "Windows Update が再取得できるダウンロード済みファイル (SoftwareDistribution\\Download) だけを削除します。数 GB の空き容量になることがあります。" +
            "更新履歴のデータストアと署名カタログ (catroot2) は削除しません。関連サービスを一時停止してから削除し、完了後に元の状態へ戻します。",
        targetProvider: () => WindowsUpdateCacheTargets,
        executor: executor,
        servicesToStop: WindowsUpdateCacheServices,
        preferences: preferences,
        checkListKeySelector: GetWindowsUpdateCacheGroupName);

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
