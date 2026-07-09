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

    public static void Initialize()
    {
        Directory.CreateDirectory(AppPaths.LogsDirectory);

        LogManager.Configure(builder =>
        {
            builder.AddSuperLightFile(Path.Combine(AppPaths.LogsDirectory, "Lumin4ti_${shortdate}.log"));
            builder.SetMinimumLevel(LogLevel.Information);
        });
    }

    public static void Shutdown() => LogManager.Shutdown();
}
