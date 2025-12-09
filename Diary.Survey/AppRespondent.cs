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

    public event EventHandler<string>? ReceiveMessage;

    public bool Connect(string hostIpAddress)
    {
        if (_respondent != null)
            return false;

        _respondent = NngManager.Factory.RespondentOpen().Ok();
        _respondent.SetOpt(Defines.NNG_OPT_SENDTIMEO, new nng_duration(){TimeMs = 3000});
        _respondent.SetOpt(Defines.NNG_OPT_RECVTIMEO, new nng_duration(){TimeMs = 3000});
        _dialer = _respondent.DialWithDialer($"tcp://{hostIpAddress}:{NngManager.ListenPort}", Defines.NngFlag.NNG_FLAG_NONBLOCK).Unwrap();
        _dialer.SetOpt(Defines.NNG_OPT_RECONNMINT, new nng_duration(){TimeMs = 3000});
        _dialer.SetOpt(Defines.NNG_OPT_RECONNMAXT, new nng_duration(){TimeMs = 0});
        _respondentCtx = _respondent.CreateAsyncContext(NngManager.Factory).Unwrap();
        _respondentCtx.Aio.SetTimeout(2500);
        
        return _respondentCtx != null;
    }
    
    public void Shutdown()
    {
        _respondentCtx?.Dispose();
        _respondentCtx = null;
        _dialer?.Dispose();
        _dialer = null;
        _respondent?.Dispose();
        _respondent = null;
    }

    public void StartReceive()
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
                var msg = await _respondentCtx.Receive(token);
                if (msg.TryOk(out var data))
                {
                    var bytes = data.AsSpan();
                    var str = Encoding.UTF8.GetString(bytes);
                    ReceiveMessage?.Invoke(this, str);
                }
            }
        });
    }

    public void StopReceive()
    {
        _cts?.Cancel();
        _cts = null;
    }
}