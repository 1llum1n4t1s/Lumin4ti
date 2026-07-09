using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class EnvPathSortTests
{
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
}
