using System;
using System.Threading;
using System.Threading.Tasks;

namespace WallDistance.Core
{
    /// <summary>
    /// Owns initialization of a process-wide native resource independently of a Unity coroutine.
    /// Disposing while initialization is blocked never blocks the main thread: the completing task
    /// releases its resource, even when its original GameObject and coroutine no longer exist.
    /// </summary>
    public sealed class AsyncInitialization : IDisposable
    {
        static readonly object NativeGate = new object();
        static AsyncInitialization _owner;
        readonly Action _release;
        volatile bool _disposed;
        bool _owns;
        public Task<int> Completion { get; }

        public AsyncInitialization(Func<int> initialize, Action release)
        {
            _release = release ?? throw new ArgumentNullException(nameof(release));
            if (initialize == null) throw new ArgumentNullException(nameof(initialize));
            Completion = Task.Run(() =>
            {
                lock (NativeGate)
                {
                    // A replacement initializer must not reuse an engine awaiting old-owner cleanup.
                    if (_owner != null)
                    {
                        if (!_owner._disposed) throw new InvalidOperationException("Native inference already has an active owner");
                        _owner.ReleaseLocked();
                    }
                    if (_disposed) return -1;
                    int result = initialize();
                    if (result == 0)
                    {
                        _owns = true;
                        _owner = this;
                        if (_disposed) ReleaseLocked();
                    }
                    return result;
                }
            });
            // Covers disposal between the worker's last disposed check and Task completion.
            Completion.ContinueWith(_ => { if (_disposed) Release(); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public void Dispose()
        {
            _disposed = true;
            if (Completion.IsCompleted) Release();
        }

        void Release() { lock (NativeGate) ReleaseLocked(); }
        void ReleaseLocked()
        {
            if (!_owns) return;
            _owns = false;
            if (ReferenceEquals(_owner, this)) _owner = null;
            _release();
        }
    }
}
