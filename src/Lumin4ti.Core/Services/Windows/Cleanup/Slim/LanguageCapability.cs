using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services.Windows.Cleanup.Slim;

/// <summary>言語の入力補助機能 (Language.Basic) の状態。</summary>
public enum LanguageCapabilityStatus
{
    /// <summary>インストールされていない。</summary>
    NotInstalled,

    /// <summary>インストール済みで、どの利用者の言語設定にも使われていないため削除できる。</summary>
    Removable,

    /// <summary>UI 言語・言語一覧・キーボード配列のどれかで使われている。</summary>
    InUse,

    /// <summary>使用状況または機能の状態を確認できないため扱わない (未ロードの利用者ハイブを含む)。</summary>
    Unavailable,
}

/// <summary>言語 1 つ分の状態。<see cref="Identity"/> は Removable のときだけ入る DISM のケイパビリティ名。</summary>
/// <param name="UserSettingsUnreadable">利用者の言語設定を読み切れなかったことが原因で Unavailable になった (未ロードの利用者ハイブなど)。</param>
public sealed record LanguageCapabilityState(string Tag, LanguageCapabilityStatus Status, string? Identity, string Detail, bool UserSettingsUnreadable = false);

/// <summary>
/// 利用者の言語設定の読み取り結果。<see cref="IncompleteReason"/> があるときは一部の利用者を読めていない。
/// </summary>
/// <param name="RawLanguageTags">UI 言語・言語一覧の言語タグ (ja-JP / zh-Hans-CN など生のまま)。</param>
/// <param name="KeyboardLanguageIds">キーボード配列 ID の下位 16 ビット (LANGID)。</param>
/// <param name="IncompleteReason">未ロードの利用者ハイブや読み取り失敗など、読み切れなかった理由。</param>
public sealed record UserLanguageSnapshot(
    IReadOnlyCollection<string> RawLanguageTags,
    IReadOnlyCollection<int> KeyboardLanguageIds,
    string? IncompleteReason);

/// <summary>全利用者の言語設定を読む境界。</summary>
public interface IUserLanguageSource
{
    UserLanguageSnapshot Read();
}

/// <summary>
/// ja-JP / ko-KR / zh-TW / zh-CN の <c>Language.Basic</c> (スペル・予測など) だけを DISM で削除する。
/// IME 本体は標準配置で削除手段を確認できないため扱わない。
/// </summary>
public sealed class LanguageCapabilityService(ICommandExecutor executor, IUserLanguageSource languages)
{
    /// <summary>削除対象にできる言語。</summary>
    public static readonly IReadOnlyList<string> SupportedTags = ["ja-JP", "ko-KR", "zh-TW", "zh-CN"];

    private const string IdentityPrefix = "Language.Basic~~~";
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RemoveTimeout = TimeSpan.FromMinutes(30);

    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.Ordinal)
    {
        ["ja-JP"] = ["ja-jp", "ja"],
        ["ko-KR"] = ["ko-kr", "ko"],
        ["zh-TW"] = ["zh-tw", "zh-hant-tw", "zh-hant"],
        ["zh-CN"] = ["zh-cn", "zh-hans-cn", "zh-hans"],
    };

    private static readonly Dictionary<int, string> KeyboardLanguages = new()
    {
        [0x0411] = "ja-JP",
        [0x0412] = "ko-KR",
        [0x0404] = "zh-TW",
        [0x0804] = "zh-CN",
    };

    /// <summary>
    /// 各言語の状態を調べる (読み取りだけ)。利用者の言語設定を読み切れない場合、機能の一覧を得られない場合は
    /// すべて <see cref="LanguageCapabilityStatus.Unavailable"/> にする。
    /// </summary>
    public async Task<IReadOnlyList<LanguageCapabilityState>> ScanAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var snapshot = languages.Read();
        if (snapshot.IncompleteReason is { } incomplete)
        {
            return AllUnavailable(CleanupMessage.Of("LanguageSettingsUnreadable", incomplete), userSettingsUnreadable: true);
        }

        var query = await executor.RunAsync(
            "dism.exe",
            "/Online /Get-Capabilities /English",
            ct,
            progress,
            QueryTimeout).ConfigureAwait(false);
        var installed = ParseInstalledIdentities(query);
        if (installed is null)
        {
            return AllUnavailable(CleanupMessage.Of("LanguageListUnavailable", query.ExitCode));
        }

        var used = UsedTags(snapshot);
        return [.. SupportedTags.Select(tag => Classify(tag, installed, used))];
    }

    /// <summary>
    /// 1 言語の Language.Basic を削除する。直前に利用者設定と機能の状態を取り直し、
    /// <see cref="LanguageCapabilityStatus.Removable"/> のときだけ DISM を実行する。
    /// </summary>
    public async Task<DismOperationResult> RemoveAsync(string tag, IProgress<string>? progress, CancellationToken ct)
    {
        if (!SupportedTags.Contains(tag, StringComparer.Ordinal))
        {
            return new(false, false, CleanupMessage.Of("LanguageNotTarget", tag));
        }

        var states = await ScanAsync(progress, ct).ConfigureAwait(false);
        var state = states.First(s => s.Tag == tag);
        if (state.Status != LanguageCapabilityStatus.Removable || state.Identity is null)
        {
            return new(false, false, CleanupMessage.Of("LanguageNotRemovable", tag, state.Detail));
        }

        var result = await executor.RunAsync(
            "dism.exe",
            $"/Online /Remove-Capability /CapabilityName:{state.Identity} /English",
            ct,
            progress,
            RemoveTimeout).ConfigureAwait(false);
        return ComponentStore.InterpretOperation(result, CleanupMessage.Of("LanguageRemoval", tag));
    }

    /// <summary>生の言語タグを削除対象の言語へ対応付ける。対象外なら null。</summary>
    internal static string? Normalize(string rawTag)
    {
        var lower = rawTag.Trim().ToLowerInvariant();
        foreach (var (tag, aliases) in Aliases)
        {
            if (aliases.Any(alias => lower == alias || lower.StartsWith(alias + "-", StringComparison.Ordinal)))
            {
                return tag;
            }
        }

        return null;
    }

    internal static HashSet<string> UsedTags(UserLanguageSnapshot snapshot)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in snapshot.RawLanguageTags)
        {
            if (Normalize(raw) is { } tag)
            {
                used.Add(tag);
            }
        }

        foreach (var id in snapshot.KeyboardLanguageIds)
        {
            if (KeyboardLanguages.TryGetValue(id & 0xFFFF, out var tag))
            {
                used.Add(tag);
            }
        }

        return used;
    }

    /// <summary>
    /// /Get-Capabilities の出力から、インストール済みの Language.Basic のケイパビリティ名を言語ごとに集める。
    /// 成功を確認できない・解釈できない出力は null。
    /// </summary>
    internal static Dictionary<string, string>? ParseInstalledIdentities(CommandExecutionResult result)
    {
        if (result.ExitCode != 0 || !result.StandardOutput.Contains(ComponentStore.SuccessMarker, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var installed = new Dictionary<string, string>(StringComparer.Ordinal);
        string? identity = null;
        var sawIdentity = false;
        foreach (var raw in result.StandardOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Capability Identity", StringComparison.OrdinalIgnoreCase))
            {
                identity = ValueOf(line);
                sawIdentity = true;
            }
            else if (line.StartsWith("State", StringComparison.OrdinalIgnoreCase) && identity is not null)
            {
                if (string.Equals(ValueOf(line), "Installed", StringComparison.OrdinalIgnoreCase) &&
                    TryGetTag(identity, out var tag))
                {
                    installed[tag] = identity;
                }

                identity = null;
            }
        }

        return sawIdentity ? installed : null;
    }

    /// <summary>"Language.Basic~~~ja-JP~0.0.1.0" の形で、対象の言語・数字と点だけの版を持つ名前かを検査して言語を返す。</summary>
    internal static bool TryGetTag(string identity, out string tag)
    {
        tag = string.Empty;
        if (!identity.StartsWith(IdentityPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = identity[IdentityPrefix.Length..].Split('~');
        if (rest.Length != 2 || !SupportedTags.Contains(rest[0], StringComparer.Ordinal) ||
            rest[1].Length == 0 || rest[1].Any(c => c != '.' && !char.IsAsciiDigit(c)))
        {
            return false;
        }

        tag = rest[0];
        return true;
    }

    private static LanguageCapabilityState Classify(string tag, Dictionary<string, string> installed, HashSet<string> used)
    {
        if (!installed.TryGetValue(tag, out var identity))
        {
            return new(tag, LanguageCapabilityStatus.NotInstalled, null, CleanupMessage.Of("LanguageNotInstalled"));
        }

        return used.Contains(tag)
            ? new(tag, LanguageCapabilityStatus.InUse, null, CleanupMessage.Of("LanguageInUse"))
            : new(tag, LanguageCapabilityStatus.Removable, identity, CleanupMessage.Of("LanguageNotUsed"));
    }

    private static List<LanguageCapabilityState> AllUnavailable(string detail, bool userSettingsUnreadable = false) =>
        [.. SupportedTags.Select(tag => new LanguageCapabilityState(tag, LanguageCapabilityStatus.Unavailable, null, detail, userSettingsUnreadable))];

    private static string ValueOf(string line)
    {
        var separator = line.IndexOf(':');
        return separator < 0 ? string.Empty : line[(separator + 1)..].Trim();
    }
}
