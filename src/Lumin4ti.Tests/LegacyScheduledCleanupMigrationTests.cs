using Lumin4ti.Core.Services.Windows;
using static Lumin4ti.Core.Services.Windows.LegacyScheduledCleanupMigration;

namespace Lumin4ti.Tests;

/// <summary>
/// 旧サインイン時クリーンアップタスクの移行。タスクスケジューラーと保護ストレージは偽境界へ差し替え、
/// 実機のタスク・フォルダー・ファイルには一切触れない。
/// </summary>
[TestClass]
public sealed class LegacyScheduledCleanupMigrationTests
{
    private static readonly CurrentUser Me = new(@"PC\me", "S-1-5-21-1-2-3-1001");

    private const string ExePath = @"C:\Program Files\Lumin4ti\current\Lumin4ti.UI.exe";

    private static string TaskXml(
        string userId = @"PC\me",
        string command = ExePath,
        string arguments = "--scheduled-cleanup-temp",
        string? extraAction = null,
        bool principal = true) =>
        $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Principals>{(principal ? $"<Principal id=\"Author\"><UserId>{userId}</UserId><RunLevel>HighestAvailable</RunLevel></Principal>" : string.Empty)}</Principals>
          <Actions Context="Author">
            <Exec><Command>{command}</Command><Arguments>{arguments}</Arguments></Exec>{extraAction}
          </Actions>
        </Task>
        """;

    private sealed class FakeTask(FakeStore store, Queue<string> xmlReads) : ILegacyTask
    {
        public string ReadXml()
        {
            store.Calls.Add("ReadXml");
            store.OnReadXml?.Invoke();
            if (store.ThrowOnReadXml)
            {
                throw new InvalidOperationException("read failed");
            }

            // 最後の定義は繰り返し返す。差し替えを試すときだけ複数並べる。
            return xmlReads.Count > 1 ? xmlReads.Dequeue() : xmlReads.Peek();
        }

        public void Delete()
        {
            store.Calls.Add("DeleteTask");
            if (store.ThrowOnDelete)
            {
                throw new InvalidOperationException("delete failed");
            }
        }

        public void Dispose() => store.Calls.Add("DisposeTask");
    }

    private sealed class FakeStore : ILegacyTaskStore
    {
        public List<string> Calls { get; } = [];

        public Queue<string>? XmlReads { get; init; }

        public bool ThrowOnFind { get; init; }

        public bool ThrowOnReadXml { get; init; }

        public bool ThrowOnDelete { get; init; }

        public bool ThrowOnFolderQuery { get; init; }

        /// <summary>期限切れの模擬: 照会の最中に取消を要求する。</summary>
        public Action? OnReadXml { get; init; }

        public Action? OnFolderQuery { get; init; }

        /// <summary>null はフォルダー無し。隠しタスクやサブフォルダーがあれば false。</summary>
        public bool? FolderEmpty { get; init; } = true;

        public ILegacyTask? FindTask(string folderPath, string name)
        {
            Calls.Add($"Find {folderPath}\\{name}");
            if (ThrowOnFind)
            {
                throw new UnauthorizedAccessException("denied");
            }

            return XmlReads is null ? null : new FakeTask(this, XmlReads);
        }

        public bool? IsFolderEmpty(string folderPath)
        {
            Calls.Add("IsFolderEmpty");
            OnFolderQuery?.Invoke();
            return ThrowOnFolderQuery ? throw new InvalidOperationException("query failed") : FolderEmpty;
        }

        public void DeleteFolder(string folderPath) => Calls.Add($"DeleteFolder {folderPath}");

        public void Dispose()
        {
        }
    }

    private sealed class FakeFiles(params string[] names) : ILegacyTaskDefinitionFiles
    {
        public List<string> Deleted { get; } = [];

        public bool ThrowOnList { get; init; }

        public Action? OnList { get; init; }

        public Action? OnDelete { get; init; }

        public IReadOnlyList<string> List()
        {
            OnList?.Invoke();
            return ThrowOnList ? throw new IOException("list failed") : names;
        }

        public void Delete(string name)
        {
            Deleted.Add(name);
            OnDelete?.Invoke();
        }
    }

    private static FakeStore StoreWith(params string[] xmlReads) => new() { XmlReads = new Queue<string>(xmlReads) };

    [TestMethod]
    public void 引数の定数は旧タスクの引数と一致する()
    {
        // 旧クラスは削除済みなので、旧タスクが渡していた引数の文字列そのものと照合する。
        Assert.AreEqual("--scheduled-cleanup-temp", ObsoleteArgument);
    }

    [TestMethod]
    [DataRow(@"PC\me")]
    [DataRow("S-1-5-21-1-2-3-1001")]
    [DataRow(@"pc\ME")]
    public void 現在の利用者のタスクは名前でもSIDでも削除し空フォルダーも消す(string userId)
    {
        var store = StoreWith(TaskXml(userId: userId));

        var report = Migrate(store, new FakeFiles(), Me);

        Assert.AreEqual(TaskOutcome.Deleted, report.Task);
        Assert.IsTrue(report.FolderRemoved);
        CollectionAssert.Contains(store.Calls, "DeleteTask");
        CollectionAssert.Contains(store.Calls, @"DeleteFolder \Lumin4ti");
        Assert.AreEqual(@"Find \Lumin4ti\ScheduledTempCleanup", store.Calls[0]);
    }

    [TestMethod]
    public void 旧PerUser配置の実行ファイルも対象にする()
    {
        var store = StoreWith(TaskXml(command: "\"C:\\Users\\me\\AppData\\Local\\Lumin4ti\\current\\lumin4ti.ui.exe\""));

        Assert.AreEqual(TaskOutcome.Deleted, Migrate(store, new FakeFiles(), Me).Task);
    }

    [TestMethod]
    public void 所有者が別の利用者なら保持する()
    {
        var store = StoreWith(TaskXml(userId: @"PC\other"));

        var report = Migrate(store, new FakeFiles(), Me);

        Assert.AreEqual(TaskOutcome.Kept, report.Task);
        Assert.IsFalse(report.FolderRemoved);
        CollectionAssert.DoesNotContain(store.Calls, "DeleteTask");
        CollectionAssert.DoesNotContain(store.Calls, @"DeleteFolder \Lumin4ti");
    }

    [TestMethod]
    public void 所有者が空でも保持する()
    {
        var store = StoreWith(TaskXml(userId: " "));

        Assert.AreEqual(TaskOutcome.Kept, Migrate(store, new FakeFiles(), Me).Task);
        CollectionAssert.DoesNotContain(store.Calls, "DeleteTask");
    }

    [TestMethod]
    public void 定義が旧版の登録内容と違えば保持する()
    {
        var variants = new[]
        {
            TaskXml(arguments: "--scheduled-cleanup-temp --extra"),
            TaskXml(arguments: "--SCHEDULED-CLEANUP-TEMP"),
            TaskXml(command: @"C:\Windows\System32\cmd.exe"),
            TaskXml(command: @"C:\Program Files\Lumin4ti\current\Lumin4ti.RebootCleanup.exe"),
            TaskXml(extraAction: "<Exec><Command>C:\\x.exe</Command></Exec>"),
            TaskXml(extraAction: "<ComHandler><ClassId>{00000000-0000-0000-0000-000000000000}</ClassId></ComHandler>"),
            TaskXml(principal: false),
            "<not-xml",
            "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\" />",
        };

        foreach (var xml in variants)
        {
            var store = StoreWith(xml);

            var report = Migrate(store, new FakeFiles(), Me);

            Assert.AreEqual(TaskOutcome.Kept, report.Task, xml);
            CollectionAssert.DoesNotContain(store.Calls, "DeleteTask", xml);
            CollectionAssert.DoesNotContain(store.Calls, @"DeleteFolder \Lumin4ti", xml);
        }
    }

    [TestMethod]
    public void 削除直前に別の定義へ差し替えられたら保持する()
    {
        var store = StoreWith(TaskXml(), TaskXml(command: @"C:\Temp\evil.exe"));

        var report = Migrate(store, new FakeFiles(), Me);

        Assert.AreEqual(TaskOutcome.Kept, report.Task);
        Assert.AreEqual(2, store.Calls.Count(c => c == "ReadXml"));
        CollectionAssert.DoesNotContain(store.Calls, "DeleteTask");
        CollectionAssert.DoesNotContain(store.Calls, @"DeleteFolder \Lumin4ti");
    }

    [TestMethod]
    public void 照会に失敗したら例外を出さず保持して終える()
    {
        var store = new FakeStore { ThrowOnFind = true };

        var report = Migrate(store, new FakeFiles(), Me);

        Assert.AreEqual(TaskOutcome.Failed, report.Task);
        Assert.IsFalse(report.FolderRemoved);
        CollectionAssert.DoesNotContain(store.Calls, "DeleteTask");
        CollectionAssert.DoesNotContain(store.Calls, "IsFolderEmpty");
    }

    [TestMethod]
    public void 定義の読取に失敗したら削除しない()
    {
        var store = new FakeStore { XmlReads = new Queue<string>([TaskXml()]), ThrowOnReadXml = true };

        var report = Migrate(store, new FakeFiles(), Me);

        Assert.AreEqual(TaskOutcome.Failed, report.Task);
        CollectionAssert.DoesNotContain(store.Calls, "DeleteTask");
        CollectionAssert.Contains(store.Calls, "DisposeTask");
    }

    [TestMethod]
    public void 削除に失敗したらフォルダーへ触れない()
    {
        var store = new FakeStore { XmlReads = new Queue<string>([TaskXml()]), ThrowOnDelete = true };

        var report = Migrate(store, new FakeFiles(), Me);

        Assert.AreEqual(TaskOutcome.Failed, report.Task);
        Assert.IsFalse(report.FolderRemoved);
        CollectionAssert.DoesNotContain(store.Calls, "IsFolderEmpty");
    }

    [TestMethod]
    public void 隠しタスクやサブフォルダーが残るフォルダーは消さない()
    {
        var store = new FakeStore { XmlReads = new Queue<string>([TaskXml()]), FolderEmpty = false };

        var report = Migrate(store, new FakeFiles(), Me);

        Assert.AreEqual(TaskOutcome.Deleted, report.Task);
        Assert.IsFalse(report.FolderRemoved);
        CollectionAssert.Contains(store.Calls, "IsFolderEmpty");
        CollectionAssert.DoesNotContain(store.Calls, @"DeleteFolder \Lumin4ti");
    }

    [TestMethod]
    public void タスクが無く空フォルダーだけ残っていれば整理する()
    {
        var store = new FakeStore();

        var report = Migrate(store, new FakeFiles(), Me);

        Assert.AreEqual(TaskOutcome.NotFound, report.Task);
        Assert.IsTrue(report.FolderRemoved);
    }

    [TestMethod]
    public void フォルダーが無ければ何も消さない()
    {
        var store = new FakeStore { FolderEmpty = null };

        var report = Migrate(store, new FakeFiles(), Me);

        Assert.AreEqual(TaskOutcome.NotFound, report.Task);
        Assert.IsFalse(report.FolderRemoved);
        CollectionAssert.DoesNotContain(store.Calls, @"DeleteFolder \Lumin4ti");
    }

    [TestMethod]
    public void フォルダー照会の失敗は例外にならず消さない()
    {
        var store = new FakeStore { ThrowOnFolderQuery = true };

        var report = Migrate(store, new FakeFiles(), Me);

        Assert.IsFalse(report.FolderRemoved);
        CollectionAssert.DoesNotContain(store.Calls, @"DeleteFolder \Lumin4ti");
    }

    [TestMethod]
    public void 保護ストレージはtemp_cleanupのxmlだけ削除する()
    {
        var files = new FakeFiles(
            "temp-cleanup-0123456789abcdef.xml",
            "TEMP-CLEANUP-ABC.XML",
            "reboot-cleanup.xml",
            "temp-cleanup-x.txt",
            @"..\temp-cleanup-evil.xml",
            "sub/temp-cleanup-evil.xml");

        var report = Migrate(new FakeStore(), files, Me);

        CollectionAssert.AreEqual(
            new[] { "temp-cleanup-0123456789abcdef.xml", "TEMP-CLEANUP-ABC.XML" },
            files.Deleted);
        Assert.AreEqual(2, report.DefinitionsRemoved);
    }

    [TestMethod]
    public void 定義の一覧に失敗しても例外を出さずタスク削除は完了する()
    {
        var store = StoreWith(TaskXml());

        var report = Migrate(store, new FakeFiles { ThrowOnList = true }, Me);

        Assert.AreEqual(TaskOutcome.Deleted, report.Task);
        Assert.AreEqual(0, report.DefinitionsRemoved);
    }

    [TestMethod]
    public void 期限切れの後はタスク削除へ進まない()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeStore { XmlReads = new Queue<string>([TaskXml()]), OnReadXml = cancellation.Cancel };

        Assert.ThrowsExactly<OperationCanceledException>(() => Migrate(store, new FakeFiles(), Me, cancellation.Token));

        CollectionAssert.DoesNotContain(store.Calls, "DeleteTask");
    }

    [TestMethod]
    public void 期限切れの後はフォルダー削除へ進まない()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new FakeStore { OnFolderQuery = cancellation.Cancel };

        Assert.ThrowsExactly<OperationCanceledException>(() => Migrate(store, new FakeFiles(), Me, cancellation.Token));

        CollectionAssert.DoesNotContain(store.Calls, @"DeleteFolder \Lumin4ti");
    }

    [TestMethod]
    public void 期限切れの後は保護ストレージの定義を削除しない()
    {
        using var beforeList = new CancellationTokenSource();
        var listed = new FakeFiles("temp-cleanup-a.xml") { OnList = beforeList.Cancel };
        Assert.ThrowsExactly<OperationCanceledException>(() => Migrate(new FakeStore(), listed, Me, beforeList.Token));
        Assert.IsEmpty(listed.Deleted);

        using var betweenFiles = new CancellationTokenSource();
        var files = new FakeFiles("temp-cleanup-a.xml", "temp-cleanup-b.xml") { OnDelete = betweenFiles.Cancel };
        Assert.ThrowsExactly<OperationCanceledException>(() => Migrate(new FakeStore(), files, Me, betweenFiles.Token));
        CollectionAssert.AreEqual(new[] { "temp-cleanup-a.xml" }, files.Deleted);
    }

    [TestMethod]
    public void 他の利用者のタスクでも保護ストレージの定義は整理する()
    {
        var files = new FakeFiles("temp-cleanup-a.xml");
        var store = StoreWith(TaskXml(userId: @"PC\other"));

        var report = Migrate(store, files, Me);

        Assert.AreEqual(TaskOutcome.Kept, report.Task);
        Assert.AreEqual(1, report.DefinitionsRemoved);
    }
}
