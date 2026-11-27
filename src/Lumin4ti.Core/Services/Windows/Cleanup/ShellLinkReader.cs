using System.Buffers.Binary;
using System.Text;

namespace Lumin4ti.Core.Services.Windows.Cleanup;

/// <summary>ショートカットのリンク先を、欠損の判定に使えるかどうかで分類したもの。</summary>
public enum ShellLinkTargetKind
{
    /// <summary>固定ドライブ上の絶対パス。存在確認に使える。</summary>
    LocalPath,

    /// <summary>ID リスト (PIDL) だけでパスを持たない (シェル名前空間・フォルダー等)。判定不能。</summary>
    PidlOnly,

    /// <summary>広告型 (Windows Installer の Darwin ID)。実体は初回使用時に展開されるため判定不能。</summary>
    Advertised,

    /// <summary>ネットワーク共有。接続状態で見えなくなるだけなので判定不能。</summary>
    Network,

    /// <summary>固定ドライブ以外 (リムーバブル・光学・不明)。媒体が無いだけなので判定不能。</summary>
    NonFixedDrive,

    /// <summary>環境変数を含む対象だけで、固定のパスを持たない。判定不能。</summary>
    EnvironmentOnly,

    /// <summary>構造が壊れている・非対応の表現を含む。判定不能。</summary>
    Invalid,
}

/// <summary>ショートカットの解析結果。</summary>
/// <param name="Kind">分類。</param>
/// <param name="Path"><see cref="ShellLinkTargetKind.LocalPath"/> のときだけ、リンク先の絶対パス。</param>
/// <param name="Reason">判定不能の理由 (ログ・表示用)。</param>
public sealed record ShellLinkTarget(ShellLinkTargetKind Kind, string? Path = null, string? Reason = null)
{
    /// <summary>リンク先の存在確認に使えるか。それ以外は欠損と判定してはならない。</summary>
    public bool IsDeterminable => Kind == ShellLinkTargetKind.LocalPath;
}

/// <summary>
/// MS-SHLLINK (.lnk) を独自に解析してリンク先のパスだけを取り出す。ファイルは読まず、渡された
/// バイト列だけを見る (呼び出し側が同一ハンドルで読む)。入力は非信頼として全ての範囲を検査する。
/// 判定に使えるのは LinkInfo の固定ドライブ上のローカルパスだけで、PIDL のみ・広告型・ネットワーク・
/// 固定以外のドライブ・環境変数のみ・壊れた構造は理由付きで判定不能にする。
/// </summary>
public static class ShellLinkReader
{
    /// <summary>これを超える入力は解析しない (通常の .lnk は数 KB)。</summary>
    public const int MaxLength = 1 << 20;

    private const int HeaderSize = 0x4C;
    private const uint HasLinkTargetIdList = 0x1;
    private const uint HasLinkInfo = 0x2;
    private const uint HasName = 0x4;
    private const uint HasRelativePath = 0x8;
    private const uint HasWorkingDir = 0x10;
    private const uint HasArguments = 0x20;
    private const uint HasIconLocation = 0x40;
    private const uint IsUnicode = 0x80;
    private const uint ForceNoLinkInfo = 0x100;
    private const uint HasExpString = 0x200;
    private const uint HasDarwinId = 0x1000;
    private const uint VolumeIdAndLocalBasePath = 0x1;
    private const uint CommonNetworkRelativeLinkAndPathSuffix = 0x2;
    private const uint DriveFixed = 3;
    private const uint EnvironmentBlock = 0xA0000001;
    private const uint DarwinBlock = 0xA0000006;

    private static readonly byte[] LinkClsid =
        [0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46];

    /// <summary>バイト列を解析する。例外は投げず、解析できなければ <see cref="ShellLinkTargetKind.Invalid"/>。</summary>
    public static ShellLinkTarget Read(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxLength) return Invalid("サイズが大きすぎます");
        if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != HeaderSize ||
            !data.Slice(4, 16).SequenceEqual(LinkClsid))
        {
            return Invalid("ShellLinkHeader が不正です");
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[0x14..]);
        var pos = HeaderSize;
        if ((flags & HasLinkTargetIdList) != 0 && !TrySkipIdList(data, ref pos)) return Invalid("LinkTargetIDList が不正です");

        ReadOnlySpan<byte> linkInfo = default;
        var hasLinkInfo = (flags & HasLinkInfo) != 0 && (flags & ForceNoLinkInfo) == 0;
        if (hasLinkInfo)
        {
            if (!TryReadLinkInfoSize(data, pos, out var infoSize)) return Invalid("LinkInfo が不正です");
            linkInfo = data.Slice(pos, infoSize);
            pos += infoSize;
        }

        if (!TrySkipStringData(data, ref pos, flags)) return Invalid("StringData が不正です");

        var extra = ScanExtraData(data, pos);
        if (extra.Malformed) return Invalid("ExtraData が不正です");
        if ((flags & HasDarwinId) != 0 || extra.Darwin) return new ShellLinkTarget(ShellLinkTargetKind.Advertised, Reason: "広告型 (Darwin) のショートカットです");

        if (hasLinkInfo) return ReadLinkInfo(linkInfo);
        if ((flags & HasExpString) != 0 || extra.Environment) return new ShellLinkTarget(ShellLinkTargetKind.EnvironmentOnly, Reason: "環境変数付きのリンク先だけを持ちます");
        return (flags & HasLinkTargetIdList) != 0
            ? new ShellLinkTarget(ShellLinkTargetKind.PidlOnly, Reason: "ID リストだけでパスを持ちません")
            : Invalid("リンク先がありません");
    }

    private static ShellLinkTarget Invalid(string reason) => new(ShellLinkTargetKind.Invalid, Reason: reason);

    private static bool TrySkipIdList(ReadOnlySpan<byte> data, ref int pos)
    {
        if (pos + 2 > data.Length) return false;
        var next = pos + 2 + BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
        if (next > data.Length) return false;
        pos = next;
        return true;
    }

    private static bool TryReadLinkInfoSize(ReadOnlySpan<byte> data, int pos, out int size)
    {
        size = 0;
        if (pos + 4 > data.Length) return false;
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
        if (declared < 0x1C || declared > (uint)(data.Length - pos)) return false;
        size = (int)declared;
        return true;
    }

    private static bool TrySkipStringData(ReadOnlySpan<byte> data, ref int pos, uint flags)
    {
        var width = (flags & IsUnicode) != 0 ? 2 : 1;
        foreach (var present in new[] { HasName, HasRelativePath, HasWorkingDir, HasArguments, HasIconLocation })
        {
            if ((flags & present) == 0) continue;
            if (pos + 2 > data.Length) return false;
            var next = pos + 2 + BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]) * width;
            if (next > data.Length) return false;
            pos = next;
        }

        return true;
    }

    private readonly record struct ExtraInfo(bool Environment, bool Darwin, bool Malformed);

    private static ExtraInfo ScanExtraData(ReadOnlySpan<byte> data, int pos)
    {
        bool environment = false, darwin = false;
        while (pos + 4 <= data.Length)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
            if (size < 4) break;
            if (size < 8 || size > (uint)(data.Length - pos)) return new ExtraInfo(environment, darwin, true);
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
            environment |= signature == EnvironmentBlock;
            darwin |= signature == DarwinBlock;
            pos += (int)size;
        }

        return new ExtraInfo(environment, darwin, false);
    }

    private static ShellLinkTarget ReadLinkInfo(ReadOnlySpan<byte> info)
    {
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(info[4..]);
        var infoFlags = BinaryPrimitives.ReadUInt32LittleEndian(info[8..]);
        if (headerSize < 0x1C || headerSize > (uint)info.Length) return Invalid("LinkInfoHeader が不正です");

        if ((infoFlags & VolumeIdAndLocalBasePath) == 0)
        {
            return (infoFlags & CommonNetworkRelativeLinkAndPathSuffix) != 0
                ? new ShellLinkTarget(ShellLinkTargetKind.Network, Reason: "ネットワーク共有のリンク先です")
                : Invalid("LinkInfo にリンク先がありません");
        }

        var driveType = ReadDriveType(info);
        if (driveType is null) return Invalid("VolumeID が不正です");
        if (driveType != DriveFixed)
        {
            return new ShellLinkTarget(ShellLinkTargetKind.NonFixedDrive, Reason: $"固定ドライブ以外です (DriveType={driveType})");
        }

        var hasUnicode = headerSize >= 0x24;
        var basePath = ReadPath(info, 0x10, hasUnicode ? 0x1C : -1);
        var suffix = ReadPath(info, 0x18, hasUnicode ? 0x20 : -1);
        if (basePath is null || suffix is null) return Invalid("パスを読めません (範囲外・非 ASCII の ANSI)");

        var target = basePath + suffix;
        return IsDriveAbsolute(target)
            ? new ShellLinkTarget(ShellLinkTargetKind.LocalPath, target)
            : Invalid("ドライブ付きの絶対パスではありません");
    }

    private static uint? ReadDriveType(ReadOnlySpan<byte> info)
    {
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(info[0xC..]);
        if (offset > (uint)info.Length - 16) return null;
        return BinaryPrimitives.ReadUInt32LittleEndian(info[((int)offset + 4)..]);
    }

    /// <summary>Unicode 版を優先し、無ければ ANSI 版を ASCII だけ受け付けて読む。範囲外は null。</summary>
    private static string? ReadPath(ReadOnlySpan<byte> info, int ansiField, int unicodeField)
    {
        if (unicodeField >= 0)
        {
            var unicodeOffset = BinaryPrimitives.ReadUInt32LittleEndian(info[unicodeField..]);
            if (unicodeOffset != 0) return ReadUnicode(info, unicodeOffset);
        }

        var offset = BinaryPrimitives.ReadUInt32LittleEndian(info[ansiField..]);
        return offset == 0 && ansiField == 0x18 ? string.Empty : ReadAscii(info, offset);
    }

    private static string? ReadUnicode(ReadOnlySpan<byte> info, uint offset)
    {
        if (offset >= (uint)info.Length) return null;
        var rest = info[(int)offset..];
        for (var i = 0; i + 1 < rest.Length; i += 2)
        {
            if (rest[i] == 0 && rest[i + 1] == 0) return Encoding.Unicode.GetString(rest[..i]);
        }

        return null;
    }

    private static string? ReadAscii(ReadOnlySpan<byte> info, uint offset)
    {
        if (offset >= (uint)info.Length) return null;
        var rest = info[(int)offset..];
        var end = rest.IndexOf((byte)0);
        if (end < 0) return null;
        var text = rest[..end];
        foreach (var b in text)
        {
            if (b >= 0x80) return null;
        }

        return Encoding.ASCII.GetString(text);
    }

    private static bool IsDriveAbsolute(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\' && !path.Contains('\0');
}
