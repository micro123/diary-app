using Diary.Core.Data.App;

namespace Diary.ScriptHost;

public static class ScriptHostApiCatalog
{
    public static IReadOnlyList<string> All { get; } =
    [
        "workItems.query",
        "logItems.create",
        "templateLogItems.create",
        "templates.list",
        "trackerInstances.get",
        "trackerInstances.list",
        "clipboard.get",
        "clipboard.set",
        "ui.notify",
        "ui.confirm",
        "ui.options.select",
        "ui.directory.pick",
        "ui.exported_file.open",
        "exports.formats.list",
        "exports.export",
        "log.write",
        "script.progress",
        "host.capabilities.list",
    ];
}

public interface IHostCapabilitiesScriptApi
{
    IReadOnlyList<string> List();
}

public sealed class HostCapabilitiesScriptApi(
    Func<IReadOnlyCollection<string>> capabilitiesProvider) : IHostCapabilitiesScriptApi
{
    public IReadOnlyList<string> List() =>
        capabilitiesProvider()
            .Where(capability => !string.IsNullOrWhiteSpace(capability))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToArray();
}

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
