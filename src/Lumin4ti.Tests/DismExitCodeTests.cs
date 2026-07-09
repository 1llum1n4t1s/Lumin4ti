using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class DismExitCodeTests
{
    private static CommandExecutionResult WithExit(int exitCode) =>
        new(exitCode == 0, "dism.exe", exitCode, string.Empty, string.Empty);

    [TestMethod]
    public void 終了コード0は成功()
    {
        Assert.IsTrue(DismExitCode.IsSuccessOrRebootRequired(WithExit(0)));
    }

    [TestMethod]
    public void 終了コード3010は成功扱い_要再起動()
    {
        Assert.IsTrue(DismExitCode.IsSuccessOrRebootRequired(WithExit(DismExitCode.RebootRequired)));
    }

    [TestMethod]
    public void その他の終了コードは失敗()
    {
        Assert.IsFalse(DismExitCode.IsSuccessOrRebootRequired(WithExit(1)));
        Assert.IsFalse(DismExitCode.IsSuccessOrRebootRequired(WithExit(-1)));
    }
}
