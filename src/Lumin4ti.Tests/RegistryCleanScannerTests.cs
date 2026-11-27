using System.Text;
using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;

namespace Lumin4ti.Tests;

/// <summary>走査器とカテゴリー判定。偽のキー木・偽のファイルシステム・偽の MSI で行い、実レジストリは読まない。</summary>
[TestClass]
public sealed class RegistryCleanScannerTests
{
    private const string Cv = @"SOFTWARE\Microsoft\Windows\CurrentVersion";
    private const string UserCv = @"Software\Microsoft\Windows\CurrentVersion";

    // ---- 偽の部品 ----

    private sealed class FakeKey(string kernelName) : IRegistryCleanerKey
    {
        public Dictionary<string, RegistryRawValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, FakeKey> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Links { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Denied { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string Owner { get; set; } = "S-1-5-18";
        public DateTime LastWrite { get; set; } = new(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        public FakeKey Path(string path)
        {
            var current = this;
            foreach (var part in path.Split('\\'))
            {
                if (!current.Children.TryGetValue(part, out var next))
                {
                    next = new FakeKey(current.KernelName() + "\\" + part);
                    current.Children[part] = next;
                }

                current = next;
            }

            return current;
        }

        public FakeKey Str(string name, string value, uint type = 1)
        {
            Values[name] = new RegistryRawValue(type, Encoding.Unicode.GetBytes(value + "\0"));
            return this;
        }

        public FakeKey Dword(string name, uint value)
        {
            Values[name] = new RegistryRawValue(4, BitConverter.GetBytes(value));
            return this;
        }

        public IRegistryCleanerKey? OpenChild(string name, RegistryCleanAccess access, RegistryCleanView view)
        {
            Assert.AreEqual(RegistryCleanAccess.Read, access);
            if (Denied.Contains(name))
            {
                throw new UnauthorizedAccessException(name);
            }

            if (Links.Contains(name))
            {
                throw new RegistryCleanLinkException(name);
            }

            return Children.GetValueOrDefault(name);
        }

        public RegistryRawValue? GetValue(string name) => Values.GetValueOrDefault(name);
        public IReadOnlyList<string> GetValueNames() => [.. Values.Keys];
        public IReadOnlyList<string> GetSubKeyNames() => [.. Children.Keys];
        public RegistryKeyInfo QueryInfo() => new(LastWrite, Children.Count, Values.Count);
        public string KernelName() => kernelName;
        public string OwnerSid() => Owner;
        public void Dispose() { }
        public IRegistryCleanerKey CreateChild(string name, RegistryCleanView view) => throw new InvalidOperationException("走査は書き込まない。");
        public void SetValue(string name, RegistryRawValue value) => throw new InvalidOperationException("走査は書き込まない。");
        public void DeleteValue(string name) => throw new InvalidOperationException("走査は書き込まない。");
        public void DeleteSelf() => throw new InvalidOperationException("走査は書き込まない。");
    }

    private sealed class FakeFactory(Env env) : IRegistryCleanerRootFactory
    {
        public List<string> UserSids { get; } = [];
        public List<string> ClassesSids { get; } = [];
        public IRegistryCleanerKey OpenLocalMachine(RegistryCleanView view) => view == RegistryCleanView.Registry64 ? env.Machine64 : env.Machine32;

        public IRegistryCleanerKey OpenUserHive(string sid, RegistryCleanView view)
        {
            UserSids.Add(sid);
            return env.User;
        }

        public IRegistryCleanerKey OpenUserClasses(string sid, RegistryCleanView view)
        {
            ClassesSids.Add(sid);
            return env.UserClasses;
        }
    }

    private sealed class FakeMsi : IMsiProductState
    {
        public Dictionary<string, MsiProductStatus> States { get; } = new(StringComparer.OrdinalIgnoreCase);
        public MsiProductStatus Query(string productCode) => States.GetValueOrDefault(productCode, MsiProductStatus.NotRegistered);
    }

    private sealed class Env
    {
        private static readonly Dictionary<string, string> Folders = new(StringComparer.OrdinalIgnoreCase)
        {
            ["windir"] = @"C:\Windows",
            ["SystemRoot"] = @"C:\Windows",
            ["SystemDrive"] = "C:",
            ["ProgramFiles"] = @"C:\Program Files",
            ["ProgramW6432"] = @"C:\Program Files",
            ["ProgramFiles(x86)"] = @"C:\Program Files (x86)",
            ["CommonProgramFiles"] = @"C:\Program Files\Common Files",
            ["CommonProgramFiles(x86)"] = @"C:\Program Files (x86)\Common Files",
            ["ProgramData"] = @"C:\ProgramData",
            ["USERPROFILE"] = @"C:\Users\me",
            ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local",
            ["APPDATA"] = @"C:\Users\me\AppData\Roaming",
            ["Fonts"] = @"C:\Windows\Fonts",
        };

        private readonly HashSet<string> _existing = new(StringComparer.OrdinalIgnoreCase);
        public FakeKey Machine64 { get; } = new(@"\REGISTRY\MACHINE");
        public FakeKey Machine32 { get; set; } = new(@"\REGISTRY\MACHINE_WOW");
        public FakeKey User { get; } = new(@"\REGISTRY\USER\S-1-5-21-1");
        public FakeKey UserClasses { get; } = new(@"\REGISTRY\USER\S-1-5-21-1_Classes");
        public FakeMsi Msi { get; } = new();
        public List<string> SearchDirectories { get; } = [];
        public InteractiveScopeResult Scope { get; set; } = new(true, InteractiveScopeReason.None);
        public FakeFactory Factory { get; }

        public Env()
        {
            Factory = new FakeFactory(this);

            // 実機と同じリンクを置く: HKCU の Software\Classes は <SID>_Classes へ、32 ビット表示の App Paths は 64 ビット側へのリンク。
            User.Path("Software").Links.Add("Classes");
            Machine32.Path(@"SOFTWARE\Microsoft\Windows\CurrentVersion").Links.Add("App Paths");
        }

        public void Exists(params string[] paths)
        {
            foreach (var path in paths)
            {
                _existing.Add(path);
            }
        }

        private FileAttributes Attributes(string path)
        {
            if (_existing.Contains(path))
            {
                return FileAttributes.Archive;
            }

            var directory = path.TrimEnd('\\');
            if (directory.Length == 2 || _existing.Any(file => file.StartsWith(directory + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                return FileAttributes.Directory;
            }

            throw new FileNotFoundException(path);
        }

        public RegistryCleanScanner Scanner(bool wow = true)
        {
            var guard = new CustomFolderGuardContext(@"C:\Windows", [@"C:\Program Files"], @"C:\ProgramData", [@"C:\Users"], @"C:\Users\me");
            var resolver = new RegistryPathResolver(new CleanupBaseTrust(guard, name => Folders.GetValueOrDefault(name)), name => Folders.GetValueOrDefault(name));
            var probe = new RegistryFileProbe(
                root => (root.StartsWith('D') ? DriveType.Removable : DriveType.Fixed, true),
                Attributes);
            var locations = new RegistryOwnedLocations(@"C:\Windows", [@"C:\Program Files", @"C:\Program Files (x86)"], @"C:\Users\me\AppData\Local");
            var detectors = new RegistryCategoryDetectors(resolver, probe, locations, Msi, () => SearchDirectories);
            return new RegistryCleanScanner(Factory, detectors, () => Scope, () => "S-1-5-21-1", wow);
        }

        public RegistryScanResult Scan(params RegistryCleanCategory[] categories) => Scanner().Scan(categories);
    }

    private static string? ReasonKey(string reason) => CleanupMessage.KeyOf(reason);

    private static RegistryRetainedEntry OneRetained(RegistryScanResult result, string reasonKey)
    {
        var entry = result.Retained.Single();
        Assert.AreEqual("Cleanup.Msg." + reasonKey, ReasonKey(entry.Reason));
        return entry;
    }

    // ---- App Paths ----

    private static FakeKey AppPath(Env env, string name, string? defaultValue)
    {
        var key = env.Machine64.Path($@"{Cv}\App Paths\{name}");
        if (defaultValue is not null)
        {
            key.Str(string.Empty, defaultValue);
        }

        return key;
    }

    [TestMethod]
    public void AppPathsは実体が欠損確定のキーだけを候補にしハッシュと更新時刻を記録する()
    {
        var env = new Env();
        AppPath(env, "gone.exe", @"C:\Gone\gone.exe").Str("Path", @"C:\Gone");
        AppPath(env, "here.exe", @"C:\Here\here.exe");
        env.Exists(@"C:\Here\here.exe");

        var result = env.Scan(RegistryCleanCategory.AppPaths);

        var item = result.Items.Single();
        Assert.AreEqual($@"{Cv}\App Paths\gone.exe", item.KeyPath);
        Assert.IsNull(item.ValueName);
        Assert.IsNull(item.ValueType);
        Assert.AreEqual(RegistryCleanHive.LocalMachine, item.Hive);
        Assert.AreEqual(RegistryCleanView.Registry64, item.View);
        Assert.AreEqual(env.Machine64.Path($@"{Cv}\App Paths\gone.exe").LastWrite, item.LastWriteTimeUtc);
        Assert.AreEqual(RegistryScanHashing.OfSubtree(env.Machine64.Path($@"{Cv}\App Paths\gone.exe"), RegistryCleanView.Registry64), item.ContentHash);
        Assert.IsEmpty(result.Retained);
        Assert.IsNotNull(result.GenerationId);
    }

    [TestMethod]
    public void AppPathsはexe拡張子なしの値を両方確認する()
    {
        var env = new Env();
        AppPath(env, "a.exe", @"C:\Tools\a");
        AppPath(env, "b.exe", @"C:\Tools\b");
        env.Exists(@"C:\Tools\b.exe");

        var result = env.Scan(RegistryCleanCategory.AppPaths);

        Assert.AreEqual($@"{Cv}\App Paths\a.exe", result.Items.Single().KeyPath);
    }

    [TestMethod]
    public void AppPathsの不明と保護と空の既定値は保持する()
    {
        var env = new Env();
        AppPath(env, "removable.exe", @"D:\Tools\x.exe");
        AppPath(env, "os.exe", @"C:\Windows\System32\gone.exe");
        AppPath(env, "empty.exe", null);
        AppPath(env, "ti.exe", @"C:\Gone\ti.exe").Owner = FullTrustedInstaller();

        var result = env.Scan(RegistryCleanCategory.AppPaths);

        Assert.IsEmpty(result.Items);
        var byPath = result.Retained.ToDictionary(entry => entry.KeyPath.Split('\\')[^1]);
        Assert.AreEqual(RegistryRetentionKind.Unknown, byPath["removable.exe"].Kind);
        Assert.AreEqual(RegistryRetentionKind.Protected, byPath["os.exe"].Kind);
        Assert.AreEqual(RegistryRetentionKind.Unknown, byPath["empty.exe"].Kind);
        Assert.AreEqual(RegistryRetentionKind.Protected, byPath["ti.exe"].Kind);
        Assert.AreEqual("Cleanup.Msg." + RegistryScanReasons.TrustedInstaller, ReasonKey(byPath["ti.exe"].Reason));
    }

    private static string FullTrustedInstaller() => "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    // ---- スタートアップ ----

    [TestMethod]
    public void スタートアップの欠損確定の値はStartupApprovedの値と組で記録する()
    {
        var env = new Env();
        env.Machine64.Path($@"{Cv}\Run").Str("Gone", @"""C:\Gone\gone.exe"" /s").Str("Here", @"C:\Here\here.exe");
        env.Machine64.Path($@"{Cv}\Explorer\StartupApproved\Run").Values["Gone"] = new RegistryRawValue(3, [2, 0, 0, 0]);
        env.Exists(@"C:\Here\here.exe");

        var result = env.Scan(RegistryCleanCategory.Startup);

        var item = result.Items.Single();
        Assert.AreEqual("Gone", item.ValueName);
        Assert.AreEqual(1u, item.ValueType);
        var companion = result.Companions.Single();
        Assert.AreEqual(item, companion.Owner);
        Assert.AreEqual(RegistryCompanionKind.StartupApprovedValue, companion.Kind);
        Assert.AreEqual($@"{Cv}\Explorer\StartupApproved\Run", companion.Related.KeyPath);
        Assert.AreEqual("Gone", companion.Related.ValueName);
        Assert.AreEqual(RegistryScanHashing.OfValue(new RegistryRawValue(3, [2, 0, 0, 0])), companion.Related.ContentHash);
    }

    [TestMethod]
    public void スタートアップのWOW64側はRun32を組にしHKCUは利用者側の変数を展開する()
    {
        var env = new Env();
        env.Machine32.Path($@"{Cv}\Run").Str("Old", @"%ProgramFiles%\Gone\a.exe", type: 2);
        env.Machine64.Path($@"{Cv}\Explorer\StartupApproved\Run32").Values["Old"] = new RegistryRawValue(3, [6, 0, 0, 0]);
        env.User.Path($@"{UserCv}\Run").Str("Mine", @"%LOCALAPPDATA%\Gone\b.exe", type: 2);
        env.Exists(@"C:\Program Files (x86)\Gone\a.exe");

        var result = env.Scan(RegistryCleanCategory.Startup);

        Assert.AreEqual(1, result.Items.Count(item => item.Hive == RegistryCleanHive.CurrentUser && item.ValueName == "Mine"));
        Assert.AreEqual(0, result.Items.Count(item => item.View == RegistryCleanView.Registry32), "x86 側に実体があるため候補にしない。");
        Assert.AreEqual(0, result.Companions.Count(companion => companion.Owner.ValueName == "Old"));
    }

    [TestMethod]
    public void スタートアップのラッパー経由と型違いと保護場所は保持する()
    {
        var env = new Env();
        env.Machine64.Path($@"{Cv}\Run")
            .Str("Wrapper", @"cmd.exe /c C:\Gone\x.bat")
            .Dword("Number", 1)
            .Str("Os", @"C:\Windows\System32\gone.exe");

        var result = env.Scan(RegistryCleanCategory.Startup);

        Assert.IsEmpty(result.Items);
        Assert.AreEqual(3, result.Retained.Count);
        Assert.AreEqual(RegistryRetentionKind.Protected, result.Retained.Single(entry => entry.ValueName == "Os").Kind);
        Assert.AreEqual("Cleanup.Msg." + RegistryScanReasons.UnsupportedType, ReasonKey(result.Retained.Single(entry => entry.ValueName == "Number").Reason));
    }

    // ---- フォント ----

    [TestMethod]
    public void フォントはファイル名だけならFontsフォルダー配下で判定し縦棒を含む値は保持する()
    {
        var env = new Env();
        env.Machine64.Path(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts")
            .Str("Gone (TrueType)", "gone.ttf")
            .Str("Here (TrueType)", "here.ttf")
            .Str("Multi (TrueType)", "a.ttf|b.ttf")
            .Str("Dll (TrueType)", @"C:\Windows\System32\gone.dll");
        env.Exists(@"C:\Windows\Fonts\here.ttf");

        var result = env.Scan(RegistryCleanCategory.Fonts);

        Assert.AreEqual("Gone (TrueType)", result.Items.Single().ValueName);
        Assert.AreEqual(RegistryRetentionKind.Unknown, result.Retained.Single(entry => entry.ValueName == "Multi (TrueType)").Kind);
        Assert.AreEqual(RegistryRetentionKind.Protected, result.Retained.Single(entry => entry.ValueName == "Dll (TrueType)").Kind);
    }

    // ---- 関連付け ----

    [TestMethod]
    public void OpenWithListの欠損した登録アプリはスロットを候補にしMRUListの更新を付随させる()
    {
        var env = new Env();
        env.User.Path($@"{UserCv}\Explorer\FileExts\.txt\OpenWithList").Str("a", "gone.exe").Str("b", "here.exe").Str("MRUList", "ab");
        env.User.Path($@"{UserCv}\App Paths\gone.exe").Str(string.Empty, @"C:\Gone\gone.exe");
        env.User.Path($@"{UserCv}\App Paths\here.exe").Str(string.Empty, @"C:\Here\here.exe");
        env.Exists(@"C:\Here\here.exe");

        var result = env.Scan(RegistryCleanCategory.FileAssociations);

        var item = result.Items.Single();
        Assert.AreEqual("a", item.ValueName);
        Assert.AreEqual($@"{UserCv}\Explorer\FileExts\.txt\OpenWithList", item.KeyPath);
        var companion = result.Companions.Single();
        Assert.AreEqual(RegistryCompanionKind.OpenWithMruList, companion.Kind);
        Assert.AreEqual("MRUList", companion.Related.ValueName);
        CollectionAssert.AreEqual(new[] { "a" }, companion.RemovedSlots.ToArray());
    }

    [TestMethod]
    public void OpenWithListは登録が無い名前と標準の検索先に実体がある名前を候補にしない()
    {
        var env = new Env();
        env.User.Path($@"{UserCv}\Explorer\FileExts\.txt\OpenWithList").Str("a", "unregistered.exe").Str("b", "onpath.exe").Str("MRUList", "ab");
        env.User.Path($@"{UserCv}\App Paths\onpath.exe").Str(string.Empty, @"C:\Gone\onpath.exe");
        env.SearchDirectories.Add(@"C:\Tools");
        env.Exists(@"C:\Tools\onpath.exe");

        var result = env.Scan(RegistryCleanCategory.FileAssociations);

        Assert.IsEmpty(result.Items);
        Assert.AreEqual("Cleanup.Msg." + RegistryScanReasons.NoRegistration, ReasonKey(result.Retained.Single().Reason));
    }

    private static void Verb(FakeKey application, string verb, string command, bool delegateExecute = false)
    {
        var key = application.Path($@"shell\{verb}\command");
        key.Str(string.Empty, command);
        if (delegateExecute)
        {
            key.Str("DelegateExecute", "{GUID}");
        }
    }

    [TestMethod]
    public void Applicationsは全動詞のコマンドが欠損確定のキーだけを候補にする()
    {
        var env = new Env();
        const string classes = @"SOFTWARE\Classes\Applications";
        Verb(env.Machine64.Path($@"{classes}\orphan.exe"), "open", @"""C:\Gone\a.exe"" ""%1""");
        Verb(env.Machine64.Path($@"{classes}\half.exe"), "open", @"""C:\Gone\b.exe"" ""%1""");
        Verb(env.Machine64.Path($@"{classes}\half.exe"), "edit", @"""C:\Here\b.exe"" ""%1""");
        Verb(env.Machine64.Path($@"{classes}\delegate.exe"), "open", @"""C:\Gone\c.exe""", delegateExecute: true);
        env.Machine64.Path($@"{classes}\noshell.exe").Str("FriendlyAppName", "x");
        env.Exists(@"C:\Here\b.exe");

        var result = env.Scan(RegistryCleanCategory.FileAssociations);

        Assert.AreEqual(@"SOFTWARE\Classes\Applications\orphan.exe", result.Items.Single().KeyPath);
        Assert.AreEqual(2, result.Retained.Count);
        Assert.IsTrue(result.Retained.All(entry => entry.Kind == RegistryRetentionKind.Unknown));
    }

    [TestMethod]
    public void OpenWithProgidsはClassesに無いProgIDの値だけを候補にする()
    {
        var env = new Env();
        env.Machine64.Path(@"SOFTWARE\Classes\.xyz\OpenWithProgids").Str("xyzfile", string.Empty).Str("deadprog", string.Empty)
            .Str("AppXabc123", string.Empty).Str("liveprog", string.Empty);
        Verb(env.Machine64.Path(@"SOFTWARE\Classes\xyzfile"), "open", @"""C:\Here\x.exe"" ""%1""");
        Verb(env.Machine64.Path(@"SOFTWARE\Classes\liveprog"), "open", @"""C:\Gone\live.exe"" ""%1""");
        env.Exists(@"C:\Here\x.exe");

        var result = env.Scan(RegistryCleanCategory.FileAssociations);

        var items = result.Items.Where(item => item.KeyPath.EndsWith("OpenWithProgids", StringComparison.Ordinal)).ToList();
        CollectionAssert.AreEquivalent(new[] { "deadprog", "liveprog" }, items.Select(item => item.ValueName).ToArray());
        Assert.AreEqual("Cleanup.Msg." + RegistryScanReasons.ProgIdUnverifiable, ReasonKey(result.Retained.Single().Reason));
    }

    [TestMethod]
    public void 利用者のClassesはリンクを辿らず実体のハイブで欠損を確定する()
    {
        var env = new Env();
        env.User.Path($@"{UserCv}\Explorer\FileExts\.txt\OpenWithList").Str("a", "gone.exe").Str("MRUList", "a");
        Verb(env.UserClasses.Path(@"Applications\gone.exe"), "open", @"""C:\Gone\gone.exe"" ""%1""");
        env.UserClasses.Path(@".txt\OpenWithProgids").Str("deadprog", string.Empty);
        env.User.Path($@"{UserCv}\Explorer\FileExts\.txt\OpenWithProgids").Str("deadprog2", string.Empty);

        var result = env.Scan(RegistryCleanCategory.FileAssociations);

        Assert.IsEmpty(result.Retained, string.Join(", ", result.Retained.Select(entry => entry.Reason)));
        var openWith = result.Items.Single(item => item.ValueName == "a");
        Assert.AreEqual(RegistryCleanHive.CurrentUser, openWith.Hive);
        var application = result.Items.Single(item => item.ValueName is null);
        Assert.AreEqual(RegistryCleanHive.UserClasses, application.Hive);
        Assert.AreEqual(@"Applications\gone.exe", application.KeyPath);
        var progId = result.Items.Single(item => item.ValueName == "deadprog");
        Assert.AreEqual(RegistryCleanHive.UserClasses, progId.Hive);
        Assert.AreEqual(@".txt\OpenWithProgids", progId.KeyPath);
        Assert.AreEqual(1, result.Items.Count(item => item.ValueName == "deadprog2" && item.Hive == RegistryCleanHive.CurrentUser));
        CollectionAssert.AreEqual(new[] { "S-1-5-21-1" }, env.Factory.ClassesSids);
        Assert.IsTrue(result.Items.All(item => RegistryBackupValidator.IsAllowedPath(item.Category, item.Hive, item.KeyPath, item.ValueName is null)));
    }

    [TestMethod]
    public void 利用者のClassesに登録があるProgIDとアプリは候補にしない()
    {
        var env = new Env();
        env.User.Path($@"{UserCv}\Explorer\FileExts\.txt\OpenWithList").Str("a", "live.exe").Str("MRUList", "a");
        Verb(env.UserClasses.Path(@"Applications\live.exe"), "open", @"""C:\Here\live.exe"" ""%1""");
        env.Machine64.Path(@"SOFTWARE\Classes\.xyz\OpenWithProgids").Str("userprog", string.Empty);
        Verb(env.UserClasses.Path("userprog"), "open", @"""C:\Here\live.exe"" ""%1""");
        env.Exists(@"C:\Here\live.exe");

        var result = env.Scan(RegistryCleanCategory.FileAssociations);

        Assert.IsEmpty(result.Items);
        Assert.IsEmpty(result.Retained);
    }

    [TestMethod]
    public void AppPathsの32ビット表示は共有キーのため走査せず保持にも出さない()
    {
        var env = new Env();
        AppPath(env, "gone.exe", @"C:\Gone\gone.exe");

        var result = env.Scan(RegistryCleanCategory.AppPaths);

        Assert.AreEqual(RegistryCleanView.Registry64, result.Items.Single().View);
        Assert.IsEmpty(result.Retained);
    }

    [TestMethod]
    public void 利用者スコープが無効ならHKCUを読まずProgIDの欠損を確定しない()
    {
        var env = new Env { Scope = new InteractiveScopeResult(false, InteractiveScopeReason.DifferentUser) };
        env.Machine64.Path(@"SOFTWARE\Classes\.xyz\OpenWithProgids").Str("deadprog", string.Empty);
        env.User.Path($@"{UserCv}\Run").Str("Mine", @"C:\Gone\a.exe");

        var result = env.Scan(RegistryCleanCategory.FileAssociations, RegistryCleanCategory.Startup);

        Assert.IsEmpty(result.Items);
        Assert.IsEmpty(env.Factory.UserSids);
        Assert.IsFalse(result.UserScope.Available);
        Assert.AreEqual(InteractiveScopeReason.DifferentUser, result.UserScope.Reason);
        Assert.IsNotNull(result.UserScope.ReasonMessage);
        Assert.AreEqual("Cleanup.Msg." + RegistryScanReasons.ProgIdUnverifiable, ReasonKey(result.Retained.Single().Reason));
    }

    // ---- アンインストール ----

    private static FakeKey Uninstall(Env env, string name, string uninstall = @"C:\Gone\unins.exe", string? location = null, string? icon = null)
    {
        var key = env.Machine64.Path($@"{Cv}\Uninstall\{name}").Str("UninstallString", uninstall);
        if (location is not null)
        {
            key.Str("InstallLocation", location);
        }

        if (icon is not null)
        {
            key.Str("DisplayIcon", icon);
        }

        return key;
    }

    [TestMethod]
    public void アンインストールは全ての参照が欠損確定のときだけ候補にする()
    {
        var env = new Env();
        Uninstall(env, "Gone", location: @"C:\Gone", icon: @"""C:\Gone\a.exe"",0");
        Uninstall(env, "EmptyLocation", location: string.Empty);
        Uninstall(env, "LocationExists", location: @"C:\Here");
        Uninstall(env, "IconExists", icon: @"C:\Here\a.ico");
        Uninstall(env, "UninstallExists", uninstall: @"""C:\Here\unins.exe""");
        env.Exists(@"C:\Here\a.ico", @"C:\Here\unins.exe", @"C:\Here\x.txt");

        var result = env.Scan(RegistryCleanCategory.Uninstall);

        CollectionAssert.AreEquivalent(new[] { "Gone", "EmptyLocation" }, result.Items.Select(item => item.KeyPath.Split('\\')[^1]).ToArray());
        Assert.IsEmpty(result.Retained);
    }

    [TestMethod]
    public void アンインストールの除外条件は候補にも保持にもしない()
    {
        var env = new Env();
        Uninstall(env, "Msi1").Dword("WindowsInstaller", 1);
        Uninstall(env, "System").Dword("SystemComponent", 1);
        Uninstall(env, "Child").Str("ParentKeyName", "Parent");
        Uninstall(env, "Update").Str("ReleaseType", "Hotfix");
        Uninstall(env, "{11111111-1111-1111-1111-111111111111}");
        env.Msi.States["{11111111-1111-1111-1111-111111111111}"] = MsiProductStatus.Registered;

        var result = env.Scan(RegistryCleanCategory.Uninstall);

        Assert.IsEmpty(result.Items);
        Assert.IsEmpty(result.Retained);
    }

    [TestMethod]
    public void アンインストールのMSI照会が不明なら保持し未登録の製品コードは判定を続ける()
    {
        var env = new Env();
        Uninstall(env, "{22222222-2222-2222-2222-222222222222}");
        Uninstall(env, "{33333333-3333-3333-3333-333333333333}");
        env.Msi.States["{22222222-2222-2222-2222-222222222222}"] = MsiProductStatus.Indeterminate;

        var result = env.Scan(RegistryCleanCategory.Uninstall);

        Assert.AreEqual("{33333333-3333-3333-3333-333333333333}", result.Items.Single().KeyPath.Split('\\')[^1]);
        OneRetained(result, RegistryScanReasons.MsiIndeterminate);
    }

    [TestMethod]
    public void アンインストールのQuietUninstallStringが存在すれば候補にしない()
    {
        var env = new Env();
        Uninstall(env, "App").Str("QuietUninstallString", @"""C:\Here\quiet.exe"" /s");
        env.Exists(@"C:\Here\quiet.exe");

        Assert.IsEmpty(env.Scan(RegistryCleanCategory.Uninstall).Items);
    }

    [TestMethod]
    [DataRow(@"""C:\a b\x.exe"",0", @"C:\a b\x.exe")]
    [DataRow(@"C:\a\x.exe,-12", @"C:\a\x.exe")]
    [DataRow(@"C:\a\x.ico", @"C:\a\x.ico")]
    public void DisplayIconはパスだけを取り出す(string text, string expected) =>
        Assert.AreEqual(expected, RegistryCategoryDetectors.IconFilePath(text));

    // ---- 共有 DLL ----

    [TestMethod]
    public void 共有DLLは値の名前を完全パスとして判定しDWORD以外は保持する()
    {
        var env = new Env();
        env.Machine64.Path($@"{Cv}\SharedDLLs").Dword(@"C:\Gone\a.dll", 1).Dword(@"C:\Here\b.dll", 2).Str(@"C:\Gone\c.dll", "x");
        env.Exists(@"C:\Here\b.dll");

        var result = env.Scan(RegistryCleanCategory.SharedDlls);

        var item = result.Items.Single();
        Assert.AreEqual(@"C:\Gone\a.dll", item.ValueName);
        Assert.AreEqual(4u, item.ValueType);
        Assert.AreEqual(RegistryScanHashing.OfValue(new RegistryRawValue(4, BitConverter.GetBytes(1u))), item.ContentHash);
        Assert.AreEqual(@"C:\Gone\c.dll", OneRetained(result, RegistryScanReasons.UnsupportedType).ValueName);
    }

    // ---- 共通 ----

    [TestMethod]
    public void 同じ物理キーを指す32ビット表示は二重に候補にしない()
    {
        var env = new Env();
        env.Machine32 = env.Machine64;
        env.Machine64.Path($@"{Cv}\SharedDLLs").Dword(@"C:\Gone\a.dll", 1);

        Assert.AreEqual(1, env.Scan(RegistryCleanCategory.SharedDlls).Items.Count);
    }

    [TestMethod]
    public void WOW64側の候補はView32で記録する()
    {
        var env = new Env();
        env.Machine32.Path($@"{Cv}\SharedDLLs").Dword(@"C:\Gone\a.dll", 1);

        var item = env.Scan(RegistryCleanCategory.SharedDlls).Items.Single();

        Assert.AreEqual(RegistryCleanView.Registry32, item.View);
        Assert.AreEqual(@"\REGISTRY\MACHINE_WOW\" + $@"{Cv}\SharedDLLs", item.KernelName);
    }

    [TestMethod]
    public void リンクとアクセス拒否は保持に回し走査を続ける()
    {
        var env = new Env();
        var appPaths = env.Machine64.Path($@"{Cv}\App Paths");
        AppPath(env, "gone.exe", @"C:\Gone\gone.exe");
        appPaths.Links.Add("link.exe");
        appPaths.Denied.Add("denied.exe");
        appPaths.Children["link.exe"] = new FakeKey("x1");
        appPaths.Children["denied.exe"] = new FakeKey("x2");

        var result = env.Scan(RegistryCleanCategory.AppPaths);

        Assert.AreEqual("gone.exe", result.Items.Single().KeyPath.Split('\\')[^1]);
        Assert.AreEqual(2, result.Retained.Count);
        Assert.IsTrue(result.Retained.All(entry => entry.Kind == RegistryRetentionKind.Unknown));
        Assert.IsTrue(result.Retained.Any(entry => ReasonKey(entry.Reason) == "Cleanup.Msg.RegistrySymlink"));
    }

    [TestMethod]
    public void 走査ごとに新しい世代IDを返す()
    {
        var env = new Env();
        var scanner = env.Scanner();

        Assert.AreNotEqual(scanner.Scan().GenerationId, scanner.Scan().GenerationId);
    }

    [TestMethod]
    public void キャンセルされた走査は例外で止まる()
    {
        var env = new Env();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => env.Scanner().Scan(null, cts.Token));
    }

    [TestMethod]
    public void ハッシュは値の型とバイト列とサブツリーの内容で変わり並びに依存しない()
    {
        var bytes = new byte[] { 1, 2, 3 };
        Assert.AreNotEqual(
            RegistryScanHashing.OfValue(new RegistryRawValue(1, bytes)),
            RegistryScanHashing.OfValue(new RegistryRawValue(3, bytes)));

        var first = new FakeKey("k1").Str("a", "1").Str("b", "2");
        first.Path("x").Str("v", "1");
        first.Path("y");
        var second = new FakeKey("k2").Str("b", "2").Str("a", "1");
        second.Path("y");
        second.Path("x").Str("v", "1");
        var view = RegistryCleanView.Registry64;
        Assert.AreEqual(RegistryScanHashing.OfSubtree(first, view), RegistryScanHashing.OfSubtree(second, view));

        second.Path("x").Str("v", "changed");
        Assert.AreNotEqual(RegistryScanHashing.OfSubtree(first, view), RegistryScanHashing.OfSubtree(second, view));
    }
}
