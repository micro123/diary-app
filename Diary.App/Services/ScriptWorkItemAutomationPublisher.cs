using System.Globalization;
using Diary.ScriptHost;

namespace Diary.App.Services;

public sealed class ScriptWorkItemAutomationPublisher(
    ScriptAutomationScheduler scheduler) : IWorkItemAutomationPublisher
{
    public void Publish(WorkItemAutomationEvent automationEvent)
    {
        ArgumentNullException.ThrowIfNull(automationEvent);
        var eventData = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workItemId"] = automationEvent.WorkItemId.ToString(CultureInfo.InvariantCulture),
            ["date"] = automationEvent.Date,
            ["comment"] = automationEvent.Title,
            ["time"] = automationEvent.Hours.ToString(CultureInfo.InvariantCulture),
            ["priority"] = automationEvent.Priority.ToString(CultureInfo.InvariantCulture),
        };
        if (automationEvent.TagId is { } tagId)
        {
            eventData["tagId"] = tagId.ToString(CultureInfo.InvariantCulture);
            eventData["tagName"] = automationEvent.TagName ?? string.Empty;
            eventData["tagLevel"] = automationEvent.TagLevel?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            eventData["tagSource"] = automationEvent.Source;
            eventData["sequence"] = automationEvent.Sequence.ToString(CultureInfo.InvariantCulture);
        }
        _ = scheduler.TriggerAsync(automationEvent.Trigger, eventData);
    }
}
