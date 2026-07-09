using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services.Windows.Actions;

/// <summary>
/// エクスプローラーのクイックアクセスにピン留めしたフォルダを名前の昇順に並べ替える。
/// PowerShell を使わず Shell.Application COM を C# (dynamic ディスパッチ) で直接操作する。
/// COM の verb 操作はメッセージポンプ前提のため専用 STA スレッドで実行する。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class QuickAccessSortAction : IMaintenanceAction
{
    private const string QuickAccessNamespace = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";

    public string Id => "quick-access-sort";

    public string Label => "クイックアクセスのピン留めをソート";

    public string Description => "クイックアクセスにピン留めしたフォルダを名前の昇順に並べ替えます (実行前にジャンプリストをバックアップします)。";

    public CommandCategory Category => CommandCategory.Organize;

    public bool RequiresReboot => false;

    public bool IsLongRunning => true;

    public Task<MaintenanceActionResult> ExecuteAsync(CancellationToken ct = default) => RunOnStaThreadAsync(Execute, ct);

    private MaintenanceActionResult Execute(CancellationToken ct)
    {
        var lines = new List<string>();

        var pinned = GetPinnedPaths();
        lines.Add($"  - 検出: ピン留め {pinned.Count} 件");
        if (pinned.Count <= 1)
        {
            lines.Add("  - 対象が1件以下のためスキップ");
            return MaintenanceActionResult.Ok(lines);
        }

        var sorted = pinned.Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (pinned.SequenceEqual(sorted, StringComparer.Ordinal))
        {
            lines.Add("  - 既に昇順のためスキップ");
            return MaintenanceActionResult.Ok(lines);
        }

        var backupPath = BackupJumpList(lines);

        // 全て外してからソート順にピン留めし直す
        UnpinAll(ct);
        Thread.Sleep(600);

        var ok = 0;
        var failedPaths = new List<string>();
        foreach (var path in sorted)
        {
            ct.ThrowIfCancellationRequested();
            if (TryPin(path))
            {
                ok++;
            }
            else
            {
                failedPaths.Add(path);
            }

            Thread.Sleep(200);
        }

        // STA スレッドがまだ生きているうちに、生成した Shell.Application COM (RCW) を確実に
        // ファイナライズさせる (スレッド消滅後だと解放経路が不確実なため)。
        GC.Collect();
        GC.WaitForPendingFinalizers();

        lines.Add($"  - 並び替え完了: 成功 {ok} / 失敗 {failedPaths.Count} / 対象 {sorted.Count}");

        if (failedPaths.Count > 0)
        {
            lines.Add("  - 以下は自動で再ピン留めできませんでした。エクスプローラーで各フォルダを右クリックし「クイックアクセスにピン留め」で戻せます:");
            foreach (var path in failedPaths)
            {
                lines.Add($"      • {path}");
            }

            if (backupPath is not null)
            {
                lines.Add($"  - 元のピン留め状態はバックアップ済みです。うまくいかない場合は次のファイルを元の名前に戻してエクスプローラーを再起動してください: {backupPath}");
            }
        }
        else if (backupPath is not null)
        {
            lines.Add($"  - 念のためバックアップを作成しました: {backupPath}");
        }

        LoggerBootstrap.Log.Info($"{Id}: 成功 {ok} / 失敗 {failedPaths.Count}");
        return MaintenanceActionResult.Ok(lines);
    }

    /// <summary>現在ピン留めされている項目のパスを表示順で返す (「ピン留めを外す」verb を持つもの)。</summary>
    private static List<string> GetPinnedPaths()
    {
        dynamic shell = CreateShell();
        dynamic? ns = shell.NameSpace(QuickAccessNamespace);
        if (ns is null)
        {
            return [];
        }

        var paths = new List<string>();
        foreach (dynamic item in ns.Items())
        {
            if (FindVerb(item.Verbs(), wantUnpin: true) is not null)
            {
                paths.Add((string)item.Path);
            }
        }

        return paths;
    }

    private static void UnpinAll(CancellationToken ct)
    {
        // 1 件外すごとに Namespace を取り直す (アイテムコレクションが古くなるため)
        for (var i = 0; i < 250; i++)
        {
            ct.ThrowIfCancellationRequested();
            dynamic shell = CreateShell();
            dynamic? ns = shell.NameSpace(QuickAccessNamespace);
            if (ns is null)
            {
                return;
            }

            dynamic? unpinVerb = null;
            foreach (dynamic item in ns.Items())
            {
                unpinVerb = FindVerb(item.Verbs(), wantUnpin: true);
                if (unpinVerb is not null)
                {
                    break;
                }
            }

            if (unpinVerb is null)
            {
                return;
            }

            try
            {
                unpinVerb.DoIt();
            }
            catch (Exception ex)
            {
                LoggerBootstrap.Log.Error("ピン留め解除に失敗しました", ex);
            }

            Thread.Sleep(150);
        }
    }

    private static bool TryPin(string folderPath)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            dynamic shell = CreateShell();
            dynamic? folder = shell.NameSpace(folderPath);
            if (folder?.Self is not null)
            {
                if (FindVerb(folder.Self.Verbs(), wantUnpin: false) is { } pinVerb)
                {
                    try
                    {
                        pinVerb.DoIt();
                        return true;
                    }
                    catch (Exception ex)
                    {
                        LoggerBootstrap.Log.Error($"ピン留めに失敗しました: {folderPath}", ex);
                    }
                }
            }

            Thread.Sleep(300);
        }

        return false;
    }

    private static dynamic CreateShell() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")
            ?? throw new InvalidOperationException("Shell.Application COM が利用できません。"))!;

    // 述語をデリゲートで渡すと dynamic 実引数との組み合わせで CS1976/CS1977 になるため bool で分岐する
    private static dynamic? FindVerb(dynamic verbs, bool wantUnpin)
    {
        foreach (dynamic verb in verbs)
        {
            var name = (string?)verb.Name;
            if (wantUnpin ? IsUnpinVerb(name) : IsPinVerb(name))
            {
                return verb;
            }
        }

        return null;
    }

    // verb 名は OS 言語依存 (日本語 / 英語のみ対応。元 PowerShell 実装と同じ判定)
    internal static bool IsPinVerb(string? name) =>
        name is not null && ((name.Contains("ピン留め") && !name.Contains("外す")) || name.Contains("Pin to"));

    internal static bool IsUnpinVerb(string? name) =>
        name is not null && ((name.Contains("ピン留め") && name.Contains("外す")) || name.Contains("Unpin from"));

    private static string? BackupJumpList(List<string> lines)
    {
        var jumpList = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Recent\AutomaticDestinations\f01b4d95cf55d32a.automaticDestinations-ms");
        if (!File.Exists(jumpList))
        {
            return null;
        }

        var backup = jumpList + ".lumin4tibak";
        try
        {
            File.Copy(jumpList, backup, overwrite: true);
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lines.Add("  - ジャンプリストのバックアップに失敗しました (処理は続行)");
            return null;
        }
    }

    private static Task<MaintenanceActionResult> RunOnStaThreadAsync(
        Func<CancellationToken, MaintenanceActionResult> work, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<MaintenanceActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(work(ct));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
