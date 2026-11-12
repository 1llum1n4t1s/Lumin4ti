using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumin4ti.Core.Services;

namespace Lumin4ti.UI.Services;

/// <summary>
/// アプリ全体の状態変更操作を追跡する。終了要求では全操作へキャンセルを通知し、
/// 各操作の補償・再検証を含む finally が完了してから終了できるようにする。
/// </summary>
public sealed class MaintenanceOperationCoordinator
{
    private readonly Lock _sync = new();
    private readonly HashSet<CancellationTokenSource> _active = [];
    private readonly Func<IDisposable?> _tryAcquireProcessLock;
    private TaskCompletionSource? _idleSignal;
    private bool _isStopping;
    private bool _showProcessing;

    /// <summary>購読側は通知時に最新の状態を読み直す。通知は同期ロックの外で行う。</summary>
    public event EventHandler? StateChanged;

    public (bool IsProcessing, bool CanCancel) ProcessingState
    {
        get
        {
            lock (_sync)
            {
                return (_active.Count != 0 && _showProcessing,
                    _active.Count != 0 && _showProcessing && !_isStopping &&
                    !_active.Any(source => source.IsCancellationRequested));
            }
        }
    }

    public bool IsStopping
    {
        get
        {
            lock (_sync)
            {
                return _isStopping;
            }
        }
    }

    public MaintenanceOperationCoordinator()
        : this(MaintenanceOperationProcessLock.TryAcquire)
    {
    }

    internal MaintenanceOperationCoordinator(Func<IDisposable?> tryAcquireProcessLock)
    {
        ArgumentNullException.ThrowIfNull(tryAcquireProcessLock);
        _tryAcquireProcessLock = tryAcquireProcessLock;
    }

    public int ActiveCount
    {
        get
        {
            lock (_sync)
            {
                return _active.Count;
            }
        }
    }

    public bool TryBegin(
        out OperationLease? lease,
        CancellationToken cancellationToken = default,
        bool showProcessing = true)
    {
        lock (_sync)
        {
            if (_isStopping || _active.Count != 0)
            {
                lease = null;
                return false;
            }

            var processLock = _tryAcquireProcessLock();
            if (processLock is null)
            {
                lease = null;
                return false;
            }

            CancellationTokenSource source;
            try
            {
                source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            }
            catch
            {
                processLock.Dispose();
                throw;
            }

            _idleSignal = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _active.Add(source);
            _showProcessing = showProcessing;
            lease = new OperationLease(this, source, processLock);
        }

        NotifyStateChanged();
        return true;
    }

    public void RequestCancellation()
    {
        CancellationTokenSource[] sources;
        lock (_sync)
        {
            // 終了受付と新規操作の拒否を同じ同期境界で確定する。
            _isStopping = true;
            sources = _active.ToArray();
        }

        foreach (var source in sources)
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 完了と終了要求が競合しただけなので無視する。
            }
            catch (AggregateException ex)
            {
                // Release でも原因を残す (Debug.WriteLine は Release ビルドで消える)。
                LoggerBootstrap.Log.Error("操作のキャンセル通知で購読先が例外を返しました", ex);
            }
        }

        NotifyStateChanged();
    }

    public Task WaitForIdleAsync()
    {
        lock (_sync)
        {
            return _active.Count == 0 ? Task.CompletedTask : _idleSignal!.Task;
        }
    }

    private void Complete(CancellationTokenSource source)
    {
        TaskCompletionSource? completedSignal = null;
        lock (_sync)
        {
            if (_active.Remove(source) && _active.Count == 0)
            {
                completedSignal = _idleSignal;
                _idleSignal = null;
                _showProcessing = false;
            }
        }

        source.Dispose();
        completedSignal?.TrySetResult();
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        if (StateChanged is not { } handlers)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                // 表示の購読先の失敗で操作リースや終了待ちを失わせない。
                LoggerBootstrap.Log.Error("操作状態の変更通知で購読先が例外を返しました", ex);
            }
        }
    }

    public sealed class OperationLease : IDisposable
    {
        private MaintenanceOperationCoordinator? _owner;
        private readonly CancellationTokenSource _source;
        private readonly IDisposable _processLock;

        internal OperationLease(
            MaintenanceOperationCoordinator owner,
            CancellationTokenSource source,
            IDisposable processLock)
        {
            _owner = owner;
            _source = source;
            _processLock = processLock;
        }

        public CancellationToken Token => _source.Token;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } owner)
            {
                return;
            }

            try
            {
                _processLock.Dispose();
            }
            finally
            {
                owner.Complete(_source);
            }
        }
    }
}
