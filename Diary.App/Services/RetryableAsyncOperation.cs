namespace Diary.App.Services;

internal sealed class RetryableAsyncOperation
{
    private readonly object _sync = new();
    private Task? _inFlight;
    private bool _completed;

    public async Task RunAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        Task task;
        lock (_sync)
        {
            if (_completed)
                return;
            task = _inFlight ??= operation();
        }

        try
        {
            await task;
        }
        catch
        {
            lock (_sync)
            {
                if (ReferenceEquals(_inFlight, task))
                    _inFlight = null;
            }
            throw;
        }

        lock (_sync)
        {
            _completed = true;
            _inFlight = null;
        }
    }
}
