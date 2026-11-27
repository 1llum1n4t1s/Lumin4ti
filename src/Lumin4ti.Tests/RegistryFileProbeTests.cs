using Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class RegistryFileProbeTests
{
    private static readonly Func<string, (DriveType, bool)> FixedReady = _ => (DriveType.Fixed, true);

    /// <summary>存在するパスの集合から属性取得を偽装する。再解析点・拒否・例外は指定で注入する。</summary>
    private static Func<string, FileAttributes> Fs(
        IEnumerable<string> directories,
        IEnumerable<string>? files = null,
        IEnumerable<string>? reparse = null,
        IEnumerable<string>? denied = null,
        Exception? ioError = null)
    {
        var dirs = new HashSet<string>(directories.Select(Trim), StringComparer.OrdinalIgnoreCase);
        var fileSet = new HashSet<string>((files ?? []).Select(Trim), StringComparer.OrdinalIgnoreCase);
        var links = new HashSet<string>((reparse ?? []).Select(Trim), StringComparer.OrdinalIgnoreCase);
        var deny = new HashSet<string>((denied ?? []).Select(Trim), StringComparer.OrdinalIgnoreCase);
        return path =>
        {
            var key = Trim(path);
            if (deny.Contains(key)) throw new UnauthorizedAccessException();
            if (ioError is not null) throw ioError;
            if (links.Contains(key)) return FileAttributes.Directory | FileAttributes.ReparsePoint;
            if (dirs.Contains(key)) return FileAttributes.Directory;
            if (fileSet.Contains(key)) return FileAttributes.Archive;
            throw new FileNotFoundException();
        };
    }

    private static string Trim(string path) => path.TrimEnd('\\');

    private static RegistryFileProbe Probe(Func<string, FileAttributes> fs, Func<string, (DriveType, bool)>? drive = null) =>
        new(root => drive?.Invoke(root) ?? (DriveType.Fixed, true), fs);

    [TestMethod]
    public void 存在するファイルはExists()
    {
        var probe = Probe(Fs([@"C:\", @"C:\Tools"], [@"C:\Tools\a.exe"]));
        Assert.AreEqual(RegistryFileState.Exists, probe.Probe(@"C:\Tools\a.exe"));
    }

    [TestMethod]
    public void 固定ドライブで不存在が明示されればConfirmedMissing()
    {
        var probe = Probe(Fs([@"C:\", @"C:\Tools"]));
        Assert.AreEqual(RegistryFileState.ConfirmedMissing, probe.Probe(@"C:\Tools\gone.exe"));
        Assert.AreEqual(RegistryFileState.ConfirmedMissing, probe.Probe(@"C:\Nope\sub\gone.exe"));
    }

    [TestMethod]
    [DataRow(@"\\server\share\a.exe")]
    [DataRow(@"\\?\C:\Tools\a.exe")]
    [DataRow(@"\\.\C:\Tools\a.exe")]
    [DataRow(@"a.exe")]
    [DataRow(@"Tools\a.exe")]
    [DataRow(@"C:a.exe")]
    [DataRow(@"C:\Tools\..\a.exe")]
    [DataRow("")]
    public void 対象外の形はUnknown(string path)
    {
        var probe = Probe(Fs([@"C:\", @"C:\Tools"]));
        Assert.AreEqual(RegistryFileState.Unknown, probe.Probe(path));
    }

    [TestMethod]
    [DataRow(DriveType.Removable, true)]
    [DataRow(DriveType.Network, true)]
    [DataRow(DriveType.NoRootDirectory, false)]
    [DataRow(DriveType.Fixed, false)]
    [DataRow(DriveType.CDRom, true)]
    public void 固定でないか未準備のドライブはUnknown(DriveType type, bool ready)
    {
        var probe = Probe(Fs([@"C:\"]), _ => (type, ready));
        Assert.AreEqual(RegistryFileState.Unknown, probe.Probe(@"C:\gone.exe"));
    }

    [TestMethod]
    public void ドライブ情報の取得に失敗したらUnknown()
    {
        var probe = new RegistryFileProbe(_ => throw new IOException(), Fs([@"C:\"]));
        Assert.AreEqual(RegistryFileState.Unknown, probe.Probe(@"C:\gone.exe"));
    }

    [TestMethod]
    public void アクセス拒否とI_OエラーはUnknown()
    {
        Assert.AreEqual(RegistryFileState.Unknown, Probe(Fs([@"C:\"], denied: [@"C:\a.exe"])).Probe(@"C:\a.exe"));
        Assert.AreEqual(RegistryFileState.Unknown, Probe(Fs([@"C:\"], ioError: new IOException())).Probe(@"C:\a.exe"));
    }

    [TestMethod]
    public void 祖先に再解析点があればUnknown()
    {
        var probe = Probe(Fs([@"C:\"], reparse: [@"C:\Link"]));
        Assert.AreEqual(RegistryFileState.Unknown, probe.Probe(@"C:\Link\sub\gone.exe"));
    }

    [TestMethod]
    public void 祖先の確認で拒否されたらUnknown()
    {
        var probe = Probe(Fs([@"C:\", @"C:\Tools"], denied: [@"C:\Tools"]));
        Assert.AreEqual(RegistryFileState.Unknown, probe.Probe(@"C:\Tools\gone.exe"));
    }

    [TestMethod]
    public void 祖先がファイルならUnknown()
    {
        var probe = Probe(Fs([@"C:\"], [@"C:\file"]));
        Assert.AreEqual(RegistryFileState.Unknown, probe.Probe(@"C:\file\x\gone.exe"));
    }

    [TestMethod]
    public void 候補は存在があればExists_全て欠損確定のときだけConfirmedMissing()
    {
        var probe = Probe(Fs([@"C:\", @"C:\A"], [@"C:\A\x.exe"]));
        Assert.AreEqual(RegistryFileState.Exists, probe.ProbeAll([@"C:\B\x.exe", @"C:\A\x.exe"]));
        Assert.AreEqual(RegistryFileState.ConfirmedMissing, probe.ProbeAll([@"C:\B\x.exe", @"C:\A\y.exe"]));
        Assert.AreEqual(RegistryFileState.Unknown, probe.ProbeAll([@"C:\B\x.exe", @"\\s\x.exe"]));
        Assert.AreEqual(RegistryFileState.Unknown, probe.ProbeAll([]));
    }
}
