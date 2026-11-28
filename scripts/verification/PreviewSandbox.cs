namespace Lumin4ti.Verification;

/// <summary>
/// クリーンアップ画面のプレビュー専用の一時フォルダー。起動時に <c>%TEMP%</c> の下へ合成ファイルを作り、終了時に消す。
/// 高度・スリム化の走査と削除はこのフォルダーの外へ届かない (起点・カスタムフォルダーの検査・スリム化の場所をここへ固定する)。
/// </summary>
internal sealed class PreviewSandbox : IDisposable
{
    public const string Prefix = "lumin4ti-preview-";

    private readonly List<FileStream> _locks = [];
    private readonly DateTime _now = DateTime.UtcNow;
    private int _seed = 7;

    private PreviewSandbox(string root) => Root = root;

    public string Root { get; }

    /// <summary>使用中として開いたままにしている、高度タブの合成ファイル (削除が失敗に数えられる)。</summary>
    public string LockedAdvancedFile => Path.Combine(Root, "advanced", "Locked", "in-use.tmp");

    /// <summary>高度タブの走査の起点 (ドライブの代わり)。</summary>
    public string AdvancedRoot => Path.Combine(Root, "advanced") + Path.DirectorySeparatorChar;

    /// <summary>カスタムフォルダーとして追加してよい場所 (設定タブで追加して確認する用)。</summary>
    public string CustomFolder => Path.Combine(Root, "custom");

    public string SlimWindows => Path.Combine(Root, "slim", "Windows");

    public string SlimPublic => Path.Combine(Root, "slim", "Public");

    public string InstallerDirectory => Path.Combine(SlimWindows, "Installer");

    /// <summary>インストール済みの登録として扱う (孤児にしない) Installer のファイル。</summary>
    public string RegisteredInstallerFile => Path.Combine(InstallerDirectory, "registered-product.msi");

    /// <summary>パスが (字面の上で) このフォルダーの中か。合成ファイルを作るときの検査。リンクは見ない。</summary>
    public bool Contains(string path)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 走査の起点・カスタムフォルダーとして渡してよいか。合成フォルダーの中 (自身を含む) に正規化され、
    /// かつ途中にリンク・ジャンクション (再解析ポイント) が無いものだけが真。
    /// UNC・デバイス形式 (<c>\\?\</c>)・相対パス・<c>..</c> を含むもの・別ドライブ・外を指すリンクは偽。
    /// 存在しない部分は検査できないため、見つかった祖先だけを調べる (存在しないものは何も走査・削除しない)。
    /// </summary>
    public bool IsInside(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var raw = path.Trim();
        if (raw.StartsWith(@"\\", StringComparison.Ordinal) || raw.StartsWith("//", StringComparison.Ordinal)) return false;
        if (!Path.IsPathFullyQualified(raw) || raw.Split('\\', '/').Contains("..")) return false;
        string full;
        try
        {
            full = Path.GetFullPath(raw).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var sameAsRoot = string.Equals(full, Root, StringComparison.OrdinalIgnoreCase);
        if (!sameAsRoot && !full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;

        // 合成フォルダー自身までの各階層がリンクでないこと (リンクなら実体は外にありうる)。
        for (var current = full; current.Length >= Root.Length; current = Path.GetDirectoryName(current) ?? string.Empty)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // まだ無い階層は、リンクでもない。
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 全ドライブの直下と、合成フォルダーのあるドライブの直下 (製品の検査で「利用者プロファイルを束ねるフォルダー」として渡し、
    /// 合成フォルダーの外を一律に拒否させるため)。ドライブの中身は読まない。
    /// </summary>
    public static IEnumerable<string> AllDriveRoots(PreviewSandbox sandbox) =>
        DriveInfo.GetDrives().Select(d => d.Name)
            .Append(Path.GetPathRoot(sandbox.Root)!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    public static PreviewSandbox Create()
    {
        // TEMP / TMP は上書きしない。ふだんの一時置き場の下に、衝突しない名前で作る。
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.Combine(temp, Prefix + Guid.NewGuid().ToString("N")[..8]);
        if (Directory.Exists(root)) throw new IOException("preview sandbox already exists: " + root);
        Directory.CreateDirectory(root);
        var sandbox = new PreviewSandbox(root);
        sandbox.Populate();
        return sandbox;
    }

    private void Populate()
    {
        PopulateAdvanced();
        PopulateSlim();
        File.WriteAllText(
            Path.Combine(Root, "README.txt"),
            "Lumin4ti クリーンアップ画面のプレビュー専用の合成ファイルです。プレビューの終了時に削除されます。" + Environment.NewLine);
        Directory.CreateDirectory(CustomFolder);
        for (var i = 1; i <= 6; i++) Write(Path.Combine(CustomFolder, $"custom-file-{i:00}.dat"), 4096 * i, 3);
    }

    private void PopulateAdvanced()
    {
        var root = Path.Combine(Root, "advanced");
        var temp = Path.Combine(root, "Users", "preview", "AppData", "Local", "Temp");
        for (var i = 1; i <= 40; i++) Write(Path.Combine(temp, $"~DF{i:X4}.tmp"), 1024 * (1 + i % 9), 45 + i);
        Write(Path.Combine(temp, "setup.log"), 30_000, 90);
        Write(Path.Combine(temp, "setup-old.log.old"), 12_000, 400);
        Write(Path.Combine(root, "Docs", "~$budget.xlsx"), 165, 20);
        Write(Path.Combine(root, "Docs", "report.docx.bak"), 80_000, 200);
        Write(Path.Combine(root, "Pictures", "Thumbs.db"), 24_576, 120);
        Write(Path.Combine(root, "Pictures", "family", "Thumbs.db"), 12_288, 120);
        Write(Path.Combine(root, "CrashDumps", "app-1.dmp"), 600_000, 60);
        Write(Path.Combine(root, "CrashDumps", "app-2.mdmp"), 420_000, 75);
        Write(Path.Combine(root, "Build", "obj", "main.pch"), 90_000, 150);
        Write(Path.Combine(root, "Build", "obj", "link.ilk"), 40_000, 150);
        Write(Path.Combine(root, "FOUND.000", "FILE0001.CHK"), 8_192, 500);
        Write(Path.Combine(root, "data", "recovery.chk"), 16_384, 300);
        Write(Path.Combine(root, "zero", "empty-1.dat"), 0, 100);
        Write(Path.Combine(root, "zero", "empty-2.txt"), 0, 100);
        Write(Path.Combine(root, "Recent", "fresh.tmp"), 2_048, 0);
        Write(Path.Combine(root, "Archive", "old-1.dat"), 700_000, 700);
        Write(Path.Combine(root, "Archive", "old-2.dat"), 300_000, 500);

        // 走査で枝刈りされる場所 (件数に載らない)。
        Write(Path.Combine(root, "Projects", ".git", "objects", "x.tmp"), 512, 10);
        Write(Path.Combine(root, "Credentials", "token.tmp"), 256, 10);

        // 使用中のファイル (削除が部分失敗になる)。プレビューの終了まで開いたままにする。
        // 既定の名前パターン (*.tmp) に当たる名前にする (*.log は既定オフで、走査の対象にも全選択にも入らない)。
        var locked = Path.Combine(root, "Locked", "in-use.tmp");
        Write(locked, 5_000, 30);
        _locks.Add(new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None));

        // 一覧の仮想化の確認用 (数百行)。
        for (var i = 1; i <= 300; i++) Write(Path.Combine(root, "Cache", $"cache-{i:0000}.tmp"), 512 + _seed++ % 7 * 700, 31 + i % 40);
    }

    private void PopulateSlim()
    {
        // Windows 側は偽の %SystemRoot%。実際の C:\Windows には一切触れない。
        Write(RegisteredInstallerFile, 3_000_000, 200);
        for (var i = 1; i <= 5; i++) Write(Path.Combine(InstallerDirectory, $"orphan-{i}.{(i % 2 == 0 ? "msp" : "msi")}"), 1_500_000 + i * 90_000, 120 + i);
        Write(Path.Combine(InstallerDirectory, "fresh-setup.msi"), 400_000, 0);
        Write(Path.Combine(InstallerDirectory, "$PatchCache$", "Managed", "A1B2", "patch-1.dat"), 800_000, 90);
        Write(Path.Combine(InstallerDirectory, "$PatchCache$", "Managed", "A1B2", "patch-2.dat"), 600_000, 90);
        for (var i = 1; i <= 6; i++) Write(Path.Combine(SlimWindows, "SoftwareDistribution", "Download", $"update-{i}.cab"), 700_000 + i * 50_000, 40);
        var locked = Path.Combine(SlimWindows, "SoftwareDistribution", "Download", "locked-in-use.tmp");
        Write(locked, 90_000, 40);
        _locks.Add(new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None));
        for (var i = 1; i <= 4; i++) Write(Path.Combine(SlimWindows, "Help", $"preview-help-{i}.chm"), 250_000, 800);
        Write(Path.Combine(SlimWindows, "Web", "Wallpaper", "Windows", "img0.jpg"), 900_000, 800);
        Write(Path.Combine(SlimWindows, "Web", "Screen", "img100.jpg"), 500_000, 800);

        Write(Path.Combine(SlimPublic, "Music", "Sample Music", "Kalimba.mp3"), 2_000_000, 3000);
        Write(Path.Combine(SlimPublic, "Music", "Sample Music", "Sleep Away.mp3"), 1_800_000, 3000);
        Write(Path.Combine(SlimPublic, "Pictures", "Sample Pictures", "Desert.jpg"), 800_000, 3000);
        Write(Path.Combine(SlimPublic, "Pictures", "Sample Pictures", "Koala.jpg"), 700_000, 3000);
    }

    private void Write(string path, int size, int ageDays)
    {
        if (!Contains(path)) throw new InvalidOperationException("outside the preview sandbox: " + path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var block = new byte[8192];
            Array.Fill(block, (byte)'x');
            for (var left = size; left > 0; left -= block.Length) stream.Write(block, 0, Math.Min(block.Length, left));
        }

        var time = _now.AddDays(-ageDays);
        File.SetCreationTimeUtc(path, time);
        File.SetLastWriteTimeUtc(path, time);
    }

    public void Dispose()
    {
        foreach (var held in _locks) held.Dispose();
        _locks.Clear();
        // 自分が作った名前・場所のときだけ消す (リンクは辿らない)。
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(Root);
        if (!string.Equals(parent, temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(Root).StartsWith(Prefix, StringComparison.Ordinal) ||
            !Directory.Exists(Root) ||
            (File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0)
        {
            return;
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("preview sandbox was not removed: " + Root);
        }
    }
}
