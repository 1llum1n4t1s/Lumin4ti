namespace Lumin4ti.Core.Services;

/// <summary>設定・ログと、特権復元データの保存先を解決する。</summary>
public static class AppPaths
{
    private const string AppFolderName = "Lumin4ti";

    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    public static string SettingsFilePath => Path.Combine(AppDataDirectory, "settings.json");

    public static string LogsDirectory => Path.Combine(AppDataDirectory, "logs");

    /// <summary>
    /// HKLM / BCD へ復元する値の正本。ACL の作成・検証は
    /// <see cref="ProtectedBackupStorage"/> が担当する。
    /// </summary>
    public static string ProtectedBackupsDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AppFolderName,
            "backups");
}
