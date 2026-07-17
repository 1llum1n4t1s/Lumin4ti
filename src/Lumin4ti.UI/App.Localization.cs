using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;

namespace Lumin4ti.UI;

/// <summary>
/// アプリのローカライズ (Komorebi / Lhamiel と同一方式)。
/// 翻訳は Resources/Locales/*.axaml (ResourceDictionary) が持ち、XAML からは {DynamicResource Text.Xxx}、
/// C# からは <see cref="Text"/> で引く。<see cref="SetLocale"/> が MergedDictionaries を差し替えて実行時切替する。
/// DynamicResource は切替で自動更新されるが、C# の <see cref="Text"/> を使うプロパティは
/// <see cref="LocaleChanged"/> を購読して再評価する必要がある。
/// </summary>
public partial class App
{
    /// <summary>選択可能なロケール (表示名, キー)。Komorebi と同一セット。</summary>
    public static IReadOnlyList<LocaleOption> SupportedLocales { get; } =
    [
        new("Deutsch", "de_DE"),
        new("English", "en_US"),
        new("Español", "es_ES"),
        new("Filipino (Tagalog)", "fil_PH"),
        new("Français", "fr_FR"),
        new("Bahasa Indonesia", "id_ID"),
        new("Italiano", "it_IT"),
        new("日本語", "ja_JP"),
        new("한국어", "ko_KR"),
        new("Latina (Latin)", "la"),
        new("Português (Brasil)", "pt_BR"),
        new("Русский", "ru_RU"),
        new("संस्कृतम् (Sanskrit)", "sa"),
        new("தமிழ் (Tamil)", "ta_IN"),
        new("Українська", "uk_UA"),
        new("简体中文", "zh_CN"),
        new("繁體中文", "zh_TW"),
    ];

    private Avalonia.Controls.ResourceDictionary? _activeLocale;

    /// <summary>現在適用中のロケールキー。</summary>
    public static string CurrentLocaleKey { get; private set; } = "ja_JP";

    /// <summary>言語切替時に発火。App.Text を使う ViewModel プロパティの再評価に使う。</summary>
    public static event Action? LocaleChanged;

    /// <summary>
    /// 表示言語を切り替える。App.axaml に登録されたロケール辞書をキーで検索し、現在のロケールと置き換える。
    /// </summary>
    public static void SetLocale(string localeKey)
    {
        localeKey = ResolveLocaleKey(localeKey);
        if (Current is not App app ||
            app.Resources[localeKey] is not Avalonia.Controls.ResourceDictionary targetLocale)
        {
            return;
        }

        if (ReferenceEquals(targetLocale, app._activeLocale))
        {
            return;
        }

        if (app._activeLocale is not null)
        {
            app.Resources.MergedDictionaries.Remove(app._activeLocale);
        }

        app.Resources.MergedDictionaries.Add(targetLocale);
        app._activeLocale = targetLocale;
        CurrentLocaleKey = localeKey;
        LocaleChanged?.Invoke();
    }

    /// <summary>保存値が未対応・廃止済みなら OS 既定、最後に英語へ正規化する。</summary>
    internal static string ResolveLocaleKey(string? requestedLocale)
    {
        if (!string.IsNullOrWhiteSpace(requestedLocale) &&
            SupportedLocales.Any(locale => locale.Key.Equals(requestedLocale, StringComparison.Ordinal)))
        {
            return requestedLocale;
        }

        var detected = DetectDefaultLocale();
        return SupportedLocales.Any(locale => locale.Key.Equals(detected, StringComparison.Ordinal))
            ? detected
            : "en_US";
    }

    /// <summary>
    /// ローカライズ済み文字列を返す。現在ロケール辞書に "Text.{key}" があればそれを、
    /// なければ <paramref name="fallback"/> (通常は日本語のコード内マスター) を使い、args で string.Format する。
    /// </summary>
    public static string Text(string key, string fallback, params object[] args)
    {
        var fmt = Current?.FindResource($"Text.{key}") as string;
        if (string.IsNullOrWhiteSpace(fmt))
        {
            fmt = fallback;
        }

        if (args is null || args.Length == 0)
        {
            return fmt;
        }

        try
        {
            return string.Format(fmt, args);
        }
        catch (FormatException)
        {
            return fmt;
        }
    }

    /// <summary>OS の UI カルチャから既定ロケールを推定する (Komorebi と同ロジック)。</summary>
    public static string DetectDefaultLocale()
    {
        var supported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var locale in SupportedLocales)
        {
            supported.Add(locale.Key);
        }

        var culture = CultureInfo.CurrentUICulture;

        var exact = culture.Name.Replace('-', '_');
        if (supported.Contains(exact))
        {
            return exact;
        }

        var lang = culture.TwoLetterISOLanguageName;
        if (lang == "zh")
        {
            var name = culture.Name;
            if (name.Contains("Hant") || name.Contains("TW") || name.Contains("HK") || name.Contains("MO"))
            {
                return "zh_TW";
            }

            return "zh_CN";
        }

        foreach (var locale in supported)
        {
            if (locale.StartsWith(lang + "_", StringComparison.Ordinal) || locale == lang)
            {
                return locale;
            }
        }

        return "en_US";
    }
}

/// <summary>ロケール選択肢 (表示名とキー)。</summary>
public sealed record LocaleOption(string Name, string Key);
