using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>キーを開くときに要求する権限。</summary>
internal enum CleanupRegistryAccess
{
    /// <summary>値・サブキーの列挙だけ (走査用)。</summary>
    Read,
    /// <summary>値の削除。</summary>
    ModifyValues,
    /// <summary>値の削除とキー自体の削除 (サブキーを消すとき)。</summary>
    DeleteTree,
}

/// <summary>開こうとしたキーがレジストリのシンボリックリンクだった。リンク自身もリンク先も触らない。</summary>
internal sealed class CleanupRegistryLinkException(string name)
    : IOException(CleanupMessage.Of("RegistrySymlink", name));

/// <summary>
/// 開いたキー 1 つ分のハンドル。各階層は REG_OPTION_OPEN_LINK 相当で 1 階層ずつ開き、リンクなら
/// <see cref="CleanupRegistryLinkException"/> を投げる。値・キーの削除は、そのキーを開いた同じハンドルで行う。
/// テストでは偽の実装に差し替える。
/// </summary>
internal interface ICleanupRegistryKey : IDisposable
{
    /// <summary>直下の子を 1 階層だけ開く。無ければ null。名前に <c>\</c> を含めてはならない。</summary>
    ICleanupRegistryKey? OpenChild(string name, CleanupRegistryAccess access);

    IReadOnlyList<string> GetSubKeyNames();

    IReadOnlyList<string> GetValueNames();

    /// <summary>値を削除する (既に無ければ何もしない)。</summary>
    void DeleteValue(string name);

    /// <summary>このハンドルのキー自体を削除する。サブキーが残っていれば失敗する。</summary>
    void DeleteSelf();
}

internal enum CleanupRegistryStatus
{
    /// <summary>対象をすべて処理した (走査では全件を数えた)。</summary>
    Done,
    /// <summary>キーが存在しない (未インストール。異常ではない)。</summary>
    Missing,
    /// <summary>パスの形・リンクにより対象外として拒否した。何も変更していない。</summary>
    Rejected,
    /// <summary>一部を処理できなかった (リンク・権限不足・使用中など)。</summary>
    Partial,
    /// <summary>キーを開けず何も処理できなかった。</summary>
    Failed,
}

/// <param name="Values">削除した (走査では該当する) 値の数。</param>
/// <param name="Keys">削除した (走査では該当する) サブキーの数。</param>
/// <param name="Skipped">リンク・権限不足などで残した項目数。</param>
/// <param name="Examples">残した理由の代表例。</param>
internal sealed record CleanupRegistryResult(CleanupRegistryStatus Status, long Values, long Keys, long Skipped, IReadOnlyList<string> Examples);

/// <summary>
/// <c>registryValues</c> / <c>registryTree</c> の走査と削除。走査は件数を数えるだけで何も変更しない。
/// 基点 (<paramref name="userRoot"/>) は呼び出し側が渡し、対話利用者の HKCU ハイブだけを指すようにする
/// (<see cref="NativeRegistryKey.OpenUserHive"/>)。キー自体は残し、中の値とサブキーだけを消す。
/// </summary>
internal sealed class CleanupRegistryEditor(ICleanupRegistryKey userRoot)
{
    private const int MaxDepth = 64;
    private const int MaxExamples = 8;

    /// <summary>
    /// 件数を数える。<paramref name="values"/> が null ならキー配下の全値・全サブキー (registryTree)、
    /// 指定があれば直下の該当する値だけ (registryValues)。
    /// </summary>
    public CleanupRegistryResult Scan(string key, IReadOnlyList<string>? values, CancellationToken ct) =>
        Execute(key, values, CleanupRegistryAccess.Read, write: false, tree: values is null, ct);

    /// <summary>キー直下の該当する値を、キーを開いた同じハンドルで削除する (registryValues)。</summary>
    public CleanupRegistryResult DeleteValues(string key, IReadOnlyList<string> values, CancellationToken ct) =>
        Execute(key, values, CleanupRegistryAccess.ModifyValues, write: true, tree: false, ct);

    /// <summary>キー配下の全値・全サブキーを削除する。キー自体は残す (registryTree)。</summary>
    public CleanupRegistryResult DeleteTree(string key, CancellationToken ct) =>
        Execute(key, null, CleanupRegistryAccess.DeleteTree, write: true, tree: true, ct);

    private CleanupRegistryResult Execute(string key, IReadOnlyList<string>? values, CleanupRegistryAccess access, bool write, bool tree, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var segments = ParseKey(key);
        if (segments is null) return Empty(CleanupRegistryStatus.Rejected, "キーは HKCU\\Software\\ の下の2階層以上にしてください");
        if (!tree && !AreValidPatterns(values)) return Empty(CleanupRegistryStatus.Rejected, "値名の指定が不正です (ワイルドカードは末尾の * だけ)");

        var pinned = new List<ICleanupRegistryKey>();
        try
        {
            var target = OpenChain(segments, access, pinned, out var early);
            if (target is null) return early!;
            var tally = new Tally();
            if (tree)
            {
                if (write) ClearKey(target, 0, tally, ct);
                else CountKey(target, 0, tally, ct);
            }
            else
            {
                HandleValues(target, values!, write, tally, ct);
            }

            return tally.ToResult();
        }
        finally
        {
            for (var index = pinned.Count - 1; index >= 0; index--) pinned[index].Dispose();
        }
    }

    /// <summary>基点から 1 階層ずつ開く。途中がリンクなら拒否し、無ければ Missing にする。</summary>
    private ICleanupRegistryKey? OpenChain(string[] segments, CleanupRegistryAccess access, List<ICleanupRegistryKey> pinned, out CleanupRegistryResult? early)
    {
        early = null;
        var current = userRoot;
        for (var index = 0; index < segments.Length; index++)
        {
            var wanted = index == segments.Length - 1 ? access : CleanupRegistryAccess.Read;
            ICleanupRegistryKey? child;
            try { child = current.OpenChild(segments[index], wanted); }
            catch (CleanupRegistryLinkException ex) { early = Empty(CleanupRegistryStatus.Rejected, ex.Message); return null; }
            catch (Exception ex) when (IsRegistryFailure(ex)) { early = Empty(CleanupRegistryStatus.Failed, $"{segments[index]}: {ex.Message}"); return null; }
            if (child is null) { early = Empty(CleanupRegistryStatus.Missing, null); return null; }
            pinned.Add(child);
            current = child;
        }

        return current;
    }

    private static void HandleValues(ICleanupRegistryKey key, IReadOnlyList<string> patterns, bool write, Tally tally, CancellationToken ct)
    {
        IReadOnlyList<string> names;
        try { names = key.GetValueNames(); }
        catch (Exception ex) when (IsRegistryFailure(ex)) { tally.Skip("(values)", ex); return; }
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            if (!patterns.Any(pattern => MatchesValue(pattern, name))) continue;
            if (!write) { tally.Values++; continue; }
            DeleteOneValue(key, name, tally);
        }
    }

    private static void DeleteOneValue(ICleanupRegistryKey key, string name, Tally tally)
    {
        try { key.DeleteValue(name); tally.Values++; }
        catch (Exception ex) when (IsRegistryFailure(ex)) { tally.Skip(name, ex); }
    }

    /// <summary>キーの全値を消し、サブキーを深さ優先で消す。サブキー内に残りがあればそのキーは消さない。</summary>
    private static void ClearKey(ICleanupRegistryKey key, int depth, Tally tally, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth >= MaxDepth) { tally.Skip("(depth)", CleanupMessage.Of("RegistryDepthLimit")); return; }
        if (!TryList(key, tally, out var valueNames, out var subKeys)) return;
        foreach (var name in valueNames) DeleteOneValue(key, name, tally);
        foreach (var name in subKeys)
        {
            ICleanupRegistryKey? child;
            try { child = key.OpenChild(name, CleanupRegistryAccess.DeleteTree); }
            catch (Exception ex) when (IsRegistryFailure(ex)) { tally.Skip(name, ex); continue; }
            if (child is null) continue;
            using (child)
            {
                var skippedBefore = tally.Skipped;
                ClearKey(child, depth + 1, tally, ct);
                if (tally.Skipped != skippedBefore) continue;
                try { child.DeleteSelf(); tally.Keys++; }
                catch (Exception ex) when (IsRegistryFailure(ex)) { tally.Skip(name, ex); }
            }
        }
    }

    private static void CountKey(ICleanupRegistryKey key, int depth, Tally tally, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth >= MaxDepth) { tally.Skip("(depth)", CleanupMessage.Of("RegistryDepthLimit")); return; }
        if (!TryList(key, tally, out var valueNames, out var subKeys)) return;
        tally.Values += valueNames.Count;
        foreach (var name in subKeys)
        {
            ICleanupRegistryKey? child;
            try { child = key.OpenChild(name, CleanupRegistryAccess.Read); }
            catch (Exception ex) when (IsRegistryFailure(ex)) { tally.Skip(name, ex); continue; }
            if (child is null) continue;
            using (child)
            {
                CountKey(child, depth + 1, tally, ct);
                tally.Keys++;
            }
        }
    }

    private static bool TryList(ICleanupRegistryKey key, Tally tally, out IReadOnlyList<string> valueNames, out IReadOnlyList<string> subKeys)
    {
        try
        {
            valueNames = key.GetValueNames();
            subKeys = key.GetSubKeyNames();
            return true;
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            tally.Skip("(list)", ex);
            valueNames = subKeys = [];
            return false;
        }
    }

    /// <summary><c>HKCU\Software\X\Y</c> 以上の形だけを受け付け、基点からの相対の階層名を返す。</summary>
    internal static string[]? ParseKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var parts = key.Split('\\');
        var shapeOk = parts.Length >= 4 && parts[0].Equals("HKCU", StringComparison.OrdinalIgnoreCase) &&
                      parts[1].Equals("Software", StringComparison.OrdinalIgnoreCase) &&
                      parts.All(part => part.Length > 0 && part.Trim() == part && part.IndexOfAny(['\0', '*', '?']) < 0);
        return shapeOk ? parts[1..] : null;
    }

    private static bool AreValidPatterns(IReadOnlyList<string>? values) =>
        values is { Count: > 0 } && values.All(pattern =>
        {
            var star = pattern.IndexOf('*');
            return pattern.Length > 0 && pattern.IndexOf('\0') < 0 && pattern.IndexOf('?') < 0 &&
                   (star < 0 || (star == pattern.Length - 1 && pattern.Length > 1));
        });

    /// <summary>値名の一致。末尾 <c>*</c> は前方一致、それ以外は完全一致 (大文字小文字を区別しない)。</summary>
    internal static bool MatchesValue(string pattern, string name) =>
        pattern.EndsWith('*')
            ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : name.Equals(pattern, StringComparison.OrdinalIgnoreCase);

    private static bool IsRegistryFailure(Exception ex) =>
        ex is Win32Exception or IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException;

    private static CleanupRegistryResult Empty(CleanupRegistryStatus status, string? reason) =>
        new(status, 0, 0, 0, reason is null ? [] : [reason]);

    private sealed class Tally
    {
        private readonly List<string> _examples = [];

        public long Values { get; set; }

        public long Keys { get; set; }

        public long Skipped { get; private set; }

        public void Skip(string name, Exception ex) => Skip(name, ex.Message);

        public void Skip(string name, string reason)
        {
            Skipped++;
            if (_examples.Count < MaxExamples) _examples.Add($"{name}: {reason}".Replace('\r', ' ').Replace('\n', ' '));
        }

        public CleanupRegistryResult ToResult() =>
            new(Skipped == 0 ? CleanupRegistryStatus.Done : CleanupRegistryStatus.Partial, Values, Keys, Skipped, _examples);
    }
}

/// <summary>
/// 実レジストリのキー。各階層を <c>REG_OPTION_OPEN_LINK</c> で開いてリンクを拒否し、削除は開いたハンドルに対して行う
/// (パスを再解決しないので、途中でリンクに差し替えられても別のキーへ作用しない)。
/// </summary>
internal sealed class NativeRegistryKey : ICleanupRegistryKey
{
    private const uint OpenLink = 0x8;
    private const uint KeyRead = 0x20019;
    private const uint KeySetValue = 0x2;
    private const uint Delete = 0x10000;
    private const uint RegLink = 6;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorNoMoreItems = 259;
    private static readonly nint HkeyUsers = new(unchecked((int)0x80000003));
    private readonly SafeRegistryHandle _handle;

    private NativeRegistryKey(SafeRegistryHandle handle) => _handle = handle;

    /// <summary>
    /// <c>HKEY_USERS\{sid}</c> (指定した利用者の HKCU に相当) を基点として開く。
    /// 昇格利用者と対話利用者が別人のときは、対話利用者の SID を渡すのではなく利用者スコープの規則自体を無効にすること。
    /// </summary>
    public static NativeRegistryKey OpenUserHive(string sid)
    {
        var parsed = new SecurityIdentifier(sid);
        using var users = new SafeRegistryHandle(HkeyUsers, ownsHandle: false);
        var error = RegOpenKeyEx(users, parsed.Value, OpenLink, KeyRead, out var handle);
        if (error != 0) throw new Win32Exception(error);
        try
        {
            if (IsLink(handle)) throw new CleanupRegistryLinkException(parsed.Value);
            return new NativeRegistryKey(handle);
        }
        catch { handle.Dispose(); throw; }
    }

    public ICleanupRegistryKey? OpenChild(string name, CleanupRegistryAccess access)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('\\')) throw new ArgumentException("1 階層の名前だけを指定できます", nameof(name));
        var sam = access switch
        {
            CleanupRegistryAccess.Read => KeyRead,
            CleanupRegistryAccess.ModifyValues => KeyRead | KeySetValue,
            _ => KeyRead | KeySetValue | Delete,
        };
        var error = RegOpenKeyEx(_handle, name, OpenLink, sam, out var handle);
        if (error is ErrorFileNotFound or ErrorPathNotFound) return null;
        if (error != 0) throw new Win32Exception(error);
        try
        {
            if (IsLink(handle)) throw new CleanupRegistryLinkException(name);
            return new NativeRegistryKey(handle);
        }
        catch { handle.Dispose(); throw; }
    }

    public IReadOnlyList<string> GetSubKeyNames() => Enumerate(255 + 1, (index, buffer, size) => RegEnumKeyEx(_handle, index, buffer, ref size, 0, 0, 0, 0));

    public IReadOnlyList<string> GetValueNames() => Enumerate(16383 + 1, (index, buffer, size) => RegEnumValue(_handle, index, buffer, ref size, 0, 0, 0, 0));

    private static List<string> Enumerate(int capacity, EnumerateCall call)
    {
        var names = new List<string>();
        var buffer = new StringBuilder(capacity);
        for (uint index = 0; ; index++)
        {
            buffer.Clear();
            var error = call(index, buffer, (uint)capacity);
            if (error == ErrorNoMoreItems) return names;
            if (error != 0) throw new Win32Exception(error);
            names.Add(buffer.ToString());
        }
    }

    public void DeleteValue(string name)
    {
        var error = RegDeleteValue(_handle, name);
        if (error is not (0 or ErrorFileNotFound)) throw new Win32Exception(error);
    }

    public void DeleteSelf()
    {
        var status = NtDeleteKey(_handle);
        if (status < 0) throw new Win32Exception((int)RtlNtStatusToDosError(status));
    }

    public void Dispose() => _handle.Dispose();

    /// <summary>リンクのキーは REG_LINK 型の <c>SymbolicLinkValue</c> を持つ (OPEN_LINK で開いたハンドルで確認できる)。</summary>
    private static bool IsLink(SafeRegistryHandle handle) =>
        RegQueryValueEx(handle, "SymbolicLinkValue", 0, out var type, 0, 0) == 0 && type == RegLink;

    private delegate int EnumerateCall(uint index, StringBuilder buffer, uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyEx(SafeRegistryHandle key, string subKey, uint options, uint access, out SafeRegistryHandle result);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", EntryPoint = "RegEnumKeyExW", CharSet = CharSet.Unicode)]
    private static extern int RegEnumKeyEx(SafeRegistryHandle key, uint index, StringBuilder name, ref uint size, nint reserved, nint cls, nint classSize, nint lastWrite);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", EntryPoint = "RegEnumValueW", CharSet = CharSet.Unicode)]
    private static extern int RegEnumValue(SafeRegistryHandle key, uint index, StringBuilder name, ref uint size, nint reserved, nint type, nint data, nint dataSize);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueEx(SafeRegistryHandle key, string name, nint reserved, out uint type, nint data, nint dataSize);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", EntryPoint = "RegDeleteValueW", CharSet = CharSet.Unicode)]
    private static extern int RegDeleteValue(SafeRegistryHandle key, string name);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ntdll.dll")]
    private static extern int NtDeleteKey(SafeRegistryHandle key);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
