using System.Text;
using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;

namespace Lumin4ti.Tests;

/// <summary>復元。偽のキー木・偽の保存先で行い、実レジストリは変えない。</summary>
[TestClass]
public sealed class RegistryRestoreTests
{
    private static string B64(string text) => Convert.ToBase64String(Encoding.Unicode.GetBytes(text + "\0"));

    private static RegistryBackupDocument Doc(string sid, params RegistryBackupItem[] items) =>
        new(RegistryBackupDocument.CurrentVersion, DateTime.UtcNow, sid, items);

    private static RegistryBackupItem ValueItem(
        string path, string name, string text, RegistryCleanHive hive = RegistryCleanHive.LocalMachine,
        RegistryCleanCategory category = RegistryCleanCategory.Fonts) =>
        new(category, hive, RegistryCleanView.Registry64, path, name, 1, B64(text), null);

    private static RegistryBackupItem KeyItem(string path, RegistryBackupKeyNode node, RegistryCleanCategory category = RegistryCleanCategory.AppPaths) =>
        new(category, RegistryCleanHive.LocalMachine, RegistryCleanView.Registry64, path, null, null, null, node);

    private static string Put(RegHarness h, RegistryBackupDocument document)
    {
        var id = $"20260101T000000000Z-{Guid.NewGuid():N}";
        h.Store.Documents[id] = document;
        return id;
    }

    [TestMethod]
    public void MissingValueIsWrittenBackAndRestored()
    {
        var h = new RegHarness();
        var id = Put(h, Doc(RegHarness.Sid, ValueItem(RegHarness.Fonts, "Gone (TrueType)", "gone.ttf")));

        var result = h.Restore.Restore(id);

        Assert.AreEqual(RegistryOverallState.Success, result.Overall);
        Assert.AreEqual(RegistryItemState.Restored, result.Items.Single().State);
        Assert.AreEqual("gone.ttf", h.Machine.Find(RegHarness.Fonts)!.Text("Gone (TrueType)"));
        Assert.IsTrue(result.ScanInvalidated);
    }

    [TestMethod]
    public void SameValueIsAlreadyPresentAndDifferentValueIsConflictWithoutOverwrite()
    {
        var h = new RegHarness();
        h.Machine.Path(RegHarness.Fonts).Str("Same", "x.ttf").Str("Other", "mine.ttf");
        var id = Put(h, Doc(RegHarness.Sid, ValueItem(RegHarness.Fonts, "Same", "x.ttf"), ValueItem(RegHarness.Fonts, "Other", "theirs.ttf")));

        var result = h.Restore.Restore(id);

        Assert.AreEqual(RegistryOverallState.Partial, result.Overall);
        Assert.AreEqual(RegistryItemState.AlreadyPresent, result.Items[0].State);
        Assert.AreEqual(RegistryItemState.Conflict, result.Items[1].State);
        Assert.AreEqual("mine.ttf", h.Machine.Find(RegHarness.Fonts)!.Text("Other"));
        Assert.IsFalse(h.Tree.Ops.Any(op => op.StartsWith("set:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void MissingKeySubtreeIsRecreatedWithChildrenAndValues()
    {
        var h = new RegHarness();
        var node = new RegistryBackupKeyNode(
            [new RegistryBackupValue("", 1, B64(@"C:\gone\x.exe"))],
            [new RegistryBackupSubKey("Sub", new RegistryBackupKeyNode([new RegistryBackupValue("v", 1, B64("1"))], []))]);
        var id = Put(h, Doc(RegHarness.Sid, KeyItem(RegHarness.AppPaths + @"\x.exe", node)));

        var result = h.Restore.Restore(id);

        Assert.AreEqual(RegistryItemState.Restored, result.Items.Single().State);
        var key = h.Machine.Find(RegHarness.AppPaths + @"\x.exe")!;
        Assert.AreEqual(@"C:\gone\x.exe", key.Text(""));
        Assert.AreEqual("1", key.Children["Sub"].Text("v"));
    }

    [TestMethod]
    public void ExistingKeyIsMergedByValueWithConflictKept()
    {
        var h = new RegHarness();
        var existing = h.Machine.Path(RegHarness.AppPaths + @"\x.exe").Str("", "different");
        var node = new RegistryBackupKeyNode(
            [new RegistryBackupValue("", 1, B64("original")), new RegistryBackupValue("Path", 1, B64("p"))], []);
        var id = Put(h, Doc(RegHarness.Sid, KeyItem(RegHarness.AppPaths + @"\x.exe", node)));

        var result = h.Restore.Restore(id);

        Assert.AreEqual(RegistryItemState.Conflict, result.Items.Single().State);
        Assert.AreEqual(RegistryOverallState.Partial, result.Overall);
        Assert.AreEqual("different", existing.Text(""));
        Assert.AreEqual("p", existing.Text("Path"));
    }

    [TestMethod]
    public void ExistingIdenticalKeyIsAlreadyPresent()
    {
        var h = new RegHarness();
        h.Machine.Path(RegHarness.AppPaths + @"\x.exe").Str("", "same");
        var node = new RegistryBackupKeyNode([new RegistryBackupValue("", 1, B64("same"))], []);
        var id = Put(h, Doc(RegHarness.Sid, KeyItem(RegHarness.AppPaths + @"\x.exe", node)));

        var result = h.Restore.Restore(id);

        Assert.AreEqual(RegistryItemState.AlreadyPresent, result.Items.Single().State);
        Assert.AreEqual(RegistryOverallState.Success, result.Overall);
    }

    [TestMethod]
    public void UserHiveBackupFromAnotherSidIsRejectedWithoutWriting()
    {
        var h = new RegHarness();
        const string other = "S-1-5-21-3999999999-2888888888-1777777777-1002";
        var id = Put(h, Doc(other, ValueItem(RegHarness.OpenWith, "a", "x.exe", RegistryCleanHive.CurrentUser, RegistryCleanCategory.FileAssociations)));

        var result = h.Restore.Restore(id);

        Assert.AreEqual(RegistryOverallState.Failed, result.Overall);
        Assert.IsEmpty(result.Items);
        Assert.IsFalse(result.ScanInvalidated);
        Assert.IsEmpty(h.Tree.Ops);
        Assert.IsNull(h.User.Find(RegHarness.OpenWith));
    }

    [TestMethod]
    public void MachineOnlyBackupFromAnotherSidIsStillRestorable()
    {
        var h = new RegHarness();
        var id = Put(h, Doc("S-1-5-21-3999999999-2888888888-1777777777-1002", ValueItem(RegHarness.Fonts, "F", "f.ttf")));

        Assert.AreEqual(RegistryOverallState.Success, h.Restore.Restore(id).Overall);
    }

    [TestMethod]
    public void DisallowedPathAndUnsupportedVersionAreRejected()
    {
        var h = new RegHarness();
        var evil = Put(h, Doc(RegHarness.Sid, ValueItem(@"SOFTWARE\Evil", "x", "y")));
        var future = Put(h, new RegistryBackupDocument(99, DateTime.UtcNow, RegHarness.Sid, []));

        var evilResult = h.Restore.Restore(evil);
        var futureResult = h.Restore.Restore(future);

        Assert.AreEqual(RegistryOverallState.Failed, evilResult.Overall);
        Assert.AreEqual(RegistryOverallState.Failed, futureResult.Overall);
        Assert.IsEmpty(evilResult.Items);
        Assert.IsNotNull(evilResult.Message);
        Assert.IsNull(h.Machine.Find(@"SOFTWARE\Evil"));
        Assert.IsEmpty(h.Tree.Ops);
    }

    [TestMethod]
    public void UnknownBackupIsReportedAsNotFound()
    {
        var h = new RegHarness();

        var result = h.Restore.Restore("20260101T000000000Z-" + Guid.NewGuid().ToString("N"));

        Assert.AreEqual(RegistryOverallState.Failed, result.Overall);
        Assert.IsNotNull(result.Message);
    }

    [TestMethod]
    public async Task RestoreInvalidatesTheScanAndNotifiesOnceForAssociations()
    {
        var h = new RegHarness();
        h.Machine.Path(RegHarness.Fonts).Str("F", "f.ttf");
        var scan = h.Scan([h.ValueItem(RegistryCleanCategory.Fonts, RegHarness.Fonts, "F")]);
        var id = Put(h, Doc(
            RegHarness.Sid,
            ValueItem(@"SOFTWARE\Classes\Applications\a.exe", "x", "1", category: RegistryCleanCategory.FileAssociations),
            ValueItem(@"SOFTWARE\Classes\Applications\b.exe", "x", "2", category: RegistryCleanCategory.FileAssociations)));

        var result = h.Restore.Restore(id);
        var run = await h.Run(scan);

        Assert.AreEqual(RegistryOverallState.Success, result.Overall);
        Assert.AreEqual(1, h.Notifier.Calls);
        Assert.IsTrue(run.Stale);
    }

    [TestMethod]
    public async Task DeleteThenRestoreRoundTripsTheOriginalRegistry()
    {
        var h = new RegHarness();
        h.Machine.Path(RegHarness.AppPaths + @"\gone.exe").Str("", @"C:\gone\gone.exe");
        h.Machine.Find(RegHarness.AppPaths + @"\gone.exe")!.Path("Sub").Str("v", "1");
        h.Machine.Path(RegHarness.Fonts).Str("Gone (TrueType)", "gone.ttf");
        var scan = h.Scan(
        [
            h.KeyItem(RegistryCleanCategory.AppPaths, RegHarness.AppPaths + @"\gone.exe"),
            h.ValueItem(RegistryCleanCategory.Fonts, RegHarness.Fonts, "Gone (TrueType)"),
        ]);
        var deleted = await h.Run(scan);
        Assert.IsNull(h.Machine.Find(RegHarness.AppPaths + @"\gone.exe"));

        var restored = h.Restore.Restore(deleted.BackupId!);

        Assert.AreEqual(RegistryOverallState.Success, restored.Overall);
        Assert.AreEqual(@"C:\gone\gone.exe", h.Machine.Find(RegHarness.AppPaths + @"\gone.exe")!.Text(""));
        Assert.AreEqual("1", h.Machine.Find(RegHarness.AppPaths + @"\gone.exe")!.Children["Sub"].Text("v"));
        Assert.AreEqual("gone.ttf", h.Machine.Find(RegHarness.Fonts)!.Text("Gone (TrueType)"));
        var again = h.Restore.Restore(deleted.BackupId!);
        Assert.IsTrue(again.Items.All(i => i.State == RegistryItemState.AlreadyPresent));
    }

    [TestMethod]
    public async Task UserClassesItemsRoundTripThroughTheClassesHiveWithThePlainUserSid()
    {
        var h = new RegHarness();
        const RegistryCleanCategory fa = RegistryCleanCategory.FileAssociations;
        h.UserClasses.Path(@"Applications\gone.exe\shell\open\command").Str("", @"""C:\gone\gone.exe"" ""%1""");
        h.UserClasses.Path(@".txt\OpenWithProgids").Str("deadprog", "").Str("liveprog", "");
        var scan = h.Scan(
        [
            h.KeyItem(fa, @"Applications\gone.exe", RegistryCleanHive.UserClasses),
            h.ValueItem(fa, @".txt\OpenWithProgids", "deadprog", RegistryCleanHive.UserClasses),
        ]);

        var deleted = await h.Run(scan);

        Assert.AreEqual(RegistryOverallState.Success, deleted.Overall);
        Assert.IsNull(h.UserClasses.Find(@"Applications\gone.exe"));
        Assert.IsFalse(h.UserClasses.Find(@".txt\OpenWithProgids")!.Values.ContainsKey("deadprog"));
        Assert.IsTrue(h.UserClasses.Find(@".txt\OpenWithProgids")!.Values.ContainsKey("liveprog"));
        var backup = h.Store.Documents[deleted.BackupId!];
        Assert.AreEqual(RegHarness.Sid, backup.UserSid);
        Assert.IsTrue(backup.Items.All(item => item.Hive == RegistryCleanHive.UserClasses));

        var restored = h.Restore.Restore(deleted.BackupId!);

        Assert.AreEqual(RegistryOverallState.Success, restored.Overall);
        Assert.AreEqual(@"""C:\gone\gone.exe"" ""%1""", h.UserClasses.Find(@"Applications\gone.exe\shell\open\command")!.Text(""));
        Assert.AreEqual("", h.UserClasses.Find(@".txt\OpenWithProgids")!.Text("deadprog"));
        Assert.IsTrue(h.Factory.ClassesSids.All(sid => sid == RegHarness.Sid));
        Assert.IsNotEmpty(h.Factory.ClassesSids);
    }

    [TestMethod]
    public void UserClassesBackupFromAnotherSidIsRejectedWithoutWriting()
    {
        var h = new RegHarness();
        const string other = "S-1-5-21-3999999999-2888888888-1777777777-1002";
        var id = Put(h, Doc(other, ValueItem(@".txt\OpenWithProgids", "p", "", RegistryCleanHive.UserClasses, RegistryCleanCategory.FileAssociations)));

        var result = h.Restore.Restore(id);

        Assert.AreEqual(RegistryOverallState.Failed, result.Overall);
        Assert.IsEmpty(result.Items);
        Assert.IsEmpty(h.Tree.Ops);
        Assert.IsEmpty(h.Factory.ClassesSids);
    }

    [TestMethod]
    public async Task RestoredOpenWithSlotIsAppendedToTheRewrittenMruListWithoutConflict()
    {
        var h = new RegHarness();
        h.User.Path(RegHarness.OpenWith).Str("a", "gone-a.exe").Str("b", "kept.exe").Str("c", "kept2.exe").Str("MRUList", "cab");
        var a = h.ValueItem(RegistryCleanCategory.FileAssociations, RegHarness.OpenWith, "a", RegistryCleanHive.CurrentUser);
        var mru = h.ValueItem(RegistryCleanCategory.FileAssociations, RegHarness.OpenWith, "MRUList", RegistryCleanHive.CurrentUser);
        var scan = h.Scan([a], [RegHarness.Companion(a, RegistryCompanionKind.OpenWithMruList, mru, "a")]);
        var deleted = await h.Run(scan);
        var container = h.User.Find(RegHarness.OpenWith)!;
        Assert.AreEqual("cb", container.Text("MRUList"));

        // 削除後に利用者が別のアプリを選び、MRUList の並びが変わった。既存の並びは保つ。
        container.Str("d", "new.exe").Str("MRUList", "dbc");
        var restored = h.Restore.Restore(deleted.BackupId!);

        Assert.AreEqual(RegistryOverallState.Success, restored.Overall);
        Assert.AreEqual("gone-a.exe", container.Text("a"));
        Assert.AreEqual("dbca", container.Text("MRUList"));
        Assert.AreEqual(RegistryItemState.Restored, restored.Items.Single(i => i.Item.ValueName == "MRUList").State);

        var again = h.Restore.Restore(deleted.BackupId!);
        Assert.IsTrue(again.Items.All(i => i.State == RegistryItemState.AlreadyPresent));
        Assert.AreEqual("dbca", container.Text("MRUList"));
    }

    [TestMethod]
    public async Task MruListIsRecreatedWhenEverySlotWasDeleted()
    {
        var h = new RegHarness();
        h.User.Path(RegHarness.OpenWith).Str("a", "gone-a.exe").Str("b", "gone-b.exe").Str("MRUList", "ba");
        var a = h.ValueItem(RegistryCleanCategory.FileAssociations, RegHarness.OpenWith, "a", RegistryCleanHive.CurrentUser);
        var b = h.ValueItem(RegistryCleanCategory.FileAssociations, RegHarness.OpenWith, "b", RegistryCleanHive.CurrentUser);
        var mru = h.ValueItem(RegistryCleanCategory.FileAssociations, RegHarness.OpenWith, "MRUList", RegistryCleanHive.CurrentUser);
        var scan = h.Scan(
            [a, b],
            [
                RegHarness.Companion(a, RegistryCompanionKind.OpenWithMruList, mru, "a"),
                RegHarness.Companion(b, RegistryCompanionKind.OpenWithMruList, mru, "b"),
            ]);
        var deleted = await h.Run(scan);
        Assert.IsFalse(h.User.Find(RegHarness.OpenWith)!.Values.ContainsKey("MRUList"));

        var restored = h.Restore.Restore(deleted.BackupId!);

        Assert.AreEqual(RegistryOverallState.Success, restored.Overall);
        Assert.AreEqual("ba", h.User.Find(RegHarness.OpenWith)!.Text("MRUList"));
    }

    [TestMethod]
    public async Task ConflictingSlotIsNotAddedToTheMruList()
    {
        var h = new RegHarness();
        h.User.Path(RegHarness.OpenWith).Str("a", "gone-a.exe").Str("b", "kept.exe").Str("MRUList", "ab");
        var a = h.ValueItem(RegistryCleanCategory.FileAssociations, RegHarness.OpenWith, "a", RegistryCleanHive.CurrentUser);
        var mru = h.ValueItem(RegistryCleanCategory.FileAssociations, RegHarness.OpenWith, "MRUList", RegistryCleanHive.CurrentUser);
        var scan = h.Scan([a], [RegHarness.Companion(a, RegistryCompanionKind.OpenWithMruList, mru, "a")]);
        var deleted = await h.Run(scan);

        // 削除後、同じ文字のスロットに別のアプリが入った (MRUList にはまだ載っていない)。
        var container = h.User.Find(RegHarness.OpenWith)!;
        container.Str("a", "other.exe");
        var restored = h.Restore.Restore(deleted.BackupId!);

        Assert.AreEqual(RegistryOverallState.Partial, restored.Overall);
        Assert.AreEqual(RegistryItemState.Conflict, restored.Items.Single(i => i.Item.ValueName == "a").State);
        Assert.AreEqual("other.exe", container.Text("a"));
        Assert.AreEqual("b", container.Text("MRUList"));
    }

    [TestMethod]
    public void ListAndDeleteGoThroughTheStore()
    {
        var h = new RegHarness();
        var id = Put(h, Doc(RegHarness.Sid, ValueItem(RegHarness.Fonts, "F", "f.ttf")));

        Assert.AreEqual(id, h.Restore.ListBackups().Single());
        h.Restore.DeleteBackup(id);
        Assert.IsEmpty(h.Restore.ListBackups());
    }
}
