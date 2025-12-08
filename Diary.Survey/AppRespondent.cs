using nng;
using nng.Native;

namespace Diary.Survey;

public class AppRespondent
{
    private IRespondentSocket? _respondent;
    private INngDialer? _dialer;
    private ISurveyorAsyncContext<INngMsg>? _respondentAio;

    public event EventHandler<string>? RecievedMessage;

    public AppRespondent()
    {
        
    }

    public bool Connect(string hostIpAddress)
    {
        if (_respondent != null)
            return false;

        return false;
    }
    
    public void Shutdown()
    {
        _respondent = null;
    }
}