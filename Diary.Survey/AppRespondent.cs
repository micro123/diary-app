using System.Text;
using Diary.Utils;
using Microsoft.Extensions.Logging;
using nng;
using nng.Native;

namespace Diary.Survey;

public class AppRespondent
{
    private readonly ushort _port;
    private IRespondentSocket? _respondent;
    private INngDialer? _dialer;
    private ISurveyorAsyncContext<INngMsg>? _respondentCtx;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _shutdownTask;
    private bool _isStopping;
    private readonly Queue<string> _msgToSend = new();
    private readonly SemaphoreSlim _sendAvailable = new(0);
    private readonly Lock _messageLock = new();
    private readonly Lock _lifecycleLock = new();
    private ILogger Logger => Logging.Logger;

    public AppRespondent(ushort port = SurveyPorts.Legacy)
    {
        _port = port;
    }

    public event EventHandler<string>? ReceiveMessage;
    public event EventHandler<Exception>? ReceiveMessageHandlerError;

    public bool Connect(string hostIpAddress)
    {
        lock (_lifecycleLock)
        {
            if (_respondent != null || _isStopping)
                return false;

            _respondent = NngManager.Factory.RespondentOpen().Ok();
            _respondent.SetOpt(Defines.NNG_OPT_RECVTIMEO, new nng_duration() { TimeMs = 250 });
            _respondent.SetOpt(Defines.NNG_OPT_RECONNMAXT, new nng_duration() { TimeMs = 0 });
            _respondent.SetOpt(Defines.NNG_OPT_RECONNMINT, new nng_duration() { TimeMs = 1500 });
            _dialer = _respondent.DialWithDialer($"tcp://{hostIpAddress}:{_port}", Defines.NngFlag.NNG_FLAG_NONBLOCK).Unwrap();
            _dialer.SetOpt(Defines.NNG_OPT_RECONNMINT, new nng_duration() { TimeMs = 1500 });
            _dialer.SetOpt(Defines.NNG_OPT_RECONNMAXT, new nng_duration() { TimeMs = 0 });
            _respondentCtx = _respondent.CreateAsyncContext(NngManager.Factory).Unwrap();
            _respondentCtx.Aio.SetTimeout(250);

            Logger.LogInformation("Respondent connecting to {Host}:{Port}", hostIpAddress, _port);
            StartReceive(_respondentCtx);
            return true;
        }
    }

    public void Shutdown()
    {
        ObserveBackgroundTask(ShutdownAsync(), "Respondent shutdown");
    }

    public Task ShutdownAsync()
    {
        Task? receiveTask;
        CancellationTokenSource? cts;
        ISurveyorAsyncContext<INngMsg>? context;

        lock (_lifecycleLock)
        {
            if (_isStopping)
                return _shutdownTask ?? Task.CompletedTask;

            _isStopping = true;
            receiveTask = _receiveTask;
            cts = _cts;
            context = _respondentCtx;
            cts?.Cancel();
            context?.Aio.Cancel();
            _shutdownTask = ShutdownCoreAsync(receiveTask, cts, context);
            return _shutdownTask;
        }
    }

    private async Task ShutdownCoreAsync(
        Task? receiveTask,
        CancellationTokenSource? cts,
        ISurveyorAsyncContext<INngMsg>? context)
    {
        try
        {
            if (receiveTask is not null)
                await receiveTask.ConfigureAwait(false);

            lock (_lifecycleLock)
            {
                lock (_messageLock)
                {
                    _msgToSend.Clear();
                    while (_sendAvailable.Wait(0))
                    {
                    }
                }

                context?.Aio.Wait();
                context?.Dispose();
                _respondentCtx = null;
                _dialer?.Dispose();
                _dialer = null;
                _respondent?.Dispose();
                _respondent = null;
                _receiveTask = null;
                _cts = null;
                _isStopping = false;
            }
        }
        finally
        {
            cts?.Dispose();
            lock (_lifecycleLock)
                _shutdownTask = null;
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

                if (!msg.TryOk(out var data))
                {
                    if (token.IsCancellationRequested)
                        break;
                    if (msg.Err() != Defines.NngErrno.EAGAIN && msg.Err() != Defines.NngErrno.ETIMEDOUT)
                        Logger.LogError("Respondent receive failed with code {Code}", msg.Err());
                    continue;
                }

                var requestBytes = data.AsSpan();
                Logger.LogDebug("Respondent received {ByteCount} bytes on port {Port}", requestBytes.Length, _port);
                await DispatchReceiveMessageAsync(Encoding.UTF8.GetString(requestBytes));
                var response = await DequeueResponse(token);
                var responseBytes = Encoding.UTF8.GetBytes(response);
                var nngMsg = NngManager.Factory.CreateMessage();
                nngMsg.Append(responseBytes);
                var result = await context.Send(nngMsg);
                if (result.IsOk())
                    Logger.LogDebug("Respondent sent {ByteCount} bytes on port {Port}", responseBytes.Length, _port);
                else if (!token.IsCancellationRequested)
                    Logger.LogError("Respondent send failed with code {Code}", result.Err());
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Respondent receive loop stopped unexpectedly");
        }
    }

    private async Task<string> DequeueResponse(CancellationToken token)
    {
        await _sendAvailable.WaitAsync(token);
        lock (_messageLock)
        {
            return _msgToSend.Dequeue();
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
            Logger.LogError(ex, "Respondent ReceiveMessage subscriber failed");
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
                Logger.LogError(ex, "Respondent ReceiveMessageHandlerError subscriber failed");
            }
        }
    }

    public void Send(string msg)
    {
        lock (_lifecycleLock)
        {
            if (_respondentCtx is null || _isStopping)
                return;

            lock (_messageLock)
            {
                _msgToSend.Enqueue(msg);
                _sendAvailable.Release();
            }
        }
    }
}
