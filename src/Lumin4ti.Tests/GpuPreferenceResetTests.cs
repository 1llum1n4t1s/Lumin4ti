using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class GpuPreferenceResetTests
{
    [TestMethod]
    public void デスクトップアプリとストアアプリの登録はどちらも削除対象になる()
    {
        string[] registrations =
        [
            @"C:\Program Files\Unity\Hub\Editor\6000.4.9f1\Editor\Unity.exe",
            @"C:\Windows\System32\mstsc.exe",
            "Microsoft.Windows.Photos_8wekyb3d8bbwe!App",
            "Claude_pzs8sxrjxfjjc!Claude",
        ];

        foreach (var name in registrations)
        {
            Assert.IsTrue(GpuPreferenceResetAction.IsAppRegistration(name), name);
        }
    }

    [TestMethod]
    public void OS全体の設定は削除対象から除外する()
    {
        // 自動 HDR / 可変リフレッシュレート / ウィンドウ表示のゲームの最適化はアプリ単位の指定ではない。
        // 同じキーに同居しているため、巻き添えで消さないことを固定する (回帰防止)。
        Assert.IsFalse(GpuPreferenceResetAction.IsAppRegistration("DirectXUserGlobalSettings"));
        Assert.IsFalse(GpuPreferenceResetAction.IsAppRegistration("directxuserglobalsettings"));
    }

    [TestMethod]
    public void 既定値は削除対象にしない()
    {
        // GetValueNames は既定値を空文字列で返す。削除を試みても意味が無いので除外する。
        Assert.IsFalse(GpuPreferenceResetAction.IsAppRegistration(string.Empty));
    }
}
