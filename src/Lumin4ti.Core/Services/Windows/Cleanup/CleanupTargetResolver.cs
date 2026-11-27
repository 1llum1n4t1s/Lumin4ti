using System.IO.Enumeration;
using System.Runtime.Versioning;
using Lumin4ti.Core.Services.Windows.Actions;
using Microsoft.Win32;
using EngineTarget = Lumin4ti.Core.Services.Windows.Actions.CleanupTarget;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>検出条件 (path / registry / package) を確かめる OS への境界。何も変更しない。テストでは差し替える。</summary>
internal interface ICleanupProbe
{
    bool PathExists(string fullPath);

    /// <summary>HKCU / HKLM のキー (セグメントの <c>*</c>・<c>?</c> 可) と、あれば値の存在・一致を確かめる。</summary>
    bool RegistryMatches(CleanupDetect detect);

    /// <summary>パッケージファミリー名のアプリがこの利用者に入っているか。</summary>
    bool PackageInstalled(string packageFamilyName);
}

/// <summary>展開後の対象 1 件。<see cref="Path"/> は展開した絶対パス (レジストリ・ハンドラーでは null)。</summary>
/// <param name="Rejection">展開後の検証で拒否した理由。拒否されたら実行しない。</param>
internal sealed record ResolvedTarget(CleanupRuleTarget Source, string? Path, string? Rejection);

/// <param name="Truncated">展開数が上限に達し、残りは対象に含めていない。</param>
internal sealed record ResolvedRule(IReadOnlyList<ResolvedTarget> Targets, bool Truncated);

/// <summary>
/// 規則の対象を、実行時の環境変数とパス途中の <c>*</c>・<c>?</c> から実在する絶対パスへ展開する。
/// 展開した実パスごとに、検証器と同じ保護・認証情報の拒否を再実施する (実在するフォルダー名が拒否表に当たれば、その 1 件だけ拒否)。
/// 展開ではリンク (再解析点) を辿らず、展開数に上限を設ける。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class CleanupTargetResolver
{
    /// <summary>1 つの対象から展開する最大数。超えた分は対象に含めない。</summary>
    internal const int MaxExpansions = 256;

    private readonly Func<string, string?> _environment;
    private readonly ICleanupProbe _probe;
    private readonly Lazy<CleanupBaseTrust> _trust;

    /// <param name="environment">環境変数の読み取り。利用者が差し替えられるため、基点は <paramref name="trust"/> で照合してから使う。</param>
    /// <param name="probe">検出の境界。</param>
    /// <param name="trust">基点の信頼の根拠。省略時は実環境 (HKLM の ProfileList・既知フォルダー)。</param>
    public CleanupTargetResolver(Func<string, string?> environment, ICleanupProbe probe, CleanupBaseTrust? trust = null)
    {
        _environment = environment;
        _probe = probe;
        _trust = trust is null ? new Lazy<CleanupBaseTrust>(CleanupBaseTrust.FromEnvironment) : new Lazy<CleanupBaseTrust>(trust);
    }

    public CleanupTargetResolver()
        : this(Environment.GetEnvironmentVariable, new SystemCleanupProbe(Environment.GetEnvironmentVariable))
    {
    }

    /// <summary>detect のどれか 1 つでも満たすか (この PC にその製品・場所があるか)。</summary>
    public bool IsDetected(CleanupRule rule) => rule.Detect.Any(IsDetected);

    private bool IsDetected(CleanupDetect detect)
    {
        if (detect.Package is not null) return _probe.PackageInstalled(detect.Package);
        if (detect.Registry is not null) return _probe.RegistryMatches(detect);
        if (detect.Path is null) return false;
        var expanded = Expand(detect.Path, includeFiles: true);
        // 基点を信頼できない場合は「無い」と偽らず、検出済みとして扱い、展開の拒否理由を走査結果に出す。
        return expanded.Untrusted || expanded.Paths.Any(p => _probe.PathExists(p.Full));
    }

    /// <summary>規則の全対象を展開して検証する。</summary>
    public ResolvedRule Resolve(CleanupRule rule)
    {
        var targets = new List<ResolvedTarget>();
        var truncated = false;
        foreach (var target in rule.Targets)
        {
            if (target.Type is CleanupTargetType.Contents or CleanupTargetType.Files or CleanupTargetType.Sqlite or CleanupTargetType.IniValues)
            {
                truncated |= ResolvePaths(rule, target, targets);
            }
            else
            {
                targets.Add(new ResolvedTarget(target, null, null));
            }
        }

        return new ResolvedRule(targets, truncated);
    }

    private bool ResolvePaths(CleanupRule rule, CleanupRuleTarget target, List<ResolvedTarget> output)
    {
        var includeFiles = target.Type is CleanupTargetType.Sqlite or CleanupTargetType.IniValues;
        var expanded = Expand(target.Path ?? string.Empty, includeFiles);
        if (expanded.Rejection is not null)
        {
            output.Add(new ResolvedTarget(target, null, expanded.Rejection));
            return false;
        }

        foreach (var (raw, full) in expanded.Paths)
        {
            output.Add(new ResolvedTarget(target, full, Revalidate(rule, target, raw, full)));
        }

        return expanded.Truncated;
    }

    /// <summary>展開した実パスを、検証器と同じ検査 (認証情報・拒否表・保護フォルダー・自身の領域) にもう一度かける。</summary>
    private static string? Revalidate(CleanupRule rule, CleanupRuleTarget target, string raw, string full)
    {
        var probeRule = rule with { Targets = [target with { Path = raw }] };
        var violations = CleanupRuleValidator.Validate(probeRule);
        if (violations.Count > 0) return $"{violations[0].Code}: {violations[0].Message}";
        var allowProtected = target.Type != CleanupTargetType.Contents;
        // full は展開済みの実パス。再展開しない。
        return FileCleanupEngine.TryResolveActual(full, allowProtected, out _, out var rejection) ? null : rejection;
    }

    /// <param name="Untrusted">基点の環境変数が信頼できる場所を指していないための拒否。</param>
    private readonly record struct Expansion(IReadOnlyList<(string Raw, string Full)> Paths, bool Truncated, string? Rejection, bool Untrusted = false);

    /// <summary>
    /// <c>%ENV%\a\*\b</c> を、実行時の環境変数と実在するフォルダーで展開する。<c>Raw</c> は検証器へ渡す環境変数付きの形、
    /// <c>Full</c> は環境変数を展開した絶対パス。
    /// </summary>
    private Expansion Expand(string raw, bool includeFiles)
    {
        var close = raw.Length > 1 && raw[0] == '%' ? raw.IndexOf('%', 1) : -1;
        if (close < 2 || (close + 1 < raw.Length && raw[close + 1] != '\\')) return Reject("パスが環境変数で始まっていません");
        var name = raw[1..close];
        var baseDirectory = _environment(name);
        if (string.IsNullOrWhiteSpace(baseDirectory) || !Path.IsPathFullyQualified(baseDirectory)) return Reject("環境変数を解決できませんでした");
        // 短縮名 (8.3) の基点は長い名前へ戻してから、信頼の照合・除外・重なり判定・エンジンへ渡す。
        try { baseDirectory = PathNames.ToLongName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory))); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return Reject("環境変数を解決できませんでした"); }
        // 値に %名前% が残っていると、後段の再展開で信頼の検査の外へ逃げられる。展開後の基点に % が残れば使わない。
        if (baseDirectory.Contains('%')) return new Expansion([], false, CleanupMessage.Of("BaseProtectedLocation", name), Untrusted: true);
        if (_trust.Value.Check(name, baseDirectory) is { } untrusted) return new Expansion([], false, untrusted, Untrusted: true);
        var segments = close + 1 < raw.Length ? raw[(close + 2)..].Split('\\') : [];

        var frontier = new List<string[]> { Array.Empty<string>() };
        var truncated = false;
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (!HasWildcard(segment))
            {
                frontier = [.. frontier.Select(parent => (string[])[.. parent, segment])];
                continue;
            }

            frontier = ExpandSegment(baseDirectory, frontier, segment, includeFiles && index == segments.Length - 1, ref truncated);
        }

        var prefix = $"%{name}%";
        var paths = frontier
            .Select(relative => (Raw: relative.Length == 0 ? prefix : $"{prefix}\\{string.Join('\\', relative)}", Full: Path.Combine([baseDirectory, .. relative])))
            .ToList();
        return new Expansion(paths, truncated, null);

        static Expansion Reject(string reason) => new([], false, reason);
    }

    private static List<string[]> ExpandSegment(string baseDirectory, List<string[]> frontier, string pattern, bool includeFiles, ref bool truncated)
    {
        var next = new List<string[]>();
        foreach (var parent in frontier)
        {
            foreach (var child in ListChildren(Path.Combine([baseDirectory, .. parent]), pattern, includeFiles))
            {
                if (next.Count >= MaxExpansions)
                {
                    truncated = true;
                    return next;
                }

                next.Add([.. parent, child]);
            }
        }

        return next;
    }

    /// <summary>
    /// <paramref name="directory"/> の直下から、パターンに一致する名前を返す。リンク (再解析点) は含めず、リンクのフォルダー自体も開かない。
    /// 末尾が <c>.</c>・空白の名前 (パスの正規化で別名になる) は返さない。
    /// </summary>
    private static IEnumerable<string> ListChildren(string directory, string pattern, bool includeFiles)
    {
        try
        {
            if ((File.GetAttributes(directory) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != FileAttributes.Directory) return [];
            var options = new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
            };
            var entries = new FileSystemEnumerable<string>(directory, static (ref FileSystemEntry entry) => entry.FileName.ToString(), options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                    (entry.IsDirectory || includeFiles) &&
                    !entry.FileName.EndsWith(".") && !entry.FileName.EndsWith(" ") &&
                    FileSystemName.MatchesSimpleExpression(pattern, entry.FileName, ignoreCase: true),
            };
            return entries.Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static bool HasWildcard(string segment) => segment.AsSpan().IndexOfAny('*', '?') >= 0;
}

/// <summary>実 OS での検出。読み取りだけを行う。</summary>
[SupportedOSPlatform("windows")]
internal sealed class SystemCleanupProbe(Func<string, string?> environment) : ICleanupProbe
{
    public bool PathExists(string fullPath) => File.Exists(fullPath) || Directory.Exists(fullPath);

    public bool PackageInstalled(string packageFamilyName)
    {
        if (environment("LOCALAPPDATA") is not { Length: > 0 } local) return false;
        var packages = Path.Combine(local, "Packages");
        try
        {
            return packageFamilyName.Contains('_')
                ? Directory.Exists(Path.Combine(packages, packageFamilyName))
                : Directory.Exists(packages) && Directory.EnumerateDirectories(packages, packageFamilyName + "_*").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool RegistryMatches(CleanupDetect detect)
    {
        var parts = detect.Registry!.Split('\\');
        var hive = parts[0].Equals("HKCU", StringComparison.OrdinalIgnoreCase) ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            return MatchKey(root, parts, 1, detect);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool MatchKey(RegistryKey key, string[] parts, int index, CleanupDetect detect)
    {
        if (index == parts.Length) return ValueMatches(key, detect);
        var segment = parts[index];
        if (segment.AsSpan().IndexOfAny('*', '?') < 0)
        {
            using var child = key.OpenSubKey(segment, writable: false);
            return child is not null && MatchKey(child, parts, index + 1, detect);
        }

        foreach (var name in key.GetSubKeyNames().Where(n => FileSystemName.MatchesSimpleExpression(segment, n, ignoreCase: true)))
        {
            using var child = key.OpenSubKey(name, writable: false);
            if (child is not null && MatchKey(child, parts, index + 1, detect)) return true;
        }

        return false;
    }

    private static bool ValueMatches(RegistryKey key, CleanupDetect detect)
    {
        if (detect.ValueName is null) return true;
        var value = key.GetValue(detect.ValueName, null);
        if (value is null) return false;
        return detect.ValueEquals is null ||
               string.Equals(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), detect.ValueEquals, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>展開した対象を削除エンジンの対象へ変換する。</summary>
internal static class CleanupEngineTargets
{
    /// <summary>contents / files の規則対象を、展開済みの絶対パスの <see cref="EngineTarget"/> にする。</summary>
    /// <param name="keepCredentials">カスタムフォルダー。認証情報の名前の子孫を、走査でも削除でも残す。</param>
    public static EngineTarget From(CleanupRuleTarget target, string fullPath, bool keepCredentials = false) =>
        target.Type == CleanupTargetType.Files
            ? new EngineTarget(fullPath, CleanupTargetKind.Files, target.Pattern, target.Recurse, target.RemoveEmptyDirs, target.OlderThanDays) { KeepCredentials = keepCredentials, IsActualPath = true }
            : new EngineTarget(fullPath, CleanupTargetKind.Contents, target.Pattern, false, target.RemoveEmptyDirs, target.OlderThanDays) { KeepCredentials = keepCredentials, IsActualPath = true };
}
