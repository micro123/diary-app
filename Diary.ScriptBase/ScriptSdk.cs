namespace Diary.ScriptBase;

public abstract class ApplicationScript : IApplicationScriptV1
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public virtual string? Description => null;

    public ScriptDescriptor Descriptor => new(
        Id,
        Name,
        ScriptApiVersion.V1,
        ScriptScope.Application,
        Description,
        EntryKind: ScriptEntryKind.Application);

    public abstract ValueTask<ScriptExecutionResult> ExecuteAsync(
        IScriptApplicationContext context,
        CancellationToken cancellationToken = default);
}

public abstract class EditorScript : IEditorScriptV1
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public virtual string? Description => null;
    public virtual IReadOnlyList<ScriptEditorTargetKind>? SupportedTargets => null;

    public ScriptDescriptor Descriptor => new(
        Id,
        Name,
        ScriptApiVersion.V1,
        ScriptScope.Editor,
        Description,
        SupportedTargets,
        ScriptEntryKind.Editor);

    public abstract ValueTask<ScriptExecutionResult> ExecuteAsync(
        IScriptEditorContext context,
        CancellationToken cancellationToken = default);
}

public abstract class AutomationScript : IAutomationScriptV1
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public virtual string? Description => null;

    public ScriptDescriptor Descriptor => new(
        Id,
        Name,
        ScriptApiVersion.V1,
        ScriptScope.Application,
        Description,
        EntryKind: ScriptEntryKind.Automation);

    public abstract ValueTask<ScriptExecutionResult> ExecuteAsync(
        IScriptAutomationContext context,
        CancellationToken cancellationToken = default);
}

public abstract class QueryScript : IQueryScriptV1
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public virtual string? Description => null;

    public ScriptDescriptor Descriptor => new(
        Id,
        Name,
        ScriptApiVersion.V1,
        ScriptScope.Application,
        Description,
        EntryKind: ScriptEntryKind.Query);

    public abstract ValueTask<ScriptExecutionResult> ExecuteAsync(
        IScriptApplicationContext context,
        CancellationToken cancellationToken = default);
}
