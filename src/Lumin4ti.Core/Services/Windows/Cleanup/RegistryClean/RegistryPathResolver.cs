using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;

/// <summary>値を読んだ場所。環境変数の展開と候補の増やし方が変わる。</summary>
internal enum RegistryPathOrigin
{
    /// <summary>HKLM の 64 ビット側。</summary>
    Machine,

    /// <summary>HKLM の WOW6432Node (32 ビットアプリの登録)。</summary>
    MachineWow64,

    /// <summary>HKCU。利用者側の変数 (<c>USERPROFILE</c> <c>LOCALAPPDATA</c> <c>APPDATA</c>) を展開できる唯一の場所。</summary>
    User,
}

/// <summary>
/// レジストリ値のパス・コマンドラインを、存在判定する完全パスの候補へ解釈する純粋な部品。
/// 返す一覧が空のときは「解釈できない」で、呼び出し側は <see cref="RegistryFileState.Unknown"/> として扱う。
/// </summary>
internal interface IRegistryPathResolver
{
    /// <summary>単純なパス (引用符なし・引数なし) を展開する。共有 DLL の値の名前などに使う。</summary>
    IReadOnlyList<string> ExpandPath(string value, RegistryPathOrigin origin);

    /// <summary>コマンドラインを解釈して実行ファイルの候補を返す (ラッパー経由・曖昧なコマンドは空)。</summary>
    IReadOnlyList<string> ResolveCommand(string command, RegistryPathOrigin origin);

    /// <summary>App Paths の <c>(Default)</c>。引用符を外し、そのままと <c>+.exe</c> の両方を返す。</summary>
    IReadOnlyList<string> ResolveAppPath(string value, RegistryPathOrigin origin);

    /// <summary>フォントの値。ファイル名だけなら Fonts フォルダー配下、<c>|</c> を含む値は空。</summary>
    IReadOnlyList<string> ResolveFontPath(string value, RegistryPathOrigin origin);
}

/// <summary>
/// <see cref="IRegistryPathResolver"/> の実装。許可した環境変数だけを既知フォルダーで展開し (プロセスの環境変数は読まない)、
/// 利用者側の変数は HKCU 由来の値に限り <see cref="CleanupBaseTrust"/> (ProfileList 照合) を通す。
/// </summary>
internal sealed class RegistryPathResolver : IRegistryPathResolver
{
    private const int MaxCandidates = 16;

    private static readonly HashSet<string> MachineVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "SystemRoot", "windir", "SystemDrive", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
        "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432", "ProgramData", "ALLUSERSPROFILE",
    };

    private static readonly HashSet<string> UserVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "USERPROFILE", "LOCALAPPDATA", "APPDATA",
    };

    private readonly CleanupBaseTrust _trust;
    private readonly Func<string, string?> _knownFolder;

    /// <param name="trust">利用者側の変数の照合。</param>
    /// <param name="knownFolder">変数名 (と <c>Fonts</c>) から OS の既知フォルダーを返す。無ければ null。</param>
    public RegistryPathResolver(CleanupBaseTrust trust, Func<string, string?> knownFolder)
    {
        _trust = trust;
        _knownFolder = knownFolder;
    }

    /// <summary>実環境の既知フォルダーと ProfileList 照合で作る。</summary>
    public static RegistryPathResolver FromEnvironment() => new(CleanupBaseTrust.FromEnvironment(), DefaultKnownFolder);

    /// <summary>OS の既知フォルダー。環境変数は使わない。</summary>
    public static string? DefaultKnownFolder(string name)
    {
        var folder = name.ToUpperInvariant() switch
        {
            "SYSTEMROOT" or "WINDIR" => Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "SYSTEMDRIVE" => Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))?.TrimEnd('\\'),
            "PROGRAMFILES" or "PROGRAMW6432" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PROGRAMFILES(X86)" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "COMMONPROGRAMFILES" or "COMMONPROGRAMW6432" => Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            "COMMONPROGRAMFILES(X86)" => Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            "PROGRAMDATA" or "ALLUSERSPROFILE" => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "USERPROFILE" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "LOCALAPPDATA" => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "APPDATA" => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FONTS" => Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(folder) ? null : folder;
    }

    public IReadOnlyList<string> ExpandPath(string value, RegistryPathOrigin origin)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? [] : ExpandVariables(text, origin);
    }

    public IReadOnlyList<string> ResolveCommand(string command, RegistryPathOrigin origin)
    {
        var results = new List<string>();
        foreach (var expanded in ExpandPath(command, origin))
        {
            // 展開済みなので、TryResolveExecutable 内の Environment.ExpandEnvironmentVariables は何も変えない。
            var executable = StartupCommandParser.TryResolveExecutable(expanded);
            if (executable is null)
            {
                return [];
            }

            results.Add(executable);
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> ResolveAppPath(string value, RegistryPathOrigin origin)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = text[1..^1].Trim();
        }

        if (text.Contains('"'))
        {
            return [];
        }

        var results = new List<string>();
        foreach (var path in ExpandPath(text, origin))
        {
            results.Add(path);
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(path + ".exe");
            }
        }

        return results;
    }

    public IReadOnlyList<string> ResolveFontPath(string value, RegistryPathOrigin origin)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Contains('|'))
        {
            return [];
        }

        if (text.IndexOfAny(['\\', '/', ':', '%']) < 0)
        {
            var fonts = _knownFolder("Fonts");
            return fonts is null || text is "." or ".." ? [] : [Path.Combine(fonts, text)];
        }

        return ExpandPath(text, origin);
    }

    private List<string> ExpandVariables(string text, RegistryPathOrigin origin)
    {
        var results = new List<string> { string.Empty };
        var position = 0;
        while (position < text.Length)
        {
            var open = text.IndexOf('%', position);
            if (open < 0)
            {
                results = Append(results, [text[position..]]);
                break;
            }

            var close = text.IndexOf('%', open + 1);
            if (close < 0)
            {
                return [];
            }

            results = Append(results, [text[position..open]]);
            var alternatives = VariableValues(text[(open + 1)..close], origin);
            if (alternatives.Count == 0)
            {
                return [];
            }

            results = Append(results, alternatives);
            if (results.Count > MaxCandidates)
            {
                return [];
            }

            position = close + 1;
        }

        return AddWowAlternatives(results, origin);
    }

    private static List<string> Append(List<string> prefixes, IReadOnlyList<string> suffixes)
    {
        if (suffixes.Count == 1)
        {
            for (var i = 0; i < prefixes.Count; i++)
            {
                prefixes[i] += suffixes[0];
            }

            return prefixes;
        }

        return prefixes.SelectMany(prefix => suffixes.Select(suffix => prefix + suffix)).ToList();
    }

    /// <summary>変数の展開値 (WOW6432Node 由来の Program Files 系は 64 ビット版と x86 版の両方)。許可外・確認できない変数は空。</summary>
    private List<string> VariableValues(string name, RegistryPathOrigin origin)
    {
        if (UserVariables.Contains(name))
        {
            var folder = origin == RegistryPathOrigin.User ? _knownFolder(name) : null;
            return folder is not null && _trust.Check(name.ToUpperInvariant(), folder) is null ? [folder] : [];
        }

        if (!MachineVariables.Contains(name))
        {
            return [];
        }

        var value = _knownFolder(name);
        if (value is null)
        {
            return [];
        }

        var list = new List<string> { value };
        var counterpart = origin == RegistryPathOrigin.MachineWow64 ? WowCounterpart(name) : null;
        if (counterpart is not null && _knownFolder(counterpart) is { } other &&
            !string.Equals(other, value, StringComparison.OrdinalIgnoreCase))
        {
            list.Add(other);
        }

        return list;
    }

    private static string? WowCounterpart(string name) => name.ToUpperInvariant() switch
    {
        "PROGRAMFILES" => "ProgramFiles(x86)",
        "COMMONPROGRAMFILES" => "CommonProgramFiles(x86)",
        _ => null,
    };

    /// <summary>WOW6432Node の値: System32 は SysWOW64 も、Sysnative は System32 を候補にする。それ以外の場所の Sysnative は解釈しない。</summary>
    private List<string> AddWowAlternatives(List<string> paths, RegistryPathOrigin origin)
    {
        var windows = _knownFolder("windir");
        if (windows is null)
        {
            return paths;
        }

        var result = new List<string>();
        foreach (var path in paths)
        {
            var sysnative = ReplacePrefix(path, Path.Combine(windows, "Sysnative"), Path.Combine(windows, "System32"));
            if (sysnative is not null)
            {
                if (origin == RegistryPathOrigin.MachineWow64)
                {
                    result.Add(sysnative);
                }

                continue;
            }

            result.Add(path);
            if (origin == RegistryPathOrigin.MachineWow64 &&
                ReplacePrefix(path, Path.Combine(windows, "System32"), Path.Combine(windows, "SysWOW64")) is { } wow)
            {
                result.Add(wow);
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? ReplacePrefix(string path, string prefix, string replacement)
    {
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = path[prefix.Length..];
        return rest.Length == 0 || rest[0] is '\\' or '/' ? replacement + rest : null;
    }
}
