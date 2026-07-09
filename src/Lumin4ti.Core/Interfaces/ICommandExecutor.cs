using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Interfaces;

/// <summary>
/// 外部プロセスを実行する最下層の抽象。アプリ自体が管理者権限で起動しているため、
/// 子プロセスもそのまま昇格状態を継承する。
/// </summary>
/// <remarks>
/// arguments は組み立て済みの 1 本の文字列で渡す (配列にしない)。
/// reg / dism / powershell -Command はそれぞれ独自のクォート解釈を持つため、
/// カタログ側が対象コマンドの流儀に合わせて正しくクォートした文字列を保持する。
/// </remarks>
public interface ICommandExecutor
{
    /// <param name="fileName">実行ファイル。</param>
    /// <param name="arguments">組み立て済み引数文字列。</param>
    /// <param name="ct">キャンセル用トークン。</param>
    /// <param name="onOutputLine">
    /// 指定すると標準出力を 1 行受信するたびに呼ばれる (長時間コマンドのライブ進捗表示用)。
    /// null なら完了時にまとめて返すだけ。
    /// </param>
    Task<CommandExecutionResult> RunAsync(
        string fileName,
        string arguments,
        CancellationToken ct = default,
        IProgress<string>? onOutputLine = null);
}
