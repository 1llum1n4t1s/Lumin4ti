using System.Diagnostics;
using Lumin4ti.Core.Services;

namespace Lumin4ti.Tests;

/// <summary>
/// 出力パイプを継承した孫プロセスが残っても、起動したプロセスの終了で結果を返すことの回帰テスト。
/// 旧実装は EOF を待ってから終了を待っていたため、dism の DismHost のように孫が生き残ると
/// 実処理が数秒で終わっていても呼び出し側が延々と待たされていた。
/// </summary>
[TestClass]
public sealed class ProcessCommandExecutorDrainTests
{
    [TestMethod]
    public async Task 孫プロセスがパイプを握っていても終了後すぐ結果を返す()
    {
        var executor = new ProcessCommandExecutor(
            commandTimeout: TimeSpan.FromMinutes(1),
            outputDrainGrace: TimeSpan.FromMilliseconds(300));

        // cmd 自体は即座に終了し、start /b で起動した ping が出力ハンドルを持ったまま数秒残る
        var stopwatch = Stopwatch.StartNew();
        var result = await executor.RunAsync("cmd.exe", "/c start /b ping -n 6 127.0.0.1");
        stopwatch.Stop();

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        Assert.IsTrue(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"孫プロセスの終了を待たずに戻るべきだが {stopwatch.Elapsed.TotalSeconds:F1} 秒かかった");
    }

    [TestMethod]
    public async Task 通常のコマンドは出力を取りこぼさない()
    {
        var executor = new ProcessCommandExecutor(
            commandTimeout: TimeSpan.FromMinutes(1),
            outputDrainGrace: TimeSpan.FromSeconds(5));

        var result = await executor.RunAsync("cmd.exe", "/c echo lumin4ti-drain-test");

        Assert.IsTrue(result.Success, result.StandardError);
        StringAssert.Contains(result.StandardOutput, "lumin4ti-drain-test");
    }
}
