using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class GhostPackageCleanupTests
{
    private static PackageRegistration Package(string name, string? path) =>
        new($"{name}_1.0.0.0_neutral_neutral_cw5n1h2txyewy", name, path);

    private static GhostPackageCleanupAction CreateAction(
        IReadOnlyList<PackageRegistration> packages,
        List<string> removed,
        Func<string, bool>? isConfirmedMissing = null,
        string? removeError = null) =>
        new(
            () => packages,
            (package, _) =>
            {
                removed.Add(package.FullName);
                return Task.FromResult(removeError);
            },
            isConfirmedMissing ?? (path => path.Contains("ghost", StringComparison.OrdinalIgnoreCase)));

    [TestMethod]
    public async Task フォルダが確実に存在しない登録だけを解除する()
    {
        var removed = new List<string>();
        var action = CreateAction(
            [
                Package("Microsoft.PPIProjection", @"C:\Windows\SystemApps\ghost"),
                Package("Microsoft.WindowsCalculator", @"C:\Program Files\WindowsApps\alive"),
            ],
            removed);

        var result = await action.ExecuteAsync();

        Assert.AreEqual(MaintenanceActionStatus.Success, result.Status);
        CollectionAssert.AreEqual(
            new[] { "Microsoft.PPIProjection_1.0.0.0_neutral_neutral_cw5n1h2txyewy" },
            removed);
    }

    [TestMethod]
    public async Task パスを取得できない登録には触れない()
    {
        var removed = new List<string>();
        var action = CreateAction(
            [Package("Microsoft.Unknown", null), Package("Microsoft.Empty", string.Empty)],
            removed,
            // 不在判定に到達したら誤削除なので、常に「欠損」と答えるスタブで検出する
            isConfirmedMissing: _ => true);

        var result = await action.ExecuteAsync();

        Assert.AreEqual(MaintenanceActionStatus.Success, result.Status);
        Assert.AreEqual(0, removed.Count);
        StringAssert.Contains(result.Detail, "ゴースト登録はありませんでした");
    }

    [TestMethod]
    public async Task 解除に失敗した項目があれば部分成功として報告する()
    {
        var removed = new List<string>();
        var action = CreateAction(
            [Package("Microsoft.PPIProjection", @"C:\Windows\SystemApps\ghost")],
            removed,
            removeError: "0x80073CFA");

        var result = await action.ExecuteAsync();

        Assert.AreEqual(MaintenanceActionStatus.Partial, result.Status);
        StringAssert.Contains(result.Detail, "0x80073CFA");
    }

    [TestMethod]
    public async Task 列挙が空でも成功として扱う()
    {
        var removed = new List<string>();
        var action = CreateAction([], removed);

        var result = await action.ExecuteAsync();

        Assert.AreEqual(MaintenanceActionStatus.Success, result.Status);
        Assert.AreEqual(0, removed.Count);
    }

    [TestMethod]
    public async Task 進捗は対象ごとに通知される()
    {
        var removed = new List<string>();
        var messages = new List<string>();
        var action = CreateAction(
            [
                Package("Microsoft.PPIProjection", @"C:\Windows\SystemApps\ghost"),
                Package("Microsoft.Other", @"C:\Windows\SystemApps\ghost2"),
            ],
            removed);

        await action.ExecuteAsync(new RecordingProgress(messages), CancellationToken.None);

        Assert.AreEqual(2, removed.Count);
        Assert.IsTrue(messages.Any(m => m.Contains("Microsoft.PPIProjection", StringComparison.Ordinal)));
        Assert.IsTrue(messages.Any(m => m.Contains("Microsoft.Other", StringComparison.Ordinal)));
    }

    /// <summary>Progress&lt;T&gt; は同期コンテキスト依存で通知順が保証されないため、同期記録に差し替える。</summary>
    private sealed class RecordingProgress(List<string> messages) : IProgress<string>
    {
        public void Report(string value) => messages.Add(value);
    }
}
