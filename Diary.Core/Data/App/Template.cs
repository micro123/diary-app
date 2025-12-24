namespace Diary.Core.Data.App;

public record Template
{
    public string Name { get; set; } =  string.Empty;
    public string DefaultTitle { get; set; } =  string.Empty;
    public double DefaultTime { get; set; } =  0;
    public int DefaultActivity {get; set; } = 0;
    public int DefaultIssue { get; set; } = 0;
    public ICollection<int> DefaultWorkTags { get; set; } = Array.Empty<int>();
}
