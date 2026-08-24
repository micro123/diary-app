using Diary.App.Models;
using Diary.App.ViewModels;

namespace Diary.UtilTests;

[TestClass]
public sealed class SurveyResultTests
{
    [TestMethod]
    public void ZeroTotalProducesFiniteZeroPercentages()
    {
        var data = new RespondData
        {
            Hostname = "host",
            Username = "user",
            Tags =
            [
                new RespondTag
                {
                    TagName = "primary",
                    TagTime = 0,
                    SubTags = [new RespondTag { TagName = "secondary", TagTime = 0 }],
                },
            ],
        };

        _ = new SurveyResult(data, 0);

        Assert.AreEqual(0, data.Tags[0].Percent);
        Assert.AreEqual(0, data.Tags[0].SubTags[0].Percent);
        Assert.IsFalse(double.IsNaN(data.Tags[0].Percent));
        Assert.IsFalse(double.IsInfinity(data.Tags[0].Percent));
    }

    [TestMethod]
    public void ResultTreeStartsCollapsed()
    {
        var data = new RespondData
        {
            Hostname = "host",
            Username = "user",
            Tags =
            [
                new RespondTag
                {
                    TagName = "primary",
                    TagTime = 1,
                    SubTags = [new RespondTag { TagName = "secondary", TagTime = 1 }],
                },
            ],
        };

        var result = new SurveyResult(data, 1);

        Assert.AreEqual(1, result.GridSource.Rows.Count);
    }
}
