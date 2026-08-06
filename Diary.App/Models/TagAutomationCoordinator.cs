using Diary.Core.Data.Base;
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
    string InstanceId,
    bool Succeeded,
    IReadOnlyCollection<string> AppliedFields,
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
            var instanceId = ((ITrackerEditorExtension)extension).InstanceId;
            try
            {
                results.Add(new TagAutomationInstanceResult(
                    instanceId,
                    true,
                    extension.ApplyTagDefaults(tag)));
            }
            catch (Exception ex)
            {
                results.Add(new TagAutomationInstanceResult(
                    instanceId,
                    false,
                    Array.Empty<string>(),
                    ex.Message));
            }
        }
        return new TagAutomationResult(results);
    }
}
