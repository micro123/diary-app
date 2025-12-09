using System.Diagnostics;
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

        respondent.ReceiveMessage += (o, s) =>
        {
            Debug.WriteLine(s);
        };
        await Task.Delay(10000);
        
        respondent.Shutdown();
    }
}