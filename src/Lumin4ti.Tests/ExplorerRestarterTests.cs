using Lumin4ti.Core.Services.Windows;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class ExplorerRestarterTests
{
    [TestMethod]
    public void 実環境の正規Explorerを完全パスで解決できる()
    {
        var actual = ExplorerRestarter.ResolveExplorerPath();

        Assert.IsTrue(Path.IsPathFullyQualified(actual));
        Assert.IsTrue(File.Exists(actual));
        Assert.IsTrue(string.Equals(
            "explorer.exe",
            Path.GetFileName(actual),
            StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ExplorerはWindowsDirectory配下の存在確認済み完全パスで解決する()
    {
        var windowsDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Windows"));
        var expected = Path.Combine(windowsDirectory, "explorer.exe");
        string? checkedPath = null;

        var actual = ExplorerRestarter.ResolveExplorerPath(
            windowsDirectory,
            path =>
            {
                checkedPath = path;
                return true;
            });

        Assert.IsTrue(Path.IsPathFullyQualified(actual));
        Assert.AreEqual(expected, actual);
        Assert.AreEqual(expected, checkedPath);
    }

    [TestMethod]
    public void Explorerが存在しなければ停止前に失敗する()
    {
        var windowsDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Windows"));

        var exception = Assert.Throws<FileNotFoundException>(() =>
            ExplorerRestarter.ResolveExplorerPath(windowsDirectory, _ => false));

        Assert.AreEqual(Path.Combine(windowsDirectory, "explorer.exe"), exception.FileName);
    }

    [TestMethod]
    public void 相対WindowsDirectoryは拒否する()
    {
        Assert.Throws<ArgumentException>(() =>
            ExplorerRestarter.ResolveExplorerPath("Windows", _ => true));
    }

    [TestMethod]
    public void 現在セッションかつWindows配下のExplorerだけを終了対象にする()
    {
        const int currentSession = 4;
        var expected = Path.GetFullPath(@"C:\Windows\explorer.exe");

        Assert.IsTrue(ExplorerRestarter.IsCurrentSessionExplorer(
            currentSession,
            @"C:\WINDOWS\explorer.exe",
            currentSession,
            expected));
        Assert.IsFalse(ExplorerRestarter.IsCurrentSessionExplorer(
            currentSession + 1,
            expected,
            currentSession,
            expected), "別ユーザーのセッションを終了してはいけません");
        Assert.IsFalse(ExplorerRestarter.IsCurrentSessionExplorer(
            currentSession,
            @"C:\Users\test\explorer.exe",
            currentSession,
            expected), "同名の偽プロセスを終了してはいけません");
    }
}
