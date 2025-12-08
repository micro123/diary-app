using Diary.Survey;

namespace Diary.SurveyTests;

[TestClass]
public class RespondentTests
{
    [TestMethod]
    public async Task Connect()
    {
        var respondent = new AppRespondent();
        respondent.Connect("127.0.0.1");
        
        var cts = new CancellationTokenSource();
        var msg = await respondent.RecieveMessage(cts.Token);
        
        respondent.Shutdown();
    }
}