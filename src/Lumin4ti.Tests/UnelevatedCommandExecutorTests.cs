using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class UnelevatedCommandExecutorTests
{
    [TestMethod]
    public void Explorerプロセスの検証ハンドルは待機権限も要求する()
    {
        Assert.AreEqual(0x00101000u, UnelevatedCommandExecutor.GetShellProcessAccessMask());
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public void Explorer検証用アクセスマスクで稼働中プロセスを待機確認できる()
    {
        var process = OpenProcess(
            UnelevatedCommandExecutor.GetShellProcessAccessMask(),
            inheritHandle: false,
            checked((uint)Environment.ProcessId));
        Assert.AreNotEqual(nint.Zero, process);

        try
        {
            Assert.AreEqual(0x00000102u, WaitForSingleObject(process, 0));
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

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
        StringAssert.Contains(bootstrap, "$env:LUMIN4TI_SCRIPT_GZIP_B64");
        StringAssert.Contains(bootstrap, "GZipStream");
        StringAssert.Contains(bootstrap, "ScriptBlock");
    }

    [TestMethod]
    public void 長いクイックアクセスscriptは環境ブロック上限内へ圧縮できる()
    {
        var encoded = UnelevatedCommandExecutor.EncodeScriptForEnvironment(
            QuickAccessSortAction.PowerShellScript);
        var environmentBlock = UnelevatedCommandExecutor.BuildEnvironmentBlock(
            new Dictionary<string, string>
            {
                ["LUMIN4TI_SCRIPT_GZIP_B64"] = encoded,
                ["LUMIN4TI_RESULT_PATH"] = @"C:\Users\test\result.json",
            });

        Assert.IsTrue(environmentBlock.Length < 32767, environmentBlock.Length.ToString());
        Assert.AreEqual(QuickAccessSortAction.PowerShellScript, DecodeScript(encoded));
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

    private static string DecodeScript(string encoded)
    {
        using var compressed = new MemoryStream(Convert.FromBase64String(encoded));
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.Unicode);
        return reader.ReadToEnd();
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
