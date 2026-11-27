using System;
using System.Globalization;
using System.Linq;
using Lumin4ti.Core.Services.Windows.Cleanup;

namespace Lumin4ti.UI.ViewModels;

/// <summary>
/// クリーンアップ画面のコード側の文言解決。辞書 (Resources/Locales) に訳があればそれを、無ければ
/// <c>CleanupText.Japanese.cs</c> の日本語マスターを使う (conventions.md のローカライズ規約)。
/// キーは <c>Text.</c> を除いた形で渡す。XAML から引く文言は {DynamicResource Text.…} を使い、ここでは引かない。
/// </summary>
internal static partial class CleanupText
{
    /// <summary>キーの文言を返す。<paramref name="args"/> があれば string.Format する。</summary>
    public static string Get(string key, params object[] args) => App.Text(key, JapaneseOf(key), args);

    /// <summary>
    /// Core が返す辞書キー (<c>Text.Cleanup.Rule.…</c>・<c>Text.Cleanup.Slim.…</c>・<c>Text.Cleanup.Warning.…</c>) の文言を返す。
    /// </summary>
    public static string GetByCoreKey(string coreKey, params object[] args)
    {
        ArgumentException.ThrowIfNullOrEmpty(coreKey);
        return Get(coreKey.StartsWith("Text.", StringComparison.Ordinal) ? coreKey[5..] : coreKey, args);
    }

    /// <summary>
    /// Core が返した理由・結果の文言を現在の言語にする。<see cref="CleanupMessage"/> の符号なら辞書から引き、
    /// 符号でない文字列 (従来の日本語の診断文など) はそのまま返す。引数の入れ子の符号も引く。
    /// </summary>
    public static string Detail(string? text) => CleanupMessage.Resolve(text, static (key, args) => Get(key, [.. args]));

    /// <summary>規則の表示名。nameKey があれば辞書の名称、無ければ種別の書式に製品名を入れる (製品名は翻訳しない)。</summary>
    public static string RuleName(CleanupRuleInfo rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.NameKey is { } nameKey
            ? GetByCoreKey(nameKey)
            : Get($"Cleanup.Kind.{rule.Kind}", rule.Name);
    }

    /// <summary>バイト数を「1.0 KB」の形に整える (単位は翻訳しない)。</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes:N0} B"
            : value.ToString("N1", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>日本語マスター。表に無いキーはキー名をそのまま返し、欠落を画面で見つけられるようにする。</summary>
    internal static string JapaneseOf(string key) =>
        JapaneseTable.TryGetValue(key, out var text) ? text : CleanupMessages.JapaneseOf(key) ?? key;

    /// <summary>日本語マスターに載っているキー (一貫性テスト用)。</summary>
    internal static System.Collections.Generic.IEnumerable<string> JapaneseKeys => JapaneseTable.Keys.Concat(CleanupMessages.Keys);
}
