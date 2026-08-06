using Diary.Survey;

namespace Diary.SurveyTests;

[TestClass]
public sealed class RespondentTests
{
    [TestMethod]
    public void ConnectAndShutdownAreIdempotent()
    {
        var respondent = new AppRespondent();

        Assert.IsTrue(respondent.Connect("127.0.0.1"));
        Assert.IsFalse(respondent.Connect("127.0.0.1"));
        respondent.Shutdown();
        respondent.Shutdown();

        Assert.IsTrue(respondent.Connect("127.0.0.1"));
        respondent.Shutdown();
    }

    [TestMethod]
    public void RapidConnectAndShutdownDoesNotRaceReceiveLoop()
    {
        var respondent = new AppRespondent();

        for (var i = 0; i < 20; i++)
        {
            Assert.IsTrue(respondent.Connect("127.0.0.1"));
            respondent.Shutdown();
        }
    }
}
