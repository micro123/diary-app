using Diary.Core.Data.Base;
using Diary.PluginBase;
using Diary.PluginUI;
using Diary.Utils;

namespace Diary.App.Models;

public enum TagAddSource
{
    User,
    Template,
    Batch,
}

public sealed record TagAutomationContext(
    TagAddSource Source,
    int Sequence);

public sealed record TagAutomationInstanceResult(
    TrackerKey TrackerKey,
    bool Succeeded,
    IReadOnlyCollection<string> ChangedFields,
    IReadOnlyCollection<TrackerTagDefaultConflict> Conflicts,
    IReadOnlyCollection<TrackerTagDefaultInvalidTarget> InvalidTargets,
    string? Error = null);

public sealed record TagAutomationResult(
    IReadOnlyCollection<TagAutomationInstanceResult> Instances)
{
    public bool Succeeded => Instances.All(instance => instance.Succeeded);
}

public interface ITagAutomationCoordinator
{
    TagAutomationResult TagAdded(
        WorkItem? item,
        WorkTag tag,
        TagAutomationContext context,
        IReadOnlyCollection<ITrackerEditorExtension> extensions);
}

[DiAutoRegister(singleton: true, serviceType: typeof(ITagAutomationCoordinator))]
public sealed class TagAutomationCoordinator : ITagAutomationCoordinator
{
    public TagAutomationResult TagAdded(
        WorkItem? item,
        WorkTag tag,
        TagAutomationContext context,
        IReadOnlyCollection<ITrackerEditorExtension> extensions)
    {
        var results = new List<TagAutomationInstanceResult>();
        foreach (var extension in extensions.OfType<ITrackerTagDefaults>())
        {
            var key = ((ITrackerEditorExtension)extension).Key;
            try
            {
                var applied = extension.ApplyTagDefaults(tag);
                results.Add(new TagAutomationInstanceResult(
                    key,
                    true,
                    applied.ChangedFields,
                    applied.Conflicts,
                    applied.InvalidTargets));
            }
            catch (Exception ex)
            {
                results.Add(new TagAutomationInstanceResult(
                    key,
                    false,
                    Array.Empty<string>(),
                    Array.Empty<TrackerTagDefaultConflict>(),
                    Array.Empty<TrackerTagDefaultInvalidTarget>(),
                    ex.Message));
            }
        }
        return new TagAutomationResult(results);
    }
}
