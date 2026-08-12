using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Tests;

[TestClass]
public sealed class FileCleanupTests
{
    private string _root = string.Empty;

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

    /// <summary>
    /// nul 等の予約デバイス名と同じ名前のファイルを作る。通常の File.WriteAllText は
    /// デバイス名として解釈されて書き込めないため、\\?\ 拡張パス経由で作成する。
    /// </summary>
    private string CreateReservedNameFile(string relativePath, string content = "x")
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(@"\\?\" + full, content);
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
    public void フォルダごと削除する対象は対象自体も消す()
    {
        var target = Path.Combine(_root, "leftover");
        CreateFile(@"leftover\a.tmp");

        var outcome = FileCleanupEngine.Run(
            [CleanupTarget.Remove(target)],
            scheduleBlockedForReboot: false,
            progress: null,
            ct: CancellationToken.None);

        Assert.IsFalse(Directory.Exists(target));
        Assert.AreEqual(1, outcome.DeletedFiles);
        Assert.AreEqual(1, outcome.DeletedDirectories);
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

        Assert.AreEqual(1, outcome.DeletedFiles);
        Assert.IsFalse(File.Exists(Path.Combine(target, "mail.ost")));
        Assert.IsTrue(File.Exists(Path.Combine(target, "keep.pst")), "パターン外は残す");
        Assert.IsTrue(File.Exists(Path.Combine(target, "sub", "nested.ost")), "サブフォルダは辿らない");
    }

    [TestMethod]
    public void 再帰探索は全階層のパターン一致ファイルを削除する()
    {
        CreateFile("a.tmp");
        CreateFile(@"sub\a.tmp");
        CreateFile(@"sub\deeper\a.tmp");
        CreateFile(@"sub\keep.txt");

        var outcome = FileCleanupEngine.Run(
            [CleanupTarget.RecursiveFiles(_root, "a.tmp")],
            scheduleBlockedForReboot: false,
            progress: null,
            ct: CancellationToken.None);

        Assert.AreEqual(3, outcome.DeletedFiles);
        Assert.IsTrue(File.Exists(Path.Combine(_root, "sub", "keep.txt")), "パターン外は残す");
    }

    [TestMethod]
    public void 再帰探索でもリンクのサブフォルダは辿らない()
    {
        var real = Path.Combine(_root, "real");
        Directory.CreateDirectory(real);
        var keep = CreateFile(@"real\a.tmp");
        var cache = Path.Combine(_root, "cache");
        Directory.CreateDirectory(cache);
        CreateJunction(Path.Combine(cache, "link"), real);

        // 探索起点を cache に絞る (real を _root 直下に置くと、リンクを介さず直接到達できてしまうため)。
        var outcome = FileCleanupEngine.Run(
            [CleanupTarget.RecursiveFiles(cache, "a.tmp")],
            scheduleBlockedForReboot: false,
            progress: null,
            ct: CancellationToken.None);

        Assert.AreEqual(0, outcome.DeletedFiles, "リンク先の実体は辿らない");
        Assert.IsTrue(File.Exists(keep), "リンク先の実体は残す");
    }

    [TestMethod]
    public void 再帰探索は予約デバイス名と同じ名前のファイルも削除できる()
    {
        var nulDir = Path.Combine(_root, "sub");
        CreateReservedNameFile(@"sub\nul", "junk");
        CreateFile(@"sub\keep.txt");

        var outcome = FileCleanupEngine.Run(
            [CleanupTarget.RecursiveFiles(_root, "nul")],
            scheduleBlockedForReboot: false,
            progress: null,
            ct: CancellationToken.None);

        Assert.AreEqual(1, outcome.DeletedFiles);
        Assert.AreEqual(4, outcome.FreedBytes);
        Assert.IsFalse(Directory.GetFiles(nulDir, "nul").Length > 0, "nul ファイルは削除されている");
        Assert.IsTrue(File.Exists(Path.Combine(nulDir, "keep.txt")), "パターン外は残す");
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

        Assert.AreEqual(1, outcome.DeletedFiles);
        Assert.IsFalse(File.Exists(file));
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
    public void リンクになっているサブフォルダはリンクだけ消して中身を辿らない()
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
        Assert.AreEqual(1, outcome.DeletedDirectories, "リンク自体は削除する");
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
        .. FileCleanupGroups.AppCacheTargets,
        .. FileCleanupGroups.PackageCacheTargets,
        .. FileCleanupGroups.ShellCacheTargets,
        .. FileCleanupGroups.WindowsUpdateCacheTargets,
        .. FileCleanupGroups.OsIndexTargets,
        .. FileCleanupGroups.DriveRootLeftoverTargets,
        .. FileCleanupGroups.OutlookOfflineCacheTargets,
        .. FileCleanupGroups.NulFileTargets,
    ];

    [TestMethod]
    public void 全ての削除対象は安全ガードを通過する()
    {
        foreach (var target in AllStaticTargets())
        {
            var allowProtectedDirectory = target.Kind is CleanupTargetKind.Files or CleanupTargetKind.RecursiveFiles;
            var allowDriveRoot = target.Kind == CleanupTargetKind.RecursiveFiles;
            Assert.IsTrue(
                FileCleanupEngine.TryResolve(target.RawPath, allowProtectedDirectory, allowDriveRoot, out _, out var reason),
                $"{target.RawPath}: {reason}");
        }
    }

    [TestMethod]
    public void 最近使ったファイルの履歴は実体パスの履歴ショートカットだけを消す()
    {
        // %USERPROFILE%\Recent は Windows 標準のジャンクションで、リンク先の実体を消さない
        // ガードに毎回弾かれる。説明文が約束している履歴を実際に消すには実体を直接指定する。
        // ただし同じフォルダの AutomaticDestinations にはクイックアクセスのピン留め
        // (再生成できない利用者データ) が入るため、中身ごとの削除にはしない。
        var recent = FileCleanupGroups.UserTempTargets
            .Where(t => t.RawPath.Contains(@"Windows\Recent", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.HasCount(1, recent);
        Assert.AreEqual(@"%APPDATA%\Microsoft\Windows\Recent", recent[0].RawPath);
        Assert.AreEqual(CleanupTargetKind.Files, recent[0].Kind, "中身ごと消すとピン留めを巻き込みます");
        Assert.AreEqual("*.lnk", recent[0].Pattern);
        Assert.IsFalse(
            FileCleanupGroups.UserTempTargets.Any(
                t => t.RawPath.Equals(@"%USERPROFILE%\Recent", StringComparison.OrdinalIgnoreCase)),
            "ジャンクションを対象にすると毎回リンク保護で除外され、履歴が消えません");
    }

    [TestMethod]
    public void ドライブ直下の残骸は説明に列挙した名前だけを消す()
    {
        // このグループだけはフォルダを丸ごと消すため、説明に無い名前が黙って増えると
        // 利用者が把握できないまま実データを失う (復元手段も無い)。
        var description = FileCleanupGroups.CreateDriveRootLeftovers().Description;

        foreach (var target in FileCleanupGroups.DriveRootLeftoverTargets)
        {
            var name = Path.GetFileName(target.RawPath.TrimEnd('\\'));
            StringAssert.Contains(description, name, $"{name} が説明に列挙されていません");
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
    public void ドライブ直下を対象にできるのは再帰ファイル名指定のときだけ()
    {
        // nul のような迷子ファイルをドライブ全体から拾うため、RecursiveFiles だけドライブ直下を許可する。
        // 一致した個々のファイルしか触らないため、フォルダを丸ごと消す指定より安全。
        Assert.IsTrue(
            FileCleanupEngine.TryResolve(@"C:\", allowProtectedDirectory: true, allowDriveRoot: true, out _, out _));
        Assert.IsFalse(
            FileCleanupEngine.TryResolve(@"C:\", allowProtectedDirectory: true, allowDriveRoot: false, out _, out var reason));
        StringAssert.Contains(reason, "ドライブ直下");
    }

    [TestMethod]
    public void パターン指定の対象だけがパターンを持つ()
    {
        foreach (var target in AllStaticTargets())
        {
            if (target.Kind is CleanupTargetKind.Files or CleanupTargetKind.RecursiveFiles)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(target.Pattern), target.RawPath);
            }
            else
            {
                Assert.IsNull(target.Pattern, target.RawPath);
            }
        }
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
    }

    [TestMethod]
    public void グループは全てクリーンアップカテゴリの実行型項目になる()
    {
        var groups = FileCleanupGroups.CreateAll(new NoopExecutor()).ToList();

        Assert.AreEqual(12, groups.Count);
        foreach (var group in groups)
        {
            Assert.IsInstanceOfType<IMaintenanceAction>(group, group.Id);
            Assert.AreEqual(CommandCategory.Cleanup, group.Category, group.Id);
            StringAssert.StartsWith(group.Id, "cleanup-", group.Id);
        }
    }

    [TestMethod]
    public void ブラウザキャッシュの対象はインストール済みブラウザだけを列挙する()
    {
        // 実機に何が入っているかに依存しないよう、生成された対象がすべて実在することだけを確認する。
        foreach (var target in FileCleanupGroups.EnumerateBrowserTargets())
        {
            Assert.AreEqual(CleanupTargetKind.Contents, target.Kind);
            Assert.IsTrue(Path.IsPathFullyQualified(target.RawPath), target.RawPath);
        }
    }
}
