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
            Directory.CreateDirectory(AppPaths.LogsDirectory);

            LogManager.Configure(builder =>
            {
                builder.AddSuperLightFile(Path.Combine(AppPaths.LogsDirectory, "Lumin4ti_${shortdate}.log"));
                builder.SetMinimumLevel(LogLevel.Information);
            });
        }
        catch (Exception ex)
        {
            // ここで失敗するとログ自体が残せないため、標準エラーだけが唯一の手がかりになる。
            InitializationFailure = $"{ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine(
                $"[Lumin4ti] ログ出力を構成できませんでした ({AppPaths.LogsDirectory}): {InitializationFailure}");
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

    public static void Shutdown() => LogManager.Shutdown();
}
