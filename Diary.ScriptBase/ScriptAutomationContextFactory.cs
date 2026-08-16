using System.Collections.Immutable;

namespace Diary.ScriptBase;

public static class ScriptAutomationContextFactory
{
    public static ScriptAutomationContext FromRequest(ScriptExecutionRequest request) =>
        new(request.Source switch
        {
            ScriptExecutionSource.Automation => ScriptAutomationTriggerKind.Scheduled,
            ScriptExecutionSource.Startup => ScriptAutomationTriggerKind.Startup,
            ScriptExecutionSource.WorkItemCreated => ScriptAutomationTriggerKind.WorkItemCreated,
            ScriptExecutionSource.WorkItemSaved => ScriptAutomationTriggerKind.WorkItemSaved,
            ScriptExecutionSource.TagAdded => ScriptAutomationTriggerKind.TagAdded,
            _ => ScriptAutomationTriggerKind.Unknown,
        },
        request.Arguments ?? ImmutableDictionary<string, string>.Empty,
        request.IdempotencyKey);
}
