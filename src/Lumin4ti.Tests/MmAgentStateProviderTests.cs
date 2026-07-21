using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class MmAgentStateProviderTests
{
    [TestMethod]
    public async Task 初期取得後の切り替え成功をキャッシュへ反映する()
    {
        var executor = new SequenceExecutor(
            Result(success: true, output: "{\"MemoryCompression\":true}"),
            Result(success: true));
        var provider = new MmAgentStateProvider(executor);
        var toggle = CreateToggle(executor, provider);

        Assert.AreEqual(true, await toggle.GetStateAsync());

        var result = await toggle.SetStateAsync(false);

        Assert.IsTrue(result.Success, result.Detail);
        Assert.AreEqual(false, await toggle.GetStateAsync());
        Assert.AreEqual(2, executor.CallCount, "切り替え後の Get で古い共有ロードや追加照会を使ってはいけません");
    }

    [TestMethod]
    public async Task 共有ロード失敗後の次回取得で再試行する()
    {
        var executor = new SequenceExecutor(
            Result(success: false, error: "temporary failure"),
            Result(success: true, output: "{\"MemoryCompression\":true}"));
        var provider = new MmAgentStateProvider(executor);

        Assert.IsNull(await provider.GetAsync("MemoryCompression"));
        Assert.AreEqual(true, await provider.GetAsync("MemoryCompression"));
        Assert.AreEqual(2, executor.CallCount);
    }

    [TestMethod]
    public async Task 共有ロードの例外後も次回取得で再試行する()
    {
        var executor = new CallbackExecutor((call, _) => call switch
        {
            1 => Task.FromException<CommandExecutionResult>(new InvalidOperationException("temporary exception")),
            2 => Task.FromResult(Result(success: true, output: "{\"MemoryCompression\":true}")),
            _ => throw new InvalidOperationException($"予期しない呼び出し: {call}"),
        });
        var provider = new MmAgentStateProvider(executor);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetAsync("MemoryCompression"));
        Assert.AreEqual(true, await provider.GetAsync("MemoryCompression"));
        Assert.AreEqual(2, executor.CallCount);
    }

    [TestMethod]
    public async Task 失敗後の並行再試行も一つの共有ロードへ集約する()
    {
        var executor = new RetryingSharedLoadExecutor();
        var provider = new MmAgentStateProvider(executor);

        var initialMemory = provider.GetAsync("MemoryCompression");
        var initialCombining = provider.GetAsync("PageCombining");
        await executor.FirstStarted;
        Assert.AreEqual(1, executor.CallCount);

        executor.CompleteFirst(Result(success: false, error: "temporary failure"));
        Assert.IsNull(await initialMemory);
        Assert.IsNull(await initialCombining);

        var retryMemory = provider.GetAsync("MemoryCompression");
        var retryCombining = provider.GetAsync("PageCombining");
        await executor.SecondStarted;
        Assert.AreEqual(2, executor.CallCount);

        executor.CompleteSecond(Result(
            success: true,
            output: "{\"MemoryCompression\":true,\"PageCombining\":false}"));
        Assert.AreEqual(true, await retryMemory);
        Assert.AreEqual(false, await retryCombining);
        Assert.AreEqual(true, await provider.GetAsync("MemoryCompression"));
        Assert.AreEqual(2, executor.CallCount);
    }

    [TestMethod]
    public async Task 冪等成功で確認した変更後値をキャッシュへ反映する()
    {
        var executor = new SequenceExecutor(
            Result(success: true, output: "{\"MemoryCompression\":true}"),
            Result(success: false, error: "already disabled"),
            Result(success: true, output: "{\"MemoryCompression\":false}"));
        var provider = new MmAgentStateProvider(executor);
        var toggle = CreateToggle(executor, provider);

        Assert.AreEqual(true, await toggle.GetStateAsync());

        var result = await toggle.SetStateAsync(false);

        Assert.IsTrue(result.Success, result.Detail);
        Assert.AreEqual(false, await toggle.GetStateAsync());
        Assert.AreEqual(3, executor.CallCount);
    }

    [TestMethod]
    public async Task 共有ロード待機は呼び出し側のキャンセルを尊重する()
    {
        var executor = new PendingExecutor();
        var provider = new MmAgentStateProvider(executor);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        try
        {
            await provider.GetAsync("MemoryCompression", cts.Token);
            Assert.Fail("キャンセルされるはずです");
        }
        catch (OperationCanceledException)
        {
            // 期待どおり
        }
        finally
        {
            executor.Complete();
        }
    }

    [TestMethod]
    public async Task 共有ロード失敗中の切り替え成功を優先する()
    {
        var executor = new InitialLoadRaceExecutor();
        var provider = new MmAgentStateProvider(executor);
        var toggle = CreateToggle(executor, provider);

        var loading = provider.GetAsync("MemoryCompression");
        Assert.AreEqual(1, executor.CallCount);

        var result = await toggle.SetStateAsync(false);
        executor.CompleteInitial(Result(success: false, error: "initial load failed"));

        Assert.IsTrue(result.Success, result.Detail);
        Assert.AreEqual(false, await loading);
        Assert.AreEqual(2, executor.CallCount);
    }

    [TestMethod]
    public async Task 切り替え失敗後に確認した実状態を古いキャッシュより優先する()
    {
        var executor = new SequenceExecutor(
            Result(success: true, output: "{\"MemoryCompression\":true}"),
            Result(success: false, error: "enable failed"),
            Result(success: true, output: "{\"MemoryCompression\":false}"));
        var provider = new MmAgentStateProvider(executor);
        var toggle = CreateToggle(executor, provider);

        Assert.AreEqual(true, await toggle.GetStateAsync());

        var result = await toggle.SetStateAsync(true);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(false, await toggle.GetStateAsync());
        Assert.AreEqual(3, executor.CallCount);
    }

    [TestMethod]
    public async Task OS非対応エラー後は機能を操作不能にする()
    {
        var executor = new SequenceExecutor(
            Result(success: true, output: "{\"MemoryCompression\":true}"),
            Result(success: false, error: "Disable-MMAgent : この要求はサポートされていません。"),
            Result(success: true, output: "{\"MemoryCompression\":true}"));
        var provider = new MmAgentStateProvider(executor);
        var toggle = CreateToggle(executor, provider);

        Assert.AreEqual(true, await toggle.GetStateAsync());

        var result = await toggle.SetStateAsync(false);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Detail, "操作を無効化しました");
        Assert.IsNull(await toggle.GetStateAsync());
        Assert.AreEqual(3, executor.CallCount, "非対応判定後に追加の状態照会を実行してはいけません");
    }

    [TestMethod]
    [DataRow("OperationAPI")]
    [DataRow("ApplicationLaunchPrefetching")]
    public async Task Windowsビルドで先回りせず実コマンドから状態を取得する(string propertyName)
    {
        var executor = new SequenceExecutor(
            Result(success: true, output: $"{{\"{propertyName}\":true}}"));
        var provider = new MmAgentStateProvider(executor);
        var toggle = new MmAgentFeatureToggle(
            executor,
            provider,
            propertyName,
            "test-mmagent",
            propertyName,
            "テスト用の説明です。");

        Assert.AreEqual(true, await toggle.GetStateAsync());
        Assert.AreEqual(1, executor.CallCount);
    }

    [TestMethod]
    public void OS非対応エラーを言語とHRESULTから判定する()
    {
        Assert.IsTrue(MmAgentFeatureToggle.IsNotSupportedError("この要求はサポートされていません。"));
        Assert.IsTrue(MmAgentFeatureToggle.IsNotSupportedError("The request is not supported"));
        Assert.IsTrue(MmAgentFeatureToggle.IsNotSupportedError("HRESULT 0x80070032"));
        Assert.IsFalse(MmAgentFeatureToggle.IsNotSupportedError("Access is denied"));
    }

    [TestMethod]
    public async Task 変更中のキャンセル後は次回取得で実状態を再確認する()
    {
        using var cts = new CancellationTokenSource();
        var executor = new CallbackExecutor((call, ct) => call switch
        {
            1 => Task.FromResult(Result(success: true, output: "{\"MemoryCompression\":true}")),
            2 => CancelCommand(cts, ct),
            3 => Task.FromResult(Result(success: true, output: "{\"MemoryCompression\":false}")),
            _ => throw new InvalidOperationException($"予期しない呼び出し: {call}"),
        });
        var provider = new MmAgentStateProvider(executor);
        var toggle = CreateToggle(executor, provider);

        Assert.AreEqual(true, await toggle.GetStateAsync());

        try
        {
            await toggle.SetStateAsync(false, cts.Token);
            Assert.Fail("キャンセルされるはずです");
        }
        catch (OperationCanceledException)
        {
            // 期待どおり
        }

        Assert.AreEqual(false, await toggle.GetStateAsync());
        Assert.AreEqual(3, executor.CallCount);
    }

    [TestMethod]
    public async Task 同一プロパティへの並行切り替えを直列化する()
    {
        var executor = new SerializedSetExecutor();
        var provider = new MmAgentStateProvider(executor);
        var toggle = CreateToggle(executor, provider);

        var first = toggle.SetStateAsync(false);
        await executor.FirstStarted;
        var second = toggle.SetStateAsync(true);
        await Task.Delay(50);
        Assert.AreEqual(1, executor.CallCount, "2つ目のコマンドは1つ目の完了前に開始してはいけません");

        executor.CompleteFirst();
        await Task.WhenAll(first, second);

        Assert.AreEqual(2, executor.CallCount);
        Assert.AreEqual(true, await toggle.GetStateAsync());
    }

    private static MmAgentFeatureToggle CreateToggle(
        ICommandExecutor executor,
        MmAgentStateProvider provider) =>
        new(
            executor,
            provider,
            "MemoryCompression",
            "mmagent-memory-compression",
            "メモリ圧縮",
            "テスト用の説明です。");

    private static CommandExecutionResult Result(
        bool success,
        string output = "",
        string error = "") =>
        new(success, "powershell.exe", success ? 0 : 1, output, error);

    private static Task<CommandExecutionResult> CancelCommand(
        CancellationTokenSource cancellation,
        CancellationToken ct)
    {
        cancellation.Cancel();
        return Task.FromCanceled<CommandExecutionResult>(ct);
    }

    private sealed class SequenceExecutor(params CommandExecutionResult[] results) : ICommandExecutor
    {
        private readonly Queue<CommandExecutionResult> _results = new(results);

        public int CallCount { get; private set; }

        public Task<CommandExecutionResult> RunAsync(
            string fileName,
            string arguments,
            CancellationToken ct = default,
            IProgress<string>? onOutputLine = null)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class PendingExecutor : ICommandExecutor
    {
        private readonly TaskCompletionSource<CommandExecutionResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CommandExecutionResult> RunAsync(
            string fileName,
            string arguments,
            CancellationToken ct = default,
            IProgress<string>? onOutputLine = null) => _completion.Task;

        public void Complete() => _completion.TrySetResult(
            new CommandExecutionResult(false, "powershell.exe", -1, string.Empty, "test complete"));
    }

    private sealed class RetryingSharedLoadExecutor : ICommandExecutor
    {
        private readonly TaskCompletionSource _firstStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CommandExecutionResult> _firstCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CommandExecutionResult> _secondCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public Task FirstStarted => _firstStarted.Task;

        public Task SecondStarted => _secondStarted.Task;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<CommandExecutionResult> RunAsync(
            string fileName,
            string arguments,
            CancellationToken ct = default,
            IProgress<string>? onOutputLine = null)
        {
            return Interlocked.Increment(ref _callCount) switch
            {
                1 => Start(_firstStarted, _firstCompletion),
                2 => Start(_secondStarted, _secondCompletion),
                var call => throw new InvalidOperationException($"予期しない呼び出し: {call}"),
            };
        }

        public void CompleteFirst(CommandExecutionResult result) => _firstCompletion.TrySetResult(result);

        public void CompleteSecond(CommandExecutionResult result) => _secondCompletion.TrySetResult(result);

        private static Task<CommandExecutionResult> Start(
            TaskCompletionSource started,
            TaskCompletionSource<CommandExecutionResult> completion)
        {
            started.TrySetResult();
            return completion.Task;
        }
    }

    private sealed class InitialLoadRaceExecutor : ICommandExecutor
    {
        private readonly TaskCompletionSource<CommandExecutionResult> _initial =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<CommandExecutionResult> RunAsync(
            string fileName,
            string arguments,
            CancellationToken ct = default,
            IProgress<string>? onOutputLine = null) => Interlocked.Increment(ref _callCount) switch
            {
                1 => _initial.Task,
                2 => Task.FromResult(Result(success: true)),
                var call => throw new InvalidOperationException($"予期しない呼び出し: {call}"),
            };

        public void CompleteInitial(CommandExecutionResult result) => _initial.TrySetResult(result);
    }

    private sealed class CallbackExecutor(
        Func<int, CancellationToken, Task<CommandExecutionResult>> callback) : ICommandExecutor
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<CommandExecutionResult> RunAsync(
            string fileName,
            string arguments,
            CancellationToken ct = default,
            IProgress<string>? onOutputLine = null) => callback(Interlocked.Increment(ref _callCount), ct);
    }

    private sealed class SerializedSetExecutor : ICommandExecutor
    {
        private readonly TaskCompletionSource _firstStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CommandExecutionResult> _firstCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public Task FirstStarted => _firstStarted.Task;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<CommandExecutionResult> RunAsync(
            string fileName,
            string arguments,
            CancellationToken ct = default,
            IProgress<string>? onOutputLine = null)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                _firstStarted.TrySetResult();
                return _firstCompletion.Task;
            }

            return call == 2
                ? Task.FromResult(Result(success: true))
                : throw new InvalidOperationException($"予期しない呼び出し: {call}");
        }

        public void CompleteFirst() => _firstCompletion.TrySetResult(Result(success: true));
    }
}
