namespace Diary.RedMine;

internal sealed class SharedDisposableResource<T>(T resource) : IDisposable
    where T : class, IDisposable
{
    private readonly object _gate = new();
    private T? _resource = resource ?? throw new ArgumentNullException(nameof(resource));
    private int _leaseCount;
    private bool _disposeRequested;

    public Lease? TryAcquire()
    {
        lock (_gate)
        {
            if (_disposeRequested || _resource is null)
                return null;

            _leaseCount++;
            return new Lease(this, _resource);
        }
    }

    public void Dispose()
    {
        T? resourceToDispose = null;
        lock (_gate)
        {
            if (_disposeRequested)
                return;

            _disposeRequested = true;
            if (_leaseCount == 0)
            {
                resourceToDispose = _resource;
                _resource = null;
            }
        }

        resourceToDispose?.Dispose();
    }

    private void Release()
    {
        T? resourceToDispose = null;
        lock (_gate)
        {
            _leaseCount--;
            if (_leaseCount < 0)
                throw new InvalidOperationException("资源租约计数不能小于零。");
            if (_disposeRequested && _leaseCount == 0)
            {
                resourceToDispose = _resource;
                _resource = null;
            }
        }

        resourceToDispose?.Dispose();
    }

    internal sealed class Lease(
        SharedDisposableResource<T> owner,
        T resource) : IDisposable
    {
        private SharedDisposableResource<T>? _owner = owner;

        public T Resource { get; } = resource;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
