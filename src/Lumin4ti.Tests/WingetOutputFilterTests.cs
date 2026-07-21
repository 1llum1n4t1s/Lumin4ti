using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class WingetOutputFilterTests
{
    private const string OfficialSource =
        """{"Arg":"https://cdn.winget.microsoft.com/cache","Data":"Microsoft.Winget.Source_8wekyb3d8bbwe","Explicit":false,"Identifier":"Microsoft.Winget.Source_8wekyb3d8bbwe","Name":"winget","TrustLevel":["Trusted","StoreOrigin"],"Type":"Microsoft.PreIndexed.Package"}""";

    [TestMethod]
    public void プログレスバーとスピナー行は除外される()
    {
        Assert.IsFalse(WingetUpgradeAction.IsMeaningfulLine("██████████░░  45%"));
        Assert.IsFalse(WingetUpgradeAction.IsMeaningfulLine("▒▒▒▒▒▒"));
        Assert.IsFalse(WingetUpgradeAction.IsMeaningfulLine("-"));
        Assert.IsFalse(WingetUpgradeAction.IsMeaningfulLine("\\"));
        Assert.IsFalse(WingetUpgradeAction.IsMeaningfulLine("   "));
        Assert.IsFalse(WingetUpgradeAction.IsMeaningfulLine("---------------"));
    }

    [TestMethod]
    public void 成功件数を日英ロケールで数えられる()
    {
        string[] lines =
        [
            "見つかりました uv [astral-sh.uv] バージョン 0.11.26",
            "正常にインストールされました",
            "Successfully installed",
            "インストーラーが終了コードで失敗しました: 0x8a150003",
        ];

        Assert.AreEqual(2, WingetUpgradeAction.CountSuccessfulInstalls(lines));
        Assert.AreEqual(0, WingetUpgradeAction.CountSuccessfulInstalls(["失敗しました"]));
    }

    [TestMethod]
    public void 意味のある行は通過する()
    {
        Assert.IsTrue(WingetUpgradeAction.IsMeaningfulLine("Google Chrome を更新しています"));
        Assert.IsTrue(WingetUpgradeAction.IsMeaningfulLine("インストールが完了しました"));
        Assert.IsTrue(WingetUpgradeAction.IsMeaningfulLine("2 個のパッケージにアップグレードがあります"));
    }

    [TestMethod]
    public void 公式ソースJSONは余計な診断行があっても検証できる()
    {
        var output = $"診断メッセージ{Environment.NewLine}{OfficialSource}{Environment.NewLine}完了";
        var reversedTrustLevel = OfficialSource.Replace(
            "[\"Trusted\",\"StoreOrigin\"]",
            "[\"StoreOrigin\",\"Trusted\"]",
            StringComparison.Ordinal);

        Assert.IsTrue(WingetUpgradeAction.IsOfficialSourceExport(output));
        Assert.IsTrue(WingetUpgradeAction.IsOfficialSourceExport(reversedTrustLevel));
    }

    [TestMethod]
    public void 不正または必須値欠損のソースJSONは拒否する()
    {
        string[] invalidOutputs =
        [
            "{\"Name\":\"winget\"}",
            "{\"Name\":\"winget\"",
            "[]",
        ];

        foreach (var output in invalidOutputs)
        {
            Assert.IsFalse(WingetUpgradeAction.IsOfficialSourceExport(output));
        }
    }

    [TestMethod]
    public void 公式ソースの識別値または信頼レベルが違えば拒否する()
    {
        string[] invalidOutputs =
        [
            OfficialSource.Replace(
                "https://cdn.winget.microsoft.com/cache",
                "https://evil.example/cache",
                StringComparison.Ordinal),
            OfficialSource.Replace(
                "\"Data\":\"Microsoft.Winget.Source_8wekyb3d8bbwe\"",
                "\"Data\":\"Untrusted.Source\"",
                StringComparison.Ordinal),
            OfficialSource.Replace(
                "\"Identifier\":\"Microsoft.Winget.Source_8wekyb3d8bbwe\"",
                "\"Identifier\":\"Untrusted.Source\"",
                StringComparison.Ordinal),
            OfficialSource.Replace(
                "\"Name\":\"winget\"",
                "\"Name\":\"untrusted\"",
                StringComparison.Ordinal),
            OfficialSource.Replace(
                "\"Type\":\"Microsoft.PreIndexed.Package\"",
                "\"Type\":\"Microsoft.Rest.Source\"",
                StringComparison.Ordinal),
            OfficialSource.Replace(
                "[\"Trusted\",\"StoreOrigin\"]",
                "[\"Trusted\"]",
                StringComparison.Ordinal),
            OfficialSource.Replace(
                "[\"Trusted\",\"StoreOrigin\"]",
                "[\"Trusted\",\"StoreOrigin\",\"Other\"]",
                StringComparison.Ordinal),
            OfficialSource.Replace(
                "[\"Trusted\",\"StoreOrigin\"]",
                "[\"Trusted\",\"Trusted\",\"StoreOrigin\"]",
                StringComparison.Ordinal),
        ];

        foreach (var output in invalidOutputs)
        {
            Assert.IsFalse(WingetUpgradeAction.IsOfficialSourceExport(output));
        }
    }

    [TestMethod]
    public void 複数のソースJSONは拒否する()
    {
        Assert.IsFalse(WingetUpgradeAction.IsOfficialSourceExport(
            OfficialSource + Environment.NewLine + OfficialSource));
    }

    [TestMethod]
    public void 必須プロパティの重複は拒否する()
    {
        var duplicateName = OfficialSource.Replace(
            "\"Name\":\"winget\"",
            "\"Name\":\"winget\",\"Name\":\"winget\"",
            StringComparison.Ordinal);

        Assert.IsFalse(WingetUpgradeAction.IsOfficialSourceExport(duplicateName));
    }

    [TestMethod]
    public async Task 公式ソース確認後だけ固定ソースで更新する()
    {
        var executor = new RecordingExecutor(
            Success(OfficialSource),
            Success(PackageTable(("Git", "Git.Git"))),
            Success(PackageTable(("Git", "Git.Git"))),
            Success("Successfully installed"));
        var action = new WingetUpgradeAction(executor);

        var result = await action.ExecuteAsync();

        Assert.IsTrue(result.Success);
        Assert.AreEqual(4, executor.Calls.Count);
        Assert.AreEqual(WingetUpgradeAction.SourceExportArguments, executor.Calls[0].Arguments);
        Assert.AreEqual(WingetUpgradeAction.ListUpgradesArguments, executor.Calls[1].Arguments);
        Assert.AreEqual(WingetUpgradeAction.BuildSearchArguments("Git.Git"), executor.Calls[2].Arguments);
        Assert.AreEqual(WingetUpgradeAction.BuildUpgradeArguments("Git.Git"), executor.Calls[3].Arguments);
        StringAssert.Contains(executor.Calls[3].Arguments, "--id Git.Git --exact");
        Assert.IsFalse(executor.Calls[3].Arguments.Contains("--all", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task インストール名とカタログ名が違う候補は更新しない()
    {
        var executor = new RecordingExecutor(
            Success(OfficialSource),
            Success(PackageTable(("Brave Origin", "Brave.BraveOrigin.Nightly"))),
            Success(PackageTable(("Brave Origin Nightly", "Brave.BraveOrigin.Nightly"))));
        var action = new WingetUpgradeAction(executor);

        var result = await action.ExecuteAsync();

        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        Assert.AreEqual(3, executor.Calls.Count);
        Assert.IsFalse(executor.Calls.Any(call =>
            call.Arguments.StartsWith("upgrade ", StringComparison.Ordinal)));
        StringAssert.Contains(result.Detail, "カタログ名「Brave Origin Nightly」と一致しないため除外");
    }

    [TestMethod]
    public async Task 公式ソースと不一致なら更新を実行しない()
    {
        var maliciousSource = OfficialSource.Replace(
            "https://cdn.winget.microsoft.com/cache",
            "https://evil.example/cache",
            StringComparison.Ordinal);
        var executor = new RecordingExecutor(Success(maliciousSource));
        var action = new WingetUpgradeAction(executor);

        var result = await action.ExecuteAsync();

        Assert.IsFalse(result.Success);
        Assert.AreEqual(1, executor.Calls.Count);
        StringAssert.Contains(result.Detail, "更新を中止");
    }

    [TestMethod]
    public async Task ソース取得失敗時も更新を実行しない()
    {
        var executor = new RecordingExecutor(
            new CommandExecutionResult(false, string.Empty, 5, string.Empty, "error"));
        var action = new WingetUpgradeAction(executor);

        var result = await action.ExecuteAsync();

        Assert.IsFalse(result.Success);
        Assert.AreEqual(1, executor.Calls.Count);
    }

    [TestMethod]
    public async Task 一部packageだけ更新できた場合は部分成功を返す()
    {
        var executor = new RecordingExecutor(
            Success(OfficialSource),
            Success(PackageTable(("Git", "Git.Git"), ("uv", "astral-sh.uv"))),
            Success(PackageTable(("Git", "Git.Git"))),
            Success("Successfully installed"),
            Success(PackageTable(("uv", "astral-sh.uv"))),
            new CommandExecutionResult(
                false,
                string.Empty,
                1,
                "Installer failed with exit code: 1",
                "error"));
        var action = new WingetUpgradeAction(executor);

        var result = await action.ExecuteAsync();

        Assert.AreEqual(MaintenanceActionStatus.Partial, result.Status);
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Detail, "成功 1 件 / 失敗 1 件 / 安全確認で除外 0 件");
    }

    [TestMethod]
    public async Task ライブ出力フィルタは呼出元SynchronizationContextを捕捉しない()
    {
        var executor = new RecordingExecutor(
            Success(OfficialSource),
            Success(PackageTable(("Git", "Git.Git"))),
            Success(PackageTable(("Git", "Git.Git"))),
            Success("更新が完了しました"))
        {
            OutputLines = ["████ 50%", "更新が完了しました"],
        };
        var action = new WingetUpgradeAction(executor);
        var received = new List<string>();
        var context = new CountingSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        Task<MaintenanceActionResult> execution;

        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            execution = action.ExecuteAsync(new InlineProgress<string>(received.Add));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        Assert.IsTrue((await execution).Success);
        Assert.AreEqual(0, context.PostCount);
        CollectionAssert.Contains(received, "更新が完了しました");
        Assert.IsFalse(received.Any(line => line.Contains('█')));
    }

    [TestMethod]
    public void 切り詰められた名前や不正なIDを含む一覧は拒否する()
    {
        Assert.IsFalse(WingetUpgradeAction.TryParsePackageTable(
            PackageTable(("Brave Origin…", "Brave.BraveOrigin.Nightly")),
            out _,
            out var truncatedTableFound));
        Assert.IsTrue(truncatedTableFound);

        Assert.IsFalse(WingetUpgradeAction.TryParsePackageTable(
            PackageTable(("安全でない候補", "invalid;command")),
            out _,
            out var unsafeTableFound));
        Assert.IsTrue(unsafeTableFound);
    }

    [TestMethod]
    public void 表末尾の集計行はpackageとして扱わない()
    {
        var output = PackageTable(("Git", "Git.Git"))
                     + Environment.NewLine
                     + "1 アップグレードを利用できます。";

        Assert.IsTrue(WingetUpgradeAction.TryParsePackageTable(
            output,
            out var packages,
            out var tableFound));
        Assert.IsTrue(tableFound);
        Assert.AreEqual(1, packages.Count);
        Assert.AreEqual("Git.Git", packages[0].Id);
    }

    [TestMethod]
    public void 日本語ヘッダーの全角幅で列位置がずれても解析できる()
    {
        const string output =
            "名前         ID                        バージョン   利用可能\n" +
            "---------------------------------------------------------------\n" +
            "Brave Origin Brave.BraveOrigin.Nightly 150.1.92.141 151.1.94.83\n" +
            "Git          Git.Git                   2.55.0.2     2.55.0.3\n" +
            "2 アップグレードを利用できます。";

        Assert.IsTrue(WingetUpgradeAction.TryParsePackageTable(
            output,
            out var packages,
            out var tableFound));
        Assert.IsTrue(tableFound);
        Assert.AreEqual(2, packages.Count);
        Assert.AreEqual("Brave Origin", packages[0].Name);
        Assert.AreEqual("Brave.BraveOrigin.Nightly", packages[0].Id);
    }

    private static CommandExecutionResult Success(string output) =>
        new(true, string.Empty, 0, output, string.Empty);

    private static string PackageTable(params (string Name, string Id)[] packages)
    {
        var nameWidth = Math.Max("名前".Length, packages.Max(package => package.Name.Length)) + 2;
        var header = "名前".PadRight(nameWidth) + "ID  バージョン";
        var rows = packages.Select(package =>
            package.Name.PadRight(nameWidth) + package.Id + "  1.0");
        return string.Join(
            Environment.NewLine,
            new[] { header, new string('-', header.Length) }.Concat(rows));
    }

    private sealed class RecordingExecutor(params CommandExecutionResult[] results) : ICommandExecutor
    {
        private readonly Queue<CommandExecutionResult> _results = new(results);

        public List<(string FileName, string Arguments)> Calls { get; } = [];

        public IReadOnlyList<string> OutputLines { get; init; } = [];

        public Task<CommandExecutionResult> RunAsync(
            string fileName,
            string arguments,
            CancellationToken ct = default,
            IProgress<string>? onOutputLine = null)
        {
            Calls.Add((fileName, arguments));
            foreach (var line in OutputLines)
            {
                onOutputLine?.Report(line);
            }

            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class CountingSynchronizationContext : SynchronizationContext
    {
        public int PostCount { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            PostCount++;
            d(state);
        }
    }
}
