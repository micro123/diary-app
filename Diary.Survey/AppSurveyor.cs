using System.Text;
using Diary.Utils;
using Microsoft.Extensions.Logging;
using nng;
using nng.Native;

namespace Diary.Survey;

public class AppSurveyor
{
    private ISurveyorSocket? _surveyor;
    private INngListener? _listener;
    private ISurveyorAsyncContext<INngMsg>? _surveyorCtx;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _stopTask;
    private bool _isStopping;
    private readonly Lock _lifecycleLock = new();
    private readonly SemaphoreSlim _surveyGate = new(1, 1);
    private ILogger Logger => Logging.Logger;

    public event EventHandler<string>? ReceiveMessage;
    public event EventHandler<Exception>? ReceiveMessageHandlerError;

    public bool StartServer()
    {
        lock (_lifecycleLock)
        {
            if (_surveyor != null || _isStopping)
                return false;

            _surveyor = NngManager.Factory.SurveyorOpen().Unwrap();
            _surveyor.SetOpt(Defines.NNG_OPT_RECVTIMEO, new nng_duration() { TimeMs = 3000 });
            _surveyor.SetOpt(Defines.NNG_OPT_SENDTIMEO, new nng_duration() { TimeMs = 3000 });
            _listener = _surveyor.ListenWithListener(NngManager.ListenAddress, Defines.NngFlag.NNG_FLAG_NONBLOCK).Unwrap();
            _surveyorCtx = _surveyor.CreateAsyncContext(NngManager.Factory).Unwrap();
            _surveyorCtx.Ctx.SetOpt(Defines.NNG_OPT_SURVEYOR_SURVEYTIME, new nng_duration() { TimeMs = 2500 });

            return true;
        }
    }

    public void StopServer()
    {
        ObserveBackgroundTask(StopServerAsync(), "Surveyor stop");
    }

    public Task StopServerAsync()
    {
        Task? receiveTask;
        CancellationTokenSource? cts;
        ISurveyorAsyncContext<INngMsg>? context;

        lock (_lifecycleLock)
        {
            if (_isStopping)
                return _stopTask ?? Task.CompletedTask;

            _isStopping = true;
            receiveTask = _receiveTask;
            cts = _cts;
            context = _surveyorCtx;
            cts?.Cancel();
            context?.Aio.Cancel();
            _stopTask = StopServerCoreAsync(receiveTask, cts, context);
            return _stopTask;
        }
    }

    private async Task StopServerCoreAsync(
        Task? receiveTask,
        CancellationTokenSource? cts,
        ISurveyorAsyncContext<INngMsg>? context)
    {
        try
        {
            if (receiveTask is not null)
                await receiveTask.ConfigureAwait(false);

            await _surveyGate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_lifecycleLock)
                {
                    context?.Aio.Wait();
                    context?.Dispose();
                    _surveyorCtx = null;
                    _listener?.Dispose();
                    _listener = null;
                    _surveyor?.Dispose();
                    _surveyor = null;
                    _receiveTask = null;
                    _cts = null;
                    _isStopping = false;
                }
            }
            finally
            {
                _surveyGate.Release();
            }
        }
        finally
        {
            cts?.Dispose();
            lock (_lifecycleLock)
                _stopTask = null;
        }
    }

    private void ObserveBackgroundTask(Task task, string operation)
    {
        _ = task.ContinueWith(
            completedTask => Logger.LogError(completedTask.Exception, "{Operation} failed", operation),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void StartReceive(ISurveyorAsyncContext<INngMsg> context)
    {
        if (_receiveTask is { IsCompleted: false })
            return;

        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _receiveTask = Task.Run(() => ReceiveLoop(context, cts.Token));
    }

    private async Task ReceiveLoop(ISurveyorAsyncContext<INngMsg> context, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var pendingReceive = context.Receive(CancellationToken.None);
                NngResult<INngMsg> msg;
                try
                {
                    msg = await pendingReceive.WaitAsync(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    context.Aio.Cancel();
                    try
                    {
                        await pendingReceive.ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                    break;
                }

                if (msg.TryOk(out var data))
                {
                    await DispatchReceiveMessageAsync(Encoding.UTF8.GetString(data.AsSpan()));
                    continue;
                }

                if (token.IsCancellationRequested || msg.Err() == Defines.NngErrno.EAGAIN || msg.Err() == Defines.NngErrno.ETIMEDOUT)
                    break;

                Logger.LogError("Surveyor receive failed with code {Code}", msg.Err());
                break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Surveyor receive loop stopped unexpectedly");
        }
    }

    private async Task DispatchReceiveMessageAsync(string message)
    {
        var handlers = ReceiveMessage;
        if (handlers == null)
            return;

        foreach (EventHandler<string> handler in handlers.GetInvocationList())
            await Task.Run(() => InvokeReceiveHandler(handler, message));
    }

    private void InvokeReceiveHandler(EventHandler<string> handler, string message)
    {
        try
        {
            handler(this, message);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Surveyor ReceiveMessage subscriber failed");
            DispatchHandlerError(ex);
        }
    }

    private void DispatchHandlerError(Exception exception)
    {
        var handlers = ReceiveMessageHandlerError;
        if (handlers == null)
            return;

        foreach (EventHandler<Exception> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, exception);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Surveyor ReceiveMessageHandlerError subscriber failed");
            }
        }
    }

    public void Survey(string question)
    {
        ObserveBackgroundTask(SurveyAsync(question), "Surveyor survey");
    }

    public async Task SurveyAsync(string question)
    {
        await _surveyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Task? receiveTask;
            ISurveyorAsyncContext<INngMsg> context;
            lock (_lifecycleLock)
            {
                if (_surveyor == null || _surveyorCtx == null || _isStopping)
                    return;

                context = _surveyorCtx;
                receiveTask = _receiveTask;
                if (receiveTask is { IsCompleted: false })
                {
                    _cts?.Cancel();
                    context.Aio.Cancel();
                }
            }

            if (receiveTask is { IsCompleted: false })
                await receiveTask.ConfigureAwait(false);

            lock (_lifecycleLock)
            {
                if (_isStopping || !ReferenceEquals(_surveyorCtx, context))
                    return;
            }

            var message = NngManager.Factory.CreateMessage();
            message.Append(Encoding.UTF8.GetBytes(question));
            var result = await context.Send(message).ConfigureAwait(false);
            if (result.IsOk())
            {
                lock (_lifecycleLock)
                {
                    if (!_isStopping && ReferenceEquals(_surveyorCtx, context))
                        StartReceive(context);
                }
            }
            else
                Logger.LogError("Surveyor send failed with code {Code}", result.Err());
        }
        finally
        {
            _surveyGate.Release();
        }
    }
}
