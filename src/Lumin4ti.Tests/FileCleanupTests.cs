using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.Core.Services.Windows.Actions;
using System.Security.AccessControl;
using System.Security.Principal;
using CleanupExclusionMatcher = Lumin4ti.Core.Services.Windows.Cleanup.CleanupExclusionMatcher;
using CleanupScanOptions = Lumin4ti.Core.Services.Windows.Cleanup.CleanupScanOptions;
using FileCleanupScanner = Lumin4ti.Core.Services.Windows.Cleanup.FileCleanupScanner;

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

        Assert.AreEqual(WithoutAutoInheritFlags(new RawSecurityDescriptor(original, 0).GetSddlForm(AccessControlSections.Access)),
            WithoutAutoInheritFlags(restored.GetSecurityDescriptorSddlForm(AccessControlSections.Access)), $"テスト用 DACL の復元失敗: {entry.FullName}");
    }

    /// <summary>
    /// DACL の SDDL から自動継承の制御フラグ (AI / AR) だけを除く。
    /// SetAccessControl は継承を再計算して AI を付け直すため、元 DACL に AI が無い環境
    /// (GitHub Actions の runner の一時フォルダー等) では ACE が同一でも "D:(" と "D:AI(" で不一致になる。
    /// 保護フラグ (P) と ACE 列はそのまま比較し、復元の検出能力は落とさない。
    /// </summary>
    private static string WithoutAutoInheritFlags(string? sddl)
    {
        ArgumentNullException.ThrowIfNull(sddl);
        var start = sddl.IndexOf("D:", StringComparison.Ordinal);
        if (start < 0) return sddl;
        var flagsStart = start + 2;
        var aceStart = sddl.IndexOf('(', flagsStart);
        var flagsEnd = aceStart < 0 ? sddl.Length : aceStart;
        var flags = sddl[flagsStart..flagsEnd].Replace("AI", string.Empty, StringComparison.Ordinal)
            .Replace("AR", string.Empty, StringComparison.Ordinal);
        return string.Concat(sddl.AsSpan(0, flagsStart), flags, sddl.AsSpan(flagsEnd));
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
        .. FileCleanupGroups.ShellCacheTargets,
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
                    new[] { "IconCache.db", "FNTCACHE.DAT" },
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

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFile, string existingFile, nint security);

    [TestMethod]
    public void 列挙はファイルを開かずにサイズと各時刻を返す()
    {
        var file = CreateFile(@"detail\a.bin", "12345678");
        File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var info = new FileInfo(file);

        using var directory = NativeCleanupEntry.Open(Path.GetDirectoryName(file)!, true);
        var entry = directory.EnumerateDetailed().Single();

        Assert.AreEqual("a.bin", entry.Name);
        Assert.AreEqual(8, entry.Size);
        Assert.AreEqual(info.CreationTimeUtc.ToFileTimeUtc(), entry.CreationTime);
        Assert.AreEqual(info.LastWriteTimeUtc.ToFileTimeUtc(), entry.LastWriteTime);
        Assert.AreEqual(info.LastAccessTimeUtc.ToFileTimeUtc(), entry.LastAccessTime);
    }

    [TestMethod]
    public void 除外はRetainedに数え親フォルダーの削除失敗をBlockedにしない()
    {
        var keep = CreateFile(@"ex\keep.dat");
        var keepInner = CreateFile(@"ex\sub\keep2.dat");
        CreateFile(@"ex\drop.dat");
        CreateFile(@"ex\sub\drop2.dat");
        CreateFile(@"ex\Skip\inner.dat");
        var options = new CleanupRunOptions(new CleanupExclusionMatcher([keep, keepInner], [Path.Combine(_root, @"ex\Skip")], null));

        var outcome = FileCleanupEngine.Run([CleanupTarget.Contents(Path.Combine(_root, "ex"))], false, null, CancellationToken.None, options);

        Assert.AreEqual(2, outcome.DeletedFiles);
        Assert.AreEqual(3, outcome.Retained, "keep.dat・keep2.dat・Skip フォルダー");
        Assert.AreEqual(0, outcome.Blocked, string.Join("; ", outcome.BlockedExamples));
        Assert.IsEmpty(outcome.RejectedTargets);
        Assert.IsTrue(File.Exists(keep) && File.Exists(keepInner));
        Assert.IsTrue(File.Exists(Path.Combine(_root, @"ex\Skip\inner.dat")));
        Assert.IsFalse(File.Exists(Path.Combine(_root, @"ex\drop.dat")));
        Assert.IsTrue(FileCleanupEngine.DescribeOutcome(outcome).Any(line => line.Contains("3 個の項目を残しました")));
    }

    [TestMethod]
    public void 基点フォルダー自体の除外は何も消さずRetainedに数える()
    {
        var file = CreateFile(@"base\a.dat");
        var options = new CleanupRunOptions(new CleanupExclusionMatcher(null, [Path.Combine(_root, "base")], null));

        var outcome = FileCleanupEngine.Run([CleanupTarget.Contents(Path.Combine(_root, "base"))], false, null, CancellationToken.None, options);

        Assert.AreEqual(1, outcome.Retained);
        Assert.AreEqual(0, outcome.DeletedFiles);
        Assert.IsTrue(File.Exists(file));
    }

    [TestMethod]
    public void 走査後に作られたファイルとフォルダーは削除せず残す()
    {
        CreateFile(@"late\old1.dat");
        CreateFile(@"late\sub\old2.dat");
        var path = Path.Combine(_root, "late");
        // 走査直前 50 ms 以内に作ったものは走査後扱いで残るため、既存ファイルを十分古くする。
        Thread.Sleep(150);
        var scan = FileCleanupScanner.Scan(CleanupTarget.Contents(path), null, CancellationToken.None);
        Thread.Sleep(30);
        var newFile = CreateFile(@"late\new.dat");
        var newInner = CreateFile(@"late\newdir\inner.dat");

        var outcome = FileCleanupEngine.Run([CleanupTarget.Contents(path)], false, null, CancellationToken.None,
            new CleanupRunOptions(CreatedBefore: scan.CreatedBefore));

        Assert.AreEqual(2, scan.Files);
        Assert.AreEqual(2, outcome.DeletedFiles);
        Assert.AreEqual(2, outcome.Retained, "新しいファイルと新しいフォルダー");
        Assert.AreEqual(0, outcome.Blocked, string.Join("; ", outcome.BlockedExamples));
        Assert.IsTrue(File.Exists(newFile) && File.Exists(newInner));
        Assert.IsFalse(File.Exists(Path.Combine(path, "old1.dat")));
        Assert.IsFalse(Directory.Exists(Path.Combine(path, "sub")));
    }

    [TestMethod]
    public void 絞り込み削除は古いファイルだけ消し空になったフォルダーだけ片付ける()
    {
        var old = CreateFile(@"age\sub\old.tmp");
        var mixed = CreateFile(@"age\mixed\old2.tmp");
        var fresh = CreateFile(@"age\mixed\fresh.tmp");
        var freshTop = CreateFile(@"age\fresh2.tmp");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-40));
        File.SetLastWriteTimeUtc(mixed, DateTime.UtcNow.AddDays(-40));

        var outcome = FileCleanupEngine.Run(
            [CleanupTarget.Files(Path.Combine(_root, "age"), "*.tmp", recurse: true, removeEmptyDirs: true, olderThanDays: 30)],
            false, null, CancellationToken.None);

        Assert.AreEqual(2, outcome.DeletedFiles);
        Assert.AreEqual(1, outcome.DeletedDirectories, "空になった sub だけ消える");
        Assert.AreEqual(0, outcome.Blocked, string.Join("; ", outcome.BlockedExamples));
        Assert.IsTrue(File.Exists(fresh) && File.Exists(freshTop));
        Assert.IsTrue(Directory.Exists(Path.GetDirectoryName(fresh)!));
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(old)!));
    }

    [TestMethod]
    public void 走査時と同一の実体だけを削除し差替え更新された実体は残す()
    {
        var same = CreateFile(@"id\same.dat", "AAAA");
        var appended = CreateFile(@"id\appended.dat", "BBBB");
        var replaced = CreateFile(@"id\replaced.dat", "CCCC");
        var touched = CreateFile(@"id\touched.dat", "DDDD");
        var path = Path.Combine(_root, "id");
        var scan = FileCleanupScanner.Scan(CleanupTarget.Contents(path), new CleanupScanOptions(CollectIdentities: true), CancellationToken.None);
        var replacedTime = File.GetLastWriteTimeUtc(replaced);
        File.AppendAllText(appended, "more");
        File.Delete(replaced);
        File.WriteAllText(replaced, "CCCC");
        File.SetLastWriteTimeUtc(replaced, replacedTime);
        File.SetLastWriteTimeUtc(touched, File.GetLastWriteTimeUtc(touched).AddMinutes(5));

        var outcome = FileCleanupEngine.DeleteVerified(scan.Identities, null, CancellationToken.None);

        Assert.HasCount(4, scan.Identities);
        Assert.AreEqual(1, outcome.DeletedFiles);
        Assert.AreEqual(3, outcome.Retained, "サイズ変更・実体差替え・更新時刻変更");
        Assert.AreEqual(0, outcome.Blocked);
        Assert.IsFalse(File.Exists(same));
        Assert.AreEqual("CCCC", File.ReadAllText(replaced));
        Assert.AreEqual("BBBBmore", File.ReadAllText(appended));
        Assert.IsTrue(File.Exists(touched));
    }

    [TestMethod]
    public void 同一性照合の削除は走査後に祖先がjunctionへ差し替わってもリンク先を消さない()
    {
        var inner = CreateFile(@"vs\sub\f.dat", "data");
        var outside = CreateFile(@"vs-outside\f.dat", "data");
        var scan = FileCleanupScanner.Scan(CleanupTarget.Contents(Path.Combine(_root, "vs")), new CleanupScanOptions(CollectIdentities: true), CancellationToken.None);
        var sub = Path.GetDirectoryName(inner)!;
        File.Delete(inner);
        Directory.Delete(sub);
        CreateJunction(sub, Path.GetDirectoryName(outside)!);

        var outcome = FileCleanupEngine.DeleteVerified(scan.Identities, null, CancellationToken.None);

        Assert.AreEqual(0, outcome.DeletedFiles);
        Assert.AreEqual(1, outcome.Blocked + outcome.Retained);
        Assert.AreEqual("data", File.ReadAllText(outside));
        Directory.Delete(sub);
    }

    [TestMethod]
    public void 上書き削除はハードリンクと読取専用を上書きせず通常ファイルだけ上書きする()
    {
        var linked = CreateFile(@"wipe\linked.dat", "ABCDEF");
        var other = Path.Combine(_root, @"wipe-other\link.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        Assert.IsTrue(CreateHardLinkW(other, linked, 0), $"ハードリンクを作れません: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        CreateFile(@"wipe\plain.dat", "plain data");
        var readOnly = CreateFile(@"wipe\readonly.dat", "locked");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        CreateFile(@"wipe\empty.dat", string.Empty);

        var outcome = FileCleanupEngine.Run([CleanupTarget.Contents(Path.Combine(_root, "wipe"))], false, null, CancellationToken.None,
            new CleanupRunOptions(Wipe: true));

        Assert.AreEqual(4, outcome.DeletedFiles);
        Assert.AreEqual(1, outcome.WipedFiles, "上書きできるのは plain.dat だけ");
        Assert.AreEqual(2, outcome.WipeSkipped, "リンク数 2 と読取専用は削除だけ。空ファイルは数えない");
        Assert.AreEqual(0, outcome.Blocked, string.Join("; ", outcome.BlockedExamples));
        Assert.IsFalse(File.Exists(linked));
        Assert.AreEqual("ABCDEF", File.ReadAllText(other), "他のリンク名の内容は壊さない");
    }

    [TestMethod]
    public void 上書きは開いた書込みhandleの全バイトを0にして取消では途中で止まる()
    {
        var file = Path.Combine(_root, @"zero\big.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, Enumerable.Repeat((byte)0xAB, (1 << 20) + 123).ToArray());

        using (var readOnly = NativeCleanupEntry.Open(file, false))
            Assert.ThrowsExactly<InvalidOperationException>(() => readOnly.OverwriteWithZeros(10, CancellationToken.None));
        using (var cancelled = new CancellationTokenSource())
        using (var entry = NativeCleanupEntry.Open(file, false, write: true))
        {
            cancelled.Cancel();
            Assert.IsFalse(entry.OverwriteWithZeros(entry.Length, cancelled.Token));
        }
        Assert.IsTrue(File.ReadAllBytes(file).All(value => value == 0xAB), "取消済みなら 1 バイトも書かない");
        using (var entry = NativeCleanupEntry.Open(file, false, write: true))
            Assert.IsTrue(entry.OverwriteWithZeros(entry.Length, CancellationToken.None));
        Assert.AreEqual((1 << 20) + 123, new FileInfo(file).Length);
        Assert.IsTrue(File.ReadAllBytes(file).All(value => value == 0));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void オプション付きの実行でも対象自体をjunction化してもリンク先を保持する(bool files)
    {
        var target = Path.Combine(_root, "opt-inplace");
        Directory.CreateDirectory(target);
        var outside = CreateFile(@"opt-outside\canary.etl", "external data");
        var progress = new SynchronousProgress(_ => CreateJunctionInPlace(target, Path.GetDirectoryName(outside)!));
        var options = new CleanupRunOptions(new CleanupExclusionMatcher(null, null, [".bak"]), DateTime.UtcNow, Wipe: true);

        var outcome = FileCleanupEngine.Run(
            [files ? CleanupTarget.Files(target, "*.etl", recurse: true) : CleanupTarget.Contents(target)], false, progress, CancellationToken.None, options);

        Assert.AreEqual(0, outcome.DeletedFiles);
        Assert.AreEqual(0, outcome.WipedFiles);
        Assert.IsTrue(outcome.Blocked > 0 || outcome.RejectedTargets.Count > 0);
        Assert.AreEqual("external data", File.ReadAllText(outside));
        Assert.IsTrue((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0, "リンク自身も保持する");
    }

    [TestMethod]
    public void リンクを含む対象はオプション付きでもリンクとリンク先を保持する()
    {
        CreateFile(@"lk\real.dat");
        var outside = CreateFile(@"lk-outside\secret.dat", "secret");
        var junction = Path.Combine(_root, "lk", "junction");
        CreateJunction(junction, Path.GetDirectoryName(outside)!);

        var outcome = FileCleanupEngine.Run([CleanupTarget.Contents(Path.Combine(_root, "lk"))], false, null, CancellationToken.None,
            new CleanupRunOptions(Wipe: true));

        Assert.AreEqual(1, outcome.DeletedFiles);
        Assert.HasCount(1, outcome.RejectedTargets);
        Assert.AreEqual("secret", File.ReadAllText(outside));
        Assert.IsTrue(Directory.Exists(junction));
        Directory.Delete(junction);
    }

}
