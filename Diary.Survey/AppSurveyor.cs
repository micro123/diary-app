using System.Diagnostics;
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
    private ILogger Logger => Logging.Logger;
    
    public event EventHandler<string>? ReceiveMessage;
    
    public bool StartServer()
    {
        if (_surveyor != null)
            return false;
        
        _surveyor = NngManager.Factory.SurveyorOpen().Unwrap();
        _surveyor.SetOpt(Defines.NNG_OPT_RECVTIMEO, new nng_duration(){TimeMs = 3000});
        _surveyor.SetOpt(Defines.NNG_OPT_SENDTIMEO, new nng_duration() { TimeMs = 3000 });
        // _surveyor.SetOpt(Defines.NNG_OPT_SURVEYOR_SURVEYTIME, new nng_duration() { TimeMs = 2500 });
        _listener = _surveyor.ListenWithListener(NngManager.ListenAddress, Defines.NngFlag.NNG_FLAG_NONBLOCK).Unwrap();
        _surveyorCtx = _surveyor.CreateAsyncContext(NngManager.Factory).Unwrap();
        // _surveyorCtx.Aio.SetTimeout(2500);
        _surveyorCtx.Ctx.SetOpt(Defines.NNG_OPT_SURVEYOR_SURVEYTIME, new nng_duration() { TimeMs = 2500 });
        
        return _surveyorCtx != null;
    }

    public void StopServer()
    {
        StopReceive();
        
        _surveyorCtx?.Aio.Cancel();
        _surveyorCtx?.Aio.Wait();
        _surveyorCtx?.Dispose();
        _surveyorCtx = null;
        _listener?.Dispose();
        _listener = null;
        _surveyor?.Dispose();
        _surveyor = null;
    }

    private void StartReceive()
    {
        if (_surveyorCtx == null || _cts != null)
            return;
        _cts = new CancellationTokenSource();

        Task.Run(async () =>
        {
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                var msg = _surveyor!.RecvMsg();
                if (!msg.TryOk(out var data))
                {
                    var code = msg.Err();
                    if (code != Defines.NngErrno.EAGAIN)
                    {
                        Logger.LogInformation("surveyor error code {code}, stopped", code);
                        await _cts.CancelAsync();
                        _cts = null;
                        break;
                    }
                }
                else
                {
                    var bytes = data.AsSpan();
                    var str = Encoding.UTF8.GetString(bytes);
                    ReceiveMessage?.Invoke(this, str);
                }
            }
        });
    }

    private void StopReceive()
    {
        _cts?.Cancel();
        _cts = null;
    }

    public void Survey(string question)
    {
        if (_surveyor == null)
            return;

        if (_surveyor.Send(Encoding.UTF8.GetBytes(question), Defines.NngFlag.NNG_FLAG_NONBLOCK).IsOk())
            StartReceive();
    }
}