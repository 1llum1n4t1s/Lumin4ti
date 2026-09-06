using System.Diagnostics;
using System.Text;
using Lumin4ti.Core.Services;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class JobProcessTests
{
    [TestMethod]
    public async Task 起動直後に生成した孫も同じJobへ所属する()
    {
        // OS 設定を触らない PowerShell を起動し、直ちに生成した孫の所属を確認する。
        var script = "$p=Start-Process -FilePath ($PSHOME+'\\powershell.exe') -ArgumentList '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 15' -WindowStyle Hidden -PassThru; $p.Id; Start-Sleep -Seconds 15";
        using var child = JobProcess.Start(SystemProcessResolver.Resolve("powershell.exe"),
            "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        Process? grandchild = null;
        try
        {
            using var reader = new StreamReader(child.StandardOutput, leaveOpen: true);
            var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(int.TryParse(line, out var processId), line);
            grandchild = Process.GetProcessById(processId);
            Assert.IsTrue(ProcessJobTracker.ContainsProcess(child.Process.Handle));
            Assert.IsTrue(ProcessJobTracker.ContainsProcess(grandchild.Handle));
        }
        finally
        {
            if (!child.Process.HasExited)
                child.Process.Kill(entireProcessTree: true);
            if (grandchild is not null)
            {
                if (!grandchild.HasExited)
                    grandchild.Kill(entireProcessTree: true);
                grandchild.Dispose();
            }
            await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task 標準入力はEOFとなり標準エラーと終了コードも回収する()
    {
        var executor = new ProcessCommandExecutor(TimeSpan.FromSeconds(10));
        var script = "if ($null -ne [Console]::ReadLine()) { exit 99 }; [Console]::Out.WriteLine('stdout'); [Console]::Error.WriteLine('stderr'); exit 7";
        var result = await executor.RunAsync("powershell.exe",
            "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        Assert.AreEqual(7, result.ExitCode, result.StandardError);
        StringAssert.Contains(result.StandardOutput, "stdout");
        StringAssert.Contains(result.StandardError, "stderr");
    }
}
