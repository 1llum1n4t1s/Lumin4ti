namespace Lumin4ti.Core.Services;

/// <summary>
/// UI とスケジュール実行をまたいで、OS の状態変更を同時に 1 件へ制限する。
/// </summary>
public sealed class MaintenanceOperationProcessLock : IDisposable
{
    private const string DefaultName = @"Global\Kagayoi.Lumin4ti.MaintenanceOperation";

    private readonly ManualResetEventSlim _releaseRequested;
    private Thread? _ownerThread;

    private MaintenanceOperationProcessLock(Thread ownerThread, ManualResetEventSlim releaseRequested)
    {
        _ownerThread = ownerThread;
        _releaseRequested = releaseRequested;
    }

    /// <summary>共有ロックを待たずに取得する。別プロセスが操作中、または作成に失敗した場合は null。</summary>
    public static MaintenanceOperationProcessLock? TryAcquire() => TryAcquire(DefaultName);

    internal static MaintenanceOperationProcessLock? TryAcquire(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var acquisitionCompleted = new ManualResetEventSlim(false);
        var releaseRequested = new ManualResetEventSlim(false);
        var acquired = false;
        Exception? acquisitionError = null;

        var ownerThread = new Thread(() =>
        {
            Mutex? mutex = null;

            try
            {
                mutex = new Mutex(initiallyOwned: false, name);

                try
                {
                    acquired = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    // 前回のプロセスが異常終了しても、OS が移譲した所有権を引き継ぐ。
                    acquired = true;
                }
            }
            catch (Exception ex)
            {
                acquisitionError = ex;
            }
            finally
            {
                acquisitionCompleted.Set();
            }

            if (!acquired)
            {
                mutex?.Dispose();
                return;
            }

            try
            {
                // Mutex は取得したスレッドから解放する必要があるため、所有専用スレッドで待機する。
                releaseRequested.Wait();
                mutex!.ReleaseMutex();
            }
            catch (Exception ex)
            {
                LoggerBootstrap.Log.Error(
                    $"メンテナンス操作の共有ロックを解放できませんでした ({ex.GetType().Name})",
                    ex);
            }
            finally
            {
                mutex?.Dispose();
            }
        })
        {
            IsBackground = true,
            Name = "Lumin4ti maintenance operation lock",
        };

        var ownerThreadStarted = false;

        try
        {
            ownerThread.Start();
            ownerThreadStarted = true;
            acquisitionCompleted.Wait();
        }
        catch (Exception ex)
        {
            LoggerBootstrap.Log.Error(
                $"メンテナンス操作の共有ロックを取得できませんでした ({ex.GetType().Name})",
                ex);
            if (ownerThreadStarted)
            {
                releaseRequested.Set();
                ownerThread.Join();
            }

            acquisitionCompleted.Dispose();
            releaseRequested.Dispose();
            return null;
        }

        acquisitionCompleted.Dispose();

        if (acquisitionError is not null)
        {
            LoggerBootstrap.Log.Error(
                $"メンテナンス操作の共有ロックを取得できませんでした ({acquisitionError.GetType().Name})",
                acquisitionError);
            ownerThread.Join();
            releaseRequested.Dispose();
            return null;
        }

        if (!acquired)
        {
            ownerThread.Join();
            releaseRequested.Dispose();
            return null;
        }

        return new MaintenanceOperationProcessLock(ownerThread, releaseRequested);
    }

    public void Dispose()
    {
        var ownerThread = Interlocked.Exchange(ref _ownerThread, null);
        if (ownerThread is null)
        {
            return;
        }

        _releaseRequested.Set();
        ownerThread.Join();
        _releaseRequested.Dispose();
    }
}
