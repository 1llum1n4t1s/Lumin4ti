using System.ComponentModel;
using System.Text;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>開いた INI ファイル 1 つ分。読み書きはすべてこの同じハンドルで行う。テストでは失敗を注入する実装に差し替える。</summary>
internal interface ICleanupIniStream : IDisposable
{
    long Length { get; }

    int Read(Span<byte> buffer, long offset);

    void Write(ReadOnlySpan<byte> data, long offset);

    void SetLength(long length);

    void Flush();
}

/// <summary>INI を開く境界。リンク・再解析点は拒否し、無ければ <see cref="FileNotFoundException"/> 相当を投げる。</summary>
internal interface ICleanupIniOpener
{
    ICleanupIniStream Open(string path, bool writable);
}

internal enum CleanupIniStatus
{
    /// <summary>キーを削除して書き戻し、再読込で確認した。</summary>
    Changed,
    /// <summary>該当するキーが無く、ファイルは変更していない。</summary>
    NoChange,
    /// <summary>ファイルが存在しない。</summary>
    Missing,
    /// <summary>対象にできない (1MB 超・可逆変換できない・重複セクション・指定が不正)。何も変更していない。</summary>
    Skipped,
    /// <summary>開けない・書込みに失敗した。<see cref="CleanupIniResult.FileRestored"/> が false なら内容が不定。</summary>
    Failed,
}

/// <param name="RemovedKeys">削除した (走査では該当する) キーの行数。</param>
/// <param name="Reason">Skipped / Failed の理由。</param>
/// <param name="FileRestored">書込み失敗後に原文へ戻し、再読込で一致を確認できたか。</param>
internal sealed record CleanupIniResult(CleanupIniStatus Status, int RemovedKeys, string? Reason = null, bool FileRestored = false);

/// <summary>
/// <c>iniValues</c> の走査と削除。1MB 以下で UTF-8 / UTF-16 (BOM あり) / システム ANSI に可逆変換できるファイルだけを対象にし、
/// 指定したセクションのキーの行を消す。ファイルは書込み可で開いた同じハンドルで読み書きする (他者の書込みを拒否する共有)。
/// 書込みに失敗したら、メモリの原文を書き戻して再読込で照合する。呼び出し側は製品のプロセスが止まっていることを先に確認する。
/// </summary>
internal sealed class CleanupIniEditor(ICleanupIniOpener? opener = null)
{
    internal const int MaxBytes = 1 << 20;
    private readonly ICleanupIniOpener _opener = opener ?? NativeCleanupIniOpener.Instance;

    /// <summary>削除対象の行数を数える。ファイルは変更しない。</summary>
    public CleanupIniResult Scan(string path, IReadOnlyList<CleanupIniKey> keys, CancellationToken ct)
    {
        var result = Load(path, keys, writable: false, ct, out var session);
        using (session) return result ?? Plan(session!).Result;
    }

    /// <summary>該当するキーの行を削除する。</summary>
    public CleanupIniResult Remove(string path, IReadOnlyList<CleanupIniKey> keys, CancellationToken ct)
    {
        var result = Load(path, keys, writable: true, ct, out var session);
        if (result is not null) return result;
        using (session)
        {
            var plan = Plan(session!);
            if (plan.Updated is null) return plan.Result;
            ct.ThrowIfCancellationRequested();
            return Commit(session!, plan);
        }
    }

    private CleanupIniResult? Load(string path, IReadOnlyList<CleanupIniKey> keys, bool writable, CancellationToken ct, out Session? session)
    {
        session = null;
        ct.ThrowIfCancellationRequested();
        if (!KeysAreValid(keys)) return Skip("iniKeys の指定が不正です (ワイルドカードは末尾の * だけ)");
        if (!Path.IsPathFullyQualified(path)) return Skip("絶対パスだけを扱います");

        ICleanupIniStream stream;
        try { stream = _opener.Open(path, writable); }
        catch (Exception ex) when (IsMissing(ex)) { return new CleanupIniResult(CleanupIniStatus.Missing, 0); }
        catch (Exception ex) when (IsIoFailure(ex)) { return new CleanupIniResult(CleanupIniStatus.Failed, 0, ex.Message); }

        try
        {
            var original = ReadAll(stream, out var rejection);
            if (original is null) { stream.Dispose(); return rejection; }
            if (!IniText.TryDecode(original, out var encoding, out var preamble, out var text))
            {
                stream.Dispose();
                return Skip(CleanupMessage.Of("IniUnconvertible"));
            }

            session = new Session(stream, original, encoding, preamble, text, keys);
            return null;
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            stream.Dispose();
            return new CleanupIniResult(CleanupIniStatus.Failed, 0, ex.Message);
        }
    }

    private static byte[]? ReadAll(ICleanupIniStream stream, out CleanupIniResult? rejection)
    {
        rejection = null;
        var length = stream.Length;
        if (length > MaxBytes) { rejection = Skip(CleanupMessage.Of("IniTooLarge")); return null; }
        var bytes = new byte[(int)length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = stream.Read(bytes.AsSpan(offset), offset);
            if (read <= 0) throw new IOException(CleanupMessage.Of("IniChangedWhileReading"));
            offset += read;
        }

        if (stream.Length != length) throw new IOException(CleanupMessage.Of("IniLengthChangedWhileReading"));
        return bytes;
    }

    private static Planned Plan(Session session)
    {
        var edit = IniText.RemoveKeys(session.Text, session.Keys);
        if (edit.Ambiguous) return new Planned(Skip(CleanupMessage.Of("IniAmbiguousSection")), null);
        if (edit.Removed == 0) return new Planned(new CleanupIniResult(CleanupIniStatus.NoChange, 0), null);
        var updated = session.Preamble.Concat(session.Encoding.GetBytes(edit.Text)).ToArray();
        return new Planned(new CleanupIniResult(CleanupIniStatus.Changed, edit.Removed), updated);
    }

    /// <summary>書き戻して再読込で照合する。失敗したら原文を書き戻して再照合する (補償)。</summary>
    private static CleanupIniResult Commit(Session session, Planned plan)
    {
        var stream = session.Stream;
        var updated = plan.Updated!;
        try
        {
            if (stream.Length != session.Original.Length) throw new IOException(CleanupMessage.Of("IniLengthChangedBeforeWrite"));
            WriteAndVerify(stream, updated);
            return plan.Result;
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            var restored = TryRestore(stream, session.Original);
            var reason = CleanupMessage.Of(restored ? "IniRestored" : "IniNotRestored", ex.Message);
            return new CleanupIniResult(CleanupIniStatus.Failed, 0, reason, restored);
        }
    }

    private static bool TryRestore(ICleanupIniStream stream, byte[] original)
    {
        try
        {
            WriteAndVerify(stream, original);
            return true;
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return false;
        }
    }

    private static void WriteAndVerify(ICleanupIniStream stream, byte[] data)
    {
        stream.Write(data, 0);
        stream.SetLength(data.Length);
        stream.Flush();
        var actual = new byte[data.Length];
        var offset = 0;
        while (offset < actual.Length)
        {
            var read = stream.Read(actual.AsSpan(offset), offset);
            if (read <= 0) break;
            offset += read;
        }

        if (stream.Length != data.Length || offset != data.Length || !actual.AsSpan().SequenceEqual(data))
            throw new IOException(CleanupMessage.Of("IniVerifyMismatch"));
    }

    private static bool KeysAreValid(IReadOnlyList<CleanupIniKey> keys) =>
        keys is { Count: > 0 } && keys.All(key => IsPlainName(key.Section, allowWildcard: false) && IsPlainName(key.Key, allowWildcard: true));

    private static bool IsPlainName(string name, bool allowWildcard)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim() != name || name.IndexOfAny(['\0', '\r', '\n', '=', '?', '[', ']']) >= 0) return false;
        var star = name.IndexOf('*');
        return star < 0 || (allowWildcard && star == name.Length - 1 && name.Length > 1);
    }

    private static CleanupIniResult Skip(string reason) => new(CleanupIniStatus.Skipped, 0, reason);

    private static bool IsMissing(Exception ex) =>
        ex is FileNotFoundException or DirectoryNotFoundException || ex is Win32Exception { NativeErrorCode: 2 or 3 };

    private static bool IsIoFailure(Exception ex) =>
        ex is IOException or Win32Exception or UnauthorizedAccessException or ArgumentException;

    private sealed class Session(ICleanupIniStream stream, byte[] original, Encoding encoding, byte[] preamble, string text, IReadOnlyList<CleanupIniKey> keys) : IDisposable
    {
        public ICleanupIniStream Stream { get; } = stream;

        public byte[] Original { get; } = original;

        public Encoding Encoding { get; } = encoding;

        public byte[] Preamble { get; } = preamble;

        public string Text { get; } = text;

        public IReadOnlyList<CleanupIniKey> Keys { get; } = keys;

        public void Dispose() => Stream.Dispose();
    }

    private sealed record Planned(CleanupIniResult Result, byte[]? Updated);
}

/// <summary>INI のバイト列と文字列の可逆変換、および行単位のキー削除 (行末の形式は保つ)。</summary>
internal static class IniText
{
    internal readonly record struct Edit(string Text, int Removed, bool Ambiguous);

    /// <summary>
    /// BOM があればそれに従い、無ければ UTF-8、だめならシステム ANSI で厳密に復号する。
    /// 再符号化して元のバイト列と完全に一致し、NUL を含まない場合だけ成功にする。
    /// </summary>
    internal static bool TryDecode(byte[] bytes, out Encoding encoding, out byte[] preamble, out string text)
    {
        foreach (var candidate in Candidates(bytes))
        {
            try
            {
                var body = bytes.AsSpan(candidate.Preamble.Length);
                var decoded = candidate.Encoding.GetString(body);
                if (decoded.Contains('\0') || !candidate.Encoding.GetBytes(decoded).AsSpan().SequenceEqual(body)) continue;
                (encoding, preamble, text) = (candidate.Encoding, candidate.Preamble, decoded);
                return true;
            }
            catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException or EncoderFallbackException)
            {
                // 次の候補へ。
            }
        }

        (encoding, preamble, text) = (Encoding.UTF8, [], string.Empty);
        return false;
    }

    private static IEnumerable<(Encoding Encoding, byte[] Preamble)> Candidates(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { yield return (new UTF8Encoding(false, true), [0xEF, 0xBB, 0xBF]); yield break; }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { yield return (new UnicodeEncoding(false, false, true), [0xFF, 0xFE]); yield break; }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { yield return (new UnicodeEncoding(true, false, true), [0xFE, 0xFF]); yield break; }
        yield return (new UTF8Encoding(false, true), []);
        var ansi = TryGetAnsi();
        if (ansi is not null) yield return (ansi, []);
    }

    private static Encoding? TryGetAnsi()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>指定セクションの該当キー行を取り除く。対象セクションが複数あれば曖昧として何も変えない。</summary>
    internal static Edit RemoveKeys(string text, IReadOnlyList<CleanupIniKey> keys)
    {
        var targets = keys.GroupBy(key => key.Section, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(key => key.Key).ToArray(), StringComparer.OrdinalIgnoreCase);
        var output = new StringBuilder(text.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[]? active = null;
        var removed = 0;
        foreach (var (content, terminated) in SplitLines(text))
        {
            var trimmed = content.Trim();
            if (TryReadSection(trimmed, out var section))
            {
                if (targets.TryGetValue(section, out active) && !seen.Add(section)) return new Edit(text, 0, true);
                output.Append(terminated);
                continue;
            }

            if (active is not null && MatchesKeyLine(trimmed, active)) { removed++; continue; }
            output.Append(terminated);
        }

        return new Edit(output.ToString(), removed, false);
    }

    /// <summary>行を行末記号込みで返す。(内容, 行末込みの全文) の組。</summary>
    private static IEnumerable<(string Content, string Full)> SplitLines(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = text.AsSpan(start).IndexOfAny('\r', '\n');
            if (end < 0) { yield return (text[start..], text[start..]); yield break; }
            end += start;
            var next = text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? end + 2 : end + 1;
            yield return (text[start..end], text[start..next]);
            start = next;
        }
    }

    private static bool TryReadSection(string trimmed, out string section)
    {
        section = string.Empty;
        if (trimmed.Length < 3 || trimmed[0] != '[') return false;
        var close = trimmed.IndexOf(']');
        if (close < 2) return false;
        section = trimmed[1..close].Trim();
        return section.Length > 0;
    }

    private static bool MatchesKeyLine(string trimmed, string[] patterns)
    {
        if (trimmed.Length == 0 || trimmed[0] == ';') return false;
        var equals = trimmed.IndexOf('=');
        if (equals <= 0) return false;
        var name = trimmed[..equals].Trim();
        return patterns.Any(pattern => MatchesKey(pattern, name));
    }

    /// <summary>末尾 <c>*</c> は「前置き + 1 桁以上の数字」の連番だけに一致する。それ以外は大文字小文字を区別しない完全一致。</summary>
    private static bool MatchesKey(string pattern, string name)
    {
        if (!pattern.EndsWith('*')) return name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        var prefix = pattern.AsSpan(0, pattern.Length - 1);
        var rest = name.AsSpan();
        return rest.Length > prefix.Length && rest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && rest[prefix.Length..].IndexOfAnyExceptInRange('0', '9') < 0;
    }
}

/// <summary>実ファイルを <see cref="NativeCleanupEntry"/> で開く。リンク・再解析点は拒否し、祖先も固定する。</summary>
internal sealed class NativeCleanupIniOpener : ICleanupIniOpener
{
    internal static NativeCleanupIniOpener Instance { get; } = new();

    public ICleanupIniStream Open(string path, bool writable) =>
        new NativeStream(NativeCleanupEntry.Open(path, directory: false, readData: true, write: writable));

    private sealed class NativeStream(NativeCleanupEntry entry) : ICleanupIniStream
    {
        public long Length => entry.Length;

        public int Read(Span<byte> buffer, long offset) => RandomAccess.Read(entry.Handle, buffer, offset);

        public void Write(ReadOnlySpan<byte> data, long offset)
        {
            if (!entry.Writable) throw new InvalidOperationException("書込み用に開いていません");
            entry.EnsureNotReparse();
            RandomAccess.Write(entry.Handle, data, offset);
        }

        public void SetLength(long length) => RandomAccess.SetLength(entry.Handle, length);

        public void Flush() => RandomAccess.FlushToDisk(entry.Handle);

        public void Dispose() => entry.Dispose();
    }
}
