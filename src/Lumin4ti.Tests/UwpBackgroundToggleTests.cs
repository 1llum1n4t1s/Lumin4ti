using System.Collections.Concurrent;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows.Actions;
using Microsoft.Extensions.Logging;
using SuperLightLogger;

namespace Lumin4ti.Tests;

[TestClass]
[DoNotParallelize]
public sealed class UwpBackgroundToggleTests
{
    private static readonly MemoryLogs Logs = new();

    [AssemblyInitialize]
    public static void InitializeLogging(TestContext context)
    {
        // LoggerBootstrap は取得済み ILog を保持するため、どのテストのログ取得よりも
        // 先に構成する。テスト途中の Configure では既存の NullLogger は切り替わらない。
        LogManager.Configure(builder => builder.ClearProviders().AddProvider(Logs));
    }

    [AssemblyCleanup]
    public static void ShutdownLogging() => LogManager.Shutdown();

    [TestMethod]
    public async Task 状態は現在の全対象の両方の値が一致した場合だけONになる()
    {
        var store = new FakeSettingsStore();
        store.Values["A"] = UwpBackgroundValues.Applied;
        store.Values["B"] = new(1, 0);
        var toggle = NewToggle(store);
        Assert.AreEqual(false, await toggle.GetStateAsync());
        store.Values["B"] = UwpBackgroundValues.Applied;
        Assert.AreEqual(true, await toggle.GetStateAsync());
        Assert.IsNull(toggle.StateDiagnostic);
        Assert.AreEqual(false, await new UwpBackgroundToggle(() => [], store).GetStateAsync());
    }

    [TestMethod]
    public async Task ON後の別インスタンスのOFFは元値に関係なく現在の全対象をWindows既定へ戻す()
    {
        var store = new FakeSettingsStore();
        store.Values["A"] = new(7, 8);
        store.Values["B"] = new(null, 0);
        Assert.IsTrue((await NewToggle(store).SetStateAsync(true)).Success);
        Assert.AreEqual(UwpBackgroundValues.Applied, store.Values["A"]);
        Assert.AreEqual(UwpBackgroundValues.Applied, store.Values["B"]);
        // 前回の適用後に追加・外部変更されたアプリも、今回の明示的な OFF の対象。
        store.Values["A"] = new(5, 6);
        store.Values["C"] = new(9, 10);
        Assert.IsTrue((await new UwpBackgroundToggle(() => ["A", "B", "C"], store).SetStateAsync(false)).Success);
        foreach (var family in new[] { "A", "B", "C" }) Assert.AreEqual(default, store.Values[family]);
        Assert.IsFalse((await NewToggle(store).GetStateAsync())!.Value);
    }

    [TestMethod]
    public async Task 前回の適用情報なしでもOFFは明示的な上書きを削除する()
    {
        var store = new FakeSettingsStore();
        store.Values["A"] = new(1, null);
        store.Values["B"] = new(0, 9);
        Assert.IsTrue((await NewToggle(store).SetStateAsync(false)).Success);
        Assert.AreEqual(default, store.Values["A"]);
        Assert.AreEqual(default, store.Values["B"]);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ペアの途中書込失敗は前の対象も含め操作直前の実値へ補償する(bool on)
    {
        var store = InitialStore(on);
        var originalA = store.Values["A"];
        var originalB = store.Values["B"];
        store.AfterFirstValue = (family, _, call, _) =>
        {
            if (call == 2) throw new IOException("PAIR_WRITE_DENIED " + family);
        };
        var result = await NewToggle(store).SetStateAsync(on);
        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        StringAssert.Contains(result.Detail, "PAIR_WRITE_DENIED B");
        Assert.AreEqual(originalA, store.Values["A"]);
        Assert.AreEqual(originalB, store.Values["B"]);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task 途中キャンセルは変更済みの全対象を補償してからCanceledにする(bool on)
    {
        using var cancellation = new CancellationTokenSource();
        var store = InitialStore(on);
        var originalA = store.Values["A"];
        var originalB = store.Values["B"];
        store.AfterFirstValue = (_, _, call, _) => { if (call == 2) cancellation.Cancel(); };
        var result = await NewToggle(store).SetStateAsync(on, cancellation.Token);
        Assert.AreEqual(MaintenanceActionStatus.Canceled, result.Status);
        Assert.AreEqual(originalA, store.Values["A"]);
        Assert.AreEqual(originalB, store.Values["B"]);
        Assert.IsTrue(store.CompensationTokens.All(token => !token.CanBeCanceled));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task 未着手対象への外部変更とキャンセルが重なったら外部値を保持し全件照合でFailedにする(bool on)
    {
        using var cancellation = new CancellationTokenSource();
        var store = InitialStore(on);
        var originalA = store.Values["A"];
        store.AfterFirstValue = (_, _, call, _) =>
        {
            if (call != 1) return;
            store.Values["B"] = new(7, 8);
            cancellation.Cancel();
        };

        var result = await NewToggle(store).SetStateAsync(on, cancellation.Token);

        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        Assert.AreEqual(originalA, store.Values["A"]);
        Assert.AreEqual(new UwpBackgroundValues(7, 8), store.Values["B"]);
        Assert.AreEqual(2, store.WriteCalls, "A の適用と補償だけを書き込み、未着手の B は保持する");
        StringAssert.Contains(result.Detail, "B: 補償後の照合失敗");
        StringAssert.Contains(result.Detail, "外部変更または補償不一致");
    }

    [TestMethod]
    public async Task 未着手対象の補償後読込に失敗したらCanceledにせず対象と原因を残す()
    {
        using var cancellation = new CancellationTokenSource();
        var store = InitialStore(true);
        var originalA = store.Values["A"];
        var originalB = store.Values["B"];
        store.AfterFirstValue = (_, _, call, _) => { if (call == 1) cancellation.Cancel(); };
        store.BeforeRead = (families, _) =>
        {
            if (families.Count == 1 && families[0] == "B" && store.WriteCalls == 2)
                throw new IOException("FINAL_READ_DENIED");
        };

        var result = await NewToggle(store).SetStateAsync(true, cancellation.Token);

        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        Assert.AreEqual(originalA, store.Values["A"]);
        Assert.AreEqual(originalB, store.Values["B"]);
        Assert.AreEqual(2, store.WriteCalls);
        StringAssert.Contains(result.Detail, "B: 補償後の照合失敗");
        StringAssert.Contains(result.Detail, "FINAL_READ_DENIED");
    }

    [TestMethod]
    public async Task キャンセル後に補償も失敗した場合はCanceledで隠さずFailedにする()
    {
        using var cancellation = new CancellationTokenSource();
        var store = InitialStore(true);
        store.AfterFirstValue = (_, _, call, _) =>
        {
            if (call == 2) cancellation.Cancel();
            if (call > 2) throw new IOException("COMPENSATION_DENIED");
        };
        var result = await NewToggle(store).SetStateAsync(true, cancellation.Token);
        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        StringAssert.Contains(result.Detail, "補償失敗");
        StringAssert.Contains(result.Detail, "COMPENSATION_DENIED");
    }

    [TestMethod]
    public async Task 操作開始前の外部変更は上書きせず先に変更した対象を補償する()
    {
        var store = InitialStore(true);
        var originalA = store.Values["A"];
        store.BeforeRead = (families, call) =>
        {
            if (families.Count == 1 && families[0] == "B") store.Values["B"] = new(7, 8);
        };
        var result = await NewToggle(store).SetStateAsync(true);
        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        Assert.AreEqual(originalA, store.Values["A"]);
        Assert.AreEqual(new UwpBackgroundValues(7, 8), store.Values["B"]);
        StringAssert.Contains(result.Detail, "外部");
    }

    [TestMethod]
    public async Task 失敗直後の外部変更を補償で上書きせず他の対象だけを補償する()
    {
        var store = InitialStore(true);
        var originalA = store.Values["A"];
        store.AfterFirstValue = (family, _, call, _) =>
        {
            if (call != 2) return;
            store.Values[family] = new(7, 8);
            throw new IOException("WRITE_DENIED");
        };
        var result = await NewToggle(store).SetStateAsync(true);
        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        Assert.AreEqual(originalA, store.Values["A"]);
        Assert.AreEqual(new UwpBackgroundValues(7, 8), store.Values["B"]);
        StringAssert.Contains(result.Detail, "外部変更");
    }

    [TestMethod]
    public async Task 書込成功の戻り値でも実値が違えばFailedにして補償する()
    {
        var store = InitialStore(true);
        var originalA = store.Values["A"];
        store.SkipSecondValueCall = 1;
        var result = await NewToggle(store).SetStateAsync(true);
        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        StringAssert.Contains(result.Detail, "変更を確認できません");
        Assert.AreEqual(originalA, store.Values["A"]);
    }

    [TestMethod]
    public async Task 補償の書込が成功しても元値へ戻らなければFailedに理由を残す()
    {
        var store = InitialStore(true);
        store.AfterFirstValue = (_, _, call, _) => { if (call == 2) throw new IOException("WRITE_DENIED"); };
        store.SkipSecondValueCall = 4;
        var result = await NewToggle(store).SetStateAsync(true);
        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        StringAssert.Contains(result.Detail, "補償を確認できません");
    }

    [TestMethod]
    public async Task 全対象の事前読込が失敗したら一件も変更しない()
    {
        var store = InitialStore(true);
        store.BeforeRead = (_, _) => throw new IOException("READ_DENIED");
        var toggle = NewToggle(store);
        var result = await toggle.SetStateAsync(true);
        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        Assert.AreEqual(0, store.WriteCalls);
        Assert.IsNull(await toggle.GetStateAsync());
        StringAssert.Contains(toggle.StateFailureReason!, "READ_DENIED");
    }

    [TestMethod]
    public async Task 補償と検証の失敗ログは項目名と原因を残し秘密値を伏せる()
    {
        var start = Logs.Messages.Count;
        var store = InitialStore(true);
        store.AfterFirstValue = (_, _, call, _) =>
        {
            if (call >= 2) throw new IOException("UWP_CAUSE token=UWP_SECRET_CANARY");
        };
        var toggle = NewToggle(store);
        Assert.AreEqual(MaintenanceActionStatus.Failed, (await toggle.SetStateAsync(true)).Status);
        var messages = Logs.Messages.Skip(start).ToArray();
        Assert.IsTrue(messages.Any(entry => entry.Level == LogLevel.Error &&
            entry.Message.Contains(toggle.Id) && entry.Message.Contains(toggle.Label) &&
            entry.Message.Contains("UWP_CAUSE") && entry.Message.Contains("[redacted]")));
        Assert.IsFalse(messages.Any(entry => entry.Message.Contains("UWP_SECRET_CANARY")));
    }

    private static UwpBackgroundToggle NewToggle(FakeSettingsStore store) => new(() => ["A", "B"], store);
    private static FakeSettingsStore InitialStore(bool on)
    {
        var store = new FakeSettingsStore();
        store.Values["A"] = on ? new(null, 0) : UwpBackgroundValues.Applied;
        store.Values["B"] = on ? new(0, null) : UwpBackgroundValues.Applied;
        return store;
    }

    private sealed class FakeSettingsStore : IUwpBackgroundSettingsStore
    {
        public Dictionary<string, UwpBackgroundValues> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Action<IReadOnlyList<string>, int>? BeforeRead { get; set; }
        public Action<string, UwpBackgroundValues, int, CancellationToken>? AfterFirstValue { get; set; }
        public int SkipSecondValueCall { get; set; }
        public int WriteCalls { get; private set; }
        private int _readCalls;
        public List<CancellationToken> CompensationTokens { get; } = [];
        public IReadOnlyDictionary<string, UwpBackgroundValues> ReadMany(IReadOnlyList<string> families, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            BeforeRead?.Invoke(families, ++_readCalls);
            return families.ToDictionary(family => family, family => Values.GetValueOrDefault(family), StringComparer.OrdinalIgnoreCase);
        }
        public void WriteMany(IReadOnlyList<KeyValuePair<string, UwpBackgroundValues>> values, CancellationToken ct)
        {
            foreach (var (family, target) in values)
            {
                ct.ThrowIfCancellationRequested();
                var call = ++WriteCalls;
                if (call > 2) CompensationTokens.Add(ct);
                Values[family] = Values.GetValueOrDefault(family) with { Disabled = target.Disabled };
                AfterFirstValue?.Invoke(family, target, call, ct);
                if (SkipSecondValueCall != call)
                    Values[family] = Values[family] with { DisabledByUser = target.DisabledByUser };
            }
        }
    }

    private sealed class MemoryLogs : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new MemoryLogger(Messages);
        public void Dispose() { }
        private sealed class MemoryLogger(ConcurrentQueue<(LogLevel Level, string Message)> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue((level, formatter(state, exception)));
        }
    }
}
