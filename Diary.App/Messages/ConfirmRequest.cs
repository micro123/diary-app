using System;
using System.Threading.Tasks;

namespace Diary.App.Messages;

public class ConfirmRequest<TRequest, TResponse>(TRequest request)
{
    public TRequest Request { get; } = request;
    
    private readonly TaskCompletionSource<TResponse> _tcs = new TaskCompletionSource<TResponse>();
    public Task<TResponse> Task => _tcs.Task;
    
    public bool Reply(TResponse response)
    {
        return _tcs.TrySetResult(response);
    }

    public bool Error(Exception exception)
    {
        return _tcs.TrySetException(exception);
    }
}

public record ConfirmMessage
{
    public required string Title { get;set; }
    public required string Message { get;set; }
}

