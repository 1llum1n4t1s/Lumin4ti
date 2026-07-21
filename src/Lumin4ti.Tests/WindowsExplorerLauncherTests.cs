using System.Text;
using Lumin4ti.Core.Services;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class WindowsExplorerLauncherTests
{
    [TestMethod]
    public async Task Https対象はmediumTokenのShellExecuteへ値として渡す()
    {
        const string target = "https://lumin4ti.nephilim.jp/Lumin4ti-win.msi?x='quoted'";
        var executor = new RecordingExecutor();

        await WindowsExplorerLauncher.OpenAsync(target, executor);

        StringAssert.Contains(executor.Script!, "$shell.ShellExecute($target, '', '', 'open', 1)");
        Assert.IsFalse(executor.Script!.Contains(target, StringComparison.Ordinal),
            "対象を PowerShell source へ直接連結してはいけません");
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(target));
        StringAssert.Contains(executor.Script, encoded);
    }

    [TestMethod]
    public void 完全パスとHttps以外のschemeは拒否する()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            WindowsExplorerLauncher.NormalizeTarget("relative\\folder"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            WindowsExplorerLauncher.NormalizeTarget("file:///C:/Windows/System32"));
        Assert.ThrowsExactly<ArgumentException>(() =>
            WindowsExplorerLauncher.NormalizeTarget("http://example.invalid"));
    }

    [TestMethod]
    public void 完全パスは正規化する()
    {
        var path = Path.Combine(Path.GetTempPath(), "folder", "..", "logs");

        Assert.AreEqual(Path.GetFullPath(path), WindowsExplorerLauncher.NormalizeTarget(path));
    }

    private sealed class RecordingExecutor : IUnelevatedCommandExecutor
    {
        public string? Script { get; private set; }

        public Task<UnelevatedCommandExecutionResult> RunPowerShellAsync(
            string script,
            CancellationToken ct = default)
        {
            Script = script;
            return Task.FromResult(new UnelevatedCommandExecutionResult(
                true, 0, "{\"Success\":true}", string.Empty));
        }

        public Task<UnelevatedCommandExecutionResult> RunPowerShellAsync(
            string script,
            TimeSpan timeout,
            CancellationToken ct = default) => RunPowerShellAsync(script, ct);
    }
}
