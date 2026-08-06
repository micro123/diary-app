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
    private bool _isStopping;
    private readonly Lock _lifecycleLock = new();
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
        Task? receiveTask;
        CancellationTokenSource? cts;
        ISurveyorAsyncContext<INngMsg>? context;

        lock (_lifecycleLock)
        {
            if (_isStopping)
                return;

            _isStopping = true;
            receiveTask = _receiveTask;
            cts = _cts;
            context = _surveyorCtx;
            cts?.Cancel();
            context?.Aio.Cancel();
        }

        receiveTask?.GetAwaiter().GetResult();

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
            cts?.Dispose();
            _isStopping = false;
        }
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
                var msg = await context.Receive(CancellationToken.None);
                if (msg.TryOk(out var data))
                {
                    DispatchReceiveMessage(Encoding.UTF8.GetString(data.AsSpan()));
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

    private void DispatchReceiveMessage(string message)
    {
        var handlers = ReceiveMessage;
        if (handlers == null)
            return;

        _ = Task.Run(() =>
        {
            foreach (EventHandler<string> handler in handlers.GetInvocationList())
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
        });
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
        lock (_lifecycleLock)
        {
            if (_surveyor == null || _surveyorCtx == null || _isStopping)
                return;

            if (_receiveTask is { IsCompleted: false })
            {
                _cts?.Cancel();
                _surveyorCtx.Aio.Cancel();
                _receiveTask.GetAwaiter().GetResult();
            }

            var message = NngManager.Factory.CreateMessage();
            message.Append(Encoding.UTF8.GetBytes(question));
            var result = _surveyorCtx.Send(message).GetAwaiter().GetResult();
            if (result.IsOk())
                StartReceive(_surveyorCtx);
            else
                Logger.LogError("Surveyor send failed with code {Code}", result.Err());
        }
    }
}
