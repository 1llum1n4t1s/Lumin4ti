using System.Runtime.Versioning;
using Lumin4ti.Core.Interfaces;

namespace Lumin4ti.Core.Services.Windows.Cleanup.Handlers;

/// <summary>
/// DNS リゾルバーキャッシュを <c>ipconfig /flushdns</c> で消す。
/// 実行は <see cref="ICommandExecutor"/> を通し、<c>SystemProcessResolver</c> のフルパス解決を外さない。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DnsCacheHandler(ICommandExecutor executor) : ICleanupHandler
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public string Id => "dnsCache";

    public IReadOnlyList<string> Services => [];

    public Task<CleanupHandlerScan> ScanAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new CleanupHandlerScan(CleanupHandlerScanState.Unmeasurable));
    }

    public async Task<CleanupHandlerResult> RunAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return new CleanupHandlerResult(CleanupHandlerStatus.Canceled);
        }

        progress?.Report("  - DNS キャッシュを消去しています");
        try
        {
            var result = await executor.RunAsync("ipconfig.exe", "/flushdns", ct, timeout: Timeout);
            return result.Success
                ? new CleanupHandlerResult(CleanupHandlerStatus.Succeeded)
                : CleanupHandlerResult.Failed(CleanupMessage.Of("DnsFlushFailed", CommandFailureDiagnostic.Format(result)));
        }
        catch (OperationCanceledException)
        {
            return new CleanupHandlerResult(CleanupHandlerStatus.Canceled);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return CleanupHandlerResult.Failed(CleanupMessage.Of("DnsFlushError", CommandFailureDiagnostic.Sanitize(ex.Message)));
        }
    }
}
