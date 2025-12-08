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

    public event EventHandler<string>? RecievedMessage;

    public AppRespondent()
    {
        
    }

    public bool Connect(string hostIpAddress)
    {
        if (_respondent != null)
            return false;

        _respondent = NngManager.Factory.RespondentOpen().Ok();
        _respondent.SetOpt(Defines.NNG_OPT_SENDTIMEO, new nng_duration(){TimeMs = 10000});
        _respondent.SetOpt(Defines.NNG_OPT_RECVTIMEO, new nng_duration(){TimeMs = 10000});
        _dialer = _respondent.DialWithDialer($"tcp://{hostIpAddress}:{NngManager.ListenPort}", Defines.NngFlag.NNG_FLAG_NONBLOCK).Unwrap();
        _dialer.SetOpt(Defines.NNG_OPT_RECONNMINT, new nng_duration(){TimeMs = 3000});
        _dialer.SetOpt(Defines.NNG_OPT_RECONNMAXT, new nng_duration(){TimeMs = 0});
        _respondentCtx = _respondent.CreateAsyncContext(NngManager.Factory).Unwrap();
        
        return false;
    }
    
    public void Shutdown()
    {
        _dialer?.Dispose();
        _dialer = null;
        _respondent?.Dispose();
        _respondent = null;
    }

    public async Task<string> RecieveMessage(CancellationToken ct)
    {
        if (_respondentCtx is null)
            return string.Empty;

        var msg = await _respondentCtx.Receive(ct);
        if (msg.TryOk(out var data))
        {
            var bytes = data.AsSpan();
            var str = Encoding.UTF8.GetString(bytes);
            // data.Dispose();
            return str;
        }
        return string.Empty;
    }
}