using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class ScheduledTempCleanupTests
{
    private sealed class RecordingExecutor : ICommandExecutor
    {
        public string? LastFileName { get; private set; }

        public string? LastArguments { get; private set; }

        public bool NextSuccess { get; set; } = true;

        public Task<CommandExecutionResult> RunAsync(
            string fileName,
            string arguments,
            CancellationToken ct = default,
            IProgress<string>? onOutputLine = null,
            TimeSpan? timeout = null)
        {
            LastFileName = fileName;
            LastArguments = arguments;
            return Task.FromResult(new CommandExecutionResult(
                NextSuccess,
                $"{fileName} {arguments}",
                NextSuccess ? 0 : 1,
                string.Empty,
                NextSuccess ? string.Empty : "エラーが発生しました"));
        }
    }

    [TestMethod]
    public void 照会コマンドはタスク名を含む()
    {
        var arguments = ScheduledTempCleanupToggle.BuildQueryArguments();

        StringAssert.Contains(arguments, "/query");
        StringAssert.Contains(arguments, ScheduledTempCleanupToggle.TaskName);
    }

    [TestMethod]
    public void 削除コマンドは確認なしでタスク名を消す()
    {
        var arguments = ScheduledTempCleanupToggle.BuildDeleteArguments();

        StringAssert.Contains(arguments, "/delete");
        StringAssert.Contains(arguments, ScheduledTempCleanupToggle.TaskName);
        StringAssert.Contains(arguments, "/f");
    }

    [TestMethod]
    public void 登録コマンドは実行ファイルパスをクォートして専用引数を渡す()
    {
        const string exePath = @"C:\Program Files\Lumin4ti\Lumin4ti.exe";
        var arguments = ScheduledTempCleanupToggle.BuildCreateArguments(exePath);

        StringAssert.Contains(arguments, "/create");
        StringAssert.Contains(arguments, ScheduledTempCleanupToggle.TaskName);
        StringAssert.Contains(arguments, $"\\\"{exePath}\\\"");
        StringAssert.Contains(arguments, ScheduledTempCleanup.CommandLineArgument);
    }

    [TestMethod]
    public void 登録コマンドはログオン時トリガーと非昇格実行を指定する()
    {
        var arguments = ScheduledTempCleanupToggle.BuildCreateArguments(@"C:\Lumin4ti\Lumin4ti.exe");

        StringAssert.Contains(arguments, "/sc onlogon");
        StringAssert.Contains(arguments, "/rl limited");
    }

    [TestMethod]
    public async Task 照会が成功すればONと判定する()
    {
        var executor = new RecordingExecutor { NextSuccess = true };
        var toggle = new ScheduledTempCleanupToggle(executor);

        var state = await toggle.GetStateAsync();

        Assert.AreEqual(true, state);
        StringAssert.Contains(executor.LastArguments, "/query");
    }

    [TestMethod]
    public async Task 照会が失敗すればOFFと判定する()
    {
        var executor = new RecordingExecutor { NextSuccess = false };
        var toggle = new ScheduledTempCleanupToggle(executor);

        var state = await toggle.GetStateAsync();

        Assert.AreEqual(false, state);
    }

    [TestMethod]
    public async Task ONにするとschtasksを論理名で呼び出す()
    {
        var executor = new RecordingExecutor { NextSuccess = true };
        var toggle = new ScheduledTempCleanupToggle(executor, isTrustedInstalledExecutable: _ => true);

        var result = await toggle.SetStateAsync(true);

        Assert.AreEqual(MaintenanceActionStatus.Success, result.Status);
        Assert.AreEqual("schtasks", executor.LastFileName);
        StringAssert.Contains(executor.LastArguments, "/create");
    }

    [TestMethod]
    public async Task 信頼できない実行ファイルパスでは登録を拒否する()
    {
        var executor = new RecordingExecutor { NextSuccess = true };
        var toggle = new ScheduledTempCleanupToggle(executor, isTrustedInstalledExecutable: _ => false);

        var result = await toggle.SetStateAsync(true);

        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        Assert.IsNull(executor.LastFileName, "schtasks を呼び出す前に拒否する必要があります");
    }

    [TestMethod]
    public async Task OFFにするとschtasksへ削除を渡す()
    {
        var executor = new RecordingExecutor { NextSuccess = true };
        var toggle = new ScheduledTempCleanupToggle(executor);

        var result = await toggle.SetStateAsync(false);

        Assert.AreEqual(MaintenanceActionStatus.Success, result.Status);
        StringAssert.Contains(executor.LastArguments, "/delete");
    }

    [TestMethod]
    public async Task 登録に失敗すると結果に理由を含めて失敗を返す()
    {
        var executor = new RecordingExecutor { NextSuccess = false };
        var toggle = new ScheduledTempCleanupToggle(executor, isTrustedInstalledExecutable: _ => true);

        var result = await toggle.SetStateAsync(true);

        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
    }

    [TestMethod]
    public void クリーンアップカテゴリの項目である()
    {
        var toggle = new ScheduledTempCleanupToggle(new RecordingExecutor());

        Assert.AreEqual(CommandCategory.Cleanup, toggle.Category);
        Assert.IsFalse(toggle.RequiresReboot);
    }

    [TestMethod]
    public void TEMP環境変数は安全ガードを通過する()
    {
        // ScheduledTempCleanup.Run は CleanupTarget.Contents(@"%TEMP%") を使う。
        // 保護フォルダ (基点ディレクトリ) 判定に引っかからず、実行時に解決できることを確認する。
        Assert.IsTrue(FileCleanupEngine.TryResolve(@"%TEMP%", out var resolved, out var reason), reason);
        Assert.IsTrue(Path.IsPathFullyQualified(resolved), resolved);
    }
}
