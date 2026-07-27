using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class GhostPackageCleanupTests
{
    private const string GhostFamily = "Microsoft.PPIProjection_cw5n1h2txyewy";
    private const string GhostFullName = "Microsoft.PPIProjection_10.0.22621.1_neutral_neutral_cw5n1h2txyewy";
    private const string GhostPath = @"C:\Windows\SystemApps\Microsoft.PPIProjection_cw5n1h2txyewy";

    private static PackageRegistration Ghost() =>
        new(GhostFullName, GhostFamily, "Microsoft.PPIProjection", GhostPath);

    private static PackageRegistration Package(string family, string path) =>
        new($"{family}_1.0.0.0_x64__cw5n1h2txyewy", family, family, path);

    /// <summary>登録解除に成功した (= 再列挙で消える) 挙動をエミュレートする。</summary>
    private sealed class FakeStore(IEnumerable<PackageRegistration> packages)
    {
        private readonly List<PackageRegistration> _packages = [.. packages];

        public List<(string FullName, bool Deprovision)> RemoveCalls { get; } = [];

        public HashSet<string> Unremovable { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<PackageRegistration> Enumerate() => [.. _packages];

        /// <summary>本体の入れ直しで実体フォルダが復活した状態を再現する。</summary>
        public void RestoreInstallLocation(string familyName)
        {
            for (var i = 0; i < _packages.Count; i++)
            {
                if (_packages[i].FamilyName == familyName)
                {
                    _packages[i] = _packages[i] with { InstalledPath = @"C:\Windows\SystemApps\restored" };
                }
            }
        }

        public Task<string?> RemoveAsync(PackageRegistration package, bool deprovision, CancellationToken ct)
        {
            RemoveCalls.Add((package.FullName, deprovision));
            if (Unremovable.Contains(package.FullName))
            {
                // 「成功を返すのに登録が残る」システムアプリの挙動を再現する
                return Task.FromResult<string?>(null);
            }

            _packages.RemoveAll(p => p.FullName == package.FullName);
            return Task.FromResult<string?>(null);
        }
    }

    private static GhostPackageCleanupAction CreateAction(
        FakeStore store,
        IReadOnlyList<string>? startAppIds,
        Action? refreshStartMenu = null,
        Func<string, bool>? isConfirmedMissing = null,
        Func<string, IProgress<string>?, CancellationToken, Task<string?>>? repairSystemAppAsync = null,
        Func<string, bool>? canRepairSystemApp = null) =>
        new(
            store.Enumerate,
            store.RemoveAsync,
            isConfirmedMissing ?? (path => path.Contains("ghost", StringComparison.OrdinalIgnoreCase) || path == GhostPath),
            () => startAppIds,
            refreshStartMenu ?? (() => { }),
            repairSystemAppAsync,
            canRepairSystemApp);

    [TestMethod]
    public async Task スタートに出ている壊れた登録だけを解除する()
    {
        var store = new FakeStore([
            Ghost(),
            // スタートに出ない更新残骸 (LKG / SxS) はフォルダが無くても触らない
            Package("MicrosoftWindows.LKG.Search_cw5n1h2txyewy", @"C:\Windows\SystemApps\LKG\ghost"),
            Package("MicrosoftWindows.61869720.Voiess_cw5n1h2txyewy", @"C:\Windows\SystemApps\SxS\ghost"),
        ]);
        var refreshed = false;

        var result = await CreateAction(
                store,
                [$"{GhostFamily}!Microsoft.PPIProjection", "308046B0AF4A39CB"],
                refreshStartMenu: () => refreshed = true)
            .ExecuteAsync();

        Assert.AreEqual(MaintenanceActionStatus.Success, result.Status);
        CollectionAssert.AreEqual(new[] { GhostFullName }, store.RemoveCalls.Select(c => c.FullName).ToArray());
        Assert.IsTrue(refreshed, "解除したらスタートメニューを再構築する");
    }

    [TestMethod]
    public async Task スタートに出ていてもフォルダがあるなら触らない()
    {
        var store = new FakeStore([new(GhostFullName, GhostFamily, "ワイヤレス ディスプレイ", GhostPath)]);

        var result = await CreateAction(
                store,
                [$"{GhostFamily}!Microsoft.PPIProjection"],
                isConfirmedMissing: _ => false)
            .ExecuteAsync();

        Assert.AreEqual(MaintenanceActionStatus.Success, result.Status);
        Assert.AreEqual(0, store.RemoveCalls.Count);
        StringAssert.Contains(result.Detail, "壊れた項目はありませんでした");
    }

    [TestMethod]
    public async Task パスを取得できない登録には触れない()
    {
        var store = new FakeStore([new(GhostFullName, GhostFamily, "Microsoft.PPIProjection", null)]);

        var result = await CreateAction(store, [$"{GhostFamily}!Microsoft.PPIProjection"], isConfirmedMissing: _ => true)
            .ExecuteAsync();

        Assert.AreEqual(MaintenanceActionStatus.Success, result.Status);
        Assert.AreEqual(0, store.RemoveCalls.Count);
    }

    [TestMethod]
    public async Task スタート一覧を読めないときは何も変更しない()
    {
        var store = new FakeStore([Ghost()]);
        var refreshed = false;

        var result = await CreateAction(store, startAppIds: null, refreshStartMenu: () => refreshed = true)
            .ExecuteAsync();

        Assert.AreEqual(MaintenanceActionStatus.Failed, result.Status);
        Assert.AreEqual(0, store.RemoveCalls.Count);
        Assert.IsFalse(refreshed);
    }

    [TestMethod]
    public async Task 成功応答でも登録が残るならプロビジョニング解除で再試行する()
    {
        var store = new FakeStore([Ghost()]);
        store.Unremovable.Add(GhostFullName);

        var result = await CreateAction(store, [$"{GhostFamily}!Microsoft.PPIProjection"]).ExecuteAsync();

        Assert.AreEqual(MaintenanceActionStatus.Partial, result.Status);
        CollectionAssert.AreEqual(
            new[] { false, true },
            store.RemoveCalls.Select(c => c.Deprovision).ToArray(),
            "1 回目は通常解除、残ったら 2 回目でプロビジョニング解除を伴う再試行");
        StringAssert.Contains(result.Detail, "残存");
    }

    [TestMethod]
    public async Task 削除できないシステムアプリは本体を入れ直して修復する()
    {
        var store = new FakeStore([Ghost()]);
        store.Unremovable.Add(GhostFullName);
        var repairedFamilies = new List<string>();
        var refreshed = false;

        var result = await CreateAction(
                store,
                [$"{GhostFamily}!Microsoft.PPIProjection"],
                refreshStartMenu: () => refreshed = true,
                repairSystemAppAsync: (family, _, _) =>
                {
                    repairedFamilies.Add(family);
                    store.RestoreInstallLocation(family);
                    return Task.FromResult<string?>(null);
                })
            .ExecuteAsync();

        Assert.AreEqual(MaintenanceActionStatus.Success, result.Status);
        CollectionAssert.AreEqual(new[] { GhostFamily }, repairedFamilies);
        StringAssert.Contains(result.Detail, "再インストール");
        Assert.IsTrue(refreshed, "再インストール後もスタートメニューを再構築する");
    }

    [TestMethod]
    public async Task 入れ直しもできない項目は理由付きで残存として報告する()
    {
        var store = new FakeStore([Ghost()]);
        store.Unremovable.Add(GhostFullName);

        var result = await CreateAction(
                store,
                [$"{GhostFamily}!Microsoft.PPIProjection"],
                repairSystemAppAsync: (_, _, _) =>
                    Task.FromResult<string?>("この項目に対応するオプション機能が分かりません"))
            .ExecuteAsync();

        Assert.AreEqual(MaintenanceActionStatus.Partial, result.Status);
        StringAssert.Contains(result.Detail, "残存");
        StringAssert.Contains(result.Detail, "オプション機能が分かりません");
    }

    [TestMethod]
    public async Task 本体を追加しても実体が戻らなければ成功として報告しない()
    {
        var store = new FakeStore([Ghost()]);
        store.Unremovable.Add(GhostFullName);

        // オプション機能が「インストール済み」のままで DISM が何もせず成功を返すケース
        var result = await CreateAction(
                store,
                [$"{GhostFamily}!Microsoft.PPIProjection"],
                repairSystemAppAsync: (_, _, _) => Task.FromResult<string?>(null))
            .ExecuteAsync();

        Assert.AreEqual(MaintenanceActionStatus.Partial, result.Status);
        StringAssert.Contains(result.Detail, "実体が復元されませんでした");
    }

    [TestMethod]
    public async Task 解除できたシステムアプリは本体を入れ直す()
    {
        var store = new FakeStore([Ghost()]);
        var repairedFamilies = new List<string>();

        var result = await CreateAction(
                store,
                [$"{GhostFamily}!Microsoft.PPIProjection"],
                repairSystemAppAsync: (family, _, _) =>
                {
                    repairedFamilies.Add(family);
                    return Task.FromResult<string?>(null);
                },
                canRepairSystemApp: family => family == GhostFamily)
            .ExecuteAsync();

        Assert.AreEqual(MaintenanceActionStatus.Success, result.Status);
        CollectionAssert.AreEqual(new[] { GhostFamily }, repairedFamilies, "残骸を消したうえで本体を入れ直す");
        StringAssert.Contains(result.Detail, "再インストール");
    }

    [TestMethod]
    public async Task 解除後の入れ直しに失敗しても部分失敗にはしない()
    {
        var store = new FakeStore([Ghost()]);

        var result = await CreateAction(
                store,
                [$"{GhostFamily}!Microsoft.PPIProjection"],
                repairSystemAppAsync: (_, _, _) => Task.FromResult<string?>("WSUS 環境のため取得できません"),
                canRepairSystemApp: family => family == GhostFamily)
            .ExecuteAsync();

        // ゴースト自体は消えているので表示問題は解決している
        Assert.AreEqual(MaintenanceActionStatus.Success, result.Status);
        StringAssert.Contains(result.Detail, "本体の入れ直しは見送りました");
    }

    [TestMethod]
    public void 修復対象のシステムアプリを判定できる()
    {
        Assert.IsTrue(SystemAppCapabilityRepair.CanRepair("Microsoft.PPIProjection_cw5n1h2txyewy"));
        Assert.IsFalse(SystemAppCapabilityRepair.CanRepair("Contoso.SomeStoreApp_8wekyb3d8bbwe"));
    }

    [TestMethod]
    public async Task 進捗は対象ごとに通知される()
    {
        var store = new FakeStore([Ghost()]);
        var messages = new List<string>();

        await CreateAction(store, [$"{GhostFamily}!Microsoft.PPIProjection"])
            .ExecuteAsync(new RecordingProgress(messages), CancellationToken.None);

        Assert.IsTrue(messages.Any(m => m.Contains("Microsoft.PPIProjection", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AUMIDからパッケージファミリー名を取り出す()
    {
        Assert.AreEqual(GhostFamily, StartMenuAppListReader.TryGetPackageFamilyName($"{GhostFamily}!App"));
        // Win32 アプリの AUMID にはファミリー名が無い
        Assert.IsNull(StartMenuAppListReader.TryGetPackageFamilyName("308046B0AF4A39CB"));
        Assert.IsNull(StartMenuAppListReader.TryGetPackageFamilyName("!LeadingSeparator"));
    }

    /// <summary>Progress&lt;T&gt; は同期コンテキスト依存で通知順が保証されないため、同期記録に差し替える。</summary>
    private sealed class RecordingProgress(List<string> messages) : IProgress<string>
    {
        public void Report(string value) => messages.Add(value);
    }
}
