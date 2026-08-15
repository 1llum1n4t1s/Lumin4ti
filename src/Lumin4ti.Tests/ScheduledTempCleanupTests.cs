using System.Text;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class ScheduledTempCleanupTests
{
    private sealed class FailedSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new() { ScheduledCleanupGroupIds = [] };

        public SettingsLoadStatus LoadStatus => SettingsLoadStatus.Failed;

        public object SyncRoot { get; } = new();

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

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

    /// <summary>
    /// タスク定義の置き場のフェイク。既定は ACL を固定した ProgramData 配下で、
    /// 非昇格のテスト実行では書けないため差し替える。
    /// </summary>
    private sealed class FakeTaskDefinitionStore : ITaskDefinitionStore
    {
        public string? LastPath { get; private set; }

        public byte[]? LastContent { get; private set; }

        public bool Deleted { get; private set; }

        public string WriteNew(string name, byte[] content)
        {
            LastContent = content;
            LastPath = Path.Combine(@"C:\ProgramData\Lumin4ti\backups\scheduled-tasks", name);
            return LastPath;
        }

        public void Delete(string name) => Deleted = true;
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
    public void 登録コマンドはタスク定義XMLのパスをクォートして渡す()
    {
        const string xmlPath = @"C:\Users\Test\AppData\Local\Temp\Lumin4ti-task.xml";
        var arguments = ScheduledTempCleanupToggle.BuildCreateArguments(xmlPath);

        StringAssert.Contains(arguments, "/create");
        StringAssert.Contains(arguments, ScheduledTempCleanupToggle.TaskName);
        StringAssert.Contains(arguments, $"/xml \"{xmlPath}\"");
        StringAssert.Contains(arguments, "/f");
    }

    [TestMethod]
    public void タスク定義はログオン時トリガーと非昇格実行を指定する()
    {
        const string exePath = @"C:\Program Files\Lumin4ti\Lumin4ti.exe";
        var xml = ScheduledTempCleanupToggle.BuildTaskXml(exePath, @"TESTPC\Test");

        StringAssert.Contains(xml, "<LogonTrigger>");
        StringAssert.Contains(xml, "<RunLevel>LeastPrivilege</RunLevel>");
        StringAssert.Contains(xml, "<LogonType>InteractiveToken</LogonType>");
        StringAssert.Contains(xml, $"<Command>{exePath}</Command>");
        StringAssert.Contains(xml, $"<Arguments>{ScheduledTempCleanup.CommandLineArgument}</Arguments>");
        StringAssert.Contains(xml, @"<UserId>TESTPC\Test</UserId>");
    }

    [TestMethod]
    public void タスク定義はバッテリー駆動でも実行と中断なしを指定する()
    {
        var xml = ScheduledTempCleanupToggle.BuildTaskXml(@"C:\Lumin4ti\Lumin4ti.exe", @"TESTPC\Test");

        StringAssert.Contains(xml, "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
        StringAssert.Contains(xml, "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
        StringAssert.Contains(xml, "<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
    }

    [TestMethod]
    public void タスク定義はXMLとして解析できエスケープが効く()
    {
        var xml = ScheduledTempCleanupToggle.BuildTaskXml(@"C:\Program Files\A & B\Lumin4ti.exe", @"TESTPC\A&B");

        // 宣言の encoding="UTF-16" と string の内部表現が食い違うので、宣言を外してから解析する。
        var body = xml[(xml.IndexOf("?>", StringComparison.Ordinal) + 2)..];
        var document = System.Xml.Linq.XDocument.Parse(body);
        var ns = (System.Xml.Linq.XNamespace)"http://schemas.microsoft.com/windows/2004/02/mit/task";

        Assert.AreEqual(
            @"C:\Program Files\A & B\Lumin4ti.exe",
            document.Root!.Element(ns + "Actions")!.Element(ns + "Exec")!.Element(ns + "Command")!.Value);
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
        var toggle = new ScheduledTempCleanupToggle(executor, new FakeTaskDefinitionStore(), _ => true);

        var result = await toggle.SetStateAsync(true);

        Assert.AreEqual(MaintenanceActionStatus.Success, result.Status);
        Assert.AreEqual("schtasks", executor.LastFileName);
        StringAssert.Contains(executor.LastArguments, "/create");
    }

    [TestMethod]
    public async Task タスク定義は保護ストレージへ書いてそのパスを渡す()
    {
        // %TEMP% へ書くと、書き終えてから昇格 schtasks が読むまでの間に同一ユーザーの
        // 非昇格プロセスが定義を差し替えられる (ログオン時に自動実行されるタスクの乗っ取り)。
        var executor = new RecordingExecutor { NextSuccess = true };
        var store = new FakeTaskDefinitionStore();
        var toggle = new ScheduledTempCleanupToggle(executor, store, _ => true);

        await toggle.SetStateAsync(true);

        Assert.IsNotNull(store.LastPath, "タスク定義は保護ストレージ経由で書き出す必要があります");
        StringAssert.Contains(executor.LastArguments, $"/xml \"{store.LastPath}\"");
        Assert.IsTrue(store.Deleted, "登録後にタスク定義を削除する必要があります");
    }

    [TestMethod]
    public async Task タスク定義はBOM付きUTF16で書き出す()
    {
        // schtasks /xml は UTF-16 かつ BOM 付きでないと受け付けない。
        var store = new FakeTaskDefinitionStore();
        var toggle = new ScheduledTempCleanupToggle(
            new RecordingExecutor { NextSuccess = true },
            store,
            _ => true);

        await toggle.SetStateAsync(true);

        var content = store.LastContent!;
        CollectionAssert.AreEqual(new byte[] { 0xFF, 0xFE }, content.Take(2).ToArray());
        StringAssert.Contains(
            Encoding.Unicode.GetString(content, 2, content.Length - 2),
            "<LogonTrigger>");
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
        var toggle = new ScheduledTempCleanupToggle(executor, new FakeTaskDefinitionStore(), _ => true);

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
    public void 定期実行は選ばれた項目だけをカタログの並び順で実行する()
    {
        var actions = FileCleanupGroups.CreateCleanupActions(new RecordingExecutor());

        var selected = ScheduledTempCleanup.SelectActions(
            actions,
            ["cleanup-browser-cache", "cleanup-user-temp", "存在しない項目"]);

        CollectionAssert.AreEqual(
            new[] { "cleanup-user-temp", "cleanup-browser-cache" },
            selected.Select(a => a.Id).ToArray(),
            "設定の並びではなくカタログの並び順で実行し、未知の Id は無視する必要があります");
    }

    [TestMethod]
    public void 設定読込失敗時は既定対象を推測して削除しない()
    {
        var executor = new RecordingExecutor();

        var exitCode = ScheduledTempCleanup.Run(new FailedSettingsService(), executor);

        Assert.AreEqual(1, exitCode);
        Assert.IsNull(executor.LastFileName);
    }

    [TestMethod]
    public void 定期実行の既定項目はすべてカタログに存在する()
    {
        var ids = FileCleanupGroups.CreateCleanupActions(new RecordingExecutor())
            .Select(a => a.Id)
            .ToArray();

        foreach (var id in CleanupPreferences.DefaultScheduledGroupIds)
        {
            CollectionAssert.Contains(ids, id);
        }
    }

    [TestMethod]
    public void 実行項目のチェックリストはカタログの全項目を出す()
    {
        var toggle = new ScheduledTempCleanupToggle(new RecordingExecutor());
        var expected = FileCleanupGroups.CreateCleanupActions(new RecordingExecutor()).Select(a => a.Id).ToArray();

        var entries = toggle.GetCheckListEntries();

        CollectionAssert.AreEqual(expected, entries.Select(e => e.Value).ToArray());
        Assert.IsTrue(
            entries.Where(e => CleanupPreferences.DefaultScheduledGroupIds.Contains(e.Value)).All(e => e.IsSelected),
            "既定セットの項目は初期状態でチェック済みである必要があります");
    }

    [TestMethod]
    public void ストア型キャッシュは開発ツールキャッシュ群にも含まれる()
    {
        // 手動実行のグループとサインイン時の定期削除で対象がずれないようにする。
        var packagePaths = FileCleanupGroups.PackageCacheTargets.Select(t => t.RawPath).ToArray();

        Assert.IsTrue(FileCleanupGroups.StoreCacheTargets.Length > 0);
        foreach (var target in FileCleanupGroups.StoreCacheTargets)
        {
            CollectionAssert.Contains(packagePaths, target.RawPath);
        }
    }

    [TestMethod]
    public void ストア型キャッシュの対象は安全ガードを通過する()
    {
        foreach (var target in FileCleanupGroups.StoreCacheTargets)
        {
            Assert.IsTrue(
                FileCleanupEngine.TryResolve(target.RawPath, out _, out var reason),
                $"{target.RawPath}: {reason}");
        }
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
