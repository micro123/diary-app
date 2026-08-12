using Diary.App.Models;

namespace Diary.AppTests;

[TestClass]
public sealed class TimeExpressionParserTests
{
    [TestMethod]
    public void ParsesMinutes() => AssertParses("30m", 0.5);

    [TestMethod]
    public void ParsesHoursAndMinutes() => AssertParses("1h30m", 1.5);

    [TestMethod]
    public void ParsesChineseHoursAndMinutes() => AssertParses("1小时30分钟", 1.5);

    [TestMethod]
    public void ParsesMinuteAlias() => AssertParses("90min", 1.5);

    [TestMethod]
    public void ParsesPlainHours() => AssertParses("1.5", 1.5);

    [TestMethod]
    public void RejectsMinutesGreaterThanOneHourWhenHoursArePresent()
        => AssertRejects("1h90m");

    [TestMethod]
    public void RejectsMoreThanOneDay() => AssertRejects("25h");

    [TestMethod]
    public void RejectsUnknownText() => AssertRejects("abc");

    private static void AssertParses(string expression, double expectedHours)
    {
        Assert.IsTrue(TimeExpressionParser.TryParse(expression, out var hours, out var error), error);
        Assert.AreEqual(expectedHours, hours, 0.0001);
    }

    private static void AssertRejects(string expression)
    {
        Assert.IsFalse(TimeExpressionParser.TryParse(expression, out _, out var error));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }
}
