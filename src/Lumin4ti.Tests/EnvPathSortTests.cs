using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class EnvPathSortTests
{
    [TestMethod]
    public async Task 実行前キャンセルはレジストリ処理を開始しない()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new EnvPathSortAction().ExecuteAsync(cancellation.Token));
    }

    [TestMethod]
    public void 重複除去と昇順ソートを行う()
    {
        var sorted = EnvPathSortAction.SortPathValue(@"C:\zzz;C:\aaa; C:\bbb ;C:\AAA;;C:\bbb");

        Assert.AreEqual(@"C:\aaa;C:\bbb;C:\zzz", sorted);
    }

    [TestMethod]
    public void 展開前の環境変数参照を保持する()
    {
        var sorted = EnvPathSortAction.SortPathValue(@"%SystemRoot%\system32;%SystemRoot%");

        Assert.AreEqual(@"%SystemRoot%;%SystemRoot%\system32", sorted);
    }

    [TestMethod]
    public void システム側が失敗した場合は全体を失敗にする()
    {
        var result = EnvPathSortAction.CreateResult(
        [
            (true, "ユーザー Path: 完了"),
            (false, "システム Path: 権限不足"),
        ]);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        StringAssert.Contains(result.Detail, "システム Path: 権限不足");
    }

    [TestMethod]
    public void 通知だけ失敗した場合は部分成功にする()
    {
        var result = EnvPathSortAction.CreateResult(
        [
            (true, "ユーザー Path: 完了"),
            (true, "システム Path: 完了"),
            (false, "変更通知: タイムアウト"),
        ]);

        Assert.AreEqual(MaintenanceActionStatus.Partial, result.Status);
        StringAssert.Contains(result.Detail, "変更通知: タイムアウト");
    }

    [TestMethod]
    public void ロールバックは逆順の全操作を一方が失敗しても試す()
    {
        var attempted = new List<string>();

        var result = EnvPathSortAction.TryRestoreAll(
        [
            ("システム Path", () =>
            {
                attempted.Add("system");
                throw new IOException("system restore failed");
            }),
            ("ユーザー Path", () => attempted.Add("user")),
        ]);

        CollectionAssert.AreEqual(new[] { "system", "user" }, attempted);
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Line, "システム Path");
        StringAssert.Contains(result.Line, "system restore failed");
    }
}
