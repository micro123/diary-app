namespace Diary.Core.Data.Base;

/// <summary>标签对工作项业务行为的可移植配置。</summary>
public static class WorkTagBehavior
{
    /// <summary>命中标签的工作项仅保存在本地，不提交到任何 Tracker。</summary>
    public const string LocalOnlyMetadataKey = "diary.work.localOnly";

    public static bool IsLocalOnly(WorkTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return tag.Metadata.TryGetValue(LocalOnlyMetadataKey, out var value)
            && bool.TryParse(value, out var enabled)
            && enabled;
    }

    public static void SetLocalOnly(IDictionary<string, string> metadata, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (enabled)
            metadata[LocalOnlyMetadataKey] = bool.TrueString;
        else
            metadata.Remove(LocalOnlyMetadataKey);
    }
}
