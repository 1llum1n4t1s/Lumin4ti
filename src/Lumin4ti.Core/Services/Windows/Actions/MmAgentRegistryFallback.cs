using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// Enable/Disable-MMAgent が ERROR_NOT_SUPPORTED (0x80070032) を返す機能を、
/// 同じ設定を保持しているレジストリ値から切り替えるためのフォールバック。
///
/// 「アプリ起動プリフェッチ」の実体は PrefetchParameters\EnablePrefetcher で、
/// cmdlet が非対応を返す Windows でもこの値からは切り替えられる。
/// cmdlet で切り替えられる限りは cmdlet を使い、ここは最後の手段として使う。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class MmAgentRegistryFallback
{
    private const string PrefetchKeyPath =
        @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters";

    /// <summary>プリフェッチ無効。</summary>
    private const int PrefetcherDisabled = 0;

    /// <summary>アプリ + ブートのプリフェッチを行う Windows 既定値。</summary>
    private const int PrefetcherEnabledDefault = 3;

    /// <summary>
    /// 元値の退避先。RegistryToggle と同じ仕組み (%ProgramData% の保護ストレージ) に載せて、
    /// アプリを再起動しても「OFF の前はどの値だったか」を失わないようにする。
    /// </summary>
    private const string BackupId = "mmagent-launch-prefetch";

    private static IReadOnlyList<RegistryToggleSpec> PrefetchSpecs { get; } =
    [
        new(
            RegistryHive.LocalMachine,
            PrefetchKeyPath,
            "EnablePrefetcher",
            RegistryValueKind.DWord,
            PrefetcherDisabled,
            PrefetcherEnabledDefault),
    ];

    /// <summary>この機能名にレジストリ経由の代替手段があるか。</summary>
    public static bool CanFallBack(string propertyName) =>
        string.Equals(propertyName, "ApplicationLaunchPrefetching", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 機能を切り替える。成功したら null、失敗したら利用者向けの理由を返す。
    /// ON では無効化前の値へ戻す (記録が無ければ Windows 既定の 3)。
    /// </summary>
    public static string? TrySetState(string propertyName, bool on, Func<int?>? readPreviousValue = null)
    {
        if (!CanFallBack(propertyName))
        {
            return "この機能にはレジストリ経由の代替手段がありません";
        }

        try
        {
            if (on)
            {
                // 無効化前の値を退避してあればそこへ戻す。無ければ Windows 既定へ。
                var lines = new List<string>();
                var restore = RegistryValueBackup.Default.TryRestore(BackupId, PrefetchSpecs, lines);
                if (restore.Status == RegistryBackupRestoreStatus.Restored)
                {
                    LoggerBootstrap.Log.Info("mmagent fallback: EnablePrefetcher を無効化前の値へ復元");
                    return null;
                }

                return WriteEnablePrefetcher(readPreviousValue?.Invoke() ?? PrefetcherEnabledDefault);
            }

            // 「OFF で Windows 既定ではなく利用者の元の値へ戻す」ため、書く前に退避する。
            RegistryValueBackup.Default.Save(BackupId, PrefetchSpecs);
            return WriteEnablePrefetcher(PrefetcherDisabled);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidDataException)
        {
            LoggerBootstrap.Log.Error("mmagent fallback: EnablePrefetcher の書き込みに失敗", ex);
            return $"EnablePrefetcher を変更できませんでした ({ex.Message})";
        }
    }

    private static string? WriteEnablePrefetcher(int value)
    {
        using var key = Registry.LocalMachine.OpenSubKey(PrefetchKeyPath, writable: true);
        if (key is null)
        {
            return $"レジストリキー {PrefetchKeyPath} を開けませんでした";
        }

        key.SetValue("EnablePrefetcher", value, RegistryValueKind.DWord);
        LoggerBootstrap.Log.Info($"mmagent fallback: EnablePrefetcher = {value}");
        return null;
    }

    /// <summary>現在のレジストリ値から状態を読む。読めない場合は null。</summary>
    public static bool? TryReadState(string propertyName)
    {
        if (!CanFallBack(propertyName))
        {
            return null;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PrefetchKeyPath);
            // 値が無い場合は Windows 既定 (有効) 扱い。
            return key?.GetValue("EnablePrefetcher") switch
            {
                int value => value != PrefetcherDisabled,
                null => true,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    /// <summary>無効化前の値を控えておくための現在値 (DWORD 以外・欠損は null)。</summary>
    public static int? TryReadRawValue()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PrefetchKeyPath);
            return key?.GetValue("EnablePrefetcher") as int?;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }
}
