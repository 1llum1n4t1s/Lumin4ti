using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class SystemPackageRepairTests
{
    private const string Family = "MicrosoftWindows.Client.CBS_cw5n1h2txyewy";
    private const string FullName = "MicrosoftWindows.Client.CBS_1000.26100.334.0_x64__cw5n1h2txyewy";
    private const string InstallPath = @"C:\Windows\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy";

    private static SystemPackageRepairAction CreateAction(
        IReadOnlyList<SystemPackageState> packages,
        List<string> registered,
        string? registerError = null,
        Func<string, bool>? isConfirmedMissing = null) =>
        new(
            "repair-shell-client-cbs",
            Family,
            "ラベル",
            "説明",
            _ => packages,
            (fullName, _) =>
            {
                registered.Add(fullName);
                return Task.FromResult(registerError);
            },
            isConfirmedMissing ?? (_ => false));

    [TestMethod]
    public async Task 実体があるシステムアプリを再登録する()
    {
        var registered = new List<string>();

        var result = await CreateAction([new(FullName, InstallPath)], registered).ExecuteAsync();

        Assert.AreEqual(MaintenanceActionStatus.Success, result.Status);
        CollectionAssert.AreEqual(new[] { FullName }, registered);
        StringAssert.Contains(result.Detail, "再登録しました");
    }

    [TestMethod]
    public async Task 登録が無ければ失敗として報告する()
    {
        var registered = new List<string>();

        var result = await CreateAction([], registered).ExecuteAsync();

        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        Assert.AreEqual(0, registered.Count);
        StringAssert.Contains(result.Detail, Family);
    }

    [TestMethod]
    public async Task 実体が消えている登録は再登録せずゴースト側の担当にする()
    {
        var registered = new List<string>();

        var result = await CreateAction(
                [new(FullName, InstallPath)],
                registered,
                isConfirmedMissing: _ => true)
            .ExecuteAsync();

        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        Assert.AreEqual(0, registered.Count, "実体が無いものを再登録しに行かない");
        StringAssert.Contains(result.Detail, "存在しません");
    }

    [TestMethod]
    public async Task 再登録に失敗したら理由を出して失敗にする()
    {
        var registered = new List<string>();

        var result = await CreateAction([new(FullName, InstallPath)], registered, registerError: "0x80073CF6")
            .ExecuteAsync();

        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        StringAssert.Contains(result.Detail, "0x80073CF6");
    }

    [TestMethod]
    public async Task 複数登録のうち一部が失敗したら部分成功にする()
    {
        var registered = new List<string>();
        var action = new SystemPackageRepairAction(
            "repair-shell-client-cbs",
            Family,
            "ラベル",
            "説明",
            _ => [new(FullName, InstallPath), new($"{FullName}.old", null)],
            (fullName, _) =>
            {
                registered.Add(fullName);
                return Task.FromResult<string?>(fullName.EndsWith(".old", StringComparison.Ordinal) ? "失敗理由" : null);
            },
            _ => false);

        var result = await action.ExecuteAsync();

        Assert.AreEqual(MaintenanceActionStatus.Partial, result.Status);
        Assert.AreEqual(2, registered.Count);
    }
}
