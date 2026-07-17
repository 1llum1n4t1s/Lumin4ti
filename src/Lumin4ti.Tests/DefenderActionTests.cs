using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class DefenderActionTests
{
    private sealed class RecordingExecutor(params CommandExecutionResult[] results) : ICommandExecutor
    {
        private readonly Queue<CommandExecutionResult> _results = new(results);

        public List<(string FileName, string Arguments, CancellationToken Token)> Calls { get; } = [];

        public Task<CommandExecutionResult> RunAsync(
            string fileName,
            string arguments,
            CancellationToken ct = default,
            IProgress<string>? onOutputLine = null)
        {
            Calls.Add((fileName, arguments, ct));
            return Task.FromResult(_results.Dequeue());
        }
    }

    [TestMethod]
    public void MpCmdRunは最新のPlatform版を選ぶ()
    {
        var oldDirectory = @"C:\ProgramData\Microsoft\Windows Defender\Platform\4.18.9.0-0";
        var latestDirectory = @"C:\ProgramData\Microsoft\Windows Defender\Platform\4.18.10.0-0";
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(oldDirectory, "MpCmdRun.exe"),
            Path.Combine(latestDirectory, "MpCmdRun.exe"),
        };

        var selected = DefenderCommandSupport.SelectMpCmdRunPath(
            [oldDirectory, latestDirectory],
            @"C:\Program Files\Windows Defender\MpCmdRun.exe",
            existing.Contains);

        Assert.AreEqual(Path.Combine(latestDirectory, "MpCmdRun.exe"), selected);
    }

    [TestMethod]
    public async Task 定義ファイルは全体を戻してから更新する()
    {
        var executor = new RecordingExecutor(Success(), Success());
        var action = new DefenderSignatureUpdateAction(executor, () => @"C:\Defender\MpCmdRun.exe");

        var result = await action.ExecuteAsync();

        Assert.IsTrue(result.Success);
        Assert.AreEqual(2, executor.Calls.Count);
        Assert.AreEqual("-RemoveDefinitions -All", executor.Calls[0].Arguments);
        Assert.AreEqual("-SignatureUpdate", executor.Calls[1].Arguments);
    }

    [TestMethod]
    public async Task 定義の巻き戻しに失敗した場合は更新成功でも全体を失敗にする()
    {
        var executor = new RecordingExecutor(Failure(5, "Access denied"), Success());
        var action = new DefenderSignatureUpdateAction(executor, () => @"C:\Defender\MpCmdRun.exe");

        var result = await action.ExecuteAsync();

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Detail, "Access denied");
    }

    [TestMethod]
    public void Defender停止エラーを利用可能状態の案内へ変換する()
    {
        var result = Failure(1, "操作に失敗しました: 0x800106ba");

        Assert.IsTrue(DefenderCommandSupport.IsDefenderUnavailable(result));
        StringAssert.Contains(DefenderCommandSupport.DescribeFailure(result), "Defender Antivirus が無効");
    }

    [TestMethod]
    public async Task 定義削除中のキャンセル後も再取得を補償実行する()
    {
        using var cts = new CancellationTokenSource();
        var executor = new CancelThenRecoverExecutor(cts);
        var action = new DefenderSignatureUpdateAction(executor, () => @"C:\Defender\MpCmdRun.exe");

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => action.ExecuteAsync(cts.Token));

        Assert.AreEqual(2, executor.Calls.Count);
        Assert.AreEqual(DefenderSignatureUpdateAction.RemoveDefinitionsArguments, executor.Calls[0].Arguments);
        Assert.AreEqual(DefenderSignatureUpdateAction.SignatureUpdateArguments, executor.Calls[1].Arguments);
        Assert.IsFalse(executor.Calls[1].Token.CanBeCanceled,
            "補償用の定義再取得は呼び出し元のキャンセルから独立している必要があります");
    }

    [TestMethod]
    public async Task キャンセル後の定義再取得失敗はキャンセルより失敗を優先する()
    {
        using var cts = new CancellationTokenSource();
        var executor = new CancelThenRecoverExecutor(cts, Failure(9, "signature recovery failed"));
        var action = new DefenderSignatureUpdateAction(executor, () => @"C:\Defender\MpCmdRun.exe");

        var result = await action.ExecuteAsync(cts.Token);

        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        StringAssert.Contains(result.Detail, "signature recovery failed");
    }

    [TestMethod]
    public void 設定リセットは正しいモジュールと厳格なエラー処理を使う()
    {
        var script = DefenderResetAction.BuildResetScript(@"C:\backup's\defender.json");

        StringAssert.Contains(script, "Import-Module Defender -ErrorAction Stop");
        StringAssert.Contains(script, "$ErrorActionPreference = 'Stop'");
        StringAssert.Contains(script, "ControlledFolderAccessProtectedFolders");
        StringAssert.Contains(script, "AttackSurfaceReductionOnlyExclusions");
        StringAssert.Contains(script, "Get-MpComputerStatus -ErrorAction Stop");
        StringAssert.Contains(script, "IsTamperProtected");
        StringAssert.Contains(script, "$after = Get-MpPreference -ErrorAction Stop");
        StringAssert.Contains(script, "$resetAppliedNames = @()");
        StringAssert.Contains(script, "$verifiableNames = @()");
        StringAssert.Contains(script, "if ([bool]$p.$name)");
        StringAssert.Contains(script, "foreach ($name in $verifiableNames)");
        StringAssert.Contains(script, "除外設定が ");
        StringAssert.Contains(script, "主要な保護設定の有効状態を確認しました");
        StringAssert.Contains(script, @"C:\backup''s\defender.json");
        StringAssert.Contains(script, "[System.IO.File]::WriteAllText");
        Assert.IsFalse(script.Contains("Set-Content", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("ConfigDefender", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("SilentlyContinue", StringComparison.Ordinal));
    }

    [TestMethod]
    public void 設定バックアップは必須プロパティと文字列値を検証する()
    {
        const string valid = """
            {
              "ExclusionPath": ["C:\\\\work"],
              "ExclusionProcess": null,
              "ExclusionExtension": ".tmp",
              "ExclusionIpAddress": [],
              "ControlledFolderAccessAllowedApplications": [],
              "ControlledFolderAccessProtectedFolders": [],
              "AttackSurfaceReductionOnlyExclusions": []
            }
            """;

        Assert.IsTrue(DefenderResetAction.TryValidateBackupJson(valid, out var failure), failure);
        Assert.IsFalse(DefenderResetAction.TryValidateBackupJson(
            "{\"Lumin4tiPending\":true}", out _));
        Assert.IsFalse(DefenderResetAction.TryValidateBackupJson(
            valid.Replace("[]", "[123]", StringComparison.Ordinal), out _));
    }

    [TestMethod]
    public void Windowsセキュリティリセットは空pipelineを成功扱いしない()
    {
        var script = SecurityAppResetAction.BuildResetScript();

        StringAssert.Contains(script, "$package = @(Get-AppxPackage -Name Microsoft.SecHealthUI -ErrorAction Stop)");
        StringAssert.Contains(script, "if ($package.Count -eq 0)");
        StringAssert.Contains(script, "$package | Reset-AppxPackage -ErrorAction Stop");
        StringAssert.Contains(script, "$after = @(Get-AppxPackage -Name Microsoft.SecHealthUI -ErrorAction Stop)");
        StringAssert.Contains(script, "if ($after.Count -eq 0)");
    }

    private static CommandExecutionResult Success() =>
        new(true, string.Empty, 0, string.Empty, string.Empty);

    private static CommandExecutionResult Failure(int exitCode, string standardError) =>
        new(false, string.Empty, exitCode, string.Empty, standardError);

    private sealed class CancelThenRecoverExecutor(
        CancellationTokenSource cancellation,
        CommandExecutionResult? recoveryResult = null) : ICommandExecutor
    {
        public List<(string Arguments, CancellationToken Token)> Calls { get; } = [];

        public Task<CommandExecutionResult> RunAsync(
            string fileName,
            string arguments,
            CancellationToken ct = default,
            IProgress<string>? onOutputLine = null)
        {
            Calls.Add((arguments, ct));
            if (Calls.Count == 1)
            {
                cancellation.Cancel();
                return Task.FromCanceled<CommandExecutionResult>(ct);
            }

            return Task.FromResult(recoveryResult ?? Success());
        }
    }
}
