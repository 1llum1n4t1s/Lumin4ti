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

    /// <summary>アプリ起動プリフェッチを表すビット。</summary>
    private const int ApplicationLaunchPrefetchingMask = 1;

    /// <summary>EnablePrefetcher で定義されているアプリ起動・ブートの全ビット。</summary>
    private const int KnownPrefetcherMask = 3;

    /// <summary>アプリ + ブートのプリフェッチを行う Windows 既定値。</summary>
    private const int PrefetcherEnabledDefault = 3;

    /// <summary>この機能名にレジストリ経由の代替手段があるか。</summary>
    public static bool CanFallBack(string propertyName) =>
        string.Equals(propertyName, "ApplicationLaunchPrefetching", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 機能を切り替える。成功したら null、失敗したら利用者向けの理由を返す。
    /// 現在値のアプリ起動ビットだけを変更し、ブートプリフェッチの状態は維持する。
    /// </summary>
    public static string? TrySetState(string propertyName, bool on) =>
        TrySetState(propertyName, on, ReadEnablePrefetcher, WriteEnablePrefetcher);

    internal static string? TrySetState(
        string propertyName, bool on, Func<int?> readCurrentValue, Func<int, string?> writeValue)
    {
        if (!CanFallBack(propertyName))
        {
            return "この機能にはレジストリ経由の代替手段がありません";
        }

        try
        {
            // 旧版の registry/mmagent-launch-prefetch.json は DWORD 全体を復元してブート側の
            // 外部変更を巻き戻すため、通常実行経路から外す。既存ファイルは削除せず保持する。
            return WriteApplicationLaunchState(on, readCurrentValue, writeValue);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidDataException)
        {
            LoggerBootstrap.Log.Error("mmagent fallback: EnablePrefetcher の書き込みに失敗", ex);
            return $"EnablePrefetcher を変更できませんでした ({ex.Message})";
        }
    }

    private static string? WriteApplicationLaunchState(
        bool on, Func<int?> readCurrentValue, Func<int, string?> writeValue)
    {
        var currentValue = readCurrentValue() ?? PrefetcherEnabledDefault;
        if (currentValue is < PrefetcherDisabled or > KnownPrefetcherMask)
        {
            return $"EnablePrefetcher に未対応の値 ({currentValue}) が設定されているため変更しませんでした";
        }

        var nextValue = on
            ? currentValue | ApplicationLaunchPrefetchingMask
            : currentValue & ~ApplicationLaunchPrefetchingMask;
        return writeValue(nextValue);
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
                PrefetcherDisabled => false,
                ApplicationLaunchPrefetchingMask => true,
                2 => false,
                KnownPrefetcherMask => true,
                null => true,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // null は UI 上「状態不明」になるだけなので、理由はログにしか残せない。
            LoggerBootstrap.Log.Error($"{PrefetchKeyPath}\\EnablePrefetcher を読み取れませんでした", ex);
            return null;
        }
    }

    /// <summary>アプリ起動ビットだけを変更するための現在値。未設定なら null。</summary>
    private static int? ReadEnablePrefetcher()
    {
        using var key = Registry.LocalMachine.OpenSubKey(PrefetchKeyPath);
        return key?.GetValue("EnablePrefetcher") switch
        {
            int value => value,
            null => null,
            _ => throw new InvalidDataException("EnablePrefetcher が DWORD ではありません"),
        };
    }
}
