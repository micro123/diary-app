namespace Diary.Core.Data.App;

public record Template
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string Name { get; set; } = string.Empty;
    public string DefaultTitle { get; set; } = string.Empty;
    public double DefaultTime { get; set; } = 0;
    public bool? OverwriteTitle { get; set; }
    public bool? OverwriteTime { get; set; }
    public bool? ReplaceTags { get; set; }

    public ICollection<int> DefaultWorkTags { get; set; } = Array.Empty<int>();

    public bool ShouldOverwriteTitle() => OverwriteTitle ?? !string.IsNullOrWhiteSpace(DefaultTitle);

    public bool ShouldOverwriteTime() => OverwriteTime ?? DefaultTime > 0;

}
