using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class QuickAccessVerbTests
{
    [TestMethod]
    public void 日本語のピン留めverbを判定できる()
    {
        Assert.IsTrue(QuickAccessSortAction.IsPinVerb("クイック アクセスにピン留めする(&P)"));
        Assert.IsFalse(QuickAccessSortAction.IsPinVerb("クイック アクセスからピン留めを外す(&U)"));
        Assert.IsTrue(QuickAccessSortAction.IsUnpinVerb("クイック アクセスからピン留めを外す(&U)"));
        Assert.IsFalse(QuickAccessSortAction.IsUnpinVerb("クイック アクセスにピン留めする(&P)"));
    }

    [TestMethod]
    public void 英語のverbを判定できる()
    {
        Assert.IsTrue(QuickAccessSortAction.IsPinVerb("Pin to Quick access"));
        Assert.IsTrue(QuickAccessSortAction.IsUnpinVerb("Unpin from Quick access"));
        Assert.IsFalse(QuickAccessSortAction.IsPinVerb("Unpin from Quick access"));
    }

    [TestMethod]
    public void nullや無関係なverbは対象外()
    {
        Assert.IsFalse(QuickAccessSortAction.IsPinVerb(null));
        Assert.IsFalse(QuickAccessSortAction.IsUnpinVerb(null));
        Assert.IsFalse(QuickAccessSortAction.IsPinVerb("開く(&O)"));
    }

    [TestMethod]
    public void mediumプロセス用scriptは検証済みbackup作成後だけ変更を開始する()
    {
        var script = QuickAccessSortAction.PowerShellScript;

        StringAssert.Contains(script, "New-Object -ComObject Shell.Application");
        StringAssert.Contains(script, "f01b4d95cf55d32a.automaticDestinations-ms");
        StringAssert.Contains(script, ".lumin4tibak");
        StringAssert.Contains(script, "Get-FileHash -LiteralPath");
        StringAssert.Contains(script, "-Algorithm SHA256");
        StringAssert.Contains(script, "Restore-OriginalState");
        StringAssert.Contains(script, "Test-PathSequence");
        StringAssert.Contains(script, "$env:LUMIN4TI_RESULT_PATH");
        StringAssert.Contains(script, "ピン留め解除を開始しません");
        StringAssert.Contains(script, "CurrentUICulture.TwoLetterISOLanguageName");
        StringAssert.Contains(script, "$shellLanguage -notin @('ja', 'en')");
        StringAssert.Contains(script, "[pscustomobject]@{ Path = $path; Name = $name }");
        StringAssert.Contains(script, "Expression = { [string]$_.Path }");
        Assert.IsFalse(script.Contains("Expression = { [string]$_.Name }", StringComparison.Ordinal));
        StringAssert.Contains(script, ".lumin4tijournal.json");
        StringAssert.Contains(script, "Read-PendingJournal");
        StringAssert.Contains(script, "Remove-PendingJournal");
        var journalSaved = script.LastIndexOf(
            "Save-PendingJournal $journalPath $jumpList $backupPath $backupHash $pinned",
            StringComparison.Ordinal);
        Assert.IsTrue(journalSaved >= 0);
        var backupVerified = script.LastIndexOf(
            "$result.BackupVerified = $true",
            journalSaved,
            StringComparison.Ordinal);
        var mutationStarted = script.LastIndexOf("$result.MutationStarted = $true", StringComparison.Ordinal);
        Assert.IsTrue(backupVerified >= 0);
        Assert.IsTrue(journalSaved > backupVerified);
        Assert.IsTrue(mutationStarted > journalSaved);
        Assert.IsFalse(script.Contains("Add-Type", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("[IO.File]::Replace", StringComparison.Ordinal));
    }

    [TestMethod]
    public void 正常なmedium結果を解析できる()
    {
        const string json =
            """
            {"SchemaVersion":2,"Status":"ok","PinnedCount":3,"SkipReason":null,"BackupPath":"C:\\Users\\test\\backup.lumin4tibak","BackupFailed":false,"BackupVerified":true,"MutationStarted":true,"RecoveryAttempted":false,"RestoredFromBackup":false,"VerificationSucceeded":true,"SuccessCount":3,"FailedPaths":[],"UnrecoveredPaths":[],"Warnings":[],"Error":null}
            """;

        Assert.IsTrue(QuickAccessSortAction.TryParseScriptResult(json, out var result, out var failure), failure);
        Assert.AreEqual(3, result.PinnedCount);
        Assert.AreEqual(3, result.SuccessCount);
        Assert.IsTrue(result.VerificationSucceeded);
    }

    [TestMethod]
    public void 失敗pathを含む成功結果を拒否する()
    {
        const string json =
            """
            {"SchemaVersion":2,"Status":"ok","PinnedCount":3,"SkipReason":null,"BackupPath":"C:\\Users\\test\\backup.lumin4tibak","BackupFailed":false,"BackupVerified":true,"MutationStarted":true,"RecoveryAttempted":false,"RestoredFromBackup":false,"VerificationSucceeded":true,"SuccessCount":3,"FailedPaths":["C:\\B"],"UnrecoveredPaths":[],"Warnings":[],"Error":null}
            """;

        Assert.IsFalse(QuickAccessSortAction.TryParseScriptResult(json, out _, out var failure));
        StringAssert.Contains(failure, "成功結果");
    }

    [TestMethod]
    public void 実状態未検証の成功結果を拒否する()
    {
        const string json =
            """
            {"SchemaVersion":2,"Status":"ok","PinnedCount":3,"SkipReason":null,"BackupPath":"C:\\Users\\test\\backup.lumin4tibak","BackupFailed":false,"BackupVerified":true,"MutationStarted":true,"RecoveryAttempted":false,"RestoredFromBackup":false,"VerificationSucceeded":false,"SuccessCount":3,"FailedPaths":[],"UnrecoveredPaths":[],"Warnings":[],"Error":null}
            """;

        Assert.IsFalse(QuickAccessSortAction.TryParseScriptResult(json, out _, out var failure));
        StringAssert.Contains(failure, "成功結果");
    }

    [TestMethod]
    public void backup失敗後に変更開始と報告する結果を拒否する()
    {
        const string json =
            """
            {"SchemaVersion":2,"Status":"error","PinnedCount":3,"SkipReason":null,"BackupPath":null,"BackupFailed":true,"BackupVerified":false,"MutationStarted":true,"RecoveryAttempted":true,"RestoredFromBackup":false,"VerificationSucceeded":false,"SuccessCount":0,"FailedPaths":["C:\\B"],"UnrecoveredPaths":["C:\\A"],"Warnings":[],"Error":"backup失敗後に変更開始"}
            """;

        Assert.IsFalse(QuickAccessSortAction.TryParseScriptResult(json, out _, out var failure));
        StringAssert.Contains(failure, "バックアップ");
    }

    [TestMethod]
    public void エラー理由のない失敗結果を拒否する()
    {
        const string json =
            """
            {"SchemaVersion":2,"Status":"error","PinnedCount":0,"SkipReason":null,"BackupPath":null,"BackupFailed":false,"BackupVerified":false,"MutationStarted":false,"RecoveryAttempted":false,"RestoredFromBackup":false,"VerificationSucceeded":false,"SuccessCount":0,"FailedPaths":[],"UnrecoveredPaths":[],"Warnings":[],"Error":null}
            """;

        Assert.IsFalse(QuickAccessSortAction.TryParseScriptResult(json, out _, out var failure));
        StringAssert.Contains(failure, "エラー理由");
    }

    [TestMethod]
    public void 前回中断から復元して実状態を検証した結果を解析できる()
    {
        const string json =
            """
            {"SchemaVersion":2,"Status":"ok","PinnedCount":3,"SkipReason":"recovered","BackupPath":"C:\\Users\\test\\backup.lumin4tibak","BackupFailed":false,"BackupVerified":true,"MutationStarted":true,"RecoveryAttempted":true,"RestoredFromBackup":true,"VerificationSucceeded":true,"SuccessCount":0,"FailedPaths":[],"UnrecoveredPaths":[],"Warnings":[],"Error":null}
            """;

        Assert.IsTrue(QuickAccessSortAction.TryParseScriptResult(json, out var result, out var failure), failure);
        Assert.AreEqual("recovered", result.SkipReason);
        Assert.IsTrue(result.RestoredFromBackup);
        Assert.IsTrue(result.VerificationSucceeded);
    }

    [TestMethod]
    public void 実状態未検証の中断復元結果を拒否する()
    {
        const string json =
            """
            {"SchemaVersion":2,"Status":"ok","PinnedCount":3,"SkipReason":"recovered","BackupPath":"C:\\Users\\test\\backup.lumin4tibak","BackupFailed":false,"BackupVerified":true,"MutationStarted":true,"RecoveryAttempted":true,"RestoredFromBackup":true,"VerificationSucceeded":false,"SuccessCount":0,"FailedPaths":[],"UnrecoveredPaths":[],"Warnings":[],"Error":null}
            """;

        Assert.IsFalse(QuickAccessSortAction.TryParseScriptResult(json, out _, out var failure));
        StringAssert.Contains(failure, "バックアップ");
    }

    [TestMethod]
    public async Task 変更開始後でも呼出元cancelをmediumプロセスへ伝播する()
    {
        using var cts = new CancellationTokenSource();
        var executor = new RecordingExecutor((_, executorToken) =>
        {
            Assert.IsTrue(executorToken.CanBeCanceled);
            cts.Cancel();
            return Task.FromResult(SuccessExecution());
        });
        var action = new QuickAccessSortAction(executor);

        await Assert.ThrowsAsync<OperationCanceledException>(() => action.ExecuteAsync(cts.Token));
        Assert.AreEqual(1, executor.InvocationCount);
        Assert.AreEqual(QuickAccessSortAction.TransactionTimeout, executor.RequestedTimeout);
        Assert.AreNotEqual(Timeout.InfiniteTimeSpan, executor.RequestedTimeout);
    }

    [TestMethod]
    public async Task backup復元と実状態検証後に呼出元cancelを再送出する()
    {
        using var cts = new CancellationTokenSource();
        var executor = new RecordingExecutor((_, executorToken) =>
        {
            Assert.IsTrue(executorToken.CanBeCanceled);
            cts.Cancel();
            return Task.FromResult(RecoveredFailureExecution());
        });
        var action = new QuickAccessSortAction(executor);

        await Assert.ThrowsAsync<OperationCanceledException>(() => action.ExecuteAsync(cts.Token));
    }

    [TestMethod]
    public async Task 未復元なら呼出元cancelより失敗を優先する()
    {
        using var cts = new CancellationTokenSource();
        var executor = new RecordingExecutor((_, executorToken) =>
        {
            Assert.IsTrue(executorToken.CanBeCanceled);
            cts.Cancel();
            return Task.FromResult(UnrecoveredFailureExecution());
        });
        var action = new QuickAccessSortAction(executor);

        var result = await action.ExecuteAsync(cts.Token);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Detail, "自動復元できていません");
    }

    [TestMethod]
    public async Task 復元済みでも並べ替え失敗を成功扱いしない()
    {
        var action = new QuickAccessSortAction(
            new RecordingExecutor((_, _) => Task.FromResult(RecoveredFailureExecution())));

        var result = await action.ExecuteAsync();

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Detail, "実状態も確認済み");
    }

    [TestMethod]
    public async Task 次回実行で中断状態を復元した場合は復元完了を表示する()
    {
        var action = new QuickAccessSortAction(
            new RecordingExecutor((_, _) => Task.FromResult(RecoveredInterruptionExecution())));

        var result = await action.ExecuteAsync();

        Assert.IsTrue(result.Success);
        StringAssert.Contains(result.Detail, "前回中断された並べ替え");
        StringAssert.Contains(result.Detail, "実状態を確認しました");
    }

    private static UnelevatedCommandExecutionResult SuccessExecution() => new(
        true,
        0,
        """
        {"SchemaVersion":2,"Status":"ok","PinnedCount":3,"SkipReason":null,"BackupPath":"C:\\Users\\test\\backup.lumin4tibak","BackupFailed":false,"BackupVerified":true,"MutationStarted":true,"RecoveryAttempted":false,"RestoredFromBackup":false,"VerificationSucceeded":true,"SuccessCount":3,"FailedPaths":[],"UnrecoveredPaths":[],"Warnings":[],"Error":null}
        """,
        string.Empty);

    private static UnelevatedCommandExecutionResult RecoveredFailureExecution() => new(
        false,
        1,
        """
        {"SchemaVersion":2,"Status":"error","PinnedCount":3,"SkipReason":null,"BackupPath":"C:\\Users\\test\\backup.lumin4tibak","BackupFailed":false,"BackupVerified":true,"MutationStarted":true,"RecoveryAttempted":true,"RestoredFromBackup":true,"VerificationSucceeded":true,"SuccessCount":0,"FailedPaths":["C:\\B"],"UnrecoveredPaths":[],"Warnings":[],"Error":"並べ替え失敗。元状態は復元済み"}
        """,
        "script failed");

    private static UnelevatedCommandExecutionResult UnrecoveredFailureExecution() => new(
        false,
        1,
        """
        {"SchemaVersion":2,"Status":"error","PinnedCount":3,"SkipReason":null,"BackupPath":"C:\\Users\\test\\backup.lumin4tibak","BackupFailed":false,"BackupVerified":true,"MutationStarted":true,"RecoveryAttempted":true,"RestoredFromBackup":false,"VerificationSucceeded":false,"SuccessCount":0,"FailedPaths":["C:\\B"],"UnrecoveredPaths":["C:\\A","C:\\B","C:\\C"],"Warnings":[],"Error":"並べ替えと自動復元に失敗"}
        """,
        "script failed");

    private static UnelevatedCommandExecutionResult RecoveredInterruptionExecution() => new(
        true,
        0,
        """
        {"SchemaVersion":2,"Status":"ok","PinnedCount":3,"SkipReason":"recovered","BackupPath":"C:\\Users\\test\\backup.lumin4tibak","BackupFailed":false,"BackupVerified":true,"MutationStarted":true,"RecoveryAttempted":true,"RestoredFromBackup":true,"VerificationSucceeded":true,"SuccessCount":0,"FailedPaths":[],"UnrecoveredPaths":[],"Warnings":[],"Error":null}
        """,
        string.Empty);

    private sealed class RecordingExecutor(
        Func<string, CancellationToken, Task<UnelevatedCommandExecutionResult>> execute)
        : IUnelevatedCommandExecutor
    {
        public int InvocationCount { get; private set; }

        public TimeSpan? RequestedTimeout { get; private set; }

        public Task<UnelevatedCommandExecutionResult> RunPowerShellAsync(
            string script,
            CancellationToken ct = default)
        {
            InvocationCount++;
            return execute(script, ct);
        }

        public Task<UnelevatedCommandExecutionResult> RunPowerShellAsync(
            string script,
            TimeSpan timeout,
            CancellationToken ct = default)
        {
            RequestedTimeout = timeout;
            InvocationCount++;
            return execute(script, ct);
        }
    }
}
