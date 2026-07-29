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
        catch (Exception ex)
        {
            // 想定外のコードページでも落ちないよう、全バイトを写像できる Latin1 を最終手段にする。
            // このフォールバックに落ちると外部コマンドの日本語出力が化けるため、必ず残す。
            OemEncoding = Encoding.Latin1;
            LoggerBootstrap.Log.Error(
                $"OEM コードページ ({CultureInfo.CurrentCulture.TextInfo.OEMCodePage}) を取得できないため Latin1 で代替します",
                ex);
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
        IProgress<string>? onOutputLine = null,
        TimeSpan? timeout = null)
    {
        var commandLine = string.IsNullOrEmpty(arguments) ? fileName : $"{fileName} {arguments}";
        // 呼び出し側が上限を指定しなければ実装既定を使う (中断コストの高いコマンドだけ延ばせる)。
        var effectiveTimeout = timeout ?? _commandTimeout;
        using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        executionCts.CancelAfter(effectiveTimeout);
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
            // 打ち切り後に残った pump が進捗を報告し続けると、完了処理でクリアした表示を上書きしてしまう。
            // 通知先をゲート越しにして、打ち切りと同時に黙らせる。
            var gatedProgress = onOutputLine is null ? null : new GatedProgress(onOutputLine);
            var readOut = PumpAsync(process.StandardOutput.BaseStream, stdoutBuffer, gatedProgress, executionToken);
            var readErr = PumpAsync(process.StandardError.BaseStream, stderrBuffer, onLine: null, executionToken);

            // 無出力のまま長時間かかるコマンド (WU 待ちの DISM 等) でも生存が分かるようにする。
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(executionToken);
            var heartbeat = ReportHeartbeatAsync(gatedProgress, commandLine, heartbeatCts.Token);
            try
            {
                await process.WaitForExitAsync(executionToken).ConfigureAwait(false);
            }
            finally
            {
                await heartbeatCts.CancelAsync().ConfigureAwait(false);
                await heartbeat.ConfigureAwait(false);
            }

            var pumps = Task.WhenAll(readOut, readErr);
            try
            {
                await pumps.WaitAsync(_outputDrainGrace, executionToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 孫プロセスがパイプを保持している。既に終了済みのコマンドの結果は確定しているので先へ進む。
                gatedProgress?.Close();
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
                $"コマンドが {FormatTimeout(effectiveTimeout)} でタイムアウトしました");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CommandExecutionResult(false, commandLine, -1, string.Empty, ex.Message);
        }
    }

    /// <summary>プロセス終了後に残りの出力を回収するための既定の猶予。</summary>
    private static readonly TimeSpan DefaultOutputDrainGrace = TimeSpan.FromSeconds(5);

    /// <summary>出力が無いまま続くコマンドの生存を知らせる間隔。</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 進捗を出さないコマンドが長引くと「固まった」のか「時間がかかっている」のか区別できない。
    /// 出力の有無にかかわらず経過時間を通知して、待ってよい状態であることを示す。
    /// </summary>
    private static async Task ReportHeartbeatAsync(
        IProgress<string>? progress,
        string commandLine,
        CancellationToken ct)
    {
        if (progress is null)
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            using var timer = new PeriodicTimer(HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                progress.Report($"実行中... ({Stopwatch.GetElapsedTime(started).TotalMinutes:F0} 分経過)");
            }
        }
        catch (OperationCanceledException)
        {
            // プロセス終了に追随して止まるだけなので通知は不要。
        }
    }

    /// <summary>
    /// 打ち切り後に取り残された pump からの進捗通知を止めるためのゲート。
    /// Close 後の Report は捨てるので、完了処理でクリアした表示が後から復活しない。
    /// </summary>
    private sealed class GatedProgress(IProgress<string> inner) : IProgress<string>
    {
        private volatile bool _closed;

        public void Close() => _closed = true;

        public void Report(string value)
        {
            if (!_closed)
            {
                inner.Report(value);
            }
        }
    }

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
            // 出力の途中で打ち切った場合 (回収の猶予切れ・バッファ上限) は末尾のマルチバイト列が
            // 分断される。末尾だけの破断で全文を OEM 扱いにすると、UTF-8 出力が丸ごと化けるため、
            // 不完全な末尾を落としてもう一度 UTF-8 として解釈できるかを先に試す。
            var trimmed = TrimIncompleteUtf8Tail(bytes);
            if (trimmed != bytes.Length)
            {
                try
                {
                    return StrictUtf8.GetString(bytes, 0, trimmed);
                }
                catch (DecoderFallbackException)
                {
                    // 末尾以外にも不正バイトがある = そもそも UTF-8 ではない。
                }
            }

            return OemEncoding.GetString(bytes);
        }
    }

    /// <summary>
    /// 末尾にある「途中で切れた UTF-8 シーケンス」を除いた長さを返す。完結していれば元の長さ。
    /// UTF-8 のマルチバイトは最大 4 バイトなので、後ろ 3 バイトまで見れば判定できる。
    /// </summary>
    private static int TrimIncompleteUtf8Tail(byte[] bytes)
    {
        for (var offset = 1; offset <= 3 && offset <= bytes.Length; offset++)
        {
            var b = bytes[^offset];
            if ((b & 0b1100_0000) == 0b1000_0000)
            {
                // 継続バイト。先頭バイトを探して遡る。
                continue;
            }

            // 先頭バイトの示す長さより手前で切れていれば、そのシーケンスごと落とす。
            var expected = b switch
            {
                >= 0b1111_0000 => 4,
                >= 0b1110_0000 => 3,
                >= 0b1100_0000 => 2,
                _ => 1,
            };
            return expected > offset ? bytes.Length - offset : bytes.Length;
        }

        return bytes.Length;
    }
}
