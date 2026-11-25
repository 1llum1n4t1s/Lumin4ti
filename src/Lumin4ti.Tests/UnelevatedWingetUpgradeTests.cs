using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class UnelevatedWingetUpgradeTests
{
    private const string WingetPath = @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.0_x64__8wekyb3d8bbwe\winget.exe";

    private static string Json(
        string schema = "1",
        string exit = "0",
        string output = "\"out\"",
        string error = "\"err\"") =>
        $"{{\"SchemaVersion\":{schema},\"ExitCode\":{exit},\"StandardOutput\":{output},\"StandardError\":{error}}}";

    [TestMethod]
    public void 正常な結果JSONを変換する()
    {
        Assert.IsTrue(UnelevatedWingetUpgrade.TryParseResult(
            Json(exit: "-1978335107"), WingetPath, out var result, out _));
        Assert.IsFalse(result.Success);
        Assert.AreEqual(-1978335107, result.ExitCode);
        Assert.AreEqual("out", result.StandardOutput);
        Assert.AreEqual("err", result.StandardError);

        Assert.IsTrue(UnelevatedWingetUpgrade.TryParseResult(Json(), WingetPath, out var ok, out _));
        Assert.IsTrue(ok.Success);
    }

    [TestMethod]
    public void 不正な結果JSONは拒否する()
    {
        var invalid = new[]
        {
            Json(schema: "2"),
            Json(schema: "\"1\""),
            Json(exit: "\"0\""),
            Json(exit: "1.5"),
            Json(output: "null"),
            Json(error: "5"),
            "[]",
            "not json",
            "{\"SchemaVersion\":1,\"ExitCode\":0,\"StandardOutput\":\"\"}",
            "{\"SchemaVersion\":1,\"SchemaVersion\":1,\"ExitCode\":0,\"StandardOutput\":\"\",\"StandardError\":\"\"}",
            "{\"SchemaVersion\":1,\"ExitCode\":0,\"StandardOutput\":\"\",\"StandardError\":\"\",\"Extra\":1}",
            Json(output: "\"" + new string('x', UnelevatedWingetUpgrade.MaximumStreamCharacters + 1) + "\""),
        };

        foreach (var json in invalid)
        {
            Assert.IsFalse(
                UnelevatedWingetUpgrade.TryParseResult(json, WingetPath, out _, out var error),
                json);
            Assert.IsFalse(string.IsNullOrEmpty(error), json);
        }
    }

    [TestMethod]
    public void スクリプトは単一引用を二重化しscopeを足さない()
    {
        var script = UnelevatedWingetUpgrade.BuildScript(
            @"C:\Apps\O'Brien\winget.exe",
            WingetUpgradeAction.BuildUpgradeArguments("Mozilla.Firefox"));

        StringAssert.Contains(script, @"'C:\Apps\O''Brien\winget.exe'");
        StringAssert.Contains(script, "upgrade --id Mozilla.Firefox --exact");
        StringAssert.Contains(script, "RedirectStandardInput=$true");
        StringAssert.Contains(script, "finally {");
        StringAssert.Contains(script, "-not $p.HasExited");
        StringAssert.Contains(script, "[Environment]::SystemDirectory,'taskkill.exe'");
        StringAssert.Contains(script, "/PID $p.Id /T /F");
        Assert.IsFalse(script.Contains("--scope", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("ArgumentList", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task 非昇格実行器へ固定スクリプトを渡して結果を返す()
    {
        var executor = new FakeExecutor(new(true, 0, Json(exit: "0"), string.Empty));

        var retry = await UnelevatedWingetUpgrade.RunAsync(
            "Mozilla.Firefox", executor, () => WingetPath, CancellationToken.None);

        Assert.IsTrue(retry.Completed);
        Assert.IsTrue(retry.Result!.Success);
        Assert.AreEqual(1, executor.Scripts.Count);
        StringAssert.Contains(executor.Scripts[0], "'" + WingetPath + "'");
    }

    [TestMethod]
    public async Task 起動不可と不正な入力と不正な結果は実行器を呼ばずまたは失敗にする()
    {
        var executor = new FakeExecutor(new(true, 0, "{}", string.Empty));

        var missing = await UnelevatedWingetUpgrade.RunAsync(
            "Mozilla.Firefox", executor, () => throw new FileNotFoundException("none"), CancellationToken.None);
        var alias = await UnelevatedWingetUpgrade.RunAsync(
            "Mozilla.Firefox", executor, () => "winget.exe", CancellationToken.None);
        var unsafeId = await UnelevatedWingetUpgrade.RunAsync(
            "bad id;calc", executor, () => WingetPath, CancellationToken.None);

        Assert.IsFalse(missing.Completed);
        Assert.IsFalse(alias.Completed);
        Assert.IsFalse(unsafeId.Completed);
        Assert.AreEqual(0, executor.Scripts.Count);

        var invalid = await UnelevatedWingetUpgrade.RunAsync(
            "Mozilla.Firefox", executor, () => WingetPath, CancellationToken.None);
        Assert.IsFalse(invalid.Completed);

        var failed = await UnelevatedWingetUpgrade.RunAsync(
            "Mozilla.Firefox",
            new FakeExecutor(new(false, -1, null, "launch failed")),
            () => WingetPath,
            CancellationToken.None);
        Assert.IsFalse(failed.Completed);
        StringAssert.Contains(failed.Error, "launch failed");
    }

    private sealed class FakeExecutor(UnelevatedCommandExecutionResult result) : IUnelevatedCommandExecutor
    {
        public List<string> Scripts { get; } = [];

        public Task<UnelevatedCommandExecutionResult> RunPowerShellAsync(string script, CancellationToken ct = default) =>
            RunPowerShellAsync(script, TimeSpan.FromMinutes(1), ct);

        public Task<UnelevatedCommandExecutionResult> RunPowerShellAsync(
            string script,
            TimeSpan timeout,
            CancellationToken ct = default)
        {
            Scripts.Add(script);
            return Task.FromResult(result);
        }
    }
}
