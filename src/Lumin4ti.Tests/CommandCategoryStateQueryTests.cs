using Lumin4ti.UI.ViewModels;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class CommandCategoryStateQueryTests
{
    [TestMethod]
    public async Task 項目固有の上限で状態取得を打ち切る()
    {
        var result = await CommandCategoryViewModel.QueryStateAsync<bool?>(
            async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return true;
            },
            "SlowItem",
            timeout: TimeSpan.FromMilliseconds(20));

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task 呼び出し元のキャンセルはタイムアウトとして握り潰さない()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CommandCategoryViewModel.QueryStateAsync<bool?>(
                async ct =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return true;
                },
                "CanceledItem",
                cts.Token,
                TimeSpan.FromSeconds(1)));
    }
}
