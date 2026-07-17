using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows.Actions;
using Microsoft.Win32;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class NtpConfigActionTests
{
    [TestMethod]
    public async Task サービスが停止中なら停止状態を維持して設定する()
    {
        var writes = 0;
        var executor = new RecordingExecutor((_, _, _, _) =>
            throw new InvalidOperationException("サービスコマンドは実行されないはずです"));
        var action = new NtpConfigAction(executor, () => false, () => writes++);

        var result = await action.ExecuteAsync();

        Assert.IsTrue(result.Success, result.Detail);
        StringAssert.Contains(result.Detail, "元の停止状態を維持");
        Assert.AreEqual(1, writes);
        Assert.HasCount(0, executor.Invocations);
    }

    [TestMethod]
    public async Task サービスが稼働中なら停止と設定の後に再起動する()
    {
        var events = new List<string>();
        var executor = new RecordingExecutor((_, _, arguments, _) =>
        {
            events.Add(arguments);
            return Result(success: true);
        });
        var action = new NtpConfigAction(executor, () => true, () => events.Add("write"));

        var result = await action.ExecuteAsync();

        Assert.IsTrue(result.Success, result.Detail);
        CollectionAssert.AreEqual(
            new[] { "stop w32time", "write", "start w32time" },
            events);
        Assert.HasCount(2, executor.Invocations);
        Assert.IsFalse(executor.Invocations[1].Token.CanBeCanceled, "再起動は補償用の CancellationToken.None で実行します");
    }

    [TestMethod]
    public async Task 停止成功直後にキャンセルされても再起動してから伝播する()
    {
        using var cancellation = new CancellationTokenSource();
        var writes = 0;
        var executor = new RecordingExecutor((call, _, _, _) =>
        {
            if (call == 1)
            {
                cancellation.Cancel();
            }

            return Result(success: true);
        });
        var action = new NtpConfigAction(executor, () => true, () => writes++);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => action.ExecuteAsync(cancellation.Token));

        Assert.AreEqual(0, writes, "キャンセル後に設定を書き始めてはいけません");
        Assert.HasCount(2, executor.Invocations);
        Assert.AreEqual("stop w32time", executor.Invocations[0].Arguments);
        Assert.AreEqual("start w32time", executor.Invocations[1].Arguments);
        Assert.IsFalse(executor.Invocations[1].Token.CanBeCanceled, "キャンセル済み token を補償へ渡してはいけません");
    }

    [TestMethod]
    public async Task 設定書き込みが失敗しても再起動してから元の例外を伝播する()
    {
        var executor = new RecordingExecutor((_, _, _, _) => Result(success: true));
        var action = new NtpConfigAction(
            executor,
            () => true,
            () => throw new InvalidOperationException("write failed"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => action.ExecuteAsync());

        Assert.AreEqual("write failed", exception.Message);
        Assert.HasCount(2, executor.Invocations);
        Assert.AreEqual("start w32time", executor.Invocations[1].Arguments);
        Assert.IsFalse(executor.Invocations[1].Token.CanBeCanceled);
    }

    [TestMethod]
    public async Task サービス停止に失敗したら設定を書き込まない()
    {
        var writes = 0;
        var executor = new RecordingExecutor((_, _, _, _) => Result(success: false, error: "stop failed"));
        var action = new NtpConfigAction(executor, () => true, () => writes++);

        var result = await action.ExecuteAsync();

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Detail, "設定を変更しませんでした");
        Assert.AreEqual(0, writes);
        Assert.HasCount(1, executor.Invocations);
    }

    [TestMethod]
    public void NTPレジストリの途中失敗は全値を開始前へ戻す()
    {
        var accessor = new FailingRegistryAccessor();
        var store = new TransactionalNtpConfigurationStore(accessor);

        var error = Assert.Throws<InvalidOperationException>(store.Apply);

        StringAssert.Contains(error.Message, "ロールバックしました");
        Assert.AreEqual("time.windows.com", accessor.Values["NtpServer"].ToRegistryValue());
        Assert.AreEqual("NT5DS", accessor.Values["Type"].ToRegistryValue());
        Assert.AreEqual(10, accessor.Values["AnnounceFlags"].ToRegistryValue());
    }

    private static CommandExecutionResult Result(bool success, string error = "") =>
        new(success, "net.exe", success ? 0 : 1, string.Empty, error);

    private sealed record Invocation(
        string FileName,
        string Arguments,
        CancellationToken Token);

    private sealed class RecordingExecutor(
        Func<int, string, string, CancellationToken, CommandExecutionResult> callback) : ICommandExecutor
    {
        private int _callCount;

        public List<Invocation> Invocations { get; } = [];

        public Task<CommandExecutionResult> RunAsync(
            string fileName,
            string arguments,
            CancellationToken ct = default,
            IProgress<string>? onOutputLine = null)
        {
            var call = Interlocked.Increment(ref _callCount);
            Invocations.Add(new Invocation(fileName, arguments, ct));
            return Task.FromResult(callback(call, fileName, arguments, ct));
        }
    }

    private sealed class FailingRegistryAccessor : IRegistryValueAccessor
    {
        private int _writeCount;

        public Dictionary<string, RegistryValueSnapshot> Values { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["NtpServer"] = RegistryValueSnapshot.FromRegistry(RegistryValueKind.String, "time.windows.com"),
            ["Type"] = RegistryValueSnapshot.FromRegistry(RegistryValueKind.String, "NT5DS"),
            ["AnnounceFlags"] = RegistryValueSnapshot.Dword(10),
        };

        public RegistryValueSnapshot Read(RegistryToggleSpec spec) => Values[spec.Name];

        public void Write(RegistryToggleSpec spec, RegistryValueSnapshot value)
        {
            _writeCount++;
            if (_writeCount == 2)
            {
                throw new IOException("simulated second write failure");
            }

            Values[spec.Name] = value;
        }
    }
}
