using System.Text;
using Lumin4ti.Core.Services;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class UnelevatedCommandExecutorTests
{
    [TestMethod]
    public void 対話シェルはWindows直下のexplorerだけを許可する()
    {
        Assert.IsTrue(UnelevatedCommandExecutor.IsExpectedShellPath(
            @"C:\Windows\explorer.exe",
            @"C:\Windows"));
        Assert.IsTrue(UnelevatedCommandExecutor.IsExpectedShellPath(
            @"c:\windows\EXPLORER.EXE",
            @"C:\Windows"));
        Assert.IsFalse(UnelevatedCommandExecutor.IsExpectedShellPath(
            @"C:\Windows\System32\explorer.exe",
            @"C:\Windows"));
        Assert.IsFalse(UnelevatedCommandExecutor.IsExpectedShellPath(
            @"C:\Users\test\explorer.exe",
            @"C:\Windows"));
    }

    [TestMethod]
    public void 結果パスはLocalAppData内のランダム名になる()
    {
        const string nonce = "0123456789abcdef0123456789abcdef";

        var path = UnelevatedCommandExecutor.BuildResultPath(@"C:\Users\test\AppData\Local", nonce);

        Assert.AreEqual(
            @"C:\Users\test\AppData\Local\Lumin4ti\command-results\operation-0123456789abcdef0123456789abcdef.json",
            path);
    }

    [TestMethod]
    public void 結果パスの不正なnonceを拒否する()
    {
        Assert.Throws<ArgumentException>(() =>
            UnelevatedCommandExecutor.BuildResultPath(
                @"C:\Users\test\AppData\Local",
                @"..\..\Windows\Temp\result"));
    }

    [TestMethod]
    public void PowerShell引数は短いEncodedCommandだけを含む()
    {
        var commandLine = UnelevatedCommandExecutor.BuildPowerShellCommandLine(
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe");

        Assert.IsTrue(commandLine.Length < 1024, commandLine.Length.ToString());
        StringAssert.Contains(commandLine, "-NoProfile -NonInteractive -EncodedCommand ");
        Assert.IsFalse(commandLine.Contains("Shell.Application", StringComparison.Ordinal));

        var encoded = commandLine[(commandLine.LastIndexOf(' ') + 1)..];
        var bootstrap = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        StringAssert.Contains(bootstrap, "$env:LUMIN4TI_SCRIPT_B64");
        StringAssert.Contains(bootstrap, "ScriptBlock");
    }

    [TestMethod]
    public void 環境ブロックは安定順序で二重null終端になる()
    {
        var block = UnelevatedCommandExecutor.BuildEnvironmentBlock(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Z_VAR"] = "z",
                ["A_VAR"] = "a",
            });

        Assert.AreEqual("A_VAR=a\0Z_VAR=z\0\0", block);
    }

    [TestMethod]
    public void 環境変数名によるblock注入を拒否する()
    {
        Assert.Throws<ArgumentException>(() =>
            UnelevatedCommandExecutor.BuildEnvironmentBlock(
                new Dictionary<string, string> { ["SAFE=EVIL"] = "x" }));
        Assert.Throws<ArgumentException>(() =>
            UnelevatedCommandExecutor.BuildEnvironmentBlock(
                new Dictionary<string, string> { ["SAFE"] = "x\0EVIL=y" }));
    }
}
