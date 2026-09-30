using System.Text;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class ProcessCommandExecutorDecodeTests
{
    [TestMethod]
    public void UTF8バイト列はUTF8として解釈される()
    {
        var bytes = Encoding.UTF8.GetBytes("完了しました");

        Assert.AreEqual("完了しました", ProcessCommandExecutor.DecodeConsoleOutput(bytes));
    }

    [TestMethod]
    public void 実効OEMバイト列はフォールバックで正しく解釈される()
    {
        // static 初期化後の実効値を使い、他試験の culture や実行順に左右されない。
        var field = typeof(ProcessCommandExecutor).GetField(
            "OemEncoding", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(field);
        var oem = field.GetValue(null) as Encoding;
        Assert.IsNotNull(oem);
        var bytes = Enumerable.Range(128, 64)
            .Select(value => new[] { (byte)value })
            .Concat(Enumerable.Range(128, 64).SelectMany(first =>
                Enumerable.Range(0, 256).Select(second => new[] { (byte)first, (byte)second })))
            .FirstOrDefault(candidate => oem.GetBytes(oem.GetString(candidate)).SequenceEqual(candidate));
        Assert.IsNotNull(bytes, $"OEM {oem.CodePage} に表現可能な非 UTF-8 canary がありません。");
        Assert.ThrowsExactly<DecoderFallbackException>(() => new UTF8Encoding(false, true).GetString(bytes));

        Assert.AreEqual(oem.GetString(bytes), ProcessCommandExecutor.DecodeConsoleOutput(bytes));
    }

    [TestMethod]
    public async Task 上限時間を超えた外部プロセスは失敗として終了する()
    {
        var executor = new ProcessCommandExecutor(TimeSpan.FromMilliseconds(200));

        var result = await executor.RunAsync(
            "powershell.exe",
            "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 5\"");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.StandardError, "タイムアウト");
    }

    [TestMethod]
    public async Task 改行のない長大な進捗行は固定上限で省略される()
    {
        var input = new MemoryStream(Enumerable.Repeat((byte)'x', 1024 * 1024).ToArray());
        var captured = new MemoryStream();
        var lines = new List<string>();

        await ProcessCommandExecutor.PumpAsync(
            input,
            captured,
            new InlineProgress<string>(lines.Add),
            CancellationToken.None);

        Assert.HasCount(1, lines);
        StringAssert.Contains(lines[0], "長すぎる出力を省略");
        Assert.IsLessThanOrEqualTo(ProcessCommandExecutor.MaxProgressLineBytes + 32, lines[0].Length);
        Assert.AreEqual(1024 * 1024, captured.Length, "全ストリームはデッドロック防止のため読み切る必要があります");
    }

    [TestMethod]
    public async Task Core内のawaitは呼出元SynchronizationContextへ戻らない()
    {
        var context = new CountingSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        var executor = new ProcessCommandExecutor();
        Task<CommandExecutionResult> execution;

        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            execution = executor.RunAsync(
                "powershell.exe",
                "-NoProfile -NonInteractive -Command \"Start-Sleep -Milliseconds 100; Write-Output done\"");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        var result = await execution;

        Assert.IsTrue(result.Success, result.StandardError);
        Assert.AreEqual(0, context.PostCount);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class CountingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
            ThreadPool.QueueUserWorkItem(_ => d(state));
        }
    }
}
