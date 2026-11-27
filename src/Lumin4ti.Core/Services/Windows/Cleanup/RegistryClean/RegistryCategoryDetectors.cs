using System.Buffers.Binary;
using System.ComponentModel;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;

/// <summary>候補の削除に付随して変更する別の値。後段の削除担当が候補と一緒にバックアップ・削除・更新する。</summary>
internal enum RegistryCompanionKind
{
    /// <summary>Run の値に対応する StartupApproved の値 (<see cref="RegistryScanCompanion.Related"/> を削除する)。</summary>
    StartupApprovedValue,

    /// <summary>OpenWithList のスロット値を消すときの MRUList の更新 (<see cref="RegistryScanCompanion.Related"/> が現在の MRUList、<see cref="RegistryScanCompanion.RemovedSlots"/> の文字を取り除く。空になれば削除)。</summary>
    OpenWithMruList,
}

/// <summary>候補 (<see cref="Owner"/>) に付随する値。<see cref="Related"/> のハッシュと LastWriteTime は、候補と同じく再照合に使う。</summary>
internal sealed record RegistryScanCompanion(
    RegistryScanItem Owner,
    RegistryCompanionKind Kind,
    RegistryScanItem Related,
    IReadOnlyList<string> RemovedSlots);

/// <summary>走査が付ける理由の符号の名前 (<see cref="CleanupMessage"/>)。辞書への追加は UI 担当が行う。</summary>
internal static class RegistryScanReasons
{
    public const string MissingFile = "RegistryScanMissingFile";
    public const string ProgIdMissing = "RegistryScanProgIdMissing";
    public const string UnknownPath = "RegistryScanUnknownPath";
    public const string Unresolved = "RegistryScanUnresolvedCommand";
    public const string UnsupportedType = "RegistryScanUnsupportedType";
    public const string ProtectedPath = "RegistryScanProtectedPath";
    public const string TrustedInstaller = "RegistryScanTrustedInstaller";
    public const string AccessDenied = "RegistryScanAccessDenied";
    public const string Delegated = "RegistryScanDelegatedCommand";
    public const string NoCommand = "RegistryScanNoCommand";
    public const string EmptyValue = "RegistryScanEmptyValue";
    public const string NoRegistration = "RegistryScanNoRegistration";
    public const string MsiIndeterminate = "RegistryScanMsiIndeterminate";
    public const string ProgIdUnverifiable = "RegistryScanProgIdUnverifiable";
    public const string SubtreeTooLarge = "RegistryScanSubtreeTooLarge";
    public const string UserScopeUnavailable = "RegistryScanUserScopeUnavailable";
}

/// <summary>候補のハッシュ。後段の再照合も同じ関数を使う。</summary>
internal static class RegistryScanHashing
{
    private const int MaxDepth = 32;
    private const int MaxKeys = 5000;

    /// <summary>値の型 (4 バイト、リトルエンディアン) とバイト列の SHA-256。</summary>
    public static string OfValue(RegistryRawValue value)
    {
        var buffer = new byte[4 + value.Data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value.Type);
        value.Data.CopyTo(buffer, 4);
        return RegistryContentHash.Of(buffer);
    }

    /// <summary>
    /// サブツリーの正規化ハッシュ。値と子を名前 (大文字小文字無視) の順に、名前・型・バイト列を長さ付きで並べる。
    /// 深さ・キー数の上限を超える、または途中で消えたときは null。
    /// </summary>
    public static string? OfSubtree(IRegistryCleanerKey key, RegistryCleanView view)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        return Append(hash, key, view, 0, ref count) ? Convert.ToHexStringLower(hash.GetHashAndReset()) : null;
    }

    private static bool Append(IncrementalHash hash, IRegistryCleanerKey key, RegistryCleanView view, int depth, ref int count)
    {
        if (depth > MaxDepth || ++count > MaxKeys)
        {
            return false;
        }

        var values = Sorted(key.GetValueNames());
        WriteInt(hash, values.Count);
        foreach (var name in values)
        {
            if (key.GetValue(name) is not { } raw)
            {
                return false;
            }

            WriteText(hash, name);
            WriteInt(hash, unchecked((int)raw.Type));
            WriteInt(hash, raw.Data.Length);
            hash.AppendData(raw.Data);
        }

        var children = Sorted(key.GetSubKeyNames());
        WriteInt(hash, children.Count);
        foreach (var name in children)
        {
            using var child = key.OpenChild(name, RegistryCleanAccess.Read, view);
            if (child is null)
            {
                return false;
            }

            WriteText(hash, name);
            if (!Append(hash, child, view, depth + 1, ref count))
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> Sorted(IReadOnlyList<string> names) =>
        [.. names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ThenBy(name => name, StringComparer.Ordinal)];

    private static void WriteInt(IncrementalHash hash, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        hash.AppendData(buffer);
    }

    private static void WriteText(IncrementalHash hash, string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text);
        WriteInt(hash, bytes.Length);
        hash.AppendData(bytes);
    }
}

/// <summary>走査する根 1 つ。<see cref="ApprovedKey"/> は StartupApproved を探す根 (HKLM の 32 ビット表示は 64 ビット側の HKLM)。</summary>
internal sealed record RegistryScanRoot(
    RegistryCleanHive Hive,
    RegistryCleanView View,
    RegistryPathOrigin Origin,
    IRegistryCleanerKey Key,
    IRegistryCleanerKey ApprovedKey)
{
    /// <summary>ソフトウェアキーの名前 (HKLM は <c>SOFTWARE</c>、HKCU は <c>Software</c>)。</summary>
    public string Software => Hive == RegistryCleanHive.LocalMachine ? "SOFTWARE" : "Software";

    /// <summary>
    /// この根の下の Classes の相対パス。HKLM は <c>SOFTWARE\Classes\…</c>、利用者の Classes (<c>&lt;SID&gt;_Classes</c>) はルートそのもの。
    /// HKCU (<c>HKEY_USERS\&lt;SID&gt;</c>) の <c>Software\Classes</c> はリンクのため使わない (<see cref="RegistryScanSession.ClassesRoots"/> に入れない)。
    /// </summary>
    public string Classes(string subPath) => Hive == RegistryCleanHive.UserClasses ? subPath : $@"{Software}\Classes\{subPath}";

    /// <summary>Classes そのもののパス (利用者の Classes は空文字 = ルート)。</summary>
    public string ClassesRootPath => Hive == RegistryCleanHive.UserClasses ? string.Empty : $@"{Software}\Classes";
}

/// <summary>1 回の走査の状態。開いたキーの所有は走査器が持つ。</summary>
internal sealed class RegistryScanSession(
    IRegistryCleanerKey machine64,
    IRegistryCleanerKey? machine32,
    IRegistryCleanerKey? user,
    IRegistryCleanerKey? userClasses,
    CancellationToken token)
{
    private readonly HashSet<string> _containers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _items = new(StringComparer.OrdinalIgnoreCase);

    public CancellationToken Token { get; } = token;

    public List<RegistryScanItem> Items { get; } = [];

    public List<RegistryRetainedEntry> Retained { get; } = [];

    public List<RegistryScanCompanion> Companions { get; } = [];

    internal Dictionary<string, RegistryJudgement> ProgIdCache { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal Dictionary<string, RegistryJudgement> AppCache { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>利用者のハイブとその Classes の両方を読める (無いことを確定するには両方が要る)。</summary>
    public bool UserAvailable => user is not null && userClasses is not null;

    /// <summary>走査する根を 64 ビット HKLM、32 ビット HKLM、HKCU の順に返す。</summary>
    public IReadOnlyList<RegistryScanRoot> Roots { get; } = BuildRoots(machine64, machine32, user);

    /// <summary>
    /// Classes を探す根を 64 ビット HKLM、32 ビット HKLM、利用者の Classes (<c>&lt;SID&gt;_Classes</c>) の順に返す。
    /// 利用者の Classes は HKCU のリンク (<c>Software\Classes</c>) を辿らず、実体のハイブを直接開いたもの。
    /// </summary>
    public IReadOnlyList<RegistryScanRoot> ClassesRoots { get; } = BuildClassesRoots(machine64, machine32, user is null ? null : userClasses);

    /// <summary>同じ物理キー (カーネル名) を二重に走査しない。初めてなら true。</summary>
    public bool EnterContainer(string kernelName) => _containers.Add(kernelName);

    /// <summary>同じ物理キー・値の候補を二重に登録しない。</summary>
    public bool TryAddItem(RegistryScanItem item)
    {
        if (!_items.Add(item.KernelName + "\0" + (item.ValueName is null ? "\u0001" : "\u0002" + item.ValueName)))
        {
            return false;
        }

        Items.Add(item);
        return true;
    }

    private static List<RegistryScanRoot> BuildRoots(IRegistryCleanerKey machine64, IRegistryCleanerKey? machine32, IRegistryCleanerKey? user)
    {
        var roots = new List<RegistryScanRoot>
        {
            new(RegistryCleanHive.LocalMachine, RegistryCleanView.Registry64, RegistryPathOrigin.Machine, machine64, machine64),
        };
        if (machine32 is not null)
        {
            roots.Add(new(RegistryCleanHive.LocalMachine, RegistryCleanView.Registry32, RegistryPathOrigin.MachineWow64, machine32, machine64));
        }

        if (user is not null)
        {
            roots.Add(new(RegistryCleanHive.CurrentUser, RegistryCleanView.Registry64, RegistryPathOrigin.User, user, user));
        }

        return roots;
    }

    private static List<RegistryScanRoot> BuildClassesRoots(IRegistryCleanerKey machine64, IRegistryCleanerKey? machine32, IRegistryCleanerKey? userClasses)
    {
        var roots = BuildRoots(machine64, machine32, null);
        if (userClasses is not null)
        {
            roots.Add(new(RegistryCleanHive.UserClasses, RegistryCleanView.Registry64, RegistryPathOrigin.User, userClasses, userClasses));
        }

        return roots;
    }
}

internal enum RegistryJudgementKind
{
    /// <summary>存在する (または設計上の除外)。候補にも保持にもしない。</summary>
    Alive,

    /// <summary>欠損を確定した。候補にする。</summary>
    Missing,

    /// <summary>確定できない。保持する。</summary>
    Unknown,

    /// <summary>OS 所有の場所。保持する。</summary>
    Protected,
}

internal readonly record struct RegistryJudgement(RegistryJudgementKind Kind, string Reason)
{
    public static RegistryJudgement Alive { get; } = new(RegistryJudgementKind.Alive, string.Empty);

    public static RegistryJudgement Unknown(string name, params object?[] args) =>
        new(RegistryJudgementKind.Unknown, CleanupMessage.Of(name, args));
}

/// <summary>
/// カテゴリーごとの判定。読み取りだけを行い (<see cref="RegistryCleanAccess.Read"/>)、実レジストリは
/// <see cref="IRegistryCleanerKey"/>、ファイルは <see cref="IRegistryFileProbe"/> を通す。
/// 欠損を確定できたものだけ候補にし、不明・保護は保持一覧へ入れる。
/// </summary>
internal sealed partial class RegistryCategoryDetectors(
    IRegistryPathResolver resolver,
    IRegistryFileProbe probe,
    RegistryOwnedLocations locations,
    IMsiProductState msi,
    Func<IReadOnlyList<string>> searchDirectories)
{
    /// <summary>TrustedInstaller の SID。所有するキーは保護する。</summary>
    internal const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    private const string CurrentVersion = @"Microsoft\Windows\CurrentVersion";

    private const uint RegSz = 1;
    private const uint RegExpandSz = 2;
    private const uint RegDword = 4;

    private readonly IRegistryPathResolver _resolver = resolver;
    private readonly IRegistryFileProbe _probe = probe;
    private readonly RegistryOwnedLocations _locations = locations;
    private readonly IMsiProductState _msi = msi;
    private readonly Func<IReadOnlyList<string>> _searchDirectories = searchDirectories;

    /// <summary>実環境の既定の部品で作る。</summary>
    public static RegistryCategoryDetectors CreateDefault() => new(
        RegistryPathResolver.FromEnvironment(),
        new RegistryFileProbe(),
        RegistryOwnedLocations.FromEnvironment(),
        new MsiProductStateQuery(),
        DefaultSearchDirectories);

    /// <summary>OpenWithList の名前が実在する場所の反証に使う標準の検索先 (Windows・System32・PATH の完全修飾パスだけ)。</summary>
    public static IReadOnlyList<string> DefaultSearchDirectories()
    {
        var directories = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.SystemDirectory };
        directories.AddRange(Environment.GetEnvironmentVariable("PATH")?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? []);
        return [.. directories
            .Select(directory => directory.Trim().Trim('"'))
            .Where(directory => directory.Length > 0 && Path.IsPathFullyQualified(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>1 つのカテゴリーを、そのカテゴリーが対象にするすべての根で走査する。</summary>
    public void Scan(RegistryCleanCategory category, RegistryScanSession session)
    {
        switch (category)
        {
            case RegistryCleanCategory.AppPaths: ScanAppPaths(session); break;
            case RegistryCleanCategory.Startup: ScanStartup(session); break;
            case RegistryCleanCategory.Fonts: ScanFonts(session); break;
            case RegistryCleanCategory.FileAssociations: ScanFileAssociations(session); break;
            case RegistryCleanCategory.Uninstall: ScanUninstall(session); break;
            case RegistryCleanCategory.SharedDlls: ScanSharedDlls(session); break;
            default: throw new ArgumentOutOfRangeException(nameof(category));
        }
    }

    // ---- 共通の判定 ----

    private enum TextKind { Absent, WrongType, Text }

    private static (TextKind Kind, string Text) ReadText(RegistryRawValue? raw)
    {
        if (raw is null)
        {
            return (TextKind.Absent, string.Empty);
        }

        if (raw.Type is not (RegSz or RegExpandSz))
        {
            return (TextKind.WrongType, string.Empty);
        }

        var text = Encoding.Unicode.GetString(raw.Data);
        var end = text.IndexOf('\0');
        return (TextKind.Text, (end >= 0 ? text[..end] : text).Trim());
    }

    private static bool IsDwordOne(RegistryRawValue? raw) =>
        raw is { Type: RegDword, Data.Length: 4 } && BinaryPrimitives.ReadUInt32LittleEndian(raw.Data) == 1;

    /// <summary>
    /// コマンドラインを実行ファイルの候補へ解釈する。<c>%1</c> <c>%*</c> <c>%~dp0</c> など引数の置換記号は、
    /// 環境変数の閉じていない <c>%</c> として解釈を失敗させるため、先に切り落とす。
    /// </summary>
    private IReadOnlyList<string> ResolveCommandLine(string command, RegistryPathOrigin origin) =>
        _resolver.ResolveCommand(StripArgumentPlaceholders(command), origin);

    internal static string StripArgumentPlaceholders(string command)
    {
        for (var i = 0; i + 1 < command.Length; i++)
        {
            if (command[i] == '%' && (char.IsAsciiDigit(command[i + 1]) || command[i + 1] is '*' or '~'))
            {
                return command[..i];
            }
        }

        return command;
    }

    /// <summary>文字列値のパス・コマンドを判定する。型違い・空・解釈不能は Unknown。</summary>
    private RegistryJudgement JudgeValue(RegistryRawValue? raw, Func<string, IReadOnlyList<string>> resolve, bool allowWindowsFonts = false)
    {
        var (kind, text) = ReadText(raw);
        if (kind == TextKind.WrongType)
        {
            return RegistryJudgement.Unknown(RegistryScanReasons.UnsupportedType, raw!.Type);
        }

        return text.Length == 0
            ? RegistryJudgement.Unknown(RegistryScanReasons.EmptyValue)
            : JudgePaths(resolve(text), allowWindowsFonts);
    }

    /// <summary>候補パスの判定。存在 → Alive、保護場所 → Protected、全候補が欠損確定 → Missing、他は Unknown。</summary>
    private RegistryJudgement JudgePaths(IReadOnlyList<string> candidates, bool allowWindowsFonts = false)
    {
        if (candidates.Count == 0)
        {
            return RegistryJudgement.Unknown(RegistryScanReasons.Unresolved);
        }

        var state = _probe.ProbeAll(candidates);
        if (state == RegistryFileState.Exists)
        {
            return RegistryJudgement.Alive;
        }

        var protectedPath = candidates.FirstOrDefault(candidate => _locations.IsProtected(candidate, allowWindowsFonts));
        if (protectedPath is not null)
        {
            return new(RegistryJudgementKind.Protected, CleanupMessage.Of(RegistryScanReasons.ProtectedPath, protectedPath));
        }

        return state == RegistryFileState.ConfirmedMissing
            ? new(RegistryJudgementKind.Missing, CleanupMessage.Of(RegistryScanReasons.MissingFile, candidates[0]))
            : RegistryJudgement.Unknown(RegistryScanReasons.UnknownPath, candidates[0]);
    }

    /// <summary>複数の根拠を 1 つにまとめる。1 つでも Alive なら Alive、次に Protected、Unknown、全部 Missing のときだけ Missing。</summary>
    private static RegistryJudgement Combine(IReadOnlyList<RegistryJudgement> judgements)
    {
        foreach (var kind in new[] { RegistryJudgementKind.Alive, RegistryJudgementKind.Protected, RegistryJudgementKind.Unknown })
        {
            foreach (var judgement in judgements)
            {
                if (judgement.Kind == kind)
                {
                    return judgement;
                }
            }
        }

        return judgements.Count == 0 ? RegistryJudgement.Unknown(RegistryScanReasons.NoCommand) : judgements[0];
    }

    // ---- キーの走査・登録 ----

    /// <summary>
    /// <paramref name="path"/> を 1 階層ずつ開く。空文字なら根そのものを、閉じても根を閉じない形で返す
    /// (利用者の Classes のハイブはルートが Classes そのもののため)。
    /// </summary>
    private static IRegistryCleanerKey? OpenPath(IRegistryCleanerKey start, string path, RegistryCleanView view)
    {
        if (path.Length == 0)
        {
            return new BorrowedKey(start);
        }

        IRegistryCleanerKey? current = null;
        try
        {
            foreach (var part in path.Split('\\'))
            {
                var next = (current ?? start).OpenChild(part, RegistryCleanAccess.Read, view);
                current?.Dispose();
                current = next;
                if (next is null)
                {
                    return null;
                }
            }

            return current;
        }
        catch
        {
            current?.Dispose();
            throw;
        }
    }

    private static void Retain(
        RegistryScanSession session, RegistryCleanCategory category, RegistryRetentionKind kind, string keyPath, string? valueName, string reason) =>
        session.Retained.Add(new RegistryRetainedEntry(category, kind, keyPath, valueName, reason));

    private static void Retain(
        RegistryScanSession session, RegistryCleanCategory category, RegistryJudgement judgement, string keyPath, string? valueName) =>
        Retain(
            session, category,
            judgement.Kind == RegistryJudgementKind.Protected ? RegistryRetentionKind.Protected : RegistryRetentionKind.Unknown,
            keyPath, valueName, judgement.Reason);

    /// <summary>
    /// 読み取りの失敗 (アクセス拒否・リンク・I/O) はその対象を Unknown として保持し、走査は続ける。
    /// キャンセルと想定外の例外は通す。
    /// </summary>
    private static void Guard(RegistryScanSession session, RegistryCleanCategory category, string keyPath, string? valueName, Action body)
    {
        try
        {
            body();
        }
        catch (RegistryCleanLinkException ex)
        {
            Retain(session, category, RegistryRetentionKind.Unknown, keyPath, valueName, ex.Message);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException or Win32Exception)
        {
            Retain(session, category, RegistryRetentionKind.Unknown, keyPath, valueName, CleanupMessage.Of(RegistryScanReasons.AccessDenied));
        }
    }

    private static bool IsTrustedInstallerOwned(IRegistryCleanerKey key) =>
        string.Equals(key.OwnerSid(), TrustedInstallerSid, StringComparison.OrdinalIgnoreCase);

    /// <summary>サブキーの名前一覧。開けなければ保持へ入れて空を返す。</summary>
    private static IReadOnlyList<string> ListSubKeys(RegistryScanSession session, RegistryCleanCategory category, RegistryScanRoot root, string path)
    {
        IReadOnlyList<string> names = [];
        Guard(session, category, path, null, () =>
        {
            using var key = OpenPath(root.Key, path, root.View);
            if (key is not null)
            {
                names = key.GetSubKeyNames();
            }
        });
        return names;
    }

    /// <summary>サブキーを 1 つずつ判定し、欠損を確定したものをキー単位の候補にする。</summary>
    private void ScanKeyContainer(
        RegistryScanSession session,
        RegistryCleanCategory category,
        RegistryScanRoot root,
        string path,
        Func<IRegistryCleanerKey, string, RegistryJudgement> judge)
    {
        Guard(session, category, path, null, () =>
        {
            using var container = OpenPath(root.Key, path, root.View);
            if (container is null || !session.EnterContainer(container.KernelName()))
            {
                return;
            }

            foreach (var name in container.GetSubKeyNames())
            {
                session.Token.ThrowIfCancellationRequested();
                var keyPath = path + "\\" + name;
                Guard(session, category, keyPath, null, () =>
                {
                    using var child = container.OpenChild(name, RegistryCleanAccess.Read, root.View);
                    if (child is null)
                    {
                        return;
                    }

                    var judgement = judge(child, name);
                    if (judgement.Kind == RegistryJudgementKind.Missing)
                    {
                        CommitKey(session, category, root, keyPath, child, judgement.Reason);
                    }
                    else if (judgement.Kind != RegistryJudgementKind.Alive)
                    {
                        Retain(session, category, judgement, keyPath, null);
                    }
                });
            }
        });
    }

    private static void CommitKey(
        RegistryScanSession session, RegistryCleanCategory category, RegistryScanRoot root, string keyPath, IRegistryCleanerKey key, string reason)
    {
        if (IsTrustedInstallerOwned(key))
        {
            Retain(session, category, RegistryRetentionKind.Protected, keyPath, null, CleanupMessage.Of(RegistryScanReasons.TrustedInstaller));
            return;
        }

        var hash = RegistryScanHashing.OfSubtree(key, root.View);
        if (hash is null)
        {
            Retain(session, category, RegistryRetentionKind.Unknown, keyPath, null, CleanupMessage.Of(RegistryScanReasons.SubtreeTooLarge));
            return;
        }

        session.TryAddItem(new RegistryScanItem(
            category, root.Hive, root.View, keyPath, null, null, hash, key.QueryInfo().LastWriteTimeUtc, key.KernelName(), reason));
    }

    /// <summary>
    /// 値を 1 つずつ判定し、欠損を確定したものを値単位の候補にする。<paramref name="prepareCompanion"/> は候補にする前に呼び、
    /// 付随する値 (StartupApproved・MRUList) を読む。読めなければその値は保持へ回り、候補にならない。
    /// </summary>
    private void ScanValueContainer(
        RegistryScanSession session,
        RegistryCleanCategory category,
        RegistryScanRoot root,
        string path,
        Func<IRegistryCleanerKey, string, RegistryRawValue, RegistryJudgement> judge,
        Func<string, bool>? skipName = null,
        Func<IRegistryCleanerKey, string, Func<RegistryScanItem, RegistryScanCompanion>?>? prepareCompanion = null)
    {
        Guard(session, category, path, null, () =>
        {
            using var container = OpenPath(root.Key, path, root.View);
            if (container is null || !session.EnterContainer(container.KernelName()))
            {
                return;
            }

            foreach (var name in container.GetValueNames())
            {
                session.Token.ThrowIfCancellationRequested();
                if (skipName?.Invoke(name) == true)
                {
                    continue;
                }

                Guard(session, category, path, name, () =>
                    JudgeAndCommitValue(session, category, root, path, container, name, judge, prepareCompanion));
            }
        });
    }

    private static void JudgeAndCommitValue(
        RegistryScanSession session,
        RegistryCleanCategory category,
        RegistryScanRoot root,
        string path,
        IRegistryCleanerKey container,
        string name,
        Func<IRegistryCleanerKey, string, RegistryRawValue, RegistryJudgement> judge,
        Func<IRegistryCleanerKey, string, Func<RegistryScanItem, RegistryScanCompanion>?>? prepareCompanion)
    {
        if (container.GetValue(name) is not { } raw)
        {
            return;
        }

        var judgement = judge(container, name, raw);
        if (judgement.Kind != RegistryJudgementKind.Missing)
        {
            if (judgement.Kind != RegistryJudgementKind.Alive)
            {
                Retain(session, category, judgement, path, name);
            }

            return;
        }

        if (IsTrustedInstallerOwned(container))
        {
            Retain(session, category, RegistryRetentionKind.Protected, path, name, CleanupMessage.Of(RegistryScanReasons.TrustedInstaller));
            return;
        }

        var companion = prepareCompanion?.Invoke(container, name);
        var item = new RegistryScanItem(
            category, root.Hive, root.View, path, name, raw.Type, RegistryScanHashing.OfValue(raw),
            container.QueryInfo().LastWriteTimeUtc, container.KernelName(), judgement.Reason);
        if (session.TryAddItem(item) && companion is not null)
        {
            session.Companions.Add(companion(item));
        }
    }

    /// <summary>キーの中の値 1 つを、付随する値として記録する形にする。</summary>
    private static RegistryScanItem RelatedValue(
        RegistryCleanCategory category, RegistryScanRoot root, RegistryCleanView view, string path, IRegistryCleanerKey key, string name, RegistryRawValue raw, string reason) =>
        new(category, root.Hive, view, path, name, raw.Type, RegistryScanHashing.OfValue(raw), key.QueryInfo().LastWriteTimeUtc, key.KernelName(), reason);

    // ---- App Paths ----

    /// <summary>
    /// App Paths は Windows 7 以降 WOW64 で共有のキーで、32 ビット表示 (WOW6432Node) は 64 ビット側へのリンク
    /// (https://learn.microsoft.com/en-us/windows/win32/winprog64/shared-registry-keys)。64 ビット側だけを走査する。
    /// </summary>
    private static IEnumerable<RegistryScanRoot> AppPathRoots(RegistryScanSession session) =>
        session.Roots.Where(root => root.View == RegistryCleanView.Registry64);

    private void ScanAppPaths(RegistryScanSession session)
    {
        foreach (var root in AppPathRoots(session))
        {
            ScanKeyContainer(
                session, RegistryCleanCategory.AppPaths, root, $@"{root.Software}\{CurrentVersion}\App Paths",
                (key, _) => JudgeValue(key.GetValue(string.Empty), value => _resolver.ResolveAppPath(value, root.Origin)));
        }
    }

    // ---- フォント ----

    private void ScanFonts(RegistryScanSession session)
    {
        foreach (var root in session.Roots.Where(root => root.View == RegistryCleanView.Registry64))
        {
            ScanValueContainer(
                session, RegistryCleanCategory.Fonts, root, $@"{root.Software}\Microsoft\Windows NT\CurrentVersion\Fonts",
                (_, _, raw) => JudgeValue(raw, value => _resolver.ResolveFontPath(value, root.Origin), allowWindowsFonts: true));
        }
    }

    // ---- 共有 DLL ----

    private void ScanSharedDlls(RegistryScanSession session)
    {
        foreach (var root in session.Roots.Where(root => root.Hive == RegistryCleanHive.LocalMachine))
        {
            ScanValueContainer(
                session, RegistryCleanCategory.SharedDlls, root, $@"{root.Software}\{CurrentVersion}\SharedDLLs",
                (_, name, raw) => raw.Type == RegDword
                    ? JudgePaths(_resolver.ExpandPath(name, root.Origin))
                    : RegistryJudgement.Unknown(RegistryScanReasons.UnsupportedType, raw.Type));
        }
    }

    // ---- スタートアップ ----

    private void ScanStartup(RegistryScanSession session)
    {
        foreach (var root in session.Roots)
        {
            var approvedPath = $@"{root.Software}\{CurrentVersion}\Explorer\StartupApproved\{(root.View == RegistryCleanView.Registry32 ? "Run32" : "Run")}";
            ScanValueContainer(
                session, RegistryCleanCategory.Startup, root, $@"{root.Software}\{CurrentVersion}\Run",
                (_, _, raw) => JudgeValue(raw, value => ResolveCommandLine(value, root.Origin)),
                skipName: name => name.Length == 0,
                prepareCompanion: (_, name) => PrepareApproved(root, approvedPath, name));
        }
    }

    /// <summary>対応する StartupApproved の値があれば、候補に付ける付随値を返す。</summary>
    private static Func<RegistryScanItem, RegistryScanCompanion>? PrepareApproved(RegistryScanRoot root, string approvedPath, string name)
    {
        using var approved = OpenPath(root.ApprovedKey, approvedPath, RegistryCleanView.Registry64);
        if (approved?.GetValue(name) is not { } raw)
        {
            return null;
        }

        // using のキーはここで閉じるため、必要な情報は先に読んでおく。
        var related = RelatedValue(RegistryCleanCategory.Startup, root, RegistryCleanView.Registry64, approvedPath, approved, name, raw, string.Empty);
        return owner => new RegistryScanCompanion(
            owner, RegistryCompanionKind.StartupApprovedValue, related with { Reason = owner.Reason }, []);
    }

    /// <summary>根のキーを借りて渡す包み。閉じても根は閉じない (根の所有は走査器)。</summary>
    private sealed class BorrowedKey(IRegistryCleanerKey inner) : IRegistryCleanerKey
    {
        public IRegistryCleanerKey? OpenChild(string name, RegistryCleanAccess access, RegistryCleanView view) => inner.OpenChild(name, access, view);

        public IRegistryCleanerKey CreateChild(string name, RegistryCleanView view) => inner.CreateChild(name, view);

        public RegistryRawValue? GetValue(string name) => inner.GetValue(name);

        public void SetValue(string name, RegistryRawValue value) => inner.SetValue(name, value);

        public IReadOnlyList<string> GetValueNames() => inner.GetValueNames();

        public IReadOnlyList<string> GetSubKeyNames() => inner.GetSubKeyNames();

        public void DeleteValue(string name) => inner.DeleteValue(name);

        public void DeleteSelf() => throw new InvalidOperationException("ハイブのルートは削除しない");

        public RegistryKeyInfo QueryInfo() => inner.QueryInfo();

        public string KernelName() => inner.KernelName();

        public string OwnerSid() => inner.OwnerSid();

        public void Dispose()
        {
        }
    }
}
