using System.Text.RegularExpressions;
using System.Xml.Linq;
using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.Core.Services.Windows.Cleanup.Slim;
using Lumin4ti.UI.ViewModels;

namespace Lumin4ti.Tests;

/// <summary>
/// クリーンアップの文言キーの一貫性。英語 (en_US) を正本とし、日本語は XAML 専用キーが ja_JP.axaml、
/// コード側のキーが <see cref="CleanupText"/> のフォールバックにあること、Core が返すキーに欠落がないこと、
/// 書式引数の個数が両言語で一致することを確かめる。
/// </summary>
[TestClass]
public sealed partial class CleanupLocalizationTests
{
    private const string Prefix = "Text.Cleanup.";

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex Placeholder();

    private static Dictionary<string, string> LoadDictionary(string locale, [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (!File.Exists(Path.Combine(directory.FullName, "Lumin4ti.slnx")))
        {
            directory = directory.Parent ?? throw new DirectoryNotFoundException("Lumin4ti.slnx を含むリポジトリルートを特定できません。");
        }

        var path = Path.Combine(directory.FullName, "src", "Lumin4ti.UI", "Resources", "Locales", locale + ".axaml");
        return XDocument.Load(path).Root!.Elements()
            .Where(e => e.Attribute(Xaml + "Key") is not null)
            .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!, e => e.Value, StringComparer.Ordinal);
    }

    [GeneratedRegex(@"CleanupMessage\.Of\(\s*""([A-Za-z0-9_.]+)""")]
    private static partial Regex MessageCall();

    private static string JapaneseOf(Dictionary<string, string> ja, string key) =>
        ja.TryGetValue(key, out var xaml) ? xaml : CleanupText.JapaneseOf(key["Text.".Length..]);

    private static HashSet<string> Arguments(string text) =>
        Placeholder().Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    [TestMethod]
    public void EveryCleanupKeyHasExactlyOneJapaneseSource()
    {
        var en = LoadDictionary("en_US");
        var ja = LoadDictionary("ja_JP");
        var code = CleanupText.JapaneseKeys.Select(k => "Text." + k).ToHashSet(StringComparer.Ordinal);
        var jaCleanup = ja.Keys.Where(k => k.StartsWith(Prefix, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);

        var cleanupKeys = en.Keys.Where(k => k.StartsWith(Prefix, StringComparison.Ordinal)).ToList();
        Assert.IsNotEmpty(cleanupKeys);
        var missing = cleanupKeys.Where(k => !jaCleanup.Contains(k) && !code.Contains(k)).ToList();
        var both = cleanupKeys.Where(k => jaCleanup.Contains(k) && code.Contains(k)).ToList();
        Assert.IsEmpty(missing, "日本語が無いキー: " + string.Join(", ", missing));
        Assert.IsEmpty(both, "日本語が二重にあるキー: " + string.Join(", ", both));
        Assert.IsEmpty(jaCleanup.Except(en.Keys).Concat(code.Except(en.Keys)).ToList(), "en_US に無い日本語キーがあります。");
    }

    [TestMethod]
    public void FormatArgumentsMatchBetweenEnglishAndJapanese()
    {
        var en = LoadDictionary("en_US");
        var ja = LoadDictionary("ja_JP");
        var mismatched = en.Where(p => p.Key.StartsWith(Prefix, StringComparison.Ordinal))
            .Where(p => !Arguments(p.Value).SetEquals(Arguments(JapaneseOf(ja, p.Key))))
            .Select(p => p.Key)
            .ToList();
        Assert.IsEmpty(mismatched, "書式引数が一致しないキー: " + string.Join(", ", mismatched));
    }

    [TestMethod]
    public void KeysReturnedByCoreAreDefinedInEnglishAndJapanese()
    {
        var en = LoadDictionary("en_US");
        var ja = LoadDictionary("ja_JP");
        var rules = CleanupRuleSource.All();
        var coreKeys = rules.Select(r => r.NameKey).Concat(rules.Select(r => r.WarningKey))
            .Concat(SlimService.Definitions.SelectMany(d => new[] { d.NameKey, d.DescriptionKey, d.WarningKey }))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.IsNotEmpty(coreKeys);
        var absent = coreKeys.Where(k => !en.ContainsKey(k) || JapaneseOf(ja, k) == k["Text.".Length..]).ToList();
        Assert.IsEmpty(absent, "文言が無い Core のキー: " + string.Join(", ", absent));
    }

    [TestMethod]
    public void MessagesProducedByCoreAreInTheJapaneseMasterAndEnglish() => AssertCoreMessages();

    private static void AssertCoreMessages([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (!File.Exists(Path.Combine(directory.FullName, "Lumin4ti.slnx")))
        {
            directory = directory.Parent ?? throw new DirectoryNotFoundException("Lumin4ti.slnx を含むリポジトリルートを特定できません。");
        }

        var core = Path.Combine(directory.FullName, "src", "Lumin4ti.Core", "Services", "Windows", "Cleanup");
        var sources = Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText).ToList();
        var used = sources
            .SelectMany(text => MessageCall().Matches(text).Select(m => m.Groups[1].Value))
            .Select(name => name.StartsWith("Cleanup.", StringComparison.Ordinal) ? name : "Cleanup.Msg." + name)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.IsNotEmpty(used);
        var en = LoadDictionary("en_US");
        var unknown = used.Where(k => CleanupMessages.JapaneseOf(k) is null || !en.ContainsKey("Text." + k)).ToList();
        Assert.IsEmpty(unknown, "日本語のマスターまたは en_US に無い文言: " + string.Join(", ", unknown));
        // 名前が条件式や引数で渡される文言もあるため、名前の文字列がどこかに書かれていれば使われているとみなす。
        var unused = CleanupMessages.Keys
            .Where(k => !sources.Any(text => text.Contains("\"" + k["Cleanup.Msg.".Length..] + "\"", StringComparison.Ordinal)))
            .ToList();
        Assert.IsEmpty(unused, "どこからも使われていない文言: " + string.Join(", ", unused));
    }

    private static readonly string[] AllLocales =
    [
        "de_DE", "en_US", "es_ES", "fil_PH", "fr_FR", "id_ID", "it_IT", "ja_JP", "ko_KR",
        "la", "pt_BR", "ru_RU", "sa", "ta_IN", "uk_UA", "zh_CN", "zh_TW",
    ];

    [TestMethod]
    public void EveryNonJapaneseLocaleHasTheSameCleanupKeysAsEnglish()
    {
        var english = LoadDictionary("en_US").Keys.Where(k => k.StartsWith(Prefix, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var locale in AllLocales.Where(l => l is not ("en_US" or "ja_JP")))
        {
            var keys = LoadDictionary(locale).Keys.Where(k => k.StartsWith(Prefix, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
            problems.AddRange(english.Except(keys).Select(k => $"{locale}: 欠落 {k}"));
            problems.AddRange(keys.Except(english).Select(k => $"{locale}: 余分 {k}"));
        }

        Assert.IsEmpty(problems, "en_US と一致しないキー: " + string.Join(", ", problems));
    }

    [TestMethod]
    public void RuleDisplayNamesAreUniqueWithinACategoryInEveryLocale()
    {
        var ja = LoadDictionary("ja_JP");
        var rules = CleanupRuleSource.All();
        var duplicated = new List<string>();
        foreach (var locale in AllLocales)
        {
            var dictionary = LoadDictionary(locale);
            string Text(string key) => locale == "ja_JP" ? JapaneseOf(ja, key) : dictionary[key];
            var groups = rules
                .Select(r => (r.Category, r.Id, Name: r.NameKey is { } nameKey
                    ? Text(nameKey)
                    : string.Format(Text($"{Prefix}Kind.{r.Kind}"), r.Name)))
                .GroupBy(x => (x.Category, Name: x.Name.ToUpperInvariant()))
                .Where(g => g.Count() > 1);
            duplicated.AddRange(groups.Select(g => $"{locale}/{g.Key.Category}: {string.Join(" = ", g.Select(x => x.Id))}"));
        }

        Assert.IsEmpty(duplicated, "同じ分類で表示名が重複する規則: " + string.Join(" | ", duplicated));
    }

    [TestMethod]
    public void EveryRuleKindAndCategoryHasAText()
    {
        var en = LoadDictionary("en_US");
        var absent = Enum.GetNames<CleanupRuleKind>().Select(n => $"{Prefix}Kind.{n}")
            .Concat(Enum.GetNames<CleanupRuleCategory>().Select(n => $"{Prefix}Category.{n}"))
            .Concat(Enum.GetNames<CleanupRunStatus>().Select(n => $"{Prefix}Run.Status.{n}"))
            .Concat(Enum.GetNames<CleanupSkipReason>().Where(n => n != "None").Select(n => $"{Prefix}Run.Skip.{n}"))
            .Concat(Enum.GetNames<SlimReason>().Where(n => n != "None").Select(n => $"{Prefix}Slim.Ui.Reason.{n}"))
            .Where(k => !en.ContainsKey(k))
            .ToList();
        Assert.IsEmpty(absent, "文言が無い列挙値: " + string.Join(", ", absent));
    }
}
