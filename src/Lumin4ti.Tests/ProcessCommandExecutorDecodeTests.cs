using System.Globalization;
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
    [DataRow("ja-JP", 932, new byte[] { 0x82, 0xA0 }, "あ")]
    [DataRow("en-US", 437, new byte[] { 0x82 }, "é")]
    public void 指定CultureのOEMバイト列は独立した期待値へ復号される(string locale, int codePage, byte[] bytes, string expected)
    {
        var oem = ProcessCommandExecutor.ResolveOemEncoding(CultureInfo.GetCultureInfo(locale));
        Assert.AreEqual(codePage, oem.CodePage);
        Assert.ThrowsExactly<DecoderFallbackException>(() => new UTF8Encoding(false, true).GetString(bytes));

        Assert.AreEqual(expected, ProcessCommandExecutor.DecodeConsoleOutput(bytes, oem));
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
