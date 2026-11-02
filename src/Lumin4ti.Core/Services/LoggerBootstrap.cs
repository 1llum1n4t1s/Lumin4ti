using Microsoft.Extensions.Logging;
using SuperLightLogger;

namespace Lumin4ti.Core.Services;

/// <summary>
/// SuperLightLogger の初期化・終了処理をまとめたエントリポイント。
/// ログ出力先は AppPaths.LogsDirectory。
/// </summary>
public static class LoggerBootstrap
{
    private static ILog? _log;

    public static ILog Log => _log ??= LogManager.GetLogger(typeof(LoggerBootstrap));

    private static bool _initialized;
    private static ILoggerFactory? _factory;

    /// <summary>
    /// ログ構成自体に失敗した理由 (成功時は null)。ファイルへ残せない状況を
    /// 利用者へ伝えるため、UI から参照できるようにしておく。
    /// </summary>
    public static string? InitializationFailure { get; private set; }

    /// <summary>
    /// ログ出力を構成する。起動経路が複数あるため冪等にし、最初の 1 回だけ実際に構成する
    /// (Velopack フックや PerMachine 移行は Avalonia 起動より前に走るため、そこからも呼べるようにする)。
    /// ログ基盤の失敗でアプリ起動そのものを止めないよう、例外は握ってここへ記録する。
    /// </summary>
    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                LegacyLogMigration.Migrate(Path.Combine(AppPaths.AppDataDirectory, "logs"), AppPaths.LogsDirectory);
                var oldLocalLogs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kagayoi", "Lumin4ti", "logs");
                LegacyLogMigration.Migrate(oldLocalLogs, AppPaths.LogsDirectory);
                LegacyLogMigration.RemoveEmptyParent(oldLocalLogs);
            }
            Directory.CreateDirectory(AppPaths.LogsDirectory);

            _factory = LoggerFactory.Create(builder =>
            {
                builder.AddSuperLightFile(Path.Combine(AppPaths.LogsDirectory, "Lumin4ti_${shortdate}.log"));
                builder.SetMinimumLevel(LogLevel.Information);
            });
            LogManager.Configure(_factory);
        }
        catch (Exception ex)
        {
            InitializationFailure = $"{ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine(
                $"[Lumin4ti] ログ出力を構成できませんでした ({AppPaths.LogsDirectory}): {InitializationFailure}");
            // ファイルを作れなくても、UI 起動後にメモリ内のデバッグ画面へ接続できるようにする。
            _factory ??= LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information));
            LogManager.Configure(_factory);
        }
    }

    /// <summary>
    /// 実行環境を 1 行残す。日付単位のログファイルは自動更新でバージョンをまたぐため、
    /// どの版・どの OS・どの権限で出た行かを後から特定できるようにする。
    /// </summary>
    public static void LogEnvironment()
    {
        var version = typeof(LoggerBootstrap).Assembly.GetName().Version?.ToString() ?? "不明";
        var elevated = OperatingSystem.IsWindows() && IsCurrentProcessElevated() ? "管理者" : "非管理者";
        Log.Info($"起動: Lumin4ti {version} / {Environment.OSVersion.VersionString} / {elevated} / PID {Environment.ProcessId}");

        // HKCU を扱う項目は「このプロセスのユーザー = 画面を操作している人」を前提にしている。
        // 別の管理者資格で昇格された場合は前提が崩れて設定が別ハイブへ行くため、記録して気付けるようにする。
        if (OperatingSystem.IsWindows() && GetInteractiveUserMismatch() is { } mismatch)
        {
            Log.Error($"起動: 実行ユーザーと対話ユーザーが異なります ({mismatch})。ユーザー単位の設定は実行ユーザー側へ適用されます");
        }
    }

    /// <summary>実行ユーザーと対話ユーザー (explorer.exe) が異なる場合だけ、その内訳を返す。</summary>
    private static string? GetInteractiveUserMismatch()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var interactiveUser = InteractiveUserResolver.TryGetInteractiveUserName();
            if (interactiveUser is null || identity.Name.Equals(interactiveUser, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return $"実行={identity.Name} / 対話={interactiveUser}";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>既存のファイル出力を維持して、切離し可能な追加出力先を登録する。</summary>
    public static IDisposable AttachProvider(ILoggerProvider provider) => AttachProvider(_factory, provider);

    internal static IDisposable AttachProvider(ILoggerFactory? factory, ILoggerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var forwarding = new DetachableProvider(provider);
        try
        {
            (factory ?? throw new InvalidOperationException("ログ出力が初期化されていません。")).AddProvider(forwarding);
            return forwarding;
        }
        catch
        {
            forwarding.Dispose();
            throw;
        }
    }

    private sealed class DetachableProvider(ILoggerProvider provider) : ILoggerProvider
    {
        private readonly object _gate = new();
        private ILoggerProvider? _provider = provider;

        public ILogger CreateLogger(string categoryName)
        {
            lock (_gate)
            {
                return new ForwardingLogger(this, _provider?.CreateLogger(categoryName));
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                var current = _provider;
                _provider = null;
                current?.Dispose();
            }
        }

        private sealed class ForwardingLogger(DetachableProvider owner, ILogger? logger) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel)
            {
                lock (owner._gate)
                    return owner._provider is not null && logger?.IsEnabled(logLevel) == true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner._gate)
                {
                    if (owner._provider is null) return;
                    try
                    {
                        logger?.Log(logLevel, eventId, state, exception, formatter);
                    }
                    catch (Exception ex)
                    {
                        // 追加出力先の障害で、本来の操作やファイルへのログ記録を失敗させない。
                        Console.Error.WriteLine($"[Lumin4ti] 追加ログ出力に失敗しました: {ex.GetType().Name}");
                    }
                }
            }
        }
    }

    public static void Shutdown()
    {
        _factory = null;
        LogManager.Shutdown();
    }
}
