using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.Core.Services.Windows.Actions;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class FileCleanupTests
{
    private string _root = string.Empty;

    private sealed class SynchronousProgress(Action<string> callback) : IProgress<string>
    {
        public void Report(string value) => callback(value);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint code, byte[] input, uint length, nint output, uint outputLength, out uint returned, nint overlapped);

    private static void CreateJunctionInPlace(string directory, string target, uint access = 0x100)
    {
        using var handle = CreateFileW(directory, access, 7, 0, 3, 0x02200000, 0);
        Assert.IsFalse(handle.IsInvalid);
        var substitute = System.Text.Encoding.Unicode.GetBytes(@"\??\" + target);
        var printable = System.Text.Encoding.Unicode.GetBytes(target);
        var data = new byte[20 + substitute.Length + printable.Length];
        void Write(int offset, ushort value) => BitConverter.GetBytes(value).CopyTo(data, offset);
        BitConverter.GetBytes(0xA0000003u).CopyTo(data, 0);
        Write(4, (ushort)(data.Length - 8));
        Write(10, (ushort)substitute.Length);
        Write(12, (ushort)(substitute.Length + 2));
        Write(14, (ushort)printable.Length);
        substitute.CopyTo(data, 16);
        printable.CopyTo(data, 18 + substitute.Length);
        Assert.IsTrue(DeviceIoControl(handle, 0x900A4, data, (uint)data.Length, 0, 0, out _, 0),
            $"既存ディレクトリの junction 化に失敗: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void 実行中に対象自体をjunction化しても外部ファイルとリンクを保持する(bool files)
    {
        var target = Path.Combine(_root, "inplace-target");
        Directory.CreateDirectory(target);
        var outside = CreateFile(@"outside\canary.etl", "external data");
        File.SetAttributes(outside, FileAttributes.ReadOnly | FileAttributes.Hidden);
        var attributes = File.GetAttributes(outside);
        var progress = new SynchronousProgress(_ => CreateJunctionInPlace(target, Path.GetDirectoryName(outside)!));

        var outcome = FileCleanupEngine.Run(
            [files ? CleanupTarget.Files(target, "*.etl") : CleanupTarget.Contents(target)], false, progress, CancellationToken.None);

        Assert.AreEqual(0, outcome.DeletedFiles);
        Assert.IsTrue(outcome.Blocked > 0 || outcome.RejectedTargets.Count > 0);
        Assert.AreEqual("external data", File.ReadAllText(outside));
        Assert.AreEqual(attributes, File.GetAttributes(outside));
        Assert.IsTrue((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0, "リンク自身も保持する");
    }

    [TestMethod]
    public void handle取得後のjunction化では外部名を列挙せず削除もしない()
    {
        var target = Path.Combine(_root, "native-inplace");
        Directory.CreateDirectory(target);
        var outside = CreateFile(@"native-outside\canary.txt", "keep");
        using (var entry = NativeCleanupEntry.Open(target, true, delete: true))
        {
            CreateJunctionInPlace(target, Path.GetDirectoryName(outside)!);
            Assert.ThrowsExactly<IOException>(() => entry.Enumerate().ToArray());
            Assert.ThrowsExactly<IOException>(() => entry.Delete());
        }
        Assert.AreEqual("keep", File.ReadAllText(outside));
        Assert.IsTrue((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0);
    }

    [TestMethod]
    public void 列挙後に祖先をjunction化しても絶対パスの子を開かない()
    {
        var parent = Path.Combine(_root, "ancestor-inplace");
        var child = Path.Combine(parent, "child");
        Directory.CreateDirectory(child);
        var outside = CreateFile(@"ancestor-outside\child\canary.txt", "keep");
        using (var root = NativeCleanupEntry.Open(parent, true))
        {
            Assert.HasCount(1, root.Enumerate().ToArray());
            // 列挙済みの空の子を別主体が取り除くと、祖先自体を in-place 変更できる。
            Directory.Delete(child);
            CreateJunctionInPlace(parent, Path.Combine(_root, "ancestor-outside"));
            Assert.Throws<System.ComponentModel.Win32Exception>(() => NativeCleanupEntry.Open(child, true, delete: true, pinAncestors: false));
        }
        Assert.AreEqual("keep", File.ReadAllText(outside));
        Assert.IsTrue((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0);
    }

    [TestMethod]
    public void deleteHandle取得中の名前差替えを拒否し属性操作は同じ実体だけに作用する()
    {
        var file = CreateFile(@"native-delete\file.tmp");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        using (var entry = NativeCleanupEntry.Open(file, false, delete: true))
        {
            Assert.ThrowsExactly<IOException>(() => File.Move(file, file + ".moved"));
            entry.SetAttributes(FileAttributes.Normal);
            entry.Delete();
        }
        Assert.IsFalse(File.Exists(file));
    }

    [TestMethod]
    public void directoryのWriteDataだけのhandleでもjunction化できるため予約ACLで拒否する()
    {
        var target = Path.Combine(_root, "write-data-junction");
        Directory.CreateDirectory(target);
        var outside = CreateFile(@"write-data-outside\keep.txt", "keep");
        CreateJunctionInPlace(target, Path.GetDirectoryName(outside)!, access: 2);
        Assert.IsTrue((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0);
        Assert.AreEqual("keep", File.ReadAllText(outside));
    }

    [TestMethod]
    public void 子のabsoluteNativePathは保持したrootのdeviceprefixを継承する()
    {
        var path = Path.Combine(_root, "native-context");
        var child = Path.Combine(path, "child");
        Directory.CreateDirectory(child);
        using var root = NativeCleanupEntry.Open(path, true);
        using var entry = root.OpenDescendant(child, true);
        Assert.AreEqual(root.NativePath + @"\child", entry.NativePath);
        Assert.ThrowsExactly<IOException>(() => root.OpenDescendant(_root, true));
    }

    [TestMethod]
    public void nativeパスはDOSaliasではなく検証済みローカルvolumeを使う()
    {
        Assert.AreEqual(@"\Device\HarddiskVolume12\cache\data.bin",
            NativeCleanupEntry.ToNativePath(@"D:\cache\data.bin", @"\Device\HarddiskVolume12"));
        Assert.ThrowsExactly<IOException>(() => NativeCleanupEntry.ToNativePath(@"D:\cache", @"\??\C:\subst"));
        Assert.ThrowsExactly<IOException>(() => NativeCleanupEntry.ToNativePath(@"D:\cache", @"\Device\Mup\server\share"));
        Assert.ThrowsExactly<IOException>(() => NativeCleanupEntry.ToNativePath(@"\\server\share\cache", @"\Device\HarddiskVolume12"));
        Assert.IsTrue(NativeCleanupEntry.ResolveNativePath(_root).StartsWith(@"\Device\HarddiskVolume", StringComparison.Ordinal));
    }

    [TestMethod]
    public void 列挙は先頭で短絡したら次の走査を行わずbufferを解放する()
    {
        var path = Path.Combine(_root, "lazy");
        Directory.CreateDirectory(path);
        using (var entry = NativeCleanupEntry.Open(path, true))
        {
            var enumeration = entry.Enumerate();
            var first = CreateFile(@"lazy\first.bin");
            using (var cursor = enumeration.GetEnumerator())
            {
                Assert.IsTrue(cursor.MoveNext());
                Assert.AreEqual("first.bin", cursor.Current.Name);
                File.Delete(first);
                var outside = Path.Combine(_root, "lazy-outside");
                Directory.CreateDirectory(outside);
                CreateJunctionInPlace(path, outside);
                // 全件走査なら次のnative取得時にreparse検出で失敗する。短絡Disposeはここで止まる。
            }
            Assert.ThrowsExactly<IOException>(() => entry.Enumerate().ToArray());
        }
        Assert.IsTrue((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
    }

    [TestMethod]
    public void WRITE_ATTRIBUTESを拒否された普通ファイルでもDELETE権限で削除できる()
    {
        var file = CreateFile(@"acl-file\normal.bin");
        var info = new FileInfo(file);
        var original = info.GetAccessControl().GetSecurityDescriptorBinaryForm();
        try
        {
            var restricted = info.GetAccessControl();
            restricted.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.WriteAttributes, AccessControlType.Deny));
            info.SetAccessControl(restricted);
            var result = FileCleanupEngine.Run([CleanupTarget.Contents(info.DirectoryName!)], false, null, CancellationToken.None);
            Assert.AreEqual(1, result.DeletedFiles, string.Join("; ", result.BlockedExamples));
            Assert.IsFalse(File.Exists(file));
        }
        finally { if (File.Exists(file)) RestoreDacl(info, original); }
    }

    [TestMethod]
    public void directory自身のDELETE不可でも内部ファイルを掃除する()
    {
        var file = CreateFile(@"acl-directory\child\normal.bin");
        var parent = new DirectoryInfo(Path.Combine(_root, "acl-directory"));
        var child = new DirectoryInfo(Path.GetDirectoryName(file)!);
        var parentAcl = parent.GetAccessControl().GetSecurityDescriptorBinaryForm();
        var childAcl = child.GetAccessControl().GetSecurityDescriptorBinaryForm();
        var user = WindowsIdentity.GetCurrent().User!;
        try
        {
            var denyParent = parent.GetAccessControl();
            denyParent.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny));
            parent.SetAccessControl(denyParent);
            var denyChild = child.GetAccessControl();
            denyChild.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Delete, AccessControlType.Deny));
            child.SetAccessControl(denyChild);
            var result = FileCleanupEngine.Run([CleanupTarget.Contents(parent.FullName)], false, null, CancellationToken.None);
            Assert.AreEqual(1, result.DeletedFiles, string.Join("; ", result.BlockedExamples));
            Assert.IsFalse(File.Exists(file));
            Assert.IsTrue(Directory.Exists(child.FullName));
            Assert.AreEqual(1, result.Blocked);
        }
        finally
        {
            try { RestoreDacl(parent, parentAcl); }
            finally { if (Directory.Exists(child.FullName)) RestoreDacl(child, childAcl); }
        }
    }

    [TestMethod]
    public void 読取共有で開かれた普通ファイルもDELETE共有があれば削除できる()
    {
        var file = CreateFile(@"share-delete\normal.bin");
        using var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var result = FileCleanupEngine.Run([CleanupTarget.Contents(Path.GetDirectoryName(file)!)], false, null, CancellationToken.None);
        Assert.AreEqual(1, result.DeletedFiles, string.Join("; ", result.BlockedExamples));
    }
    [TestMethod]
    public void HiddenSystemだけのdirectoryはWRITE_ATTRIBUTES不可でも属性を変えず削除する()
    {
        var parent = Path.Combine(_root, "acl-hidden-directory");
        var child = new DirectoryInfo(Path.Combine(parent, "child"));
        child.Create();
        child.Attributes = FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System;
        var original = child.GetAccessControl().GetSecurityDescriptorBinaryForm();
        try
        {
            var restricted = child.GetAccessControl();
            restricted.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.WriteAttributes, AccessControlType.Deny));
            child.SetAccessControl(restricted);
            var result = FileCleanupEngine.Run([CleanupTarget.Contents(parent)], false, null, CancellationToken.None);
            Assert.AreEqual(1, result.DeletedDirectories, string.Join("; ", result.BlockedExamples));
            Assert.IsFalse(Directory.Exists(child.FullName));
        }
        finally { if (Directory.Exists(child.FullName)) { RestoreDacl(child, original); child.Attributes = FileAttributes.Directory; } }
    }

    [TestMethod]
    public void directory自身がDELETE非共有でも内部ファイルを掃除する()
    {
        var file = CreateFile(@"share-directory\child\normal.bin");
        var child = Path.GetDirectoryName(file)!;
        using var other = CreateFileW(child, 0x81, 3, 0, 3, 0x02200000, 0);
        Assert.IsFalse(other.IsInvalid);
        var result = FileCleanupEngine.Run([CleanupTarget.Contents(Path.GetDirectoryName(child)!)], false, null, CancellationToken.None);
        Assert.AreEqual(1, result.DeletedFiles, string.Join("; ", result.BlockedExamples));
        Assert.IsFalse(File.Exists(file));
        Assert.IsTrue(Directory.Exists(child));
        Assert.AreEqual(1, result.Blocked);
    }
    private static void RestoreDacl(FileSystemInfo entry, byte[] original)
    {
        // GetAccessControl の読取オブジェクトをそのまま Persist しても変更済とは扱われない。
        // 新規 descriptor の Access セクションを設定し、DACL の書込対象を明示する。
        FileSystemSecurity restored;
        if (entry is DirectoryInfo directory)
        {
            var security = new DirectorySecurity();
            security.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
            directory.SetAccessControl(security);
            restored = directory.GetAccessControl(AccessControlSections.Access);
        }
        else
        {
            var file = (FileInfo)entry;
            var security = new FileSecurity();
            security.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
            file.SetAccessControl(security);
            restored = file.GetAccessControl(AccessControlSections.Access);
        }

        Assert.AreEqual(new RawSecurityDescriptor(original, 0).GetSddlForm(AccessControlSections.Access),
            restored.GetSecurityDescriptorSddlForm(AccessControlSections.Access), $"テスト用 DACL の復元失敗: {entry.FullName}");
    }

    [TestMethod]
    public void lockとアクセス拒否が混在するtargetはSYSTEM再列挙へ予約しない()
    {
        var locked = CreateFile(@"mixed-access\locked.bin");
        var denied = new FileInfo(CreateFile(@"mixed-access\denied.bin"));
        var parent = denied.Directory!;
        var parentAcl = parent.GetAccessControl().GetSecurityDescriptorBinaryForm();
        var fileAcl = denied.GetAccessControl().GetSecurityDescriptorBinaryForm();
        var user = WindowsIdentity.GetCurrent().User!;
        try
        {
            var parentDenied = parent.GetAccessControl();
            parentDenied.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny));
            parent.SetAccessControl(parentDenied);
            var fileDenied = denied.GetAccessControl();
            fileDenied.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Delete, AccessControlType.Deny));
            denied.SetAccessControl(fileDenied);
            using var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
            var outcome = FileCleanupEngine.Run([CleanupTarget.Contents(parent.FullName)], true, null, CancellationToken.None);
            Assert.AreEqual(2, outcome.Blocked);
            Assert.AreEqual(1, outcome.LockBlocked);
            Assert.HasCount(0, outcome.BlockedTargets, "ACL 拒否を含む target 全体を特権で再実行しない");
            Assert.IsTrue(File.Exists(locked));
            Assert.IsTrue(File.Exists(denied.FullName));
        }
        finally
        {
            try { RestoreDacl(parent, parentAcl); }
            finally { if (File.Exists(denied.FullName)) RestoreDacl(denied, fileAcl); }
        }
    }

    [TestMethod]
    public void lockとリンク拒否が混在するtargetも予約しない()
    {
        var locked = CreateFile(@"mixed-link\locked.bin");
        var outside = CreateFile(@"link-outside\keep.txt", "keep");
        var parent = Path.GetDirectoryName(locked)!;
        CreateJunction(Path.Combine(parent, "linked"), Path.GetDirectoryName(outside)!);
        using var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        var outcome = FileCleanupEngine.Run([CleanupTarget.Contents(parent)], true, null, CancellationToken.None);
        Assert.AreEqual(1, outcome.LockBlocked);
        Assert.AreEqual(1, outcome.RejectedTargets.Count);
        Assert.HasCount(0, outcome.BlockedTargets);
        Assert.AreEqual("keep", File.ReadAllText(outside));
        Assert.IsTrue((File.GetAttributes(Path.Combine(parent, "linked")) & FileAttributes.ReparsePoint) != 0);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void bounded設定読取は保持したreadDatahandleだけを使い更新と差替えを共有拒否する(bool utf16)
    {
        var file = CreateFile(@"read-settings\settings.json");
        const string json = "{\"Locale\":\"en_US\"}";
        File.WriteAllText(file, json, utf16 ? System.Text.Encoding.Unicode : System.Text.Encoding.UTF8);
        using (var entry = NativeCleanupEntry.Open(file, false, readData: true))
        {
            Assert.AreEqual(json, entry.ReadText(1024));
            Assert.ThrowsExactly<IOException>(() => entry.ReadText(4));
            Assert.Throws<IOException>(() => File.WriteAllText(file, "replacement"));
            Assert.Throws<IOException>(() => File.Move(file, file + ".moved"));
            Assert.AreEqual(json, entry.ReadText(1024));
        }
        File.WriteAllText(file, "released");
        Assert.AreEqual("released", File.ReadAllText(file));
    }

    [TestMethod]
    public void fileのDACL復元は新規descriptorを保存して実ACLの一致を確認する()
    {
        var file = new FileInfo(CreateFile(@"acl-restore\keep.bin"));
        var original = file.GetAccessControl().GetSecurityDescriptorBinaryForm();
        try
        {
            var restricted = file.GetAccessControl();
            restricted.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.WriteAttributes, AccessControlType.Deny));
            file.SetAccessControl(restricted);
            Assert.AreNotEqual(new RawSecurityDescriptor(original, 0).GetSddlForm(AccessControlSections.Access),
                file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
        finally { RestoreDacl(file, original); }
        file.Attributes = FileAttributes.ReadOnly;
        Assert.IsTrue((file.Attributes & FileAttributes.ReadOnly) != 0);
        file.Attributes = FileAttributes.Normal;
    }

    private sealed class NoopExecutor : ICommandExecutor
    {
        public Task<CommandExecutionResult> RunAsync(string fileName, string arguments, CancellationToken ct = default, IProgress<string>? onOutputLine = null, TimeSpan? timeout = null) =>
            Task.FromResult(new CommandExecutionResult(true, string.Empty, 0, string.Empty, string.Empty));
    }

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "Lumin4tiCleanupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                DeleteTree(new DirectoryInfo(_root));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // テスト後の掃除に失敗してもテスト結果には影響させない
        }
    }

    /// <summary>
    /// ジャンクションを辿らずに消す。Directory.Delete(recursive) は再解析ポイントに
    /// 入ろうとして失敗するため、テストの後始末では使えない。
    /// \\?\ 拡張パス経由で消すため、テストが残した nul 等の予約名ファイルも掃除できる。
    /// </summary>
    private static void DeleteTree(DirectoryInfo directory)
    {
        foreach (var file in directory.GetFiles())
        {
            var extended = new FileInfo(@"\\?\" + file.FullName);
            extended.Attributes = FileAttributes.Normal;
            extended.Delete();
        }

        foreach (var subdirectory in directory.GetDirectories())
        {
            if ((subdirectory.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                DeleteTree(subdirectory);
                continue;
            }

            subdirectory.Delete(recursive: false);
        }

        directory.Attributes = FileAttributes.Directory;
        directory.Delete(recursive: false);
    }

    /// <summary>
    /// ディレクトリジャンクションを作る。シンボリックリンクと違い管理者権限も
    /// 開発者モードも不要なため、非昇格のテスト実行でも再解析ポイントを検証できる。
    /// </summary>
    private static void CreateJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();

        Assert.AreEqual(0, process.ExitCode, $"ジャンクションを作成できませんでした: {process.StandardError.ReadToEnd()}");
        Assert.AreNotEqual(
            (FileAttributes)0,
            new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint,
            "作成したジャンクションが再解析ポイントになっていません");
    }

    private string CreateFile(string relativePath, string content = "x")
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [TestMethod]
    public void 中身だけ削除する対象はフォルダ自体を残す()
    {
        var target = Path.Combine(_root, "cache");
        CreateFile(@"cache\a.tmp");
        CreateFile(@"cache\sub\b.tmp");

        var outcome = FileCleanupEngine.Run(
            [CleanupTarget.Contents(target)],
            scheduleBlockedForReboot: false,
            progress: null,
            ct: CancellationToken.None);

        Assert.IsTrue(Directory.Exists(target), "対象フォルダ自体は残す");
        Assert.AreEqual(0, Directory.GetFileSystemEntries(target).Length);
        Assert.AreEqual(2, outcome.DeletedFiles);
        Assert.AreEqual(1, outcome.DeletedDirectories);
        Assert.AreEqual(0, outcome.Blocked);
    }

    [TestMethod]
    public void パターン指定は直下の一致ファイルだけを消す()
    {
        var target = Path.Combine(_root, "outlook");
        CreateFile(@"outlook\mail.ost");
        CreateFile(@"outlook\keep.pst");
        CreateFile(@"outlook\sub\nested.ost");

        var outcome = FileCleanupEngine.Run(
            [CleanupTarget.Files(target, "*.ost")],
            scheduleBlockedForReboot: false,
            progress: null,
            ct: CancellationToken.None);

        Assert.AreEqual(1, outcome.DeletedFiles, string.Join("; ", outcome.BlockedExamples));
        Assert.IsFalse(File.Exists(Path.Combine(target, "mail.ost")));
        Assert.IsTrue(File.Exists(Path.Combine(target, "keep.pst")), "パターン外は残す");
        Assert.IsTrue(File.Exists(Path.Combine(target, "sub", "nested.ost")), "サブフォルダは辿らない");
    }

    [TestMethod]
    public void 削除候補の検出は存在だけでなく実際の中身を見る()
    {
        var contents = CleanupTarget.Contents(Path.Combine(_root, "cache"));
        var files = CleanupTarget.Files(Path.Combine(_root, "logs"), "*.etl");

        Assert.IsFalse(FileCleanupEngine.HasCleanupCandidates(contents), "未作成フォルダは検出しない");
        Directory.CreateDirectory(Path.Combine(_root, "cache"));
        Assert.IsFalse(FileCleanupEngine.HasCleanupCandidates(contents), "空フォルダは削除対象として表示しない");

        CreateFile(@"cache\data.bin");
        Assert.IsTrue(FileCleanupEngine.HasCleanupCandidates(contents));

        CreateFile(@"logs\keep.txt");
        Assert.IsFalse(FileCleanupEngine.HasCleanupCandidates(files), "パターン外のファイルだけなら検出しない");
        CreateFile(@"logs\trace.etl");
        Assert.IsTrue(FileCleanupEngine.HasCleanupCandidates(files));
    }

    [TestMethod]
    public void チェックリストは削除候補を検出したグループだけを返す()
    {
        var detected = CleanupTarget.Contents(Path.Combine(_root, "installed", "Cache"));
        var sameGroupMissing = CleanupTarget.Contents(Path.Combine(_root, "installed", "logs"));
        var missing = CleanupTarget.Contents(Path.Combine(_root, "not-installed", "Cache"));
        CreateFile(@"installed\Cache\data.bin");
        var action = new FileCleanupAction(
            "test-detected",
            "検出テスト",
            "検出テスト",
            () => [detected, sameGroupMissing, missing],
            checkListKeySelector: target => target == missing ? "未インストール" : "インストール済み");

        var entries = action.GetCheckListEntries();

        Assert.HasCount(1, entries);
        Assert.AreEqual("インストール済み", entries[0].Label);

        File.Delete(Path.Combine(_root, "installed", "Cache", "data.bin"));
        Assert.HasCount(0, action.GetCheckListEntries(), "削除後に再検出すると行も消える必要があります");
    }

    [TestMethod]
    public void 読み取り専用ファイルも削除できる()
    {
        var file = CreateFile(@"cache\readonly.tmp");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        var outcome = FileCleanupEngine.Run(
            [CleanupTarget.Contents(Path.Combine(_root, "cache"))],
            scheduleBlockedForReboot: false,
            progress: null,
            ct: CancellationToken.None);

        Assert.AreEqual(1, outcome.DeletedFiles, string.Join("; ", outcome.BlockedExamples));
        Assert.IsFalse(File.Exists(file));
    }

    [TestMethod]
    public async Task 削除できない項目が残った場合は部分成功を返す()
    {
        var file = CreateFile(@"cache\locked.tmp");
        using var lockStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        var action = new FileCleanupAction(
            "test-cleanup",
            "テスト掃除",
            "テスト用",
            () => [CleanupTarget.Contents(Path.Combine(_root, "cache"))]);

        var result = await action.ExecuteAsync();

        Assert.AreEqual(MaintenanceActionStatus.Partial, result.Status);
        Assert.IsTrue(File.Exists(file));
        StringAssert.Contains(result.Detail, "削除できませんでした");
        StringAssert.Contains(result.Detail, "使用中");
        StringAssert.Contains(result.Detail, file);
        StringAssert.Contains(result.Detail, "0x80070020");
    }

    [TestMethod]
    public void 削除に失敗したファイルとフォルダの属性を復元する()
    {
        var directory = Path.Combine(_root, "cache", "protected");
        var file = CreateFile(@"cache\protected\locked.tmp");
        var fileAttributes = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;
        var directoryAttributes = FileAttributes.Directory | FileAttributes.ReadOnly | FileAttributes.Hidden;
        File.SetAttributes(file, fileAttributes);
        File.SetAttributes(directory, directoryAttributes);

        using var lockStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        var outcome = FileCleanupEngine.Run(
            [CleanupTarget.Contents(Path.Combine(_root, "cache"))],
            scheduleBlockedForReboot: false,
            progress: null,
            ct: CancellationToken.None);

        Assert.AreEqual(1, outcome.Blocked);
        Assert.AreEqual(fileAttributes, File.GetAttributes(file));
        Assert.AreEqual(directoryAttributes, File.GetAttributes(directory));
    }

    [TestMethod]
    public void リンクになっている対象は辿らず拒否する()
    {
        // 掃除対象フォルダを別ドライブへ逃がしている環境で、リンク先の実体を消さないことの確認。
        var real = Path.Combine(_root, "real");
        Directory.CreateDirectory(real);
        var keep = CreateFile(@"real\keep.txt");
        var link = Path.Combine(_root, "link");
        CreateJunction(link, real);

        var outcome = FileCleanupEngine.Run(
            [CleanupTarget.Contents(link)],
            scheduleBlockedForReboot: false,
            progress: null,
            ct: CancellationToken.None);

        Assert.AreEqual(1, outcome.RejectedTargets.Count);
        Assert.AreEqual(0, outcome.DeletedFiles);
        Assert.IsTrue(File.Exists(keep), "リンク先の実体は残す");
    }

    [TestMethod]
    public void パターン指定でもリンクになっている対象は辿らない()
    {
        var real = Path.Combine(_root, "real");
        Directory.CreateDirectory(real);
        var keep = CreateFile(@"real\trace.etl");
        var link = Path.Combine(_root, "link");
        CreateJunction(link, real);
        var target = CleanupTarget.Files(link, "*.etl");

        Assert.IsFalse(FileCleanupEngine.HasCleanupCandidates(target));

        var outcome = FileCleanupEngine.Run(
            [target],
            scheduleBlockedForReboot: false,
            progress: null,
            ct: CancellationToken.None);

        Assert.AreEqual(1, outcome.RejectedTargets.Count);
        Assert.AreEqual(0, outcome.DeletedFiles);
        Assert.IsTrue(File.Exists(keep), "リンク先のパターン一致ファイルも残す");
    }

    [TestMethod]
    public void 三文字拡張子パターンは長い拡張子を候補にも削除対象にもしない()
    {
        var keepEtl1 = CreateFile(@"logs\trace.etl1");
        var keepEtlBackup = CreateFile(@"logs\trace.etlbackup");
        var target = CleanupTarget.Files(Path.Combine(_root, "logs"), "*.etl");

        Assert.IsFalse(FileCleanupEngine.HasCleanupCandidates(target));

        var outcome = FileCleanupEngine.Run(
            [target],
            scheduleBlockedForReboot: false,
            progress: null,
            ct: CancellationToken.None);

        Assert.AreEqual(0, outcome.DeletedFiles);
        Assert.IsTrue(File.Exists(keepEtl1));
        Assert.IsTrue(File.Exists(keepEtlBackup));
    }

    [TestMethod]
    public void 祖先がリンクになっている対象も辿らず拒否する()
    {
        var realRoot = Path.Combine(_root, "real-browser");
        var realCache = Path.Combine(realRoot, "Profile 1", "Cache");
        Directory.CreateDirectory(realCache);
        var keep = Path.Combine(realCache, "keep.bin");
        File.WriteAllText(keep, "keep");
        var linkedRoot = Path.Combine(_root, "linked-browser");
        CreateJunction(linkedRoot, realRoot);
        var target = CleanupTarget.Contents(Path.Combine(linkedRoot, "Profile 1", "Cache"));

        Assert.IsFalse(FileCleanupEngine.HasCleanupCandidates(target));

        var outcome = FileCleanupEngine.Run(
            [target],
            scheduleBlockedForReboot: false,
            progress: null,
            ct: CancellationToken.None);

        Assert.AreEqual(1, outcome.RejectedTargets.Count);
        Assert.AreEqual(0, outcome.DeletedFiles);
        Assert.IsTrue(File.Exists(keep), "祖先リンクの先にあるキャッシュは残す");
    }

    [TestMethod]
    public void 列挙用ガードの保持中は祖先をジャンクションへ差し替えられない()
    {
        var browserRoot = Path.Combine(_root, "browser-swap");
        var userData = Path.Combine(browserRoot, "User Data");
        var movedRoot = Path.Combine(_root, "browser-moved");
        var external = Path.Combine(_root, "external-browser", "User Data", "External Profile");
        var profile = Path.Combine(userData, "Default");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(external);

        using (var targets = FileCleanupGroups.EnumerateBrowserTargets([browserRoot]).GetEnumerator())
        {
            Assert.IsTrue(targets.MoveNext());
            Assert.ThrowsExactly<IOException>(() => Directory.Move(browserRoot, movedRoot));
            while (!targets.Current.RawPath.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Assert.IsTrue(targets.MoveNext(), "通常のプロファイルを列挙する必要があります");
            }

            Assert.ThrowsExactly<IOException>(() => Directory.Move(userData, Path.Combine(browserRoot, "User Data moved")));
            Assert.ThrowsExactly<IOException>(() => Directory.Move(profile, Path.Combine(userData, "Default moved")));
        }

        Directory.Move(browserRoot, movedRoot);
        CreateJunction(browserRoot, Path.GetDirectoryName(Path.GetDirectoryName(external)!)!);
        Assert.HasCount(0, FileCleanupGroups.EnumerateBrowserTargets([browserRoot]).ToArray(),
            "保持解除後に差し替えられた祖先リンクも次の列挙で拒否する");
    }

    [TestMethod]
    public async Task 実行時の対象列挙は呼出しスレッドで行わない()
    {
        var callerThread = 0;
        var providerThread = 0;
        var action = new FileCleanupAction("test-enumeration", "test", "test", () =>
        {
            providerThread = Environment.CurrentManagedThreadId;
            return [];
        });

        // 専用の呼出しスレッドを使い、await 後に同じ pool thread が再利用される揺らぎを避ける。
        var invocation = new TaskCompletionSource<Task<MaintenanceActionResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            invocation.SetResult(action.ExecuteAsync());
        });
        caller.Start();
        await await invocation.Task;
        caller.Join();

        Assert.AreNotEqual(callerThread, providerThread);
    }

    [TestMethod]
    public async Task 実行前取消では対象が空でも列挙せず取消を返す()
    {
        var calls = 0;
        var action = new FileCleanupAction("test-cancel", "test", "test", () =>
        {
            calls++;
            return [];
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.AreEqual(MaintenanceActionStatus.Canceled, (await action.ExecuteAsync(cancellation.Token)).Status);

        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task 対象列挙中の取消は空の結果を成功にしない()
    {
        using var cancellation = new CancellationTokenSource();
        var action = new FileCleanupAction("test-cancel-enumeration", "test", "test", () =>
        {
            cancellation.Cancel();
            return [];
        });

        Assert.AreEqual(MaintenanceActionStatus.Canceled, (await action.ExecuteAsync(cancellation.Token)).Status);
    }

    [TestMethod]
    public void ブラウザ列挙はリンクになっているプロファイルを対象に含めない()
    {
        var browserRoot = Path.Combine(_root, "browser");
        var userData = Path.Combine(browserRoot, "User Data");
        var externalProfile = Path.Combine(_root, "external-profile");
        Directory.CreateDirectory(userData);
        Directory.CreateDirectory(externalProfile);
        CreateJunction(Path.Combine(userData, "Profile 1"), externalProfile);

        var targets = FileCleanupGroups.EnumerateBrowserTargets([browserRoot]).ToArray();

        Assert.IsFalse(
            targets.Any(target => target.RawPath.Contains("Profile 1", StringComparison.OrdinalIgnoreCase)),
            "リンク先プロファイルのキャッシュを対象へ展開しない");
    }

    [TestMethod]
    public void リンクになっているサブフォルダはリンク自体も中身も削除しない()
    {
        var real = Path.Combine(_root, "real");
        Directory.CreateDirectory(real);
        var keep = CreateFile(@"real\keep.txt");
        var cache = Path.Combine(_root, "cache");
        Directory.CreateDirectory(cache);
        CreateJunction(Path.Combine(cache, "link"), real);

        var outcome = FileCleanupEngine.Run(
            [CleanupTarget.Contents(cache)],
            scheduleBlockedForReboot: false,
            progress: null,
            ct: CancellationToken.None);

        Assert.AreEqual(0, outcome.DeletedFiles, "リンクの先のファイルは数えない");
        Assert.AreEqual(0, outcome.DeletedDirectories, "リンク自体も削除しない");
        Assert.AreEqual(1, outcome.RejectedTargets.Count, "リンクを安全ガードで除外する");
        Assert.IsTrue(Directory.Exists(Path.Combine(cache, "link")), "利用者が作ったリンクを残す");
        Assert.IsTrue(File.Exists(keep), "リンク先の実体は残す");
    }

    [TestMethod]
    public void 存在しない対象はスキップとして数える()
    {
        var outcome = FileCleanupEngine.Run(
            [CleanupTarget.Contents(Path.Combine(_root, "not-installed-app"))],
            scheduleBlockedForReboot: false,
            progress: null,
            ct: CancellationToken.None);

        Assert.AreEqual(1, outcome.MissingTargets);
        Assert.AreEqual(0, outcome.RejectedTargets.Count);
        Assert.IsTrue(outcome.DidNothing);
    }

    [TestMethod]
    public void 解放バイト数を集計する()
    {
        CreateFile(@"cache\a.bin", new string('a', 1000));
        CreateFile(@"cache\b.bin", new string('b', 24));

        var outcome = FileCleanupEngine.Run(
            [CleanupTarget.Contents(Path.Combine(_root, "cache"))],
            scheduleBlockedForReboot: false,
            progress: null,
            ct: CancellationToken.None);

        Assert.AreEqual(1024, outcome.FreedBytes);
        Assert.AreEqual("1.0 KB", FileCleanupEngine.FormatBytes(outcome.FreedBytes));
    }

    [TestMethod]
    public void 必須サービスを停止できなければ削除を開始しない()
    {
        var suspension = new ServiceSuspension(
            new NoopExecutor(),
            stopped: [],
            failedToStop: ["wuauserv"]);

        Assert.IsFalse(FileCleanupAction.CanRunCleanup(suspension));
        Assert.IsTrue(FileCleanupAction.CanRunCleanup(null));
    }

    // ═══ 安全ガード ═══

    [TestMethod]
    public void ドライブ直下は対象にできない()
    {
        Assert.IsFalse(FileCleanupEngine.TryResolve(@"C:\", out _, out var reason));
        StringAssert.Contains(reason, "ドライブ直下");
    }

    [TestMethod]
    public void 未定義の環境変数を含むパスは拒否する()
    {
        Assert.IsFalse(
            FileCleanupEngine.TryResolve(@"%LUMIN4TI_NOT_DEFINED_VAR%\cache", out _, out var reason));
        StringAssert.Contains(reason, "環境変数");
    }

    [TestMethod]
    public void 相対パスは拒否する()
    {
        Assert.IsFalse(FileCleanupEngine.TryResolve(@"cache\temp", out _, out var reason));
        StringAssert.Contains(reason, "絶対パス");
    }

    [TestMethod]
    public void プロファイルやシステムの基点フォルダは拒否する()
    {
        string[] roots = [@"%LOCALAPPDATA%", @"%APPDATA%", @"%USERPROFILE%", @"%SystemRoot%", @"%ProgramData%"];

        foreach (var root in roots)
        {
            Assert.IsFalse(FileCleanupEngine.TryResolve(root, out _, out var reason), root);
            StringAssert.Contains(reason, "基点フォルダ", root);
        }
    }

    [TestMethod]
    public void 拒否された対象は削除されず結果に記録される()
    {
        var outcome = FileCleanupEngine.Run(
            [CleanupTarget.Contents(@"%LUMIN4TI_NOT_DEFINED_VAR%\cache")],
            scheduleBlockedForReboot: false,
            progress: null,
            ct: CancellationToken.None);

        Assert.AreEqual(1, outcome.RejectedTargets.Count);
        Assert.IsTrue(outcome.DidNothing);
    }

    // ═══ グループ定義の妥当性 ═══

    private static IEnumerable<CleanupTarget> AllStaticTargets() =>
    [
        .. FileCleanupGroups.UserTempTargets,
        .. FileCleanupGroups.SystemTempTargets,
        .. FileCleanupGroups.SystemEtlRoots.Select(root => CleanupTarget.Files(root, "*.etl")),
        .. FileCleanupGroups.AppCacheTargets,
        .. FileCleanupGroups.PackageCacheTargets,
        .. FileCleanupGroups.ShellCacheTargets,
        .. FileCleanupGroups.WindowsUpdateCacheTargets,
        .. FileCleanupGroups.OsIndexTargets,
    ];

    [TestMethod]
    public void 全ての削除対象は安全ガードを通過する()
    {
        foreach (var target in AllStaticTargets())
        {
            var allowProtectedDirectory = target.Kind == CleanupTargetKind.Files;
            Assert.IsTrue(
                FileCleanupEngine.TryResolve(target.RawPath, allowProtectedDirectory, out _, out var reason),
                $"{target.RawPath}: {reason}");
        }
    }

    [TestMethod]
    public void 削除対象はキャッシュログ一時領域の許可リストだけを使う()
    {
        string[] forbidden =
        {
            @"\uv\python", @"\WebStorage", @"\vm_bundles", @"\UnrealEngine",
            @"\.m2\repository", @"%USERPROFILE%\.cache", @"\.matplotlib", @"\.templateengine",
            @"\.omnisharp", @"\.crossnote", @"\Windows\Recent", @"\Office\Recent",
            @"\Windows\WebCache", @"\Microsoft\Internet Explorer", @"\LocalLow\webviewdata",
            @"\LocalLow\Intel", @"\Microsoft\Network\Downloader", @"\Definition Updates\Backup", @"\Scans\History",
            @"\SoftwareDistribution\DataStore", @"\catroot2", @"\Windows.old",
        };

        foreach (var target in AllStaticTargets())
        {
            if (target.Kind == CleanupTargetKind.Files)
            {
                CollectionAssert.Contains(
                    new[] { "*.etl", "IconCache.db", "FNTCACHE.DAT" },
                    target.Pattern!,
                    "ファイル名指定は既知のキャッシュまたはログファイルだけに限定します");
            }

            foreach (var path in forbidden)
            {
                Assert.IsFalse(
                    target.RawPath.Contains(path, StringComparison.OrdinalIgnoreCase),
                    $"永続データまたは広すぎる対象を検出しました: {target.RawPath}");
            }
        }
    }

    [TestMethod]
    public void npmのnpx実行環境は削除対象に含めない()
    {
        var npmTargets = FileCleanupGroups.PackageCacheTargets
            .Where(target => FileCleanupGroups.GetPackageCacheGroupName(target) == "npm")
            .Select(target => target.RawPath)
            .ToArray();

        CollectionAssert.AreEquivalent(
            new[]
            {
                @"%LOCALAPPDATA%\npm-cache\_cacache",
                @"%LOCALAPPDATA%\npm-cache\_logs",
                @"%USERPROFILE%\.npm\_cacache",
                @"%USERPROFILE%\.npm\_logs",
            },
            npmTargets);
        Assert.IsFalse(
            FileCleanupGroups.PackageCacheTargets.Any(target =>
                target.RawPath.Contains(@"\_npx", StringComparison.OrdinalIgnoreCase)),
            "npx の展開済み実行環境を削除対象へ戻さないでください");
    }

    [TestMethod]
    public void 基点フォルダを対象にできるのはファイル名指定のときだけ()
    {
        // IconCache.db / FNTCACHE.DAT のように保護フォルダ直下の単一ファイルは許可し、
        // 同じフォルダを「中身ごと」指定した場合は従来どおり拒否する。
        Assert.IsTrue(FileCleanupEngine.TryResolve(@"%LOCALAPPDATA%", allowProtectedDirectory: true, out _, out _));
        Assert.IsFalse(FileCleanupEngine.TryResolve(@"%LOCALAPPDATA%", allowProtectedDirectory: false, out _, out _));
        Assert.IsFalse(FileCleanupEngine.TryResolve(@"C:\", allowProtectedDirectory: true, out _, out var reason));
        StringAssert.Contains(reason, "ドライブ直下");
    }

    [TestMethod]
    public void 同じ対象を複数のグループが重複して持たない()
    {
        var paths = AllStaticTargets().Select(t => $"{t.RawPath}|{t.Pattern}").ToList();

        CollectionAssert.AllItemsAreUnique(paths);
    }

    [TestMethod]
    public void ETL列挙は指定した基点配下だけを対象にしてリンクを辿らない()
    {
        var root = Path.Combine(_root, "etl-root");
        var child = Path.Combine(root, "child");
        var external = Path.Combine(_root, "external");
        Directory.CreateDirectory(child);
        Directory.CreateDirectory(external);
        var rootEtl = CreateFile(@"etl-root\root.etl");
        var childEtl = CreateFile(@"etl-root\child\child.etl");
        var keepLog = CreateFile(@"etl-root\child\keep.log");
        var keepEtl1 = CreateFile(@"etl-root\child\trace.etl1");
        var keepEtlBackup = CreateFile(@"etl-root\child\trace.etlbackup");
        var linkedEtl = CreateFile(@"external\linked.etl");
        CreateJunction(Path.Combine(root, "linked"), external);

        var targets = FileCleanupGroups.EnumerateKnownTreeEtlTargets(root).ToArray();

        CollectionAssert.AreEquivalent(
            new[] { root, child },
            targets.Select(target => target.RawPath).ToArray());
        Assert.IsTrue(targets.All(target => target.Kind == CleanupTargetKind.Files));
        Assert.IsTrue(targets.All(target => target.Pattern == "*.etl"));

        var outcome = FileCleanupEngine.Run(
            targets,
            scheduleBlockedForReboot: false,
            progress: null,
            ct: CancellationToken.None);

        Assert.AreEqual(2, outcome.DeletedFiles);
        Assert.IsFalse(File.Exists(rootEtl));
        Assert.IsFalse(File.Exists(childEtl));
        Assert.IsTrue(File.Exists(keepLog), "ETL 以外のログはこの選択では残す");
        Assert.IsTrue(File.Exists(keepEtl1), ".etl1 は .etl として削除しない");
        Assert.IsTrue(File.Exists(keepEtlBackup), ".etlbackup は .etl として削除しない");
        Assert.IsTrue(File.Exists(linkedEtl), "リンク先の ETL は削除しない");
    }

    [TestMethod]
    public async Task 実ETLツリーの列挙途中で取消したら残りを走査せず削除しない()
    {
        var root = Path.Combine(_root, "etl-cancel");
        for (var index = 0; index < 16; index++)
        {
            CreateFile($@"etl-cancel\branch-{index}\leaf\trace.etl");
        }

        using var cancellation = new CancellationTokenSource();
        var observed = 0;
        IEnumerable<CleanupTarget> EnumerateTargets()
        {
            foreach (var target in FileCleanupGroups.EnumerateKnownTreeEtlTargets(root))
            {
                observed++;
                if (observed == 2)
                {
                    // 最初の子を返した時点で実走査中のハンドルが保持されている。
                    // 全ツリーを先にリスト化してから返す実装では、この検査は成立しない。
                    Assert.ThrowsExactly<IOException>(() => Directory.Move(target.RawPath, target.RawPath + "-moved"));
                    cancellation.Cancel();
                }

                yield return target;
            }
        }

        var action = new FileCleanupAction("test-etl-cancel", "test", "test", EnumerateTargets);

        Assert.AreEqual(MaintenanceActionStatus.Canceled, (await action.ExecuteAsync(cancellation.Token)).Status);

        Assert.AreEqual(2, observed, "取消後は次の ETL ディレクトリ走査へ進まない");
        Assert.HasCount(16, Directory.GetFiles(root, "*.etl", SearchOption.AllDirectories),
            "対象確定前の取消では ETL ファイルを削除しない");
        var movedRoot = Path.Combine(_root, "etl-cancel-moved");
        Directory.Move(root, movedRoot);
        Assert.IsTrue(Directory.Exists(movedRoot), "取消による列挙終了で走査用ハンドルを解放する");
    }

    [TestMethod]
    public void ETL削除は既知のWindowsログ基点とetlパターンだけを使う()
    {
        CollectionAssert.AreEquivalent(
            new[]
            {
                @"%SystemRoot%\Logs",
                @"%SystemRoot%\System32\LogFiles",
                @"%SystemRoot%\Panther",
                @"%ProgramData%\Microsoft\Diagnosis\ETLLogs",
            },
            FileCleanupGroups.SystemEtlRoots);

        var etlTargets = FileCleanupGroups.EnumerateSystemTempTargets()
            .Where(target => string.Equals(target.Pattern, "*.etl", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.AreEqual(
            FileCleanupGroups.SystemEtlRoots.Length,
            etlTargets.Count(target => target.RawPath.Contains('%')),
            "各基点を未展開パスのまま 1 件ずつ保持します");
        Assert.IsTrue(etlTargets.All(target => target.Kind == CleanupTargetKind.Files));
    }

    [TestMethod]
    public void 認証情報や設定を持つフォルダは削除対象に含めない()
    {
        // 再生成できない鍵・認証情報・アプリ設定は、キャッシュ掃除の巻き添えで消さない (回帰防止)。
        string[] forbidden =
        [
            @"\.gnupg", @"\.aws", @"\.config", @"\.ssh", @"\.codex", @"\.gemini", @"\.android",
            @"\.dotnet", @"\.ollama", @"\.local", @"\.gk", @"\.bito", @"\.affinity",
            @"\.dbus-keyrings", @"\.monica-code", @"\Documents", @"\Desktop", @"\Downloads",
        ];

        foreach (var target in AllStaticTargets())
        {
            foreach (var segment in forbidden)
            {
                Assert.IsFalse(
                    target.RawPath.Contains(segment, StringComparison.OrdinalIgnoreCase),
                    $"{target.RawPath} が保護対象 {segment} を含んでいます");
            }
        }
    }

    [TestMethod]
    public void AMDのキャッシュ削除はシェーダーキャッシュのサブフォルダに限定する()
    {
        // %LOCALAPPDATA%\AMD 全体を消すと設定・プロファイルまで巻き添えにするため、
        // 既知のシェーダーキャッシュのサブフォルダだけを対象にする (回帰防止)。
        Assert.IsFalse(
            FileCleanupGroups.AppCacheTargets.Any(t =>
                t.RawPath.Equals(@"%LOCALAPPDATA%\AMD", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(
            FileCleanupGroups.AppCacheTargets.Any(t =>
                t.RawPath.Equals(@"%LOCALAPPDATA%\AMD\DxCache", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ブラウザキャッシュの対象はサイトデータ相当を含まない()
    {
        // IndexedDB / WebStorage / File System / Extension State はログイン状態等を保持する
        // サイトデータであり、「ログイン状態は消えません」という説明と矛盾するため対象外にする (回帰防止)。
        string[] forbiddenLeafNames = ["IndexedDB", "WebStorage", "File System", "Extension State"];

        foreach (var target in FileCleanupGroups.EnumerateBrowserTargets())
        {
            var leaf = Path.GetFileName(target.RawPath);
            Assert.IsFalse(
                forbiddenLeafNames.Contains(leaf, StringComparer.OrdinalIgnoreCase),
                target.RawPath);
        }

        CollectionAssert.DoesNotContain(FileCleanupGroups.BrowserProfileCaches, "Service Worker");
        CollectionAssert.Contains(FileCleanupGroups.BrowserProfileCaches, @"Service Worker\CacheStorage");
        CollectionAssert.Contains(FileCleanupGroups.BrowserProfileCaches, @"Service Worker\ScriptCache");
        CollectionAssert.DoesNotContain(FileCleanupGroups.BrowserProfileCaches, "screen_ai");
        CollectionAssert.DoesNotContain(FileCleanupGroups.BrowserSharedCaches, @"User Data\WidevineCdm");
    }

}
