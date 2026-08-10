using System.Runtime.Versioning;
using Lumin4ti.Core.Services.Windows.Actions;

namespace Lumin4ti.Core.Services.Windows;

/// <summary>
/// タスクスケジューラーから起動されたときに %TEMP% と迷子の nul ファイルを削除して即終了する
/// 専用エントリポイント。<see cref="Actions.ScheduledTempCleanupToggle"/> が登録するタスクは、
/// この引数を付けて自分自身 (Lumin4ti.exe) を呼び出す。通常の起動フロー (自己昇格・多重起動ガード・UI) を
/// 経由しないため、ログオンのたびに UAC を出さず無人で完走できる。
/// 手動実行だと nul ファイルを作った開発ツール (Git Bash 等) がまだファイルを掴んでいて消せないことがあるが、
/// ログオン直後ならそれらのプロセスは起動していないことが多く、削除が成功しやすい。
/// </summary>
[SupportedOSPlatform("windows")]
public static class ScheduledTempCleanup
{
    /// <summary>
    /// この引数を検知したら、呼び出し元 (Program.Main) は通常の起動フローに入らず
    /// <see cref="Run"/> だけ行って終了する。
    /// </summary>
    public const string CommandLineArgument = "--scheduled-cleanup-temp";

    /// <summary>%TEMP% の中身と迷子の nul ファイルを削除し、結果をログへ残す。例外は握りつぶして終了コードへ畳み込む。</summary>
    public static int Run()
    {
        try
        {
            var outcome = FileCleanupEngine.Run(
                [CleanupTarget.Contents(@"%TEMP%"), .. FileCleanupGroups.NulFileTargets],
                scheduleBlockedForReboot: false,
                progress: null,
                ct: CancellationToken.None);

            LoggerBootstrap.Log.Info(
                $"scheduled-cleanup-temp: files={outcome.DeletedFiles} dirs={outcome.DeletedDirectories} " +
                $"bytes={outcome.FreedBytes} blocked={outcome.Blocked} rejected={outcome.RejectedTargets.Count}");
            return 0;
        }
        catch (Exception ex)
        {
            LoggerBootstrap.Log.Error("scheduled-cleanup-temp: 失敗しました", ex);
            return 1;
        }
    }
}
