using Lumin4ti.Core.Services.Windows.Actions;
using System.Text;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class MmAgentRegistryFallbackTests
{
    [TestMethod]
    [DataRow("{broken")]
    [DataRow("{\"SchemaVersion\":999,\"Entries\":[]}")]
    [DataRow("{\"SchemaVersion\":1,\"Entries\":[]}")]
    public void 不正バックアップでは控えがあっても一切書き込まず退避を保持する(string json)
    {
        var storage = new BackupStorage { Json = json };
        var registry = new RegistryAccessor();
        var backup = new RegistryValueBackup(storage, registry);
        var writes = 0;
        var error = MmAgentRegistryFallback.TrySetState(
            "ApplicationLaunchPrefetching", true,
            () => throw new AssertFailedException("不正な退避からメモリ上の控えへ進んではいけません"),
            backup, _ => { writes++; return null; });

        Assert.IsNotNull(error);
        StringAssert.Contains(error, "変更しませんでした");
        Assert.AreEqual(0, writes + registry.Writes);
        Assert.AreEqual(json, storage.Json);
    }

    [TestMethod]
    [DataRow(null, 3)]
    [DataRow(2, 2)]
    public void バックアップが無いときだけ控えまたは既定値を使う(int? previous, int expected)
    {
        var registry = new RegistryAccessor();
        var backup = new RegistryValueBackup(new BackupStorage(), registry);
        int? written = null;
        var error = MmAgentRegistryFallback.TrySetState("ApplicationLaunchPrefetching", true,
            () => previous, backup, value => { written = value; return null; });
        Assert.IsNull(error);
        Assert.AreEqual(expected, written);
        Assert.AreEqual(0, registry.Writes);
    }

    [TestMethod]
    public void 無効化前の値を保存し有効化時に既定値より優先して復元する()
    {
        var storage = new BackupStorage();
        var registry = new RegistryAccessor { Value = RegistryValueSnapshot.Dword(2) };
        var backup = new RegistryValueBackup(storage, registry);
        Assert.IsNull(MmAgentRegistryFallback.TrySetState("ApplicationLaunchPrefetching", false,
            null, backup, value => { registry.Value = RegistryValueSnapshot.Dword(value); return null; }));
        Assert.IsNotNull(storage.Json);
        Assert.AreEqual(0, registry.Value.ToRegistryValue());

        Assert.IsNull(MmAgentRegistryFallback.TrySetState("ApplicationLaunchPrefetching", true,
            () => 3, backup, _ => throw new AssertFailedException("復元後に既定値を書いてはいけません")));
        Assert.AreEqual(2, registry.Value.ToRegistryValue());
        Assert.AreEqual(1, registry.Writes);
        Assert.IsNull(storage.Json);
    }

    private sealed class BackupStorage : IRegistryBackupStorage
    {
        public string? Json { get; set; }
        public bool FileExists(string relativePath) => Json is not null;
        public string ReadAllText(string relativePath) => Json!;
        public void Delete(string relativePath) => Json = null;
        public void WriteNewAtomically(string relativePath, Action<Stream> write)
        {
            using var stream = new MemoryStream();
            write(stream);
            Json = Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private sealed class RegistryAccessor : IRegistryValueAccessor
    {
        public RegistryValueSnapshot Value { get; set; } = RegistryValueSnapshot.Missing();
        public int Writes { get; private set; }
        public RegistryValueSnapshot Read(RegistryToggleSpec spec) => Value;
        public void Write(RegistryToggleSpec spec, RegistryValueSnapshot value)
        {
            Writes++;
            Value = value;
        }
    }

    [TestMethod]
    public void アプリ起動プリフェッチだけレジストリ経由の代替手段を持つ()
    {
        Assert.IsTrue(MmAgentRegistryFallback.CanFallBack("ApplicationLaunchPrefetching"));
        // 大文字小文字の違いは同一機能として扱う
        Assert.IsTrue(MmAgentRegistryFallback.CanFallBack("applicationlaunchprefetching"));

        // 実体となるレジストリ値が無い機能は cmdlet 以外の手段を持たない
        Assert.IsFalse(MmAgentRegistryFallback.CanFallBack("MemoryCompression"));
        Assert.IsFalse(MmAgentRegistryFallback.CanFallBack("PageCombining"));
        Assert.IsFalse(MmAgentRegistryFallback.CanFallBack("OperationAPI"));
        Assert.IsFalse(MmAgentRegistryFallback.CanFallBack("ApplicationPreLaunch"));
    }

    [TestMethod]
    public void 代替手段が無い機能は書き込みを試みずに理由を返す()
    {
        // 管理者権限が無い環境でも、対象外なら実書き込みへ進まないので安全に検証できる
        var error = MmAgentRegistryFallback.TrySetState("MemoryCompression", on: false);

        Assert.IsNotNull(error);
        StringAssert.Contains(error, "代替手段");
    }

    [TestMethod]
    public void 対象外の機能の状態は読まない()
    {
        Assert.IsNull(MmAgentRegistryFallback.TryReadState("MemoryCompression"));
    }

    [TestMethod]
    public void アプリ起動プリフェッチの状態はレジストリから読める()
    {
        // 読み取りは管理者権限が不要。値が無い環境でも「既定 = 有効」で bool が返る。
        var state = MmAgentRegistryFallback.TryReadState("ApplicationLaunchPrefetching");

        Assert.IsNotNull(state);
    }
}
