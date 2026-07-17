using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Lumin4ti.UI.Services;

/// <summary>
/// アプリ全体の状態変更操作を追跡する。終了要求では全操作へキャンセルを通知し、
/// 各操作の補償・再検証を含む finally が完了してから終了できるようにする。
/// </summary>
public sealed class MaintenanceOperationCoordinator
{
    private readonly object _sync = new();
    private readonly HashSet<CancellationTokenSource> _active = [];
    private TaskCompletionSource? _idleSignal;

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
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_active.Count != 0)
            {
                lease = null;
                return false;
            }

            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _idleSignal = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _active.Add(source);
            lease = new OperationLease(this, source);
            return true;
        }
    }

    public void RequestCancellation()
    {
        CancellationTokenSource[] sources;
        lock (_sync)
        {
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
        }
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
            }
        }

        source.Dispose();
        completedSignal?.TrySetResult();
    }

    public sealed class OperationLease : IDisposable
    {
        private MaintenanceOperationCoordinator? _owner;
        private readonly CancellationTokenSource _source;

        internal OperationLease(
            MaintenanceOperationCoordinator owner,
            CancellationTokenSource source)
        {
            _owner = owner;
            _source = source;
        }

        public CancellationToken Token => _source.Token;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Complete(_source);
    }
}
