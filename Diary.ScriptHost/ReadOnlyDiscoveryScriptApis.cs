using Diary.Core.Data.Base;
using Diary.Database;
using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.ScriptHost;

public sealed record ScriptWorkTagInfo(
    int Id,
    string Name,
    int Color,
    int Level,
    bool Disabled);

public interface IWorkTagScriptApi
{
    IReadOnlyList<ScriptWorkTagInfo> List();
}

public sealed class WorkTagScriptApi(
    Func<IReadOnlyCollection<WorkTag>> tagsProvider) : IWorkTagScriptApi
{
    public IReadOnlyList<ScriptWorkTagInfo> List() => tagsProvider()
        .Select(tag => new ScriptWorkTagInfo(
            tag.Id,
            tag.Name,
            tag.Color,
            (int)tag.Level,
            tag.Disabled))
        .OrderBy(tag => tag.Level)
        .ThenBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(tag => tag.Id)
        .ToArray();
}

public sealed record ScriptTagExtraFieldInfo(
    string FieldId,
    string FieldKey,
    int TagId,
    string TagName,
    string Label,
    string Type,
    string Description,
    int SortOrder,
    IReadOnlyList<string> Options,
    string DefaultValue,
    bool Enabled);

public interface ITagExtraFieldScriptApi
{
    IReadOnlyList<ScriptTagExtraFieldInfo> List(bool includeDisabled = false);
}

public sealed class TagExtraFieldScriptApi(
    Func<DbInterfaceBase?> databaseProvider) : ITagExtraFieldScriptApi
{
    public IReadOnlyList<ScriptTagExtraFieldInfo> List(bool includeDisabled = false)
    {
        var database = databaseProvider();
        if (database is null)
            return [];
        var tags = database.AllWorkTags().ToDictionary(tag => tag.Id);
        return database.GetAllTagExtraFieldDefinitions(includeDisabled)
            .Where(field => tags.ContainsKey(field.TagId))
            .Select(field => new ScriptTagExtraFieldInfo(
                field.FieldId,
                field.FieldKey,
                field.TagId,
                tags[field.TagId].Name,
                field.Label,
                field.Type.ToString(),
                field.Description,
                field.SortOrder,
                field.Options,
                field.DefaultValue,
                field.Enabled))
            .OrderBy(field => field.TagName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(field => field.SortOrder)
            .ThenBy(field => field.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(field => field.FieldId, StringComparer.Ordinal)
            .ToArray();
    }
}

public sealed record ScriptCurrentContext(
    string CurrentDate,
    int? SelectedWorkItemId,
    string? SelectedWorkItemTitle);

public interface ICurrentContextScriptApi
{
    ScriptCurrentContext Get();
}

public sealed class CurrentContextScriptApi(
    Func<ScriptCurrentContext> contextProvider) : ICurrentContextScriptApi
{
    public ScriptCurrentContext Get() => contextProvider();
}

public sealed record ScriptValidationInfo(
    bool Succeeded,
    IReadOnlyList<ScriptDiagnostic> Diagnostics,
    string? EngineName = null);

public interface IScriptValidationScriptApi
{
    ValueTask<ScriptValidationInfo> ValidateAsync(
        string language,
        string source,
        CancellationToken cancellationToken = default);
}

public sealed class ScriptValidationScriptApi(
    IScriptBuildService buildService) : IScriptValidationScriptApi
{
    public async ValueTask<ScriptValidationInfo> ValidateAsync(
        string language,
        string source,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return new ScriptValidationInfo(
                false,
                [new ScriptDiagnostic(
                    "SCRIPT_LANGUAGE_REQUIRED",
                    "Script language is required.",
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCategory.Validation)]);
        }
        if (source.Length > 256 * 1024)
        {
            return new ScriptValidationInfo(
                false,
                [new ScriptDiagnostic(
                    "SCRIPT_SOURCE_TOO_LARGE",
                    "Script source exceeds the validation size limit.",
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCategory.Validation)]);
        }
        var extension = language.Trim().ToLowerInvariant() switch
        {
            "csharp" or "c#" or "cs" => ".cs",
            "lua" => ".lua",
            "python" or "py" => ".py",
            _ => null,
        };
        if (extension is null)
        {
            return new ScriptValidationInfo(
                false,
                [new ScriptDiagnostic(
                    "SCRIPT_LANGUAGE_UNSUPPORTED",
                    "The requested script language is not supported.",
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCategory.Validation)]);
        }
        var result = await buildService.BuildAsync(
            new ScriptBuildRequest("agent-validation" + extension, source),
            cancellationToken);
        return new ScriptValidationInfo(
            result.Succeeded,
            result.Diagnostics.IsDefault ? [] : result.Diagnostics,
            result.EngineName);
    }
}
