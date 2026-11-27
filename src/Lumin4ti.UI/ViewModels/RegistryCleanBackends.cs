using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumin4ti.Core.Interfaces;
using Lumin4ti.Core.Services;
using Lumin4ti.Core.Services.Windows.Cleanup.RegistryClean;
using Lumin4ti.UI.Services;

namespace Lumin4ti.UI.ViewModels;

/// <summary>レジストリクリーナーの走査・削除の境界 (ViewModel のテストでは偽物に差し替える)。</summary>
internal interface IRegistryCleanBackend
{
    /// <summary>全カテゴリーを走査し、結果を現在の世代として登録して返す。</summary>
    Task<RegistryScanResult> ScanAsync(CancellationToken ct);

    Task<RegistryCleanRunResult> CleanAsync(RegistryCleanRequest request, CancellationToken ct);

    /// <summary>走査結果の世代を失効させる。</summary>
    void Invalidate();
}

/// <summary>バックアップ 1 件の一覧表示用の情報。</summary>
/// <param name="ItemCount">結果ファイルから分かる対象件数 (結果ファイルが無ければ null)。</param>
internal sealed record RegistryBackupSummary(string Id, DateTime? CreatedUtc, int? ItemCount, bool HasResultFile);

/// <summary>レジストリクリーナーの復元・バックアップ管理の境界。</summary>
internal interface IRegistryRestoreBackend
{
    IReadOnlyList<RegistryBackupSummary> List();

    RegistryRestoreResult Restore(string id, CancellationToken ct);

    void Delete(string id);
}

/// <summary>本番の境界。削除と復元は同じ <see cref="RegistryScanGeneration"/> を共有する。</summary>
internal static class RegistryCleanBackends
{
    public static (IRegistryCleanBackend Clean, IRegistryRestoreBackend Restore) CreateDefault(ICommandExecutor executor)
    {
        var generation = new RegistryScanGeneration();
        return (
            new DefaultCleanBackend(RegistryCleanScanner.CreateDefault(), RegistryCleanService.CreateDefault(executor, generation)),
            new DefaultRestoreBackend(RegistryRestoreService.CreateDefault(generation)));
    }

    private sealed class DefaultCleanBackend(RegistryCleanScanner scanner, RegistryCleanService service) : IRegistryCleanBackend
    {
        public Task<RegistryScanResult> ScanAsync(CancellationToken ct) => Task.Run(
            () =>
            {
                var scan = scanner.Scan(null, ct);
                service.Register(scan);
                return scan;
            },
            ct);

        /// <summary>
        /// 再照合・バックアップ・削除を UI スレッドの外で行う。復元ポイントの続行確認
        /// (<see cref="RegistryCleanRequest.OnRestorePointUnavailable"/>) はバックグラウンドから呼ばれるが、
        /// 窓口の <c>SlimRestorePointPrompt</c> が <c>Dispatcher.UIThread.InvokeAsync</c> で UI スレッドへ戻す (スリム化と同じ)。
        /// </summary>
        public Task<RegistryCleanRunResult> CleanAsync(RegistryCleanRequest request, CancellationToken ct) =>
            Task.Run(() => service.ExecuteAsync(request, ct), ct);

        public void Invalidate() => service.Invalidate();
    }

    private sealed class DefaultRestoreBackend(RegistryRestoreService service) : IRegistryRestoreBackend
    {
        private const string TimeFormat = "yyyyMMddTHHmmssfff'Z'";

        public IReadOnlyList<RegistryBackupSummary> List() => [.. service.ListBackups().Select(Summarize)];

        public RegistryRestoreResult Restore(string id, CancellationToken ct) => service.Restore(id, ct);

        public void Delete(string id) => service.DeleteBackup(id);

        private RegistryBackupSummary Summarize(string id)
        {
            RegistryBackupResultDocument? result = null;
            try
            {
                result = service.ReadResult(id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LoggerBootstrap.Log.Error("レジストリのバックアップ結果ファイルを読めませんでした", ex);
            }

            var separator = id.IndexOf('-', StringComparison.Ordinal);
            DateTime? created = separator > 0 &&
                DateTime.TryParseExact(id[..separator], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : null;
            return new RegistryBackupSummary(id, created, result?.Items.Length, result is not null);
        }
    }
}

/// <summary>
/// レジストリ画面の ViewModel の共通の土台。lease を取って操作を実行し、補償を含む処理が終わるまで手放さない。
/// 走査は <c>showProcessing:false</c>、削除・復元は <c>showProcessing:true</c> で呼ぶ。
/// </summary>
internal abstract class RegistryOperationViewModel(MaintenanceOperationCoordinator coordinator) : CleanupOperationViewModel
{
    private CancellationTokenSource? _cancelSource;

    internal MaintenanceOperationCoordinator Coordinator { get; } = coordinator;

    protected enum Ended
    {
        Busy,
        Completed,
        Cancelled,
        Failed,
    }

    protected override void OnCancelRequested()
    {
        CanCancel = false;
        try
        {
            _cancelSource?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 完了と中断が競合しただけ。
        }
    }

    protected async Task<(Ended Ended, T? Value)> RunLeasedAsync<T>(
        bool showProcessing,
        string label,
        Func<CancellationToken, Task<T>> work)
    {
        if (!Coordinator.TryBegin(out var lease, showProcessing: showProcessing))
        {
            return (Ended.Busy, default);
        }

        using var operation = lease!;
        using var source = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
        _cancelSource = source;
        Label = label;
        ProgressText = string.Empty;
        CanCancel = true;
        IsRunning = true;
        try
        {
            return (Ended.Completed, await work(source.Token));
        }
        catch (OperationCanceledException)
        {
            return (Ended.Cancelled, default);
        }
        catch (Exception ex)
        {
            LoggerBootstrap.Log.Error("レジストリクリーナーの操作で例外が発生しました", ex);
            return (Ended.Failed, default);
        }
        finally
        {
            _cancelSource = null;
            CanCancel = false;
            IsRunning = false;
            Label = string.Empty;
            ProgressText = string.Empty;
        }
    }

    protected static void PostToUi(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) action();
        else Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }
}
