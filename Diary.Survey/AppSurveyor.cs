using nng;
using nng.Native;

namespace Diary.Survey;

public class AppSurveyor
{
    private ISurveyorSocket? _surveyor;
    private INngListener? _listener;
    private ISurveyorAsyncContext<INngMsg>? _surveyorAio;

    public AppSurveyor()
    {
    }

    public bool Start()
    {
        if (_surveyor != null)
            return false;
        
        _surveyor = NngManager.Factory.SurveyorOpen().Unwrap();
        _surveyor.SetOpt(Defines.NNG_OPT_RECVTIMEO, 1000); // 1s 超时
        _listener = _surveyor.ListenerCreate(NngManager.ListenAddress).Unwrap();
        
        return false;
    }

    public void Stop()
    {
        _surveyorAio?.Aio.Cancel();
        _surveyorAio?.Aio.Wait();
        _surveyorAio?.Dispose();
        _surveyorAio = null;
        _surveyor?.Dispose();
        _surveyor = null;
    }

    public async Task<string> Query(string begin, string end)
    {
        await Task.Delay(500);
        return string.Empty;
    }
}