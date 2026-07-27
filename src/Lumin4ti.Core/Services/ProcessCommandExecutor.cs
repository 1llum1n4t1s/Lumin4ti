using System.Diagnostics;
using System.Globalization;
using System.Text;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Models;

namespace Lumin4ti.Core.Services;

/// <summary>
/// 外部プロセスをそのまま起動する既定の ICommandExecutor (Shisui と同実装)。
/// </summary>
public class ProcessCommandExecutor : ICommandExecutor
{
    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromHours(1);
    private readonly TimeSpan _commandTimeout;
    private readonly TimeSpan _outputDrainGrace;

    // 厳密 UTF-8 (不正バイトで例外)。CodePages プロバイダに依存しないので静的初期化順の問題も無い。
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // OEM コードページ (日本語 Windows なら CP932)。UTF-8 デコード失敗時のフォールバック用。
    private static readonly Encoding OemEncoding;

    static ProcessCommandExecutor()
    {
        // .NET Core 既定では CP932 等のレガシーコードページが未登録なので、フォールバック用に登録する。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            OemEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception)
        {
            // 想定外のコードページでも落ちないよう、全バイトを写像できる Latin1 を最終手段にする。
            OemEncoding = Encoding.Latin1;
        }
    }

    public ProcessCommandExecutor() : this(DefaultCommandTimeout)
    {
    }

    internal ProcessCommandExecutor(TimeSpan commandTimeout, TimeSpan? outputDrainGrace = null)
    {
        if (commandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(commandTimeout));
        }

        _commandTimeout = commandTimeout;
        _outputDrainGrace = outputDrainGrace ?? DefaultOutputDrainGrace;
    }

    public async Task<CommandExecutionResult> RunAsync(
        string fileName,
        string arguments,
        CancellationToken ct = default,
        IProgress<string>? onOutputLine = null)
    {
        var commandLine = string.IsNullOrEmpty(arguments) ? fileName : $"{fileName} {arguments}";
        using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        executionCts.CancelAfter(_commandTimeout);
        var executionToken = executionCts.Token;

        try
        {
            executionToken.ThrowIfCancellationRequested();
            // 解決失敗も CommandExecutionResult.Fail として呼び出し側へ返す。
            // bare exe 名を System32 等の確定パスへ解決する (バイナリプランティング LPE 対策)。
            var psi = new ProcessStartInfo(SystemProcessResolver.Resolve(fileName))
            {
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // 対話プロンプトが出ても入力待ちで固まらないよう stdin を閉じる
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.SystemDirectory,
            };

            using var process = new Process { StartInfo = psi };
            process.Start();
            process.StandardInput.Close();

            // アプリ終了時に子プロセスを OS に自動終了させ、孤児化を防ぐ
            ProcessJobTracker.Track(process.Handle);

            // キャンセル時はプロセスツリーごと確実に終了させる (WaitForExitAsync の例外だけでは
            // 起動済みの子プロセスが残るため)
            await using var ctRegistration = executionToken.Register(() =>
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // 既に終了済み等は無視
                }
            });

            // 生バイトで受け取ってから自前でデコードする。dism / reg 等の出力は環境によって
            // UTF-8 だったり OEM コードページ (日本語 = CP932) だったりするため。
            // 両ストリームを並行して読み、片方のバッファが詰まるデッドロックを避ける。
            // 起動したプロセスが終了しても、出力パイプを継承した孫プロセス (dism の DismHost 等) が
            // 生き残ると EOF が来ない。EOF 待ちを先に置くと、実処理が数秒で終わっていても
            // こちらが待ち続けてしまうため、プロセス終了を先に待ち、出力の回収には猶予を設ける。
            var stdoutBuffer = new ConcurrentByteBuffer();
            var stderrBuffer = new ConcurrentByteBuffer();
            var readOut = PumpAsync(process.StandardOutput.BaseStream, stdoutBuffer, onOutputLine, executionToken);
            var readErr = PumpAsync(process.StandardError.BaseStream, stderrBuffer, onLine: null, executionToken);

            await process.WaitForExitAsync(executionToken).ConfigureAwait(false);

            var pumps = Task.WhenAll(readOut, readErr);
            var drained = await Task.WhenAny(pumps, Task.Delay(_outputDrainGrace, executionToken)).ConfigureAwait(false);
            if (drained != pumps)
            {
                // 孫プロセスがパイプを保持している。既に終了済みのコマンドの結果は確定しているので先へ進む。
                LoggerBootstrap.Log.Info(
                    $"出力の回収が {FormatTimeout(_outputDrainGrace)} で完了しなかったため打ち切りました: {commandLine}");
            }

            // キャンセル登録がプロセスを終了すると、出力の drain と WaitForExitAsync が
            // 先に正常完了することがある。この場合も timeout / 呼出元キャンセルとして扱う。
            executionToken.ThrowIfCancellationRequested();

            return new CommandExecutionResult(
                process.ExitCode == 0,
                commandLine,
                process.ExitCode,
                DecodeConsoleOutput(stdoutBuffer.Snapshot()).TrimEnd(),
                DecodeConsoleOutput(stderrBuffer.Snapshot()).TrimEnd());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CommandExecutionResult(
                false,
                commandLine,
                -1,
                string.Empty,
                $"コマンドが {FormatTimeout(_commandTimeout)} でタイムアウトしました");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CommandExecutionResult(false, commandLine, -1, string.Empty, ex.Message);
        }
    }

    /// <summary>プロセス終了後に残りの出力を回収するための既定の猶予。</summary>
    private static readonly TimeSpan DefaultOutputDrainGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 回収を打ち切った後も pump が書き込み続ける可能性があるため、書き込みとスナップショットを
    /// 同じロックで直列化する。MemoryStream 自体はスレッドセーフではない。
    /// </summary>
    internal sealed class ConcurrentByteBuffer : MemoryStream
    {
        private readonly Lock _gate = new();

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                base.Write(buffer, offset, count);
            }
        }

        public override long Length
        {
            get
            {
                lock (_gate)
                {
                    return base.Length;
                }
            }
        }

        public byte[] Snapshot()
        {
            lock (_gate)
            {
                return ToArray();
            }
        }
    }

    private static string FormatTimeout(TimeSpan timeout) => timeout < TimeSpan.FromMinutes(1)
        ? $"{timeout.TotalSeconds:0.#} 秒"
        : $"{timeout.TotalMinutes:0.#} 分";

    /// <summary>
    /// 標準出力を全量バッファへ蓄積しつつ、行区切り (\n または \r: winget/dism は \r で
    /// プログレス行を書き換える) を検出するたびにデコードして onLine へ通知する。
    /// </summary>
    // 蓄積するコンソール出力の上限 (winget/dism/defrag の想定出力は数MB以内。
    // 異常に冗長な出力でメモリが青天井に伸びるのを防ぐため頭 8MB で打ち切る)。
    private const int MaxBufferedBytes = 8 * 1024 * 1024;
    internal const int MaxProgressLineBytes = 64 * 1024;
    private const string TruncatedProgressSuffix = " … [長すぎる出力を省略]";

    private static void AppendCapped(MemoryStream buffer, byte[] chunk, int count)
    {
        var remaining = MaxBufferedBytes - (int)buffer.Length;
        if (remaining > 0)
        {
            buffer.Write(chunk, 0, Math.Min(count, remaining));
        }
    }

    /// <summary>
    /// 回収を打ち切った後にプロセスとストリームが破棄されると、残った pump の読み取りが
    /// 例外になる。結果は既に確定しているので、EOF と同じ扱いで静かに終える。
    /// </summary>
    private static async ValueTask<int> ReadSafelyAsync(Stream source, byte[] chunk, CancellationToken ct)
    {
        try
        {
            return await source.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return 0;
        }
    }

    internal static async Task PumpAsync(
        Stream source,
        MemoryStream buffer,
        IProgress<string>? onLine,
        CancellationToken ct)
    {
        var chunk = new byte[4096];
        var lineBytes = new byte[MaxProgressLineBytes];
        var lineLength = 0;
        var lineWasTruncated = false;
        int read;

        if (onLine is null)
        {
            // 進捗通知不要でも上限付きで読む
            while ((read = await ReadSafelyAsync(source, chunk, ct).ConfigureAwait(false)) > 0)
            {
                AppendCapped(buffer, chunk, read);
            }

            return;
        }

        while ((read = await ReadSafelyAsync(source, chunk, ct).ConfigureAwait(false)) > 0)
        {
            AppendCapped(buffer, chunk, read);
            for (var i = 0; i < read; i++)
            {
                var b = chunk[i];
                if (b is (byte)'\n' or (byte)'\r')
                {
                    FlushLine(lineBytes, ref lineLength, ref lineWasTruncated, onLine);
                }
                else if (lineLength < lineBytes.Length)
                {
                    lineBytes[lineLength++] = b;
                }
                else
                {
                    // 区切りまでは読み捨ててパイプを必ず drain する。通知用の1行だけで
                    // 8MB の全体上限を迂回してメモリが増え続けることを防ぐ。
                    lineWasTruncated = true;
                }
            }
        }

        FlushLine(lineBytes, ref lineLength, ref lineWasTruncated, onLine);
    }

    private static void FlushLine(
        byte[] lineBytes,
        ref int lineLength,
        ref bool lineWasTruncated,
        IProgress<string> onLine)
    {
        if (lineLength == 0 && !lineWasTruncated)
        {
            return;
        }

        var line = DecodeConsoleOutput(lineBytes.AsSpan(0, lineLength).ToArray()).Trim();
        if (lineWasTruncated)
        {
            line += TruncatedProgressSuffix;
        }

        lineLength = 0;
        lineWasTruncated = false;
        if (line.Length > 0)
        {
            onLine.Report(line);
        }
    }

    /// <summary>
    /// まず厳密 UTF-8 として解釈し、不正バイトがあれば OEM コードページにフォールバックする。
    /// </summary>
    internal static string DecodeConsoleOutput(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return OemEncoding.GetString(bytes);
        }
    }
}
