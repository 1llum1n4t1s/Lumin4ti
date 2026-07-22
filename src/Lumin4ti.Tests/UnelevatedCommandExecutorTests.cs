using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Lumin4ti.Core.Services;

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
    public void 長いscriptは環境ブロック上限内へ圧縮できる()
    {
        var script = string.Concat(Enumerable.Repeat(
            "$value = '対話中ユーザーの medium process で実行する長いscriptです'\r\n",
            1000));
        var encoded = UnelevatedCommandExecutor.EncodeScriptForEnvironment(
            script);
        var environmentBlock = UnelevatedCommandExecutor.BuildEnvironmentBlock(
            new Dictionary<string, string>
            {
                ["LUMIN4TI_SCRIPT_GZIP_B64"] = encoded,
                ["LUMIN4TI_RESULT_PATH"] = @"C:\Users\test\result.json",
            });

        Assert.IsTrue(environmentBlock.Length < 32767, environmentBlock.Length.ToString());
        Assert.AreEqual(script, DecodeScript(encoded));
    }

    [TestMethod]
    public void ExplorerBroker引数は固定launcherだけをFileで渡す()
    {
        var arguments = UnelevatedCommandExecutor.BuildExplorerBrokerArguments(
            @"C:\Users\test\AppData\Local\Lumin4ti\medium-launchers\operation-0123.ps1");

        Assert.AreEqual(
            "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass " +
            "-WindowStyle Hidden -File \"C:\\Users\\test\\AppData\\Local\\Lumin4ti\\medium-launchers\\operation-0123.ps1\"",
            arguments);
        Assert.IsFalse(arguments.Contains("Shell.Application", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ExplorerBrokerLauncherはscriptを圧縮して安全な環境変数へ渡す()
    {
        const string script = "Write-Output 'safe'";
        var launcher = UnelevatedCommandExecutor.BuildExplorerBrokerLauncherScript(
            script,
            @"C:\Users\O'Brien\result.json",
            @"C:\Users\O'Brien\process.pid");

        StringAssert.Contains(launcher, "$env:LUMIN4TI_RESULT_PATH='C:\\Users\\O''Brien\\result.json'");
        StringAssert.Contains(launcher, "WriteAllText('C:\\Users\\O''Brien\\process.pid'");
        Assert.IsFalse(launcher.Contains(script, StringComparison.Ordinal));

        const string prefix = "$env:LUMIN4TI_SCRIPT_GZIP_B64='";
        var payloadStart = launcher.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        var payloadEnd = launcher.IndexOf("'\r\n", payloadStart, StringComparison.Ordinal);
        Assert.IsTrue(payloadStart >= prefix.Length);
        Assert.IsTrue(payloadEnd > payloadStart);
        Assert.AreEqual(script, DecodeScript(launcher[payloadStart..payloadEnd]));
    }

    [TestMethod]
    public void ExplorerBroker引数はquoteを含むlauncherPathを拒否する()
    {
        Assert.Throws<ArgumentException>(() =>
            UnelevatedCommandExecutor.BuildExplorerBrokerArguments(
                "C:\\Users\\test\\bad\"name.ps1"));
    }

    [TestMethod]
    public void Mediumプロセス起動失敗は両APIのWin32番号を示す()
    {
        var message = UnelevatedCommandExecutor.BuildProcessLaunchFailureMessage(1314, 5);

        StringAssert.Contains(message, "CreateProcessWithTokenW=Win32 1314");
        StringAssert.Contains(message, "CreateProcessAsUserW=Win32 5");
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
