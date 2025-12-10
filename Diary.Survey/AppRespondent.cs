using System.Diagnostics;
using System.Text;
using nng;
using nng.Native;

namespace Diary.Survey;

public class AppRespondent
{
    private IRespondentSocket? _respondent;
    private INngDialer? _dialer;
    private ISurveyorAsyncContext<INngMsg>? _respondentCtx;
    private CancellationTokenSource? _cts;
    private Queue<string> _msgToSend = new();
    private object _lock = new();

    public event EventHandler<string>? ReceiveMessage;

    public bool Connect(string hostIpAddress)
    {
        if (_respondent != null)
            return false;

        _respondent = NngManager.Factory.RespondentOpen().Ok();
        _respondent.SetOpt(Defines.NNG_OPT_RECVTIMEO, new nng_duration(){TimeMs = 250});
        _respondent.SetOpt(Defines.NNG_OPT_RECONNMAXT, new nng_duration(){TimeMs = 0});
        _respondent.SetOpt(Defines.NNG_OPT_RECONNMINT, new nng_duration(){TimeMs = 1500});
        _dialer = _respondent.DialWithDialer($"tcp://{hostIpAddress}:{NngManager.ListenPort}", Defines.NngFlag.NNG_FLAG_NONBLOCK).Unwrap();
        _dialer.SetOpt(Defines.NNG_OPT_RECONNMINT, new nng_duration(){TimeMs = 1500});
        _dialer.SetOpt(Defines.NNG_OPT_RECONNMAXT, new nng_duration(){TimeMs = 0});
        _respondentCtx = _respondent.CreateAsyncContext(NngManager.Factory).Unwrap();
        _respondentCtx.Aio.SetTimeout(250);
        
        StartReceive();
        
        return _respondentCtx != null;
    }
    
    public void Shutdown()
    {
        StopReceive();
        
        lock (_lock)
        {
            _msgToSend.Clear();
        }
        _respondentCtx?.Aio.Cancel();
        _respondentCtx?.Aio.Wait();
        _respondentCtx?.Dispose();
        _respondentCtx = null;
        _dialer?.Dispose();
        _dialer = null;
        _respondent?.Dispose();
        _respondent = null;
    }

    private void StartReceive()
    {
        if (_respondentCtx is null)
            return;
        if (_cts is not null)
            return;

        _cts = new CancellationTokenSource();
        Task.Run(async () =>
        {
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                string? msgToSend = null;
                lock (_lock)
                {
                    if (_msgToSend.Count > 0)
                        msgToSend = _msgToSend.Dequeue();
                }
                
                var msg = await _respondentCtx.Receive(token);
                if (msg.TryOk(out var data))
                {
                    var bytes = data.AsSpan();
                    var str = Encoding.UTF8.GetString(bytes);
                    ReceiveMessage?.Invoke(this, str);
                }

                if (msgToSend != null)
                {
                    var nngMsg = NngManager.Factory.CreateMessage();
                    nngMsg.Append(Encoding.UTF8.GetBytes(msgToSend));
                    var result = await _respondentCtx.Send(nngMsg);
                    if (!result.IsOk())
                    {
                        Debug.WriteLine($"send failed {result.Err()}");
                    }
                }
            }
        });
    }

    private void StopReceive()
    {
        _cts?.Cancel();
        _cts = null;
    }

    public void Send(string msg)
    {
        if (_respondentCtx is null)
            return;

        lock (_lock)
        {
            _msgToSend.Enqueue(msg);
        }
    }
}