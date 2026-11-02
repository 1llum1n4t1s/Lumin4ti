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

    private static string Manifest(string applications) =>
        "<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\" " +
        "xmlns:uap10=\"http://schemas.microsoft.com/appx/manifest/uap/windows10/10\" " +
        "xmlns:previewsecurity2=\"http://schemas.microsoft.com/appx/manifest/preview/windows10/security/2\"><Applications>" +
        applications + "</Applications></Package>";

    [TestMethod]
    [DataRow("<Application uap10:RuntimeBehavior=\"windowsApp\" EntryPoint=\"Example.App\" />", true)]
    [DataRow("<Application uap10:RuntimeBehavior=\"win32App\" />", false)]
    [DataRow("<Application uap10:RuntimeBehavior=\"packagedClassicApp\" />", false)]
    [DataRow("<Application EntryPoint=\"Example.App\" />", true)]
    [DataRow("<Application StartPage=\"default.html\" />", true)]
    [DataRow("<Application previewsecurity2:RuntimeBehavior=\"windowsApp\" EntryPoint=\"Example.App\" />", true)]
    [DataRow("<Application previewsecurity2:RuntimeBehavior=\"win32App\" />", false)]
    [DataRow("<Application previewsecurity2:RuntimeBehavior=\"packagedClassicApp\" />", false)]
    [DataRow("<Application previewsecurity2:RuntimeBehavior=\"appSilo\" />", false)]
    [DataRow("<Application EntryPoint=\"Windows.FullTrustApplication\" />", false)]
    [DataRow("<Application EntryPoint=\"windows.partialTrustApplication\" />", false)]
    [DataRow("<Application EntryPoint=\"Windows.FullTrustApplication\" /><Application EntryPoint=\"Example.App\" />", true)]
    [DataRow("<Application Id=\"SourceData\"><Extensions><Extension Category=\"windows.appExtension\" /></Extensions></Application>", false)]
    [DataRow("<Application Id=\"Codec\"><Extensions><Extension Category=\"windows.mediaCodec\" /></Extensions></Application>", false)]
    [DataRow("<Application Id=\"Background\"><Extensions><Extension Category=\"windows.backgroundTasks\" EntryPoint=\"Example.BackgroundTask\" /></Extensions></Application>", true)]
    [DataRow("<Application Id=\"Background\"><Extensions><Extension Category=\"windows.backgroundTasks\" uap10:RuntimeBehavior=\"win32App\" /></Extensions></Application>", false)]
    [DataRow("", false)]
    public async Task UWPを含むパッケージだけへ一括ONを書き込む(string applications, bool expectedTarget)
    {
        var store = new FakeSettingsStore();
        var setting = new UwpBackgroundBatchSetting(() =>
        {
            using var reader = new StringReader(Manifest(applications));
            return UwpBackgroundBatchSetting.ContainsUwpApplications(reader) ? ["A"] : [];
        }, store);
        Assert.IsTrue((await setting.ApplyAsync(true)).Success);
        Assert.AreEqual(expectedTarget ? 1 : 0, store.WriteCalls);
        Assert.AreEqual(expectedTarget, store.Values.ContainsKey("A"));
        if (expectedTarget) Assert.AreEqual(UwpBackgroundValues.Enabled, store.Values["A"]);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    public async Task 判別不能やDTDや読込不能は既知UWPも含め全件書込前に失敗する(int failure)
    {
        var store = new FakeSettingsStore();
        var setting = new UwpBackgroundBatchSetting(() =>
        {
            using var known = new StringReader(Manifest("<Application EntryPoint=\"Example.App\" />"));
            Assert.IsTrue(UwpBackgroundBatchSetting.ContainsUwpApplications(known));
            using TextReader invalid = failure switch
            {
                0 => new StringReader("<!DOCTYPE Package [<!ENTITY x 'value'>]>" + Manifest("")),
                1 => new StringReader(Manifest("<Application Executable=\"Unknown.exe\" />")),
                2 => new FailingManifestReader(),
                3 => new StringReader(Manifest("<Application uap10:RuntimeBehavior=\"unknown\" />")),
                4 => new StringReader(Manifest("<Application uap10:RuntimeBehavior=\"windowsApp\" previewsecurity2:RuntimeBehavior=\"win32App\" />")),
                5 => new StringReader(Manifest("<Application previewsecurity2:RuntimeBehavior=\"unknown\" />")),
                _ => new StringReader("<Package xmlns=\"urn:unknown\" />"),
            };
            UwpBackgroundBatchSetting.ContainsUwpApplications(invalid);
            return ["A", "B"];
        }, store);
        var result = await setting.ApplyAsync(true);
        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        StringAssert.Contains(result.Detail, "設定は変更していません");
        Assert.AreEqual(0, store.WriteCalls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task 起動属性のないデータ宣言を除外して背景タスク専用UWPと通常UWPを一括設定する(bool enabled)
    {
        var store = new FakeSettingsStore();
        var manifests = new Dictionary<string, string>
        {
            ["SourceData"] = Manifest("<Application Id=\"SourceData\"><Extensions><Extension Category=\"windows.appExtension\" /></Extensions></Application>"),
            ["Codec"] = Manifest("<Application Id=\"Codec\"><Extensions><Extension Category=\"windows.mediaCodec\" /></Extensions></Application>"),
            ["Background"] = Manifest("<Application Id=\"App\"><Extensions><Extension Category=\"windows.backgroundTasks\" EntryPoint=\"ContentDeliveryManager.Background.RegistrationTask\" /></Extensions></Application>"),
            ["Uwp"] = Manifest("<Application EntryPoint=\"Example.App\" />"),
        };
        var setting = new UwpBackgroundBatchSetting(() => manifests.Where(pair =>
        {
            using var reader = new StringReader(pair.Value);
            return UwpBackgroundBatchSetting.ContainsUwpApplications(reader);
        }).Select(pair => pair.Key).ToArray(), store);

        Assert.IsTrue((await setting.ApplyAsync(enabled)).Success);
        CollectionAssert.AreEquivalent(new[] { "Background", "Uwp" }, store.Values.Keys.ToArray());
        foreach (var value in store.Values.Values)
            Assert.AreEqual(enabled ? UwpBackgroundValues.Enabled : UwpBackgroundValues.DisabledValues, value);
    }

    private sealed class FailingManifestReader : TextReader
    {
        public override int Read(char[] buffer, int index, int count) => throw new IOException("MANIFEST_READ_DENIED");
    }

    [TestMethod]
    public async Task 旧Windowsパッケージを除外してWindows10のJavaScriptUWPへ適用する()
    {
        var store = new FakeSettingsStore();
        var setting = new UwpBackgroundBatchSetting(() =>
        {
            var manifests = new Dictionary<string, string>
            {
                ["Legacy"] = "<Package xmlns=\"http://schemas.microsoft.com/appx/2010/manifest\"><Applications><Application StartPage=\"default.html\" /></Applications></Package>",
                ["JavaScript"] = Manifest("<Application StartPage=\"default.html\" />"),
                ["Desktop"] = Manifest("<Application previewsecurity2:RuntimeBehavior=\"appSilo\" />"),
            };
            return manifests.Where(pair =>
            {
                using var reader = new StringReader(pair.Value);
                return UwpBackgroundBatchSetting.ContainsUwpApplications(reader);
            }).Select(pair => pair.Key).ToArray();
        }, store);
        Assert.IsTrue((await setting.ApplyAsync(true)).Success);
        CollectionAssert.AreEquivalent(new[] { "JavaScript" }, store.Values.Keys.ToArray());
        Assert.AreEqual(UwpBackgroundValues.Enabled, store.Values["JavaScript"]);
    }

    [TestMethod]
    public async Task 対象の再列挙に失敗したら書き込まず失敗にする()
    {
        var store = new FakeSettingsStore();
        var setting = new UwpBackgroundBatchSetting(() => throw new IOException("ENUMERATION_DENIED"), store);
        var result = await setting.ApplyAsync(false);
        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        StringAssert.Contains(result.Detail, "ENUMERATION_DENIED");
        Assert.AreEqual(0, store.WriteCalls);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task 同方向の連続実行と新アプリ追加後の再実行は現在の全対象へ適用する(bool enabled)
    {
        var store = new FakeSettingsStore();
        store.Values["A"] = new(7, 8);
        store.Values["B"] = new(null, 0);
        var families = new List<string> { "A", "B" };
        var setting = new UwpBackgroundBatchSetting(() => families.ToArray(), store);
        var target = enabled ? UwpBackgroundValues.Enabled : UwpBackgroundValues.DisabledValues;
        var result = await setting.ApplyAsync(enabled);
        Assert.IsTrue(result.Success);
        StringAssert.Contains(result.Detail, enabled ? "アプリ別設定を一括ON" : "アプリ別設定を一括OFF");
        StringAssert.Contains(result.Detail, "Windows 全体設定・ポリシー・アプリの制約");
        Assert.AreEqual(target, store.Values["A"]);
        Assert.AreEqual(target, store.Values["B"]);
        var writes = store.WriteCalls;
        Assert.IsTrue((await setting.ApplyAsync(enabled)).Success);
        Assert.AreEqual(writes, store.WriteCalls, "一致済みの値には不要な書込を行わない");
        // 同じインスタンスでも対象を再列挙し、追加・外部変更されたアプリへ再適用する。
        store.Values["A"] = new(5, 6);
        store.Values["C"] = new(9, 10);
        families.Add("C");
        Assert.IsTrue((await setting.ApplyAsync(enabled)).Success);
        foreach (var family in families) Assert.AreEqual(target, store.Values[family]);
    }

    [TestMethod]
    public async Task 両方向は元値や前回の適用情報に関係なく禁止と許可を明示設定する()
    {
        var store = new FakeSettingsStore();
        store.Values["A"] = new(1, null);
        store.Values["B"] = new(0, 9);
        Assert.IsTrue((await NewToggle(store).ApplyAsync(false)).Success);
        Assert.AreEqual(UwpBackgroundValues.DisabledValues, store.Values["A"]);
        Assert.AreEqual(UwpBackgroundValues.DisabledValues, store.Values["B"]);
        Assert.IsTrue((await NewToggle(store).ApplyAsync(true)).Success);
        Assert.AreEqual(UwpBackgroundValues.Enabled, store.Values["A"]);
        Assert.AreEqual(UwpBackgroundValues.Enabled, store.Values["B"]);
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
        var result = await NewToggle(store).ApplyAsync(on);
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
        var result = await NewToggle(store).ApplyAsync(on, cancellation.Token);
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

        var result = await NewToggle(store).ApplyAsync(on, cancellation.Token);

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

        var result = await NewToggle(store).ApplyAsync(true, cancellation.Token);

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
        var result = await NewToggle(store).ApplyAsync(true, cancellation.Token);
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
        var result = await NewToggle(store).ApplyAsync(true);
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
        var result = await NewToggle(store).ApplyAsync(true);
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
        var result = await NewToggle(store).ApplyAsync(true);
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
        var result = await NewToggle(store).ApplyAsync(true);
        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        StringAssert.Contains(result.Detail, "補償を確認できません");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task 全対象の事前読込が失敗したら一件も変更しない(bool enabled)
    {
        var store = InitialStore(enabled);
        var toggle = NewToggle(store);
        store.BeforeRead = (_, _) => throw new IOException("READ_DENIED");
        var result = await toggle.ApplyAsync(enabled);
        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        Assert.AreEqual(0, store.WriteCalls);
        StringAssert.Contains(result.Detail, "READ_DENIED");
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
        Assert.AreEqual(MaintenanceActionStatus.Failed, (await toggle.ApplyAsync(true)).Status);
        var messages = Logs.Messages.Skip(start).ToArray();
        Assert.IsTrue(messages.Any(entry => entry.Level == LogLevel.Error &&
            entry.Message.Contains(toggle.Id) && entry.Message.Contains(toggle.Label) &&
            entry.Message.Contains("UWP_CAUSE") && entry.Message.Contains("[redacted]")));
        Assert.IsFalse(messages.Any(entry => entry.Message.Contains("UWP_SECRET_CANARY")));
    }

    private static UwpBackgroundBatchSetting NewToggle(FakeSettingsStore store) => new(() => ["A", "B"], store);
    private static FakeSettingsStore InitialStore(bool on)
    {
        var store = new FakeSettingsStore();
        store.Values["A"] = on ? new(null, 1) : new(null, 0);
        store.Values["B"] = on ? new(1, null) : new(0, null);
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
