using Lumin4ti.Core.Services.Windows.Actions;
using Lumin4ti.Core.Services.Windows.Cleanup;
using EngineTarget = Lumin4ti.Core.Services.Windows.Actions.CleanupTarget;

namespace Lumin4ti.Tests;

/// <summary>再審査の指摘の回帰テスト。実機のレジストリ・環境変数・ACL を変えず、一時領域と注入した値だけを使う。</summary>
[TestClass]
public sealed class CleanupRereviewFixTests
{
    private static readonly CustomFolderGuardContext Context = new(
        @"C:\Windows",
        [@"C:\Program Files"],
        @"C:\ProgramData",
        [@"C:\Users"],
        @"C:\Users\me");

    private CleanupHarness _h = null!;

    [TestInitialize]
    public void Setup() => _h = new CleanupHarness();

    [TestCleanup]
    public void Cleanup() => _h.Dispose();

    // ----- 1. 基点に残った %名前% -----

    [TestMethod]
    [DataRow("LOCALAPPDATA")]
    [DataRow("APPDATA")]
    [DataRow("USERPROFILE")]
    [DataRow("TEMP")]
    [DataRow("TMP")]
    public void 基点の値に百分率の名前が残っていたら理由付きで拒否する(string variable)
    {
        // 値の %X% は、信頼の検査の後の再展開で別の場所 (..\..\Windows など) へ逃げられる。
        _h.Env[variable] = Path.Combine(_h.Root, "Local", "%X%");
        _h.Rules.Add(CleanupHarness.MakeRule("app.test", [CleanupHarness.Contents($@"%{variable}%\Test\Cache")]));

        var resolved = new CleanupTargetResolver(name => _h.Env.GetValueOrDefault(name), _h.Probe, _h.Trust).Resolve(_h.Rules[0]);

        Assert.IsNull(resolved.Targets[0].Path);
        Assert.IsTrue(CleanupMessage.TryParse(resolved.Targets[0].Rejection, out var key, out var args), resolved.Targets[0].Rejection);
        Assert.AreEqual("Cleanup.Msg.BaseProtectedLocation", key);
        Assert.AreEqual(variable, args[0]);
    }

    [TestMethod]
    public void 展開済みの実パスの対象はエンジンも走査も領域も再展開しない()
    {
        // 実在するフォルダー名 %LuminX% (未定義の変数名)。再展開すると拒否され、実パスとして扱えば処理できる。
        var file = _h.Write(@"Pct\%LuminX%\a.tmp", "123");
        var folder = Path.GetDirectoryName(file)!;
        var expanding = EngineTarget.Contents(folder);
        var actual = expanding with { IsActualPath = true };

        Assert.IsTrue(CleanupEngineTargets.From(new CleanupRuleTarget { Type = CleanupTargetType.Contents, Path = folder }, folder).IsActualPath);
        Assert.IsNull(ClaimRegion.Create("r", expanding));
        Assert.IsNotNull(ClaimRegion.Create("r", actual));
        Assert.IsNotNull(FileCleanupScanner.Scan(expanding, null, CancellationToken.None).Rejection);
        var scan = FileCleanupScanner.Scan(actual, null, CancellationToken.None);
        Assert.IsNull(scan.Rejection);
        Assert.AreEqual(1, scan.Files);

        var rejected = FileCleanupEngine.Run([expanding], false, null, CancellationToken.None);
        Assert.IsNotEmpty(rejected.RejectedTargets);
        Assert.IsTrue(File.Exists(file));

        var run = FileCleanupEngine.Run([actual], false, null, CancellationToken.None);
        Assert.IsEmpty(run.RejectedTargets, string.Join(" / ", run.RejectedTargets));
        Assert.AreEqual(1, run.DeletedFiles);
        Assert.IsFalse(File.Exists(file));
    }

    // ----- 2. プロファイル外の TEMP -----

    private static CleanupBaseTrust Trust(Func<string, bool>? canModify) => new(Context, _ => null, canModify);

    [TestMethod]
    [DataRow(@"D:\Temp", true, true)]
    [DataRow(@"D:\Temp", false, false)]
    [DataRow(@"D:\$Recycle.Bin\S-1-5-21\x", true, false)]
    [DataRow(@"D:\Recovery\t", true, false)]
    [DataRow(@"D:\Windows.old\Temp", true, false)]
    [DataRow(@"D:\System Volume Information\t", true, false)]
    [DataRow(@"D:\Work\.git\tmp", true, false)]
    [DataRow(@"C:\Windows\Temp", true, false)]
    public void プロファイル外のTEMPは枝刈りの表を避け利用者が変更できる場所だけ使う(string value, bool canModify, bool usable)
    {
        var rejection = Trust(_ => canModify).Check("TEMP", value);

        Assert.AreEqual(usable, rejection is null, rejection);
    }

    [TestMethod]
    public void 変更できるか確認できないTEMPは使わず基点の判定へ渡すのは確認した値だけ()
    {
        Assert.IsNotNull(Trust(null).Check("TEMP", @"D:\Temp"));
        var asked = new List<string>();
        Assert.IsNull(Trust(path => { asked.Add(path); return true; }).Check("TEMP", @"D:\Temp"));
        CollectionAssert.AreEqual(new[] { @"D:\Temp" }, asked);
        // プロファイル内の TEMP は、変更できるかを問わない。
        Assert.IsNull(Trust(_ => false).Check("TEMP", @"C:\Users\me\AppData\Local\Temp"));
    }

    private const string Me = "S-1-5-21-1-2-3-1001";
    private const int Modify = 0x1301BF;
    private const int ReadOnly = 0x1200A9;

    [TestMethod]
    public void 所有者が利用者ならACEを見ず変更できるとみなす()
    {
        Assert.IsTrue(InteractiveFolderAccess.AllowsUserModify(Me, [new FolderAce(Me, false, Modify, false)], Me));
        Assert.IsTrue(InteractiveFolderAccess.AllowsUserModify(Me.ToLowerInvariant(), null, Me));
    }

    [TestMethod]
    [DataRow(Me, true)]
    [DataRow("S-1-5-32-545", true)]
    [DataRow("S-1-5-11", true)]
    [DataRow("S-1-1-0", true)]
    [DataRow("S-1-5-32-544", false)]
    [DataRow("S-1-5-18", false)]
    [DataRow("S-1-5-21-1-2-3-1002", false)]
    public void 許可ACEは利用者とUsersとAuthenticatedUsersとEveryoneだけが数えられる(string sid, bool expected) =>
        Assert.AreEqual(expected, InteractiveFolderAccess.AllowsUserModify("S-1-5-32-544", [new FolderAce(sid, true, Modify, false)], Me));

    [TestMethod]
    public void 許可の合計がModify相当を満たし拒否と継承のみのACEに当たらないときだけ変更できる()
    {
        const string users = "S-1-5-32-545";
        // 読み取りだけ・作成だけでは足りない。
        Assert.IsFalse(InteractiveFolderAccess.AllowsUserModify(null, [new FolderAce(users, true, ReadOnly, false)], Me));
        Assert.IsFalse(InteractiveFolderAccess.AllowsUserModify(null, [new FolderAce(users, true, 0x6, false)], Me));
        // 複数の ACE の合計で満たす。
        Assert.IsTrue(InteractiveFolderAccess.AllowsUserModify(null,
            [new FolderAce(users, true, 0x6, false), new FolderAce(Me, true, 0x10000, false)], Me));
        // 拒否 ACE が必要な権利を欠かす。
        Assert.IsFalse(InteractiveFolderAccess.AllowsUserModify(null,
            [new FolderAce(users, true, Modify, false), new FolderAce("S-1-1-0", false, 0x10000, false)], Me));
        // 判定対象外の SID 宛てでも、自身に効く拒否 ACE が Modify に掛かれば不可。Modify に掛からない拒否・継承専用の拒否は無視する。
        Assert.IsFalse(InteractiveFolderAccess.AllowsUserModify(null,
            [new FolderAce(users, true, Modify, false), new FolderAce("S-1-5-21-1-2-3-1002", false, Modify, false)], Me));
        Assert.IsTrue(InteractiveFolderAccess.AllowsUserModify(null,
            [new FolderAce(users, true, Modify, false), new FolderAce("S-1-5-21-1-2-3-1002", false, ReadOnly & ~0x10000, false)], Me));
        Assert.IsTrue(InteractiveFolderAccess.AllowsUserModify(null,
            [new FolderAce(users, true, Modify, false), new FolderAce("S-1-5-21-1-2-3-1002", false, Modify, true)], Me));
        Assert.IsFalse(InteractiveFolderAccess.AllowsUserModify(null, [new FolderAce(users, true, Modify, true)], Me));
        Assert.IsTrue(InteractiveFolderAccess.AllowsUserModify(null,
            [new FolderAce(users, true, Modify, false), new FolderAce(users, false, Modify, true)], Me));
        // GENERIC_ALL。
        Assert.IsTrue(InteractiveFolderAccess.AllowsUserModify(null, [new FolderAce(users, true, unchecked((int)0x10000000), false)], Me));
        // 判定できない (DACL を読めない・利用者が分からない) 場合は不可。
        Assert.IsFalse(InteractiveFolderAccess.AllowsUserModify("S-1-5-32-544", null, Me));
        Assert.IsFalse(InteractiveFolderAccess.AllowsUserModify(Me, null, string.Empty));
    }

    [TestMethod]
    public void 一時領域の実フォルダーは現在の利用者が変更できると判定しリンクと無いパスは不可()
    {
        var folder = _h.Full("Own");
        Directory.CreateDirectory(folder);
        var link = _h.Full("Link");
        CleanupHarness.CreateJunction(link, folder);

        Assert.IsTrue(InteractiveFolderAccess.CanCurrentUserModify(folder));
        Assert.IsFalse(InteractiveFolderAccess.CanCurrentUserModify(link), "リンクを辿った");
        Assert.IsFalse(InteractiveFolderAccess.CanCurrentUserModify(_h.Full("Missing")));
    }

    // ----- 3. カスタムフォルダーの保持と // -----

    [TestMethod]
    [DataRow(".docker", true)]
    [DataRow(".DOCKER", true)]
    [DataRow(".Kube", true)]
    [DataRow("Web Data", true)]
    [DataRow("WEB DATA-journal", true)]
    [DataRow("cookies", true)]
    [DataRow("COOKIES-wal", true)]
    [DataRow("preferences", true)]
    [DataRow("PREFERENCES", true)]
    [DataRow("local state", true)]
    [DataRow("LOGIN DATA", true)]
    [DataRow("login data for account", true)]
    [DataRow("ID_RSA.pub", true)]
    [DataRow("x.PEM", true)]
    [DataRow("junk.tmp", false)]
    [DataRow("Cache", false)]
    [DataRow("Preferences.bak", false)]
    public void カスタムフォルダーは認証情報とブラウザーのプロファイルを大小文字を問わず名前で残す(string name, bool kept)
    {
        Assert.AreEqual(kept, FileCleanupEngine.KeepsAsCredential(EngineTarget.CustomFolder(@"D:\Work"), name));
        Assert.IsFalse(FileCleanupEngine.KeepsAsCredential(EngineTarget.Contents(@"D:\Work"), name), "カスタムフォルダー以外で保持した");
    }

    [TestMethod]
    public void 走査と削除はカスタムフォルダーのプロファイル系を同じ判定で残す()
    {
        string[] kept = [@".docker\config.json", "Web Data", "COOKIES", "preferences", "Local State", "login data"];
        foreach (var name in kept) _h.Write($@"Custom\{name}", "x");
        _h.Write(@"Custom\junk.tmp", "123");
        var target = EngineTarget.CustomFolder(_h.Full("Custom"));

        var scan = FileCleanupScanner.Scan(target, null, CancellationToken.None);
        var run = FileCleanupEngine.Run([target], false, null, CancellationToken.None);

        Assert.AreEqual(1, scan.Files);
        Assert.AreEqual(scan.Files, run.DeletedFiles);
        foreach (var name in kept) Assert.IsTrue(File.Exists(_h.Full($@"Custom\{name}")), name);
        Assert.IsFalse(File.Exists(_h.Full(@"Custom\junk.tmp")));
    }

    [TestMethod]
    [DataRow("//server/share/x")]
    [DataRow("//?/C:/Work")]
    [DataRow(@"\\server\share")]
    public void カスタムフォルダーは先頭の二重スラッシュも拒否する(string path) =>
        Assert.AreEqual(CustomFolderRejection.Invalid, CustomFolderGuard.Check(path, Context).Rejection);
}
