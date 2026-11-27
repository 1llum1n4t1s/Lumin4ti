using System.Globalization;
using System.Text;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>
/// クリーンアップが画面へ返す理由・結果の文言を、辞書キーと引数で運ぶ仕組み。
/// 文字列 (<c>Detail</c>・<c>Message</c> など) の型を変えずに、画面が翻訳して表示できる。
/// <see cref="Of"/> が返す文字列は「印 + キー + 引数」の符号で、画面は <see cref="TryParse"/> で読み取って辞書から引く。
/// ログ・テキスト書き出し・翻訳を持たない場所では <see cref="ToJapanese"/> で日本語に戻す (Core は日本語のマスターを持つ)。
/// 引数は技術的な値 (パス・終了コード・例外の文言など) で翻訳しない。ただし引数にも符号を入れ子にできる。
/// キーは <c>Cleanup.Msg.*</c> (辞書のキーは先頭に <c>Text.</c> が付く)。
/// </summary>
public static class CleanupMessage
{
    private const char Mark = '\u0001';
    private const char Separator = '\u001f';

    // 入れ子の符号を引数に入れても区切りと混ざらないよう、引数の中の印と区切りは別の制御文字へ置き換えて持つ。
    private const char EscapedMark = '\u0003';
    private const char EscapedSeparator = '\u0004';

    /// <summary>
    /// 文言の符号を作る。<paramref name="name"/> は <c>Cleanup.Msg.</c> を除いた名前 (<c>Cleanup.</c> で始まるときは完全なキー)。
    /// <paramref name="args"/> は文字列に変換して持つ (符号を入れ子にできる)。
    /// </summary>
    public static string Of(string name, params object?[] args)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var key = name.StartsWith("Cleanup.", StringComparison.Ordinal) ? name : "Cleanup.Msg." + name;
        var text = new StringBuilder().Append(Mark).Append(key);
        foreach (var arg in args)
        {
            text.Append(Separator).Append(Escape(Convert.ToString(arg, CultureInfo.InvariantCulture)));
        }

        return text.ToString();
    }

    /// <summary>符号ならキーと引数を取り出す。符号でない文字列 (従来の日本語の診断文など) は false。</summary>
    public static bool TryParse(string? text, out string key, out IReadOnlyList<string> args)
    {
        key = string.Empty;
        args = [];
        if (string.IsNullOrEmpty(text) || text[0] != Mark)
        {
            return false;
        }

        var parts = text[1..].Split(Separator);
        key = parts[0];
        args = parts.Length == 1 ? [] : [.. parts[1..].Select(Unescape)];
        return key.Length > 0;
    }

    /// <summary>
    /// 符号を日本語の文言へ戻す。符号でなければそのまま返す。入れ子の符号の引数も戻す。
    /// 日本語のマスターに無いキーはキー名と引数を並べる。
    /// </summary>
    public static string ToJapanese(string? text) => Resolve(text, static (key, args) =>
        CleanupMessages.JapaneseTable.TryGetValue(key, out var format) ? SafeFormat(format, args) : $"{key} {string.Join(' ', args)}".TrimEnd());

    /// <summary>
    /// 符号を <paramref name="translate"/> で文言にする。符号でなければそのまま返す。
    /// 入れ子の符号の引数は先に文言にしてから渡す。
    /// </summary>
    public static string Resolve(string? text, Func<string, IReadOnlyList<string>, string> translate)
    {
        ArgumentNullException.ThrowIfNull(translate);
        if (text is null)
        {
            return string.Empty;
        }

        if (!TryParse(text, out var key, out var args))
        {
            return text;
        }

        var resolved = args.Count == 0 ? args : [.. args.Select(arg => Resolve(arg, translate))];
        return translate(key, resolved);
    }

    /// <summary>テストが符号の中身 (キー) を確かめるための取り出し。符号でなければ null。</summary>
    public static string? KeyOf(string? text) => TryParse(text, out var key, out _) ? key : null;

    private static string SafeFormat(string format, IReadOnlyList<string> args)
    {
        try
        {
            return string.Format(CultureInfo.InvariantCulture, format, [.. args]);
        }
        catch (FormatException)
        {
            return format;
        }
    }

    private static string Escape(string? value) =>
        value is null
            ? string.Empty
            : value.Replace(EscapedMark, ' ').Replace(EscapedSeparator, ' ').Replace(Mark, EscapedMark).Replace(Separator, EscapedSeparator);

    private static string Unescape(string value) => value.Replace(EscapedMark, Mark).Replace(EscapedSeparator, Separator);
}
