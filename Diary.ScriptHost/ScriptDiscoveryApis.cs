using Diary.Core.Data.App;

namespace Diary.ScriptHost;

public sealed record ScriptTemplateInfo(
    string Id,
    string Name,
    string DefaultTitle,
    double DefaultHours,
    IReadOnlyCollection<int> DefaultWorkTagIds);

public interface ITemplateScriptApi
{
    IReadOnlyList<ScriptTemplateInfo> List();
}

public sealed class TemplateScriptApi(
    Func<IReadOnlyCollection<Template>> templatesProvider) : ITemplateScriptApi
{
    public IReadOnlyList<ScriptTemplateInfo> List() =>
        templatesProvider()
            .Select(template => new ScriptTemplateInfo(
                template.Id,
                template.Name,
                template.DefaultTitle,
                template.DefaultTime,
                template.DefaultWorkTags.ToArray()))
            .OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(template => template.Id, StringComparer.Ordinal)
            .ToArray();
}
