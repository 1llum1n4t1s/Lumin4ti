using System.Diagnostics;
using Lumin4ti.Core.Services.Windows;
using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.UI.Services;
using Lumin4ti.UI.ViewModels;

namespace Lumin4ti.Verification;

/// <summary>
/// LUMIN4TI_PREVIEW_SELFCHECK のときだけ使う。高度タブへ「合成フォルダーの外」をカスタムフォルダーとして与えても、
/// 走査の対象が 0 件で、外のファイルが 1 つも消えないことを確かめる。
/// 「外のファイル」は <c>%TEMP%</c> の下にこの検査が自分で作る別の一時フォルダーに置き、終わったら消す (実データは使わない)。
/// </summary>
internal static class PreviewEscapeCheck
{
    public static async Task<bool> RunAsync(MaintenanceActionCatalog catalog, PreviewSandbox sandbox, PreviewToggles toggles, Func<MaintenanceOperationCoordinator> coordinators)
    {
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var id = Guid.NewGuid().ToString("N")[..8];
        var outside = Path.Combine(temp, "lumin4ti-preview-outside-" + id);
        var junction = Path.Combine(sandbox.Root, "junction-out");
        var childLink = Path.Combine(sandbox.CustomFolder, "child-link");
        var links = new List<string>();
        var ok = true;
        try
        {
            Directory.CreateDirectory(Path.Combine(outside, "sub"));
            for (var i = 1; i <= 5; i++) WriteOld(Path.Combine(outside, $"outside-{i}.tmp"), 2048 * i);
            WriteOld(Path.Combine(outside, "sub", "deep.tmp"), 4096);
            WriteOld(Path.Combine(outside, "sub", "deep.dmp"), 4096);
            var before = Snapshot(outside);

            if (!MakeJunction(junction, outside, links) || !MakeJunction(childLink, outside, links))
            {
                Console.WriteLine("PREVIEW escape-check: ジャンクションを作れなかったため、リンクの検査は未実施 (FAIL)");
                return false;
            }

            // ---- 検査 1: 外を指すものだけを与える。走査の対象は 0 件でなければならない ----
            var other = DriveInfo.GetDrives().Select(d => d.Name)
                .FirstOrDefault(n => !n.StartsWith(Path.GetPathRoot(sandbox.Root)![..1], StringComparison.OrdinalIgnoreCase)) ?? @"Z:\";
            var tampered = new[]
            {
                Path.Combine(other, "lumin4ti-preview-escape-" + id),
                @"Z:\lumin4ti-preview-escape-" + id,
                @"\\127.0.0.1\lumin4ti-preview-escape\share",
                @"\\?\" + outside,
                outside,
                Path.Combine(outside, "sub"),
                Path.Combine(sandbox.CustomFolder, "..", "..", Path.GetFileName(outside)),
                junction,
                Path.Combine(junction, "sub"),
            };

            var settings = new MemorySettings();
            var vm = PreviewCleanupFactory.Create(catalog, settings, coordinators(), sandbox, toggles, background: true);
            var store = vm.Settings.Settings;
            var guard = vm.Advanced.GuardContext!;
            var addAccepted = tampered.Count(p => store.AddCustomFolder(p, guard).IsAllowed);
            var guardAllowed = tampered.Count(p => CustomFolderGuard.Check(p, guard).IsAllowed);
            var cleanup = settings.Current.Cleanup ??= new();
            foreach (var path in tampered) cleanup.CustomFolders.Add(path); // 設定ファイルを直接書き換えられた場合
            var listed = store.GetCustomFolders(guard).Count;
            await vm.Advanced.ScanCommand.ExecuteAsync(null);
            var scannedFolders = vm.Advanced.CustomFolders.Count;
            vm.Advanced.SelectAllCommand.Execute(null);
            vm.Advanced.RequestDeleteCommand.Execute(null);
            await vm.Advanced.ConfirmDeleteCommand.ExecuteAsync(null);
            var after1 = Snapshot(outside);
            var intact1 = before.SequenceEqual(after1);
            var pass1 = listed == 0 && scannedFolders == 0 && intact1;
            Console.WriteLine(
                $"PREVIEW escape-check 1: tampered={tampered.Length} (他ドライブ/UNC/\\\\?\\/外/..経由/ジャンクション) store.Add受理={addAccepted} 製品の検査だけで許可={guardAllowed} " +
                $"読まれた一覧={listed} 走査したカスタムフォルダー={scannedFolders} 外のファイル {before.Count}→{after1.Count} 無傷={intact1} => {(pass1 ? "PASS" : "FAIL")}");
            ok &= pass1;

            // ---- 検査 2: 合成フォルダーの中のカスタムフォルダー (中に外を指すリンクがある) は走査・削除され、リンクの先は無傷 ----
            var settings2 = new MemorySettings();
            var vm2 = PreviewCleanupFactory.Create(catalog, settings2, coordinators(), sandbox, toggles, background: true);
            var legit = vm2.Settings.Settings.AddCustomFolder(sandbox.CustomFolder, vm2.Advanced.GuardContext!);
            await vm2.Advanced.ScanCommand.ExecuteAsync(null);
            var folder = vm2.Advanced.CustomFolders.FirstOrDefault();
            var scanned2 = vm2.Advanced.CustomFolders.Count;
            var filesBefore = Directory.EnumerateFiles(sandbox.CustomFolder, "*.dat").Count();
            var conclusive = false;
            if (folder is { IsSelectable: true })
            {
                folder.IsSelected = true;
                vm2.Advanced.RequestDeleteCommand.Execute(null);
                conclusive = vm2.Advanced.IsConfirming;
                if (conclusive) await vm2.Advanced.ConfirmDeleteCommand.ExecuteAsync(null);
            }

            var filesAfter = Directory.EnumerateFiles(sandbox.CustomFolder, "*.dat").Count();
            var after2 = Snapshot(outside);
            var intact2 = before.SequenceEqual(after2);
            var pass2 = legit.IsAllowed && scanned2 == 1 && conclusive && filesAfter < filesBefore && intact2;
            Console.WriteLine(
                $"PREVIEW escape-check 2: 合成フォルダー内のカスタムフォルダー 許可={legit.IsAllowed} 走査={scanned2} 削除まで実行={conclusive} " +
                $"中のファイル {filesBefore}→{filesAfter} 外のファイル {before.Count}→{after2.Count} 無傷={intact2} => {(pass2 ? "PASS" : "FAIL")}");
            ok &= pass2;
        }
        catch (Exception ex)
        {
            Console.WriteLine("PREVIEW escape-check failed: " + ex);
            ok = false;
        }
        finally
        {
            Cleanup(links, outside, temp);
        }

        Console.WriteLine($"PREVIEW escape-check => {(ok ? "PASS" : "FAIL")}");
        return ok;
    }

    private static void WriteOld(string path, int size)
    {
        File.WriteAllBytes(path, new byte[size]);
        var old = DateTime.UtcNow.AddDays(-60);
        File.SetCreationTimeUtc(path, old);
        File.SetLastWriteTimeUtc(path, old);
    }

    private static List<string> Snapshot(string folder) =>
        [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => $"{f}|{new FileInfo(f).Length}").OrderBy(s => s, StringComparer.OrdinalIgnoreCase)];

    private static bool MakeJunction(string link, string target, List<string> links)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        var made = Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
        if (made) links.Add(link);
        return made;
    }

    /// <summary>自分が作ったジャンクション (リンクだけ) と、外のフォルダー (名前と場所を確かめたもの) を消す。</summary>
    private static void Cleanup(List<string> links, string outside, string temp)
    {
        foreach (var link in links)
        {
            try
            {
                if (Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0) Directory.Delete(link, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine("PREVIEW escape-check: ジャンクションを消せませんでした " + link);
            }
        }

        if (string.Equals(Path.GetDirectoryName(outside), temp, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(outside).StartsWith("lumin4ti-preview-outside-", StringComparison.Ordinal) &&
            Directory.Exists(outside) && (File.GetAttributes(outside) & FileAttributes.ReparsePoint) == 0)
        {
            try
            {
                Directory.Delete(outside, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine("PREVIEW escape-check: 外のフォルダーを消せませんでした " + outside);
            }
        }
    }
}
