using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class BrokenStartupCleanupTests
{
    [TestMethod]
    public async Task 実行前キャンセルはレジストリ処理を開始しない()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new BrokenStartupCleanupAction().ExecuteAsync(cancellation.Token));
    }

    [TestMethod]
    public void Startup名集合は大小文字を区別せず追加検索削除できる()
    {
        var names = BrokenStartupCleanupAction.CreateStartupNameSet();

        Assert.IsTrue(names.Add("Updater"));
        Assert.IsFalse(names.Add("updater"));
        Assert.IsTrue(names.Contains("UPDATER"));
        Assert.IsTrue(names.Remove("uPdAtEr"));
        Assert.IsFalse(names.Contains("Updater"));
    }

    [TestMethod]
    public void ドライブ相対パスは削除対象として解決しない()
    {
        Assert.IsNull(StartupCommandParser.TryResolveExecutable(@"C:Tools\app.exe --silent"));
        Assert.IsFalse(StartupCommandParser.CanTreatMissingAsBroken(
            @"C:Tools\app.exe",
            _ => (DriveType.Fixed, true)));
    }

    [TestMethod]
    public void 欠損と断定するのは準備済み固定ドライブだけ()
    {
        static (DriveType, bool) FixedReady(string _) => (DriveType.Fixed, true);
        static (DriveType, bool) FixedNotReady(string _) => (DriveType.Fixed, false);
        static (DriveType, bool) Network(string _) => (DriveType.Network, true);

        Assert.IsTrue(StartupCommandParser.CanTreatMissingAsBroken(@"C:\Tools\gone.exe", FixedReady));
        Assert.IsFalse(StartupCommandParser.CanTreatMissingAsBroken(@"C:\Tools\gone.exe", FixedNotReady));
        Assert.IsFalse(StartupCommandParser.CanTreatMissingAsBroken(@"X:\Tools\gone.exe", Network));
        Assert.IsFalse(StartupCommandParser.CanTreatMissingAsBroken(@"\\server\share\gone.exe", FixedReady));
    }

    [TestMethod]
    public void 不存在が明示された場合だけ欠損と確定する()
    {
        static (DriveType, bool) FixedReady(string _) => (DriveType.Fixed, true);
        static FileAttributes Existing(string _) => FileAttributes.Normal;
        static FileAttributes Missing(string path) =>
            path.EndsWith("gone.exe", StringComparison.OrdinalIgnoreCase)
                ? throw new FileNotFoundException()
                : FileAttributes.Directory;
        static FileAttributes Denied(string _) => throw new UnauthorizedAccessException();

        Assert.IsFalse(StartupCommandParser.IsConfirmedMissing(@"C:\Tools\app.exe", FixedReady, Existing));
        Assert.IsTrue(StartupCommandParser.IsConfirmedMissing(@"C:\Tools\gone.exe", FixedReady, Missing));
        Assert.IsFalse(StartupCommandParser.IsConfirmedMissing(@"C:\Tools\private.exe", FixedReady, Denied));
    }

    [TestMethod]
    public void 再解析点配下の不存在は一時不在として保持する()
    {
        const string executable = @"C:\Mounted\Tools\gone.exe";
        static (DriveType, bool) FixedReady(string _) => (DriveType.Fixed, true);
        static FileAttributes Attributes(string path)
        {
            if (path.Equals(executable, StringComparison.OrdinalIgnoreCase))
            {
                throw new DirectoryNotFoundException();
            }

            return path.TrimEnd('\\').Equals(@"C:\Mounted", StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : FileAttributes.Directory;
        }

        Assert.IsFalse(StartupCommandParser.IsConfirmedMissing(executable, FixedReady, Attributes));
    }
}
