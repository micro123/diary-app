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

public interface ITagAutomationCoordinator
{
    void TagAdded(
        WorkItem? item,
        WorkTag tag,
        TagAutomationContext context,
        IReadOnlyCollection<ITrackerEditorExtension> extensions);
}

[DiAutoRegister(singleton: true, serviceType: typeof(ITagAutomationCoordinator))]
public sealed class TagAutomationCoordinator : ITagAutomationCoordinator
{
    public void TagAdded(
        WorkItem? item,
        WorkTag tag,
        TagAutomationContext context,
        IReadOnlyCollection<ITrackerEditorExtension> extensions)
    {
        foreach (var extension in extensions.OfType<ITrackerTagDefaults>())
            extension.ApplyTagDefaults(tag);
    }
}
