using System.Text.Json;
using Diary.Agent.Configuration;
using Diary.Agent.Tools;

namespace Diary.AgentTests;

[TestClass]
public sealed class TimeCalculationToolTests
{
    [TestMethod]
    public async Task AddDateHandlesLeapYearAndMonthBoundary()
    {
        var tool = new TimeCalculationTool();

        var result = await InvokeAsync(tool, new
        {
            operation = "add",
            value = "2024-01-31",
            months = 1,
        });

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("diary_calculate_time", tool.Descriptor.ModelName);
        Assert.AreEqual(AgentToolRisk.ReadOnly, tool.Descriptor.Risk);
        Assert.IsTrue(AgentToolPermissionPolicy.IsEnabled(tool.Descriptor, new AiAgentToolSettings()));
        using var document = JsonDocument.Parse(result.Content);
        Assert.AreEqual("date", document.RootElement.GetProperty("inputKind").GetString());
        Assert.AreEqual("2024-02-29", document.RootElement.GetProperty("result").GetString());
        Assert.AreEqual("Thursday", document.RootElement.GetProperty("dayOfWeek").GetString());
    }

    [TestMethod]
    public async Task AddDateTimePreservesOffsetAndReturnsUtcResult()
    {
        var result = await InvokeAsync(new TimeCalculationTool(), new
        {
            operation = "add",
            value = "2026-10-26T23:30:00+08:00",
            hours = 2,
            minutes = 15,
        });

        Assert.IsTrue(result.Succeeded);
        using var document = JsonDocument.Parse(result.Content);
        Assert.AreEqual("2026-10-27T01:45:00+08:00", document.RootElement.GetProperty("result").GetString());
        Assert.AreEqual("2026-10-26T17:45:00Z", document.RootElement.GetProperty("utcResult").GetString());
    }

    [TestMethod]
    public async Task DifferenceSupportsDatesAndOffsetAwareDateTimes()
    {
        var tool = new TimeCalculationTool();
        var dateResult = await InvokeAsync(tool, new
        {
            operation = "difference",
            start = "2026-10-01",
            end = "2026-10-26",
        });
        var timeResult = await InvokeAsync(tool, new
        {
            operation = "difference",
            start = "2026-10-26T08:00:00+08:00",
            end = "2026-10-26T01:00:00Z",
        });

        Assert.IsTrue(dateResult.Succeeded);
        Assert.IsTrue(timeResult.Succeeded);
        using var dateDocument = JsonDocument.Parse(dateResult.Content);
        using var timeDocument = JsonDocument.Parse(timeResult.Content);
        Assert.AreEqual(25, dateDocument.RootElement.GetProperty("calendarDays").GetInt32());
        Assert.AreEqual(1, timeDocument.RootElement.GetProperty("totalHours").GetDouble());
        Assert.AreEqual("end_after_start", timeDocument.RootElement.GetProperty("relation").GetString());
    }

    [TestMethod]
    public async Task RejectsImplicitTimezoneMixedKindsAndDateTimeUnitsForDate()
    {
        var tool = new TimeCalculationTool();
        var implicitTimezone = await InvokeAsync(tool, new
        {
            operation = "add",
            value = "2026-10-26T08:00:00",
            hours = 1,
        });
        var mixedKinds = await InvokeAsync(tool, new
        {
            operation = "difference",
            start = "2026-10-26",
            end = "2026-10-27T00:00:00+08:00",
        });
        var dateWithHours = await InvokeAsync(tool, new
        {
            operation = "add",
            value = "2026-10-26",
            hours = 1,
        });

        Assert.IsFalse(implicitTimezone.Succeeded);
        Assert.AreEqual("invalid_arguments", implicitTimezone.ErrorCode);
        StringAssert.Contains(implicitTimezone.Content, "UTC 偏移");
        Assert.IsFalse(mixedKinds.Succeeded);
        StringAssert.Contains(mixedKinds.Content, "必须同为");
        Assert.IsFalse(dateWithHours.Succeeded);
        StringAssert.Contains(dateWithHours.Content, "纯日期");
    }

    private static async Task<AgentToolResult> InvokeAsync(TimeCalculationTool tool, object arguments)
    {
        var element = JsonSerializer.SerializeToElement(arguments);
        return await tool.InvokeAsync(
            element,
            new AgentToolInvocationContext(Guid.NewGuid(), Guid.NewGuid(), EmptyServices.Instance));
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static EmptyServices Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
