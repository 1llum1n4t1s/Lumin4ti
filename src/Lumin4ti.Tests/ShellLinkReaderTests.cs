using System.Text;
using Lumin4ti.Core.Services.Windows.Cleanup;

namespace Lumin4ti.Tests;

/// <summary>
/// MS-SHLLINK の解析。実ファイルは使わず、合成したバイト列だけを渡す。
/// 欠損の判定に使えるのは固定ドライブ上の LinkInfo パスだけで、他は全て判定不能になる。
/// </summary>
[TestClass]
public sealed class ShellLinkReaderTests
{
    private const uint HasIdList = 0x1;
    private const uint HasLinkInfo = 0x2;
    private const uint HasName = 0x4;
    private const uint HasArguments = 0x20;
    private const uint IsUnicode = 0x80;
    private const uint ForceNoLinkInfo = 0x100;
    private const uint HasExpString = 0x200;
    private const uint HasDarwinId = 0x1000;

    [TestMethod]
    public void 固定ドライブのUnicodeパスを取り出す()
    {
        var link = Link(HasLinkInfo, linkInfo: LinkInfo(3, @"C:\Program Files\App\app.exe", "", unicode: true));

        var target = ShellLinkReader.Read(link);

        Assert.AreEqual(ShellLinkTargetKind.LocalPath, target.Kind);
        Assert.AreEqual(@"C:\Program Files\App\app.exe", target.Path);
        Assert.IsTrue(target.IsDeterminable);
    }

    [TestMethod]
    public void ANSIだけの場合はASCIIパスを取り出す()
    {
        var link = Link(HasLinkInfo, linkInfo: LinkInfo(3, @"D:\Tools\x.exe", "", unicode: false));

        var target = ShellLinkReader.Read(link);

        Assert.AreEqual(ShellLinkTargetKind.LocalPath, target.Kind);
        Assert.AreEqual(@"D:\Tools\x.exe", target.Path);
    }

    [TestMethod]
    public void Unicode版があればANSI版より優先し_パス接尾辞を連結する()
    {
        var link = Link(
            HasLinkInfo,
            linkInfo: LinkInfo(3, @"C:\Program Files\", @"日本語\a.exe", unicode: true, ansiBase: @"C:\x\", ansiSuffix: "y"));

        var target = ShellLinkReader.Read(link);

        Assert.AreEqual(@"C:\Program Files\日本語\a.exe", target.Path);
    }

    [TestMethod]
    [DataRow(0u)]
    [DataRow(1u)]
    [DataRow(2u)]
    [DataRow(4u)]
    [DataRow(5u)]
    [DataRow(6u)]
    public void 固定ドライブ以外は判定不能にする(uint driveType)
    {
        var link = Link(HasLinkInfo, linkInfo: LinkInfo(driveType, @"E:\Media\a.exe", "", unicode: true));

        var target = ShellLinkReader.Read(link);

        Assert.AreEqual(ShellLinkTargetKind.NonFixedDrive, target.Kind);
        Assert.IsNull(target.Path);
        Assert.IsFalse(target.IsDeterminable);
    }

    [TestMethod]
    public void ネットワーク共有は判定不能にする()
    {
        var link = Link(HasLinkInfo, linkInfo: LinkInfo(0, "", "", unicode: false, infoFlags: 2));

        Assert.AreEqual(ShellLinkTargetKind.Network, ShellLinkReader.Read(link).Kind);
    }

    [TestMethod]
    public void IDリストだけのリンクは判定不能にする()
    {
        var link = Link(HasIdList, idList: [0x14, 0x00, 0x1F, 0x50]);

        var target = ShellLinkReader.Read(link);

        Assert.AreEqual(ShellLinkTargetKind.PidlOnly, target.Kind);
        Assert.IsFalse(target.IsDeterminable);
    }

    [TestMethod]
    public void LinkInfoを無視する指定のIDリストも判定不能にする()
    {
        var link = Link(
            HasIdList | HasLinkInfo | ForceNoLinkInfo,
            idList: [0x00, 0x00],
            linkInfo: LinkInfo(3, @"C:\gone.exe", "", unicode: true));

        Assert.AreEqual(ShellLinkTargetKind.PidlOnly, ShellLinkReader.Read(link).Kind);
    }

    [TestMethod]
    public void 広告型は旗と拡張ブロックのどちらでも判定不能にする()
    {
        var info = LinkInfo(3, @"C:\gone.exe", "", unicode: true);
        var byFlag = Link(HasDarwinId | HasLinkInfo, linkInfo: info);
        var byBlock = Link(HasLinkInfo, linkInfo: info, extras: [Extra(0xA0000006, 0x314)]);

        Assert.AreEqual(ShellLinkTargetKind.Advertised, ShellLinkReader.Read(byFlag).Kind);
        Assert.AreEqual(ShellLinkTargetKind.Advertised, ShellLinkReader.Read(byBlock).Kind);
    }

    [TestMethod]
    public void 文字列データの幅を読み飛ばして後続の拡張ブロックへ届く()
    {
        var link = Link(
            HasLinkInfo | HasName | HasArguments | IsUnicode,
            linkInfo: LinkInfo(3, @"C:\gone.exe", "", unicode: true),
            strings: ["名前", "--arg x"],
            extras: [Extra(0xA0000006, 0x314)]);

        Assert.AreEqual(ShellLinkTargetKind.Advertised, ShellLinkReader.Read(link).Kind);
    }

    [TestMethod]
    public void 環境変数だけのリンク先は判定不能にする()
    {
        var byFlag = Link(HasExpString);
        var byBlock = Link(0, extras: [Extra(0xA0000001, 0x314)]);

        Assert.AreEqual(ShellLinkTargetKind.EnvironmentOnly, ShellLinkReader.Read(byFlag).Kind);
        Assert.AreEqual(ShellLinkTargetKind.EnvironmentOnly, ShellLinkReader.Read(byBlock).Kind);
    }

    [TestMethod]
    public void 壊れた構造と非対応の表現は判定不能にする()
    {
        var valid = Link(HasLinkInfo, linkInfo: LinkInfo(3, @"C:\a.exe", "", unicode: true));
        var badClsid = (byte[])valid.Clone();
        badClsid[4] ^= 0xFF;
        var badSize = (byte[])valid.Clone();
        badSize[0] = 0x4D;

        var cases = new Dictionary<string, byte[]>
        {
            ["空"] = [],
            ["ヘッダー未満"] = valid[..40],
            ["CLSID 不一致"] = badClsid,
            ["ヘッダーサイズ不一致"] = badSize,
            ["非 ASCII の ANSI"] = Link(HasLinkInfo, linkInfo: LinkInfo(3, "C:\\é.exe", "", unicode: false)),
            ["相対パス"] = Link(HasLinkInfo, linkInfo: LinkInfo(3, @"a\b.exe", "", unicode: true)),
            ["UNC"] = Link(HasLinkInfo, linkInfo: LinkInfo(3, @"\\server\share\a.exe", "", unicode: true)),
            ["リンク先なし"] = Link(0),
            ["IDリストが範囲外"] = Link(HasIdList, idList: [], idListSizeOverride: 0xFFFF),
            ["LinkInfo が範囲外"] = valid[..^10],
            ["文字列データが範囲外"] = Link(HasName, strings: [], stringCountOverride: 0x7FFF),
            ["拡張ブロックが範囲外"] = Link(0, extras: [Extra(0xA0000001, 0x7FFFFFFF)]),
            ["大きすぎる入力"] = new byte[ShellLinkReader.MaxLength + 1],
        };

        foreach (var (name, data) in cases)
        {
            var target = ShellLinkReader.Read(data);
            Assert.AreEqual(ShellLinkTargetKind.Invalid, target.Kind, name);
            Assert.IsFalse(target.IsDeterminable, name);
            Assert.IsFalse(string.IsNullOrEmpty(target.Reason), name);
        }
    }

    [TestMethod]
    public void どの位置で切れても例外を投げず_切れた入力を欠損確定の根拠にしない()
    {
        var full = Link(
            HasIdList | HasLinkInfo | HasName | IsUnicode,
            idList: [1, 2, 3, 4],
            linkInfo: LinkInfo(3, @"C:\a.exe", "", unicode: true),
            strings: ["n"],
            extras: [Extra(0xA0000001, 0x314)]);
        Assert.AreEqual(ShellLinkTargetKind.LocalPath, ShellLinkReader.Read(full).Kind);

        for (var length = 0; length < full.Length; length++)
        {
            var target = ShellLinkReader.Read(full.AsSpan(0, length));
            var inExtra = length >= full.Length - 0x314 - 4;
            if (!inExtra)
            {
                Assert.AreEqual(ShellLinkTargetKind.Invalid, target.Kind, $"length={length}");
            }
        }
    }

    private static byte[] Extra(uint signature, int size)
    {
        var block = new byte[Math.Min(size, 0x400)];
        BitConverter.GetBytes(size).CopyTo(block, 0);
        BitConverter.GetBytes(signature).CopyTo(block, 4);
        return block;
    }

    /// <summary>ShellLinkHeader + 各部 + 終端ブロックの合成 .lnk。</summary>
    private static byte[] Link(
        uint flags,
        byte[]? idList = null,
        byte[]? linkInfo = null,
        string[]? strings = null,
        byte[][]? extras = null,
        int? idListSizeOverride = null,
        int? stringCountOverride = null)
    {
        var bytes = new List<byte>();
        var header = new byte[0x4C];
        BitConverter.GetBytes(0x4C).CopyTo(header, 0);
        new byte[] { 0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 }.CopyTo(header, 4);
        BitConverter.GetBytes(flags).CopyTo(header, 0x14);
        bytes.AddRange(header);

        if (idList is not null)
        {
            bytes.AddRange(BitConverter.GetBytes((ushort)(idListSizeOverride ?? idList.Length)));
            bytes.AddRange(idList);
        }

        if (linkInfo is not null) bytes.AddRange(linkInfo);

        var unicode = (flags & IsUnicode) != 0;
        foreach (var text in strings ?? [])
        {
            bytes.AddRange(BitConverter.GetBytes((ushort)text.Length));
            bytes.AddRange(unicode ? Encoding.Unicode.GetBytes(text) : Encoding.ASCII.GetBytes(text));
        }

        if (stringCountOverride is { } count) bytes.AddRange(BitConverter.GetBytes((ushort)count));
        foreach (var extra in extras ?? []) bytes.AddRange(extra);
        bytes.AddRange(new byte[4]);
        return [.. bytes];
    }

    /// <summary>LinkInfo 構造。パス類は ANSI を常に持ち、<paramref name="unicode"/> なら Unicode 版も持つ。</summary>
    private static byte[] LinkInfo(
        uint driveType,
        string basePath,
        string suffix,
        bool unicode,
        uint infoFlags = 1,
        string? ansiBase = null,
        string? ansiSuffix = null)
    {
        var headerSize = unicode ? 0x24 : 0x1C;
        var volume = new byte[17];
        BitConverter.GetBytes(volume.Length).CopyTo(volume, 0);
        BitConverter.GetBytes(driveType).CopyTo(volume, 4);
        BitConverter.GetBytes(0x10).CopyTo(volume, 12);

        var ansiBaseBytes = Encoding.Latin1.GetBytes((ansiBase ?? basePath) + "\0");
        var ansiSuffixBytes = Encoding.Latin1.GetBytes((ansiSuffix ?? suffix) + "\0");
        var unicodeBaseBytes = Encoding.Unicode.GetBytes(basePath + "\0");
        var unicodeSuffixBytes = Encoding.Unicode.GetBytes(suffix + "\0");

        var volumeOffset = headerSize;
        var ansiBaseOffset = volumeOffset + volume.Length;
        var ansiSuffixOffset = ansiBaseOffset + ansiBaseBytes.Length;
        var unicodeBaseOffset = ansiSuffixOffset + ansiSuffixBytes.Length;
        var unicodeSuffixOffset = unicodeBaseOffset + unicodeBaseBytes.Length;
        var size = unicode ? unicodeSuffixOffset + unicodeSuffixBytes.Length : unicodeBaseOffset;

        var info = new byte[size];
        BitConverter.GetBytes(size).CopyTo(info, 0);
        BitConverter.GetBytes(headerSize).CopyTo(info, 4);
        BitConverter.GetBytes(infoFlags).CopyTo(info, 8);
        BitConverter.GetBytes(volumeOffset).CopyTo(info, 12);
        BitConverter.GetBytes(ansiBaseOffset).CopyTo(info, 16);
        BitConverter.GetBytes(ansiSuffixOffset).CopyTo(info, 24);
        volume.CopyTo(info, volumeOffset);
        ansiBaseBytes.CopyTo(info, ansiBaseOffset);
        ansiSuffixBytes.CopyTo(info, ansiSuffixOffset);
        if (unicode)
        {
            BitConverter.GetBytes(unicodeBaseOffset).CopyTo(info, 28);
            BitConverter.GetBytes(unicodeSuffixOffset).CopyTo(info, 32);
            unicodeBaseBytes.CopyTo(info, unicodeBaseOffset);
            unicodeSuffixBytes.CopyTo(info, unicodeSuffixOffset);
        }

        return info;
    }
}
