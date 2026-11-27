using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Lumin4ti.Core.Services.Windows.Cleanup.Slim;

/// <summary>
/// ロード済みの利用者ハイブ (HKEY_USERS) と HKLM からシステム既定・UI 言語・言語一覧・キーボード配列を読む。
/// プロファイルがあるのにハイブがロードされていない利用者がいれば、読み切れないものとして報告する。
/// 読み取り専用で、レジストリを変更しない。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUserLanguageSource : IUserLanguageSource
{
    private const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public UserLanguageSnapshot Read()
    {
        var tags = new List<string>();
        var keyboards = new List<int>();
        try
        {
            ReadMachine(tags, keyboards);
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
            var loaded = new HashSet<string>(users.GetSubKeyNames(), StringComparer.OrdinalIgnoreCase);
            foreach (var name in loaded.Where(n => !n.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)))
            {
                using var hive = users.OpenSubKey(name);
                if (hive is not null)
                {
                    ReadUser(hive, tags, keyboards);
                }
            }

            var unloaded = FindUnloadedProfiles(loaded);
            return new UserLanguageSnapshot(
                tags,
                keyboards,
                unloaded.Count == 0 ? null : $"未ロードの利用者ハイブがあります ({unloaded.Count} 件)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new UserLanguageSnapshot(tags, keyboards, $"レジストリを読み取れませんでした: {ex.Message}");
        }
    }

    private static void ReadMachine(List<string> tags, List<int> keyboards)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using (var nls = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Nls\Language"))
        {
            foreach (var name in new[] { "InstallLanguage", "Default" })
            {
                if (nls?.GetValue(name) is string text &&
                    int.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var langId))
                {
                    keyboards.Add(langId);
                }
            }
        }

        using var mui = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MUI\UILanguages");
        if (mui is not null)
        {
            tags.AddRange(mui.GetSubKeyNames());
        }
    }

    private static void ReadUser(RegistryKey hive, List<string> tags, List<int> keyboards)
    {
        using (var profile = hive.OpenSubKey(@"Control Panel\International\User Profile"))
        {
            if (profile is not null)
            {
                tags.AddRange(profile.GetSubKeyNames());
                AddStrings(profile.GetValue("Languages"), tags);
            }
        }

        using (var desktop = hive.OpenSubKey(@"Control Panel\Desktop"))
        {
            AddStrings(desktop?.GetValue("PreferredUILanguages"), tags);
            AddStrings(desktop?.GetValue("PreviousPreferredUILanguages"), tags);
        }

        foreach (var keyName in new[] { @"Keyboard Layout\Preload", @"Keyboard Layout\Substitutes" })
        {
            using var key = hive.OpenSubKey(keyName);
            if (key is null)
            {
                continue;
            }

            foreach (var value in key.GetValueNames())
            {
                AddKeyboard(key.GetValue(value) as string, keyboards);
            }
        }
    }

    private static void AddKeyboard(string? layoutId, List<int> keyboards)
    {
        if (layoutId is { Length: > 0 } &&
            int.TryParse(layoutId, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id))
        {
            keyboards.Add(id & 0xFFFF);
        }
    }

    private static void AddStrings(object? value, List<string> tags)
    {
        switch (value)
        {
            case string[] many:
                tags.AddRange(many);
                break;
            case string one:
                tags.Add(one);
                break;
        }
    }

    /// <summary>プロファイル一覧に載る通常の利用者のうち、ハイブがロードされていないものの SID。</summary>
    private static List<string> FindUnloadedProfiles(HashSet<string> loaded)
    {
        var unloaded = new List<string>();
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var list = machine.OpenSubKey(ProfileListKey)
            ?? throw new IOException("プロファイル一覧を開けませんでした。");
        foreach (var sid in list.GetSubKeyNames())
        {
            var isUser = sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) ||
                         sid.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase);
            if (isUser && !loaded.Contains(sid))
            {
                unloaded.Add(sid);
            }
        }

        return unloaded;
    }
}
