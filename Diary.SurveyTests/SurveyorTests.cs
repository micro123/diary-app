using System.Diagnostics;
using Diary.Survey;

namespace Diary.SurveyTests;

[TestClass]
public sealed class SurveyorTests
{
    [TestMethod]
    public async Task SurveyorQuery()
    {
        var surveyor = new AppSurveyor();
        surveyor.ReceiveMessage += (_, s) => Debug.WriteLine(s);
        surveyor.StartServer();
        await Task.Delay(3000);
        surveyor.Survey("2025-07-01:2025-07-31");
        await Task.Delay(3000);
    }
}