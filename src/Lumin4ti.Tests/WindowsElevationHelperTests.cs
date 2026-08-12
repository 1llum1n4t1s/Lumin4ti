using Lumin4ti.UI.Services;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class WindowsElevationHelperTests
{
    [TestMethod]
    public void 非昇格かつデバッガ無しなら昇格して起動し直す()
    {
        Assert.IsTrue(WindowsElevationHelper.ShouldRelaunchElevated(isElevated: false, isDebuggerAttached: false));
    }

    [TestMethod]
    public void デバッガ接続中は昇格しない()
    {
        // 昇格は自分自身を起動し直して元プロセスを終了させるため、ここで true を返すと
        // Visual Studio のデバッグ実行が開始直後に終了してしまう。
        Assert.IsFalse(WindowsElevationHelper.ShouldRelaunchElevated(isElevated: false, isDebuggerAttached: true));
    }

    [TestMethod]
    public void 既に昇格済みなら起動し直さない()
    {
        Assert.IsFalse(WindowsElevationHelper.ShouldRelaunchElevated(isElevated: true, isDebuggerAttached: false));
        Assert.IsFalse(WindowsElevationHelper.ShouldRelaunchElevated(isElevated: true, isDebuggerAttached: true));
    }
}
