using Diary.Survey;

namespace Diary.SurveyTests;

[TestClass]
public sealed class RespondentTests
{
    [TestMethod]
    public async Task ConnectAndShutdownAreIdempotent()
    {
        var respondent = new AppRespondent();

        Assert.IsTrue(respondent.Connect("127.0.0.1"));
        Assert.IsFalse(respondent.Connect("127.0.0.1"));
        await respondent.ShutdownAsync();
        await respondent.ShutdownAsync();

        Assert.IsTrue(respondent.Connect("127.0.0.1"));
        await respondent.ShutdownAsync();
    }

    [TestMethod]
    public async Task RapidConnectAndShutdownDoesNotRaceReceiveLoop()
    {
        var respondent = new AppRespondent();

        for (var i = 0; i < 20; i++)
        {
            Assert.IsTrue(respondent.Connect("127.0.0.1"));
            await respondent.ShutdownAsync();
        }
    }
}