using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows.Cleanup;
using Lumin4ti.UI.Views;

namespace Lumin4ti.UI.ViewModels;

/// <summary>
/// 「通常」画面が外へ出る境界 (走査・削除・場所を開く・実行中アプリの確認・UI スレッドへの投稿)。
/// 本番は <see cref="Create"/> で実サービスへつなぎ、テストは偽物を渡して実機を変えずに画面の振る舞いを確かめる。
/// </summary>
internal sealed record CommonCleanServices(
    Func<IProgress<CleanupScanProgress>, CancellationToken, Task<CleanupScanSnapshot>> Scan,
    Func<CleanupRunRequest, IProgress<string>, CancellationToken, Task<CleanupRunResult>> Run,
    Func<CleanupScanSnapshot, bool> IsCurrent,
    Func<string, Task> OpenFolder,
    ICleanupRunPrompts Prompts,
    Action<Action> Post)
{
    public static CommonCleanServices Create(CleanupScanService scan, CleanupRunService run) => new(
        (progress, ct) => scan.ScanAsync(null, progress, ct),
        (request, progress, ct) => run.RunAsync(request, progress, ct),
        scan.IsCurrent,
        path => WindowsExplorerLauncher.OpenAsync(path),
        new CommonCleanProcessPrompts(),
        action => Dispatcher.UIThread.Post(action));
}

/// <summary>
/// 進捗の通知を一定間隔に間引いて UI スレッドへ投稿する。走査・削除の worker から高頻度に呼ばれても、
/// UI スレッドのキューを溢れさせない。
/// </summary>
internal sealed class CommonCleanProgress<T>(Action<T> handler, Action<Action> post) : IProgress<T>
{
    private const long IntervalMilliseconds = 100;

    private long _last = -IntervalMilliseconds;

    public void Report(T value)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _last) < IntervalMilliseconds)
        {
            return;
        }

        Interlocked.Exchange(ref _last, now);
        post(() => handler(value));
    }
}
