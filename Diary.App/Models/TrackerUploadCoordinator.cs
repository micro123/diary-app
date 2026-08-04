using Diary.Core.Data.Base;
using Diary.PluginBase;
using Diary.PluginUI;
using Diary.Utils;

namespace Diary.App.Models;

public sealed record TrackerUploadResult(
    TrackerKey Key,
    bool Success,
    bool Skipped,
    string? Error = null,
    string? RemoteId = null);

public sealed record WorkUploadResult(IReadOnlyList<TrackerUploadResult> Results)
{
    public bool Success => Results.Count > 0 && Results.All(x => x.Success || x.Skipped);

    public string? Error => string.Join(
        "; ",
        Results.Where(x => !x.Success && !string.IsNullOrWhiteSpace(x.Error))
            .Select(x => $"{x.Key.PluginId}/{x.Key.InstanceId}: {x.Error}"));
}

public interface ITrackerUploadCoordinator
{
    Task<WorkUploadResult> UploadAsync(
        WorkItem item,
        IReadOnlyCollection<ITrackerEditorExtension> extensions);
}

[DiAutoRegister(singleton: true, serviceType: typeof(ITrackerUploadCoordinator))]
public sealed class TrackerUploadCoordinator : ITrackerUploadCoordinator
{
    public async Task<WorkUploadResult> UploadAsync(
        WorkItem item,
        IReadOnlyCollection<ITrackerEditorExtension> extensions)
    {
        var results = new List<TrackerUploadResult>(extensions.Count);
        foreach (var extension in extensions)
        {
            if (extension.IsLocked)
            {
                results.Add(new TrackerUploadResult(extension.Key, true, true));
                continue;
            }

            try
            {
                var result = await extension.UploadAsync(item);
                results.Add(new TrackerUploadResult(
                    extension.Key,
                    result.Success,
                    false,
                    result.Error,
                    result.RemoteId));
            }
            catch (Exception ex)
            {
                results.Add(new TrackerUploadResult(
                    extension.Key,
                    false,
                    false,
                    ex.Message));
            }
        }

        return new WorkUploadResult(results);
    }
}
