namespace Lumin4ti.Core.Services;

/// <summary>
/// 設定・ログの保存先を解決する (Windows 専用: %APPDATA%\Lumin4ti)。
/// </summary>
public static class AppPaths
{
    private const string AppFolderName = "Lumin4ti";

    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    public static string SettingsFilePath => Path.Combine(AppDataDirectory, "settings.json");

    public static string LogsDirectory => Path.Combine(AppDataDirectory, "logs");
}
