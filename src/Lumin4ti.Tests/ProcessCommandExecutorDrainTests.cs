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
        var drainGrace = TimeSpan.FromMilliseconds(300);
        var executor = new ProcessCommandExecutor(
            commandTimeout: TimeSpan.FromMinutes(1),
            outputDrainGrace: drainGrace);

        // cmd 自体は即座に終了し、start /b で起動した ping が出力ハンドルを持ったまま残る。
        // 孫の寿命 (約 5 秒) ではなく「猶予 + 余裕」で判定し、負荷による揺れに強くする。
        const int grandchildSeconds = 5;
        var stopwatch = Stopwatch.StartNew();
        var result = await executor.RunAsync("cmd.exe", $"/c start /b ping -n {grandchildSeconds + 1} 127.0.0.1");
        stopwatch.Stop();

        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        var limit = drainGrace + TimeSpan.FromSeconds(2);
        Assert.IsTrue(
            stopwatch.Elapsed < limit,
            $"孫プロセスの終了 (約 {grandchildSeconds} 秒) を待たずに戻るべきだが {stopwatch.Elapsed.TotalSeconds:F1} 秒かかった");
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
