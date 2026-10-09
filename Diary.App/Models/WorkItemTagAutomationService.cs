using Diary.Core.Data.Base;
using Diary.PluginUI;

namespace Diary.App.Models;

public sealed class WorkItemTagAutomationPreparation : IDisposable
{
    private bool _disposed;

    private WorkItemTagAutomationPreparation(
        bool succeeded,
        IReadOnlyCollection<ITrackerEditorExtension> extensions,
        TagAutomationResult result,
        string? error)
    {
        Succeeded = succeeded;
        Extensions = extensions;
        Result = result;
        Error = error;
    }

    public bool Succeeded { get; }
    public IReadOnlyCollection<ITrackerEditorExtension> Extensions { get; }
    public TagAutomationResult Result { get; }
    public string? Error { get; }

    public static WorkItemTagAutomationPreparation Empty => new(
        true,
        [],
        new TagAutomationResult([]),
        null);

    public static WorkItemTagAutomationPreparation Success(
        IReadOnlyCollection<ITrackerEditorExtension> extensions,
        TagAutomationResult result) => new(true, extensions, result, null);

    public static WorkItemTagAutomationPreparation Failure(string error) => new(
        false,
        [],
        new TagAutomationResult([]),
        error);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var extension in Extensions.OfType<IDisposable>())
            extension.Dispose();
    }
}

public interface IWorkItemTagAutomationService
{
    WorkItemTagAutomationPreparation Prepare(
        WorkItem? existing,
        IReadOnlyCollection<WorkTag> addedTags,
        TagAddSource source);
}

public sealed class WorkItemTagAutomationService(
    TrackerUiContributionRegistry trackerRegistry,
    ITagAutomationCoordinator tagAutomation) : IWorkItemTagAutomationService
{
    public WorkItemTagAutomationPreparation Prepare(
        WorkItem? existing,
        IReadOnlyCollection<WorkTag> addedTags,
        TagAddSource source)
    {
        if (addedTags.Count == 0)
            return WorkItemTagAutomationPreparation.Empty;

        var extensions = new List<ITrackerEditorExtension>();
        try
        {
            foreach (var contribution in trackerRegistry.Contributions)
            {
                var extension = contribution.CreateEditorExtension(contribution.Instance.InstanceId);
                if (extension is not ITrackerTagDefaults)
                {
                    (extension as IDisposable)?.Dispose();
                    continue;
                }

                LoadExtension(contribution, extension, existing);
                extensions.Add(extension);
            }

            var instanceResults = new List<TagAutomationInstanceResult>();
            var sequence = 0;
            foreach (var tag in addedTags)
            {
                var result = tagAutomation.TagAdded(
                    existing,
                    tag,
                    new TagAutomationContext(source, sequence++),
                    extensions);
                instanceResults.AddRange(result.Instances);
            }

            var combined = new TagAutomationResult(instanceResults);
            if (!combined.Succeeded)
            {
                var errors = combined.Instances
                    .Where(instance => !instance.Succeeded)
                    .Select(instance => $"{instance.TrackerKey}: {instance.Error ?? "标签默认值应用失败"}");
                DisposeExtensions(extensions);
                return WorkItemTagAutomationPreparation.Failure(string.Join("；", errors));
            }

            var appliedKeys = combined.Instances
                .Where(instance => instance.Succeeded)
                .Select(instance => instance.TrackerKey)
                .ToHashSet();
            var saveExtensions = extensions
                .Where(extension => !extension.IsLocked && appliedKeys.Contains(extension.Key))
                .ToArray();
            DisposeExtensions(extensions.Where(extension => !saveExtensions.Contains(extension)));
            return WorkItemTagAutomationPreparation.Success(saveExtensions, combined);
        }
        catch (Exception exception)
        {
            DisposeExtensions(extensions);
            return WorkItemTagAutomationPreparation.Failure(exception.Message);
        }
    }

    private static void LoadExtension(
        ITrackerUiContribution contribution,
        ITrackerEditorExtension extension,
        WorkItem? existing)
    {
        if (existing is not { Id: > 0 })
        {
            extension.Load(null);
            return;
        }

        var bindings = contribution.Instance.LoadBindingsByDate(existing.CreateDate, [existing.Id]);
        object? binding = null;
        bindings?.TryGetValue(existing.Id, out binding);
        extension.LoadFromBatch(existing, binding);
    }

    private static void DisposeExtensions(IEnumerable<ITrackerEditorExtension> extensions)
    {
        foreach (var extension in extensions.OfType<IDisposable>())
            extension.Dispose();
    }
}
