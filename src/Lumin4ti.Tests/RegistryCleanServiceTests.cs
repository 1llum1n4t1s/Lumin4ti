using System.Text;
using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;
using Lumin4ti.Core.Services.Windows.Cleanup.Slim;

namespace Lumin4ti.Tests;

/// <summary>書き込みを記録する偽のキー木の共有状態。</summary>
internal sealed class MemTree
{
    public List<string> Ops { get; } = [];

    /// <summary>削除・書き込みのたびに呼ぶ (取消の差し込みに使う)。</summary>
    public Action? AfterWrite { get; set; }
}

/// <summary>メモリ上の書き込める偽のキー。実レジストリには触れない。</summary>
internal sealed class MemKey : IRegistryCleanerKey
{
    private readonly MemTree _tree;
    private readonly MemKey? _parent;
    private readonly string _name;

    public MemKey(MemTree tree, string kernel, MemKey? parent = null, string name = "")
    {
        _tree = tree;
        Kernel = kernel;
        _parent = parent;
        _name = name;
    }

    public string Kernel { get; }

    public Dictionary<string, RegistryRawValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, MemKey> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Links { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Denied { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Owner { get; set; } = "S-1-5-18";

    public DateTime LastWrite { get; set; } = new(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    public MemKey Path(string path)
    {
        var current = this;
        foreach (var part in path.Split('\\'))
        {
            if (!current.Children.TryGetValue(part, out var next))
            {
                next = new MemKey(_tree, current.Kernel + "\\" + part, current, part);
                current.Children[part] = next;
            }

            current = next;
        }

        return current;
    }

    public MemKey? Find(string path)
    {
        var current = this;
        foreach (var part in path.Split('\\'))
        {
            if (!current.Children.TryGetValue(part, out current))
            {
                return null;
            }
        }

        return current;
    }

    public MemKey Str(string name, string value, uint type = 1)
    {
        Values[name] = new RegistryRawValue(type, Encoding.Unicode.GetBytes(value + "\0"));
        return this;
    }

    public string? Text(string name) =>
        Values.TryGetValue(name, out var raw) ? Encoding.Unicode.GetString(raw.Data).TrimEnd('\0') : null;

    public IRegistryCleanerKey? OpenChild(string name, RegistryCleanAccess access, RegistryCleanView view)
    {
        if (Denied.Contains(name))
        {
            throw new UnauthorizedAccessException(name);
        }

        return Links.Contains(name) ? throw new RegistryCleanLinkException(name) : Children.GetValueOrDefault(name);
    }

    public IRegistryCleanerKey CreateChild(string name, RegistryCleanView view)
    {
        _tree.Ops.Add("createkey:" + Kernel + "\\" + name);
        return Children.TryGetValue(name, out var existing) ? existing : Path(name);
    }

    public RegistryRawValue? GetValue(string name) => Values.GetValueOrDefault(name);

    public void SetValue(string name, RegistryRawValue value)
    {
        _tree.Ops.Add("set:" + Kernel + "|" + name);
        Values[name] = value;
        _tree.AfterWrite?.Invoke();
    }

    public IReadOnlyList<string> GetValueNames() => [.. Values.Keys];

    public IReadOnlyList<string> GetSubKeyNames() => [.. Children.Keys];

    public void DeleteValue(string name)
    {
        _tree.Ops.Add("deleteval:" + Kernel + "|" + name);
        Values.Remove(name);
        _tree.AfterWrite?.Invoke();
    }

    public void DeleteSelf()
    {
        _tree.Ops.Add("deletekey:" + Kernel);
        _parent?.Children.Remove(_name);
        _tree.AfterWrite?.Invoke();
    }

    public RegistryKeyInfo QueryInfo() => new(LastWrite, Children.Count, Values.Count);

    public string KernelName() => Kernel;

    public string OwnerSid() => Owner;

    public void Dispose()
    {
    }
}

internal sealed class MemFactory(MemKey machine, MemKey user, MemKey userClasses) : IRegistryCleanerRootFactory
{
    /// <summary><see cref="OpenUserClasses"/> に渡された SID (<c>_Classes</c> を含まない利用者の SID のはず)。</summary>
    public List<string> ClassesSids { get; } = [];

    public IRegistryCleanerKey OpenLocalMachine(RegistryCleanView view) => machine;

    public IRegistryCleanerKey OpenUserHive(string sid, RegistryCleanView view) => user;

    public IRegistryCleanerKey OpenUserClasses(string sid, RegistryCleanView view)
    {
        ClassesSids.Add(sid);
        return userClasses;
    }
}

internal sealed class MemStore(MemTree tree) : IRegistryBackupStore
{
    public Dictionary<string, RegistryBackupDocument> Documents { get; } = [];

    public Dictionary<string, RegistryBackupResultDocument> Results { get; } = [];

    public bool FailSave { get; set; }

    public bool FailResult { get; set; }

    public string Save(RegistryBackupDocument document)
    {
        if (FailSave)
        {
            throw new RegistryBackupException("disk full");
        }

        RegistryBackupValidator.ThrowIfInvalid(document);
        var id = $"20260101T000000000Z-{Guid.NewGuid():N}";
        Documents[id] = document;
        tree.Ops.Add("save");
        return id;
    }

    public IReadOnlyList<string> List() => [.. Documents.Keys];

    public RegistryBackupDocument? Load(string id)
    {
        if (!Documents.TryGetValue(id, out var document))
        {
            return null;
        }

        RegistryBackupValidator.ThrowIfInvalid(document);
        return document;
    }

    public void Delete(string id)
    {
        Documents.Remove(id);
        Results.Remove(id);
    }

    public void WriteResult(string id, RegistryBackupResultDocument result)
    {
        if (FailResult)
        {
            throw new RegistryBackupException("result write failed");
        }

        Results[id] = result;
    }

    public RegistryBackupResultDocument? ReadResult(string id) => Results.GetValueOrDefault(id);
}

internal sealed class FakeNotifier : IRegistryShellNotifier
{
    public int Calls { get; private set; }

    public void AssociationsChanged() => Calls++;
}

internal sealed class FakeRestorePoints : IRegistryCleanRestorePoints
{
    public RestorePointResult Result { get; set; } =
        new(RestorePointOutcome.Created, "ok", new RestorePointInfo(1, DateTimeOffset.UtcNow, "x"));

    public int Creates { get; private set; }

    public Task<RestorePointResult> CreateAsync(string description, CancellationToken ct)
    {
        Creates++;
        return Task.FromResult(Result);
    }

    public Task<RestorePointProtectionResult> EnableProtectionAsync(CancellationToken ct) =>
        Task.FromResult(new RestorePointProtectionResult(false, "no"));
}

/// <summary>削除・復元テストの共通の部品 (偽のキー木・保存先・通知・復元ポイント)。</summary>
internal sealed class RegHarness
{
    public const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    public const string Cv = @"SOFTWARE\Microsoft\Windows\CurrentVersion";
    public const string UserCv = @"Software\Microsoft\Windows\CurrentVersion";
    public const string AppPaths = Cv + @"\App Paths";
    public const string RunKey = Cv + @"\Run";
    public const string Approved = Cv + @"\Explorer\StartupApproved\Run";
    public const string Fonts = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";
    public const string OpenWith = UserCv + @"\Explorer\FileExts\.txt\OpenWithList";

    public RegHarness()
    {
        Machine = new MemKey(Tree, @"\REGISTRY\MACHINE");
        User = new MemKey(Tree, @"\REGISTRY\USER\" + Sid);
        UserClasses = new MemKey(Tree, @"\REGISTRY\USER\" + Sid + "_Classes");

        // 実機と同じく、HKCU の Software\Classes は <SID>_Classes へのリンク (辿らない)。
        User.Path("Software").Links.Add("Classes");
        Store = new MemStore(Tree);
        Generation = new RegistryScanGeneration();
        Factory = new MemFactory(Machine, User, UserClasses);
        Service = new RegistryCleanService(Factory, Store, Points, Notifier, Generation, () => Sid);
        Restore = new RegistryRestoreService(Factory, Store, Notifier, Generation, () => Sid);
    }

    public MemTree Tree { get; } = new();

    public MemKey Machine { get; }

    public MemKey User { get; }

    public MemKey UserClasses { get; }

    public MemFactory Factory { get; }

    public MemStore Store { get; }

    public FakeRestorePoints Points { get; } = new();

    public FakeNotifier Notifier { get; } = new();

    public RegistryScanGeneration Generation { get; }

    public RegistryCleanService Service { get; }

    public RegistryRestoreService Restore { get; }

    public MemKey Root(RegistryCleanHive hive) => hive switch
    {
        RegistryCleanHive.LocalMachine => Machine,
        RegistryCleanHive.UserClasses => UserClasses,
        _ => User,
    };

    public RegistryScanItem KeyItem(RegistryCleanCategory category, string path, RegistryCleanHive hive = RegistryCleanHive.LocalMachine)
    {
        var key = Root(hive).Find(path)!;
        return new(category, hive, RegistryCleanView.Registry64, path, null, null,
            RegistryScanHashing.OfSubtree(key, RegistryCleanView.Registry64)!, key.LastWrite, key.Kernel, "r");
    }

    public RegistryScanItem ValueItem(RegistryCleanCategory category, string path, string name, RegistryCleanHive hive = RegistryCleanHive.LocalMachine)
    {
        var key = Root(hive).Find(path)!;
        var raw = key.Values[name];
        return new(category, hive, RegistryCleanView.Registry64, path, name, raw.Type,
            RegistryScanHashing.OfValue(raw), key.LastWrite, key.Kernel, "r");
    }

    public static RegistryScanCompanion Companion(RegistryScanItem owner, RegistryCompanionKind kind, RegistryScanItem related, params string[] slots) =>
        new(owner, kind, related, slots);

    /// <summary>走査結果を作り、サービスに現在の世代として登録する。</summary>
    public RegistryScanResult Scan(IReadOnlyList<RegistryScanItem> items, IReadOnlyList<RegistryScanCompanion>? companions = null, bool register = true)
    {
        var scan = new RegistryScanResult(
            Guid.NewGuid().ToString("N"), DateTime.UtcNow, items, [], companions ?? [],
            new RegistryUserScopeStatus(true, Sid, InteractiveScopeReason.None, null));
        if (register)
        {
            Service.Register(scan);
        }

        return scan;
    }

    public Task<RegistryCleanRunResult> Run(
        RegistryScanResult scan, IReadOnlyCollection<RegistryScanItem>? selected = null, bool restorePoint = false,
        CancellationToken ct = default, Func<RestorePointResult, CancellationToken, Task<RestorePointDecision>>? onUnavailable = null) =>
        Service.ExecuteAsync(new RegistryCleanRequest(scan, selected ?? scan.Items, restorePoint, onUnavailable), ct);
}

[TestClass]
public sealed class RegistryCleanServiceTests
{
    private static RegHarness WithAppPathAndFont(out RegistryScanItem appKey, out RegistryScanItem fontValue)
    {
        var h = new RegHarness();
        h.Machine.Path(RegHarness.AppPaths + @"\gone.exe").Str("", @"C:\gone\gone.exe");
        h.Machine.Path(RegHarness.Fonts).Str("Gone (TrueType)", "gone.ttf").Str("Keep (TrueType)", "keep.ttf");
        appKey = h.KeyItem(RegistryCleanCategory.AppPaths, RegHarness.AppPaths + @"\gone.exe");
        fontValue = h.ValueItem(RegistryCleanCategory.Fonts, RegHarness.Fonts, "Gone (TrueType)");
        return h;
    }

    [TestMethod]
    public async Task DeletesOnlyAfterBackupIsSavedAndWritesResultFile()
    {
        var h = WithAppPathAndFont(out var appKey, out var fontValue);
        var scan = h.Scan([appKey, fontValue]);

        var result = await h.Run(scan);

        Assert.AreEqual(RegistryOverallState.Success, result.Overall);
        Assert.IsTrue(result.Items.All(i => i.State == RegistryItemState.Deleted));
        Assert.IsNull(h.Machine.Find(RegHarness.AppPaths + @"\gone.exe"));
        Assert.IsFalse(h.Machine.Find(RegHarness.Fonts)!.Values.ContainsKey("Gone (TrueType)"));
        Assert.IsTrue(h.Machine.Find(RegHarness.Fonts)!.Values.ContainsKey("Keep (TrueType)"));
        Assert.AreEqual("save", h.Tree.Ops[0]);
        Assert.IsTrue(h.Tree.Ops.Skip(1).All(op => op.StartsWith("delete", StringComparison.Ordinal)));
        Assert.IsNotNull(result.BackupId);
        Assert.IsTrue(result.ResultFileWritten);
        var backup = h.Store.Documents[result.BackupId];
        Assert.HasCount(2, backup.Items);
        Assert.IsNotNull(backup.Items.Single(i => i.ValueName is null).Subtree);
        Assert.AreEqual(RegistryOverallState.Success, h.Store.Results[result.BackupId].Overall);
    }

    [TestMethod]
    public async Task BackupFailureDeletesNothingAndFailsEverything()
    {
        var h = WithAppPathAndFont(out var appKey, out var fontValue);
        h.Store.FailSave = true;
        var scan = h.Scan([appKey, fontValue]);

        var result = await h.Run(scan);

        Assert.AreEqual(RegistryOverallState.Failed, result.Overall);
        Assert.IsTrue(result.Items.All(i => i.State == RegistryItemState.Failed));
        Assert.IsEmpty(h.Tree.Ops);
        Assert.IsNull(result.BackupId);
        Assert.IsFalse(result.ResultFileWritten);
        Assert.IsNotNull(h.Machine.Find(RegHarness.AppPaths + @"\gone.exe"));
    }

    [TestMethod]
    public async Task ValueChangedSinceScanIsRetainedAndCountsAsSuccess()
    {
        var h = WithAppPathAndFont(out var appKey, out var fontValue);
        var scan = h.Scan([appKey, fontValue]);
        h.Machine.Find(RegHarness.Fonts)!.Str("Gone (TrueType)", "now-used.ttf");

        var result = await h.Run(scan);

        Assert.AreEqual(RegistryOverallState.Success, result.Overall);
        Assert.AreEqual(RegistryItemState.Retained, result.Items.Single(i => i.Item == fontValue).State);
        Assert.AreEqual(RegistryItemState.Deleted, result.Items.Single(i => i.Item == appKey).State);
        Assert.AreEqual("now-used.ttf", h.Machine.Find(RegHarness.Fonts)!.Text("Gone (TrueType)"));
        Assert.HasCount(1, h.Store.Documents.Single().Value.Items);
    }

    [TestMethod]
    public async Task KeySubtreeOrLastWriteChangedIsRetained()
    {
        var h = WithAppPathAndFont(out var appKey, out _);
        var scan = h.Scan([appKey]);
        h.Machine.Find(RegHarness.AppPaths + @"\gone.exe")!.LastWrite = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await h.Run(scan);

        Assert.AreEqual(RegistryItemState.Retained, result.Items.Single().State);
        Assert.IsEmpty(h.Tree.Ops);
        Assert.IsNull(result.BackupId);
    }

    [TestMethod]
    public async Task StartupValueAndApprovedValueAreDeletedAsOnePair()
    {
        var h = new RegHarness();
        h.Machine.Path(RegHarness.RunKey).Str("Gone", @"C:\gone\app.exe");
        h.Machine.Path(RegHarness.Approved).Str("Gone", "02");
        var owner = h.ValueItem(RegistryCleanCategory.Startup, RegHarness.RunKey, "Gone");
        var related = h.ValueItem(RegistryCleanCategory.Startup, RegHarness.Approved, "Gone");
        var scan = h.Scan([owner], [RegHarness.Companion(owner, RegistryCompanionKind.StartupApprovedValue, related)]);

        var result = await h.Run(scan);

        Assert.AreEqual(RegistryOverallState.Success, result.Overall);
        Assert.IsFalse(h.Machine.Find(RegHarness.RunKey)!.Values.ContainsKey("Gone"));
        Assert.IsFalse(h.Machine.Find(RegHarness.Approved)!.Values.ContainsKey("Gone"));
        Assert.HasCount(2, h.Store.Documents[result.BackupId!].Items);
    }

    [TestMethod]
    public async Task ChangedApprovedValueRetainsTheWholePair()
    {
        var h = new RegHarness();
        h.Machine.Path(RegHarness.RunKey).Str("Gone", @"C:\gone\app.exe");
        h.Machine.Path(RegHarness.Approved).Str("Gone", "02");
        var owner = h.ValueItem(RegistryCleanCategory.Startup, RegHarness.RunKey, "Gone");
        var related = h.ValueItem(RegistryCleanCategory.Startup, RegHarness.Approved, "Gone");
        var scan = h.Scan([owner], [RegHarness.Companion(owner, RegistryCompanionKind.StartupApprovedValue, related)]);
        h.Machine.Find(RegHarness.Approved)!.Str("Gone", "03");

        var result = await h.Run(scan);

        Assert.AreEqual(RegistryItemState.Retained, result.Items.Single().State);
        Assert.IsTrue(h.Machine.Find(RegHarness.RunKey)!.Values.ContainsKey("Gone"));
        Assert.IsTrue(h.Machine.Find(RegHarness.Approved)!.Values.ContainsKey("Gone"));
        Assert.IsEmpty(h.Tree.Ops);
    }

    [TestMethod]
    public async Task CompanionThatCannotBeBackedUpSkipsItsOwnerAndDeletesNothingOfIt()
    {
        var h = WithAppPathAndFont(out var appKey, out _);
        h.Machine.Path(RegHarness.RunKey).Str("Gone", @"C:\gone\app.exe");
        // 上限を超える付随値は取り込みで例外になる (アクセス拒否・I/O と同じ、バックアップの失敗)。
        h.Machine.Path(RegHarness.Approved).Values["Gone"] = new RegistryRawValue(3, new byte[RegistryBackupLimits.MaxValueBytes + 1]);
        var owner = h.ValueItem(RegistryCleanCategory.Startup, RegHarness.RunKey, "Gone");
        var related = h.ValueItem(RegistryCleanCategory.Startup, RegHarness.Approved, "Gone");
        var scan = h.Scan([owner, appKey], [RegHarness.Companion(owner, RegistryCompanionKind.StartupApprovedValue, related)]);

        var result = await h.Run(scan);

        Assert.AreEqual(RegistryOverallState.Partial, result.Overall);
        var skipped = result.Items.Single(i => i.Item == owner);
        Assert.AreEqual(RegistryItemState.Skipped, skipped.State);
        Assert.AreEqual("Cleanup.Msg." + RegistryCleanReasons.BackupItemFailed, CleanupMessage.KeyOf(skipped.Reason!));
        Assert.AreEqual(RegistryItemState.Deleted, result.Items.Single(i => i.Item == appKey).State);
        Assert.IsTrue(h.Machine.Find(RegHarness.RunKey)!.Values.ContainsKey("Gone"));
        Assert.IsTrue(h.Machine.Find(RegHarness.Approved)!.Values.ContainsKey("Gone"));
        var backup = h.Store.Documents[result.BackupId!];
        Assert.IsFalse(backup.Items.Any(i => i.ValueName == "Gone"));
    }

    [TestMethod]
    public async Task UserClassesItemOutsideTheAssociationRootsIsNotDeleted()
    {
        var h = new RegHarness();
        h.UserClasses.Path(@"CLSID\{00000000-0000-0000-0000-000000000001}").Str("", "x");
        var item = h.KeyItem(RegistryCleanCategory.FileAssociations, @"CLSID\{00000000-0000-0000-0000-000000000001}", RegistryCleanHive.UserClasses);

        var result = await h.Run(h.Scan([item]));

        Assert.AreEqual(RegistryItemState.Skipped, result.Items.Single().State);
        Assert.IsNotNull(h.UserClasses.Find(@"CLSID\{00000000-0000-0000-0000-000000000001}"));
        Assert.IsEmpty(h.Tree.Ops);
    }

    private static (RegHarness H, RegistryScanResult Scan, RegistryScanItem A, RegistryScanItem B) OpenWithSetup(string mruList)
    {
        var h = new RegHarness();
        h.User.Path(RegHarness.OpenWith).Str("a", "gone-a.exe").Str("b", "gone-b.exe").Str("c", "kept.exe").Str("MRUList", mruList);
        var a = h.ValueItem(RegistryCleanCategory.FileAssociations, RegHarness.OpenWith, "a", RegistryCleanHive.CurrentUser);
        var b = h.ValueItem(RegistryCleanCategory.FileAssociations, RegHarness.OpenWith, "b", RegistryCleanHive.CurrentUser);
        var mru = h.ValueItem(RegistryCleanCategory.FileAssociations, RegHarness.OpenWith, "MRUList", RegistryCleanHive.CurrentUser);
        var scan = h.Scan(
            [a, b],
            [
                RegHarness.Companion(a, RegistryCompanionKind.OpenWithMruList, mru, "a"),
                RegHarness.Companion(b, RegistryCompanionKind.OpenWithMruList, mru, "b"),
            ]);
        return (h, scan, a, b);
    }

    [TestMethod]
    public async Task MruListIsRewrittenWithoutDeletedSlotsAndNotifiesOnce()
    {
        var (h, scan, a, _) = OpenWithSetup("abc");

        var result = await h.Run(scan, [a]);

        Assert.AreEqual(RegistryOverallState.Success, result.Overall);
        var container = h.User.Find(RegHarness.OpenWith)!;
        Assert.AreEqual("bc", container.Text("MRUList"));
        Assert.IsFalse(container.Values.ContainsKey("a"));
        Assert.AreEqual(1, h.Notifier.Calls);
    }

    [TestMethod]
    public async Task SeveralSlotsOfOneMruListAreMergedIntoOneWriteAndEmptyListIsDeleted()
    {
        var (h, scan, _, _) = OpenWithSetup("ab");

        var result = await h.Run(scan);

        Assert.AreEqual(RegistryOverallState.Success, result.Overall);
        Assert.IsFalse(h.User.Find(RegHarness.OpenWith)!.Values.ContainsKey("MRUList"));
        Assert.AreEqual(1, h.Notifier.Calls);
        Assert.AreEqual(1, h.Store.Documents.Single().Value.Items.Count(i => i.ValueName == "MRUList"));
    }

    [TestMethod]
    public async Task SeveralSlotsOfOneMruListLeaveTheRemainingSlot()
    {
        var (h, scan, _, _) = OpenWithSetup("abc");

        await h.Run(scan);

        Assert.AreEqual("c", h.User.Find(RegHarness.OpenWith)!.Text("MRUList"));
        Assert.AreEqual(1, h.Tree.Ops.Count(op => op.StartsWith("set:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task SameKernelNameIsProcessedOnceAndSelectedKeyChildrenAreSkipped()
    {
        var h = WithAppPathAndFont(out _, out _);
        var child = h.Machine.Find(RegHarness.AppPaths + @"\gone.exe")!;
        child.Str("Path", @"C:\gone");
        var appKey = h.KeyItem(RegistryCleanCategory.AppPaths, RegHarness.AppPaths + @"\gone.exe");
        var inner = h.ValueItem(RegistryCleanCategory.AppPaths, RegHarness.AppPaths + @"\gone.exe", "Path");
        var twin = appKey with { View = RegistryCleanView.Registry32 };
        var scan = h.Scan([inner, appKey, twin]);

        var result = await h.Run(scan);

        Assert.AreEqual(RegistryOverallState.Success, result.Overall);
        Assert.IsTrue(result.Items.All(i => i.State == RegistryItemState.Deleted));
        Assert.AreEqual(1, h.Tree.Ops.Count(op => op.StartsWith("delete", StringComparison.Ordinal)));
        Assert.HasCount(1, h.Store.Documents.Single().Value.Items);
    }

    [TestMethod]
    public async Task AccessDeniedLinkAndTrustedInstallerAreSkippedAndOverallIsPartial()
    {
        var h = WithAppPathAndFont(out var appKey, out var fontValue);
        h.Machine.Path(RegHarness.AppPaths + @"\ti.exe").Str("", @"C:\gone\ti.exe");
        h.Machine.Find(RegHarness.AppPaths + @"\ti.exe")!.Owner = RegistryCategoryDetectors.TrustedInstallerSid;
        h.Machine.Path(RegHarness.AppPaths + @"\denied.exe").Str("", @"C:\gone\d.exe");
        h.Machine.Path(RegHarness.AppPaths + @"\link.exe").Str("", @"C:\gone\l.exe");
        var ti = h.KeyItem(RegistryCleanCategory.AppPaths, RegHarness.AppPaths + @"\ti.exe");
        var denied = h.KeyItem(RegistryCleanCategory.AppPaths, RegHarness.AppPaths + @"\denied.exe");
        var link = h.KeyItem(RegistryCleanCategory.AppPaths, RegHarness.AppPaths + @"\link.exe");
        var scan = h.Scan([appKey, fontValue, ti, denied, link]);
        var appPaths = h.Machine.Find(RegHarness.AppPaths)!;
        appPaths.Denied.Add("denied.exe");
        appPaths.Links.Add("link.exe");

        var result = await h.Run(scan);

        Assert.AreEqual(RegistryOverallState.Partial, result.Overall);
        Assert.AreEqual(RegistryItemState.Skipped, result.Items.Single(i => i.Item == ti).State);
        Assert.AreEqual(RegistryItemState.Skipped, result.Items.Single(i => i.Item == denied).State);
        Assert.AreEqual(RegistryItemState.Skipped, result.Items.Single(i => i.Item == link).State);
        Assert.AreEqual(RegistryItemState.Deleted, result.Items.Single(i => i.Item == appKey).State);
        Assert.AreEqual(RegistryItemState.Deleted, result.Items.Single(i => i.Item == fontValue).State);
        Assert.IsNotNull(h.Machine.Find(RegHarness.AppPaths + @"\ti.exe"));
    }

    [TestMethod]
    public async Task StaleOrAlreadyUsedScanIsRejectedWithoutTouchingAnything()
    {
        var h = WithAppPathAndFont(out var appKey, out _);
        var unregistered = h.Scan([appKey], register: false);
        var rejected = await h.Run(unregistered);
        Assert.IsTrue(rejected.Stale);
        Assert.AreEqual(RegistryOverallState.Failed, rejected.Overall);

        var scan = h.Scan([appKey]);
        var first = await h.Run(scan);
        var second = await h.Run(scan);

        Assert.IsFalse(first.Stale);
        Assert.IsTrue(second.Stale);
        Assert.AreEqual(1, h.Tree.Ops.Count(op => op.StartsWith("deletekey", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task NewerScanInvalidatesTheOlderOne()
    {
        var h = WithAppPathAndFont(out var appKey, out _);
        var older = h.Scan([appKey]);
        h.Scan([appKey]);

        var result = await h.Run(older);

        Assert.IsTrue(result.Stale);
        Assert.IsEmpty(h.Tree.Ops);
    }

    [TestMethod]
    public async Task ConcurrentRunsOfTheSameScanAllowExactlyOne()
    {
        var h = WithAppPathAndFont(out var appKey, out var fontValue);
        var scan = h.Scan([appKey, fontValue]);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => h.Run(scan))));

        Assert.AreEqual(1, results.Count(r => !r.Stale));
    }

    [TestMethod]
    public async Task CancelBeforeStartDeletesNothing()
    {
        var h = WithAppPathAndFont(out var appKey, out _);
        var scan = h.Scan([appKey]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await h.Run(scan, ct: cts.Token);

        Assert.AreEqual(RegistryOverallState.Canceled, result.Overall);
        Assert.IsEmpty(h.Tree.Ops);
    }

    [TestMethod]
    public async Task CancelDuringDeletionStopsButStillWritesTheResultFile()
    {
        var h = WithAppPathAndFont(out var appKey, out var fontValue);
        var scan = h.Scan([appKey, fontValue]);
        using var cts = new CancellationTokenSource();
        h.Tree.AfterWrite = cts.Cancel;

        var result = await h.Run(scan, ct: cts.Token);

        Assert.AreEqual(RegistryOverallState.Canceled, result.Overall);
        Assert.AreEqual(RegistryItemState.Deleted, result.Items[0].State);
        Assert.AreEqual(RegistryItemState.Skipped, result.Items[1].State);
        Assert.IsTrue(result.ResultFileWritten);
        Assert.AreEqual(RegistryOverallState.Canceled, h.Store.Results[result.BackupId!].Overall);
        Assert.IsTrue(h.Machine.Find(RegHarness.Fonts)!.Values.ContainsKey("Gone (TrueType)"));
    }

    [TestMethod]
    public async Task AssociationCategoryNotifiesOnceAndOtherCategoriesDoNot()
    {
        var h = WithAppPathAndFont(out var appKey, out var fontValue);
        await h.Run(h.Scan([appKey, fontValue]));
        Assert.AreEqual(0, h.Notifier.Calls);

        h.Machine.Path(@"SOFTWARE\Classes\Applications\gone.exe").Str("", "x");
        h.Machine.Path(@"SOFTWARE\Classes\Applications\gone2.exe").Str("", "x");
        var one = h.KeyItem(RegistryCleanCategory.FileAssociations, @"SOFTWARE\Classes\Applications\gone.exe");
        var two = h.KeyItem(RegistryCleanCategory.FileAssociations, @"SOFTWARE\Classes\Applications\gone2.exe");
        await h.Run(h.Scan([one, two]));
        Assert.AreEqual(1, h.Notifier.Calls);
    }

    [TestMethod]
    public async Task UnavailableRestorePointStopsUnlessTheCallerContinues()
    {
        var h = WithAppPathAndFont(out var appKey, out _);
        h.Points.Result = new RestorePointResult(RestorePointOutcome.Failed, "boom", null);

        var stopped = await h.Run(h.Scan([appKey]), restorePoint: true);
        Assert.AreEqual(RegistryOverallState.Failed, stopped.Overall);
        Assert.IsEmpty(h.Tree.Ops);
        Assert.IsNotNull(h.Machine.Find(RegHarness.AppPaths + @"\gone.exe"));

        var proceeded = await h.Run(
            h.Scan([appKey]), restorePoint: true,
            onUnavailable: (_, _) => Task.FromResult(RestorePointDecision.ContinueWithout));
        Assert.AreEqual(RegistryOverallState.Success, proceeded.Overall);
        Assert.AreEqual(2, h.Points.Creates);
        Assert.IsNull(h.Machine.Find(RegHarness.AppPaths + @"\gone.exe"));
    }

    [TestMethod]
    public async Task RestorePointIsCreatedOnlyWhenRequestedAndRecordedInTheResult()
    {
        var h = WithAppPathAndFont(out var appKey, out var fontValue);

        var without = await h.Run(h.Scan([appKey]));
        Assert.AreEqual(0, h.Points.Creates);
        Assert.IsNull(without.RestorePoint);

        var with = await h.Run(h.Scan([fontValue]), restorePoint: true);
        Assert.AreEqual(1, h.Points.Creates);
        Assert.AreEqual(RestorePointOutcome.Created, with.RestorePoint!.Outcome);
    }

    [TestMethod]
    public async Task ItemNotInScanOrOutsideAllowedPathsIsSkipped()
    {
        var h = WithAppPathAndFont(out var appKey, out _);
        h.Machine.Path(@"SOFTWARE\Evil\x").Str("", "x");
        var outside = new RegistryScanItem(
            RegistryCleanCategory.AppPaths, RegistryCleanHive.LocalMachine, RegistryCleanView.Registry64, @"SOFTWARE\Evil\x", null, null, "h",
            DateTime.UtcNow, @"\REGISTRY\MACHINE\SOFTWARE\Evil\x", "r");
        var stranger = appKey with { KeyPath = RegHarness.AppPaths + @"\other.exe" };
        var scan = h.Scan([appKey, outside]);

        var result = await h.Run(scan, [appKey, outside, stranger]);

        Assert.AreEqual(RegistryOverallState.Partial, result.Overall);
        Assert.AreEqual(RegistryItemState.Skipped, result.Items.Single(i => i.Item == outside).State);
        Assert.AreEqual(RegistryItemState.Skipped, result.Items.Single(i => i.Item == stranger).State);
        Assert.IsNotNull(h.Machine.Find(@"SOFTWARE\Evil\x"));
    }

    [TestMethod]
    public async Task ResultFileFailureIsReportedButDeletionStands()
    {
        var h = WithAppPathAndFont(out var appKey, out _);
        h.Store.FailResult = true;

        var result = await h.Run(h.Scan([appKey]));

        Assert.AreEqual(RegistryOverallState.Success, result.Overall);
        Assert.IsFalse(result.ResultFileWritten);
        Assert.IsNotNull(result.Message);
    }
}
