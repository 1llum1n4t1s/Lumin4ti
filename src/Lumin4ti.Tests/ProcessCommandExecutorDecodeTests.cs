using System.Text;
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
    public void CP932バイト列はフォールバックで正しく解釈される()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp932 = Encoding.GetEncoding(932);
        var bytes = cp932.GetBytes("操作は正常に終了しました。");

        Assert.AreEqual("操作は正常に終了しました。", ProcessCommandExecutor.DecodeConsoleOutput(bytes));
    }

    [TestMethod]
    public void 空バイト列は空文字を返す()
    {
        Assert.AreEqual(string.Empty, ProcessCommandExecutor.DecodeConsoleOutput([]));
    }
}
