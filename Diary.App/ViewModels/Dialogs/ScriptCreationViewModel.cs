using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.ViewModels;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels.Dialogs;

[DiAutoRegister]
public partial class ScriptCreationViewModel : ViewModelBase, IDialogContext
{
    private const string BlankTemplate = "空白脚本";
    private const string WorkItemQueryTemplate = "查询工作项";
    private const string DayTargetTemplate = "日目标脚本";
    private const string MonthTargetTemplate = "月目标脚本";
    private const string QuarterTargetTemplate = "季度目标脚本";
    private const string YearTargetTemplate = "年目标脚本";
    private const string WorkItemTargetTemplate = "当前事项脚本";
    private readonly string _scriptRoot;

    private static readonly IReadOnlyList<string> ApplicationTemplates =
        [BlankTemplate, WorkItemQueryTemplate];
    private static readonly IReadOnlyList<string> EditorTemplates =
        [
            BlankTemplate,
            WorkItemQueryTemplate,
            DayTargetTemplate,
            MonthTargetTemplate,
            QuarterTargetTemplate,
            YearTargetTemplate,
            WorkItemTargetTemplate,
        ];

    public ScriptCreationViewModel(string? scriptRoot = null)
    {
        _scriptRoot = scriptRoot ?? Path.Combine(FsTools.GetApplicationConfigDirectory(), "scripts");
    }

    public IReadOnlyList<string> Scopes { get; } = ["应用脚本", "编辑器脚本"];
    public IReadOnlyList<string> Languages { get; } = ["C#", "Lua", "Python"];
    public IReadOnlyList<string> Templates => IsEditorScope ? EditorTemplates : ApplicationTemplates;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _name = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _id = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _selectedScope = "应用脚本";
    [ObservableProperty] private string _selectedLanguage = "C#";
    [ObservableProperty] private string _selectedTemplate = BlankTemplate;
    [ObservableProperty] private string _error = string.Empty;
    [ObservableProperty] private bool _creating;

    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool IsEditorScope => SelectedScope == "编辑器脚本";

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnSelectedScopeChanged(string value)
    {
        OnPropertyChanged(nameof(IsEditorScope));
        OnPropertyChanged(nameof(Templates));
        if (!Templates.Contains(SelectedTemplate))
            SelectedTemplate = BlankTemplate;
    }

    [RelayCommand]
    public void Close() => RequestClose?.Invoke(this, null);

    public event EventHandler<object?>? RequestClose;

    private bool CanCreate() => !Creating
        && !string.IsNullOrWhiteSpace(Name)
        && ScriptCreationPolicy.IsValidId(Id);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task Create()
    {
        Error = string.Empty;
        Creating = true;
        CreateCommand.NotifyCanExecuteChanged();
        try
        {
            var scope = SelectedScope == "编辑器脚本" ? ScriptScope.Editor : ScriptScope.Application;
            var folder = Path.Combine(_scriptRoot, scope == ScriptScope.Editor ? "editor" : "application");
            var root = Path.GetFullPath(_scriptRoot);
            var fullFolder = Path.GetFullPath(folder);
            if (!ScriptCreationPolicy.IsInsideDirectory(fullFolder, root))
            {
                Error = "脚本目标目录无效。";
                return;
            }

            Directory.CreateDirectory(fullFolder);
            var sourcePath = Path.GetFullPath(Path.Combine(fullFolder, $"{Id}{GetLanguageExtension()}"));
            if (!ScriptCreationPolicy.IsInsideDirectory(sourcePath, fullFolder))
            {
                Error = "脚本 ID 生成的文件路径无效。";
                return;
            }
            var metadataPath = sourcePath + ".json";
            if (File.Exists(sourcePath) || File.Exists(metadataPath))
            {
                Error = "该脚本 ID 已存在，请换一个稳定 ID。";
                return;
            }

            var source = CreateSource(scope);
            var metadata = JsonSerializer.Serialize(new ScriptFileMetadata(
                ApiVersion: ScriptApiVersion.V1,
                Id: Id,
                Name: Name,
                Description: Description,
                Engine: GetEngineName(),
                Scope: scope,
                SupportedEditorTargets: GetSupportedEditorTargets(),
                EntryKind: scope == ScriptScope.Editor ? ScriptEntryKind.Editor : ScriptEntryKind.Application), new JsonSerializerOptions { WriteIndented = true });
            await WriteFilesAtomicallyAsync(sourcePath, source, metadataPath, metadata);
            RequestClose?.Invoke(this, sourcePath);
        }
        catch (Exception exception)
        {
            Error = $"创建脚本失败：{exception.Message}";
        }
        finally
        {
            Creating = false;
            CreateCommand.NotifyCanExecuteChanged();
        }
    }

    private static string ToClassName(string value) => string.Concat(value
        .Split(['-', '.', '_'], StringSplitOptions.RemoveEmptyEntries)
        .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private string GetLanguageExtension() => SelectedLanguage switch
    {
        "C#" => ".cs",
        "Lua" => ".lua",
        "Python" => ".py",
        _ => throw new InvalidOperationException($"不支持的脚本语言：{SelectedLanguage}"),
    };

    private string GetEngineName() => SelectedLanguage switch
    {
        "C#" => "csharp",
        "Lua" => "lua",
        "Python" => "python",
        _ => throw new InvalidOperationException($"不支持的脚本语言：{SelectedLanguage}"),
    };

    private string CreateSource(ScriptScope scope)
    {
        var entryName = scope == ScriptScope.Editor ? "editor_main" : "application_main";
        return SelectedLanguage switch
        {
            "C#" => CreateCSharpSource(scope),
            "Lua" when IsEditorTargetTemplate => CreateLuaTargetSource(scope),
            "Lua" when SelectedTemplate == WorkItemQueryTemplate && scope == ScriptScope.Editor => string.Join(Environment.NewLine, [
                $"function {entryName}(context)",
            "    local range = context.getDateRange()",
            "    if range ~= nil then",
            "        for item in context.items.stream() do",
            "            print(item.date .. ': ' .. item.comment)",
            "        end",
            "    elseif context.workItem ~= nil then",
            "        print(context.workItem.comment)",
            "    end",
            "end", ""]),
            "Lua" when SelectedTemplate == WorkItemQueryTemplate => string.Join(Environment.NewLine, [
    $"function {entryName}(context)",
            "    local result = diary.workItems.query({ limit = 100 })",
            "    if not result.succeeded then",
            "        error(result.error.message)",
            "    end",
            "end", ""]),
            "Lua" => string.Join(Environment.NewLine, [
                $"function {entryName}(context)",
            "    -- context.target、context.dateRange 和 context.workItem 来自上下文菜单。",
            "    -- context.getDateRange() 在事项目标下返回 nil。",
            "    diary.log.debug(\"开始执行脚本\")",
            "end", ""]),
            "Python" when IsEditorTargetTemplate => CreatePythonTargetSource(scope),
            "Python" when SelectedTemplate == WorkItemQueryTemplate && scope == ScriptScope.Editor => string.Join(Environment.NewLine, [
                $"def {entryName}(context):",
            "    date_range = context.getDateRange()",
            "    if date_range is not None:",
            "        for item in context.items.stream():",
            "            print(item['date'], item['comment'])",
            "    elif context.workItem is not None:",
            "        print(context.workItem['comment'])",
            "    return None", ""]),
            "Python" when SelectedTemplate == WorkItemQueryTemplate => string.Join(Environment.NewLine, [
    $"def {entryName}(context):",
            "    result = context.diary.workItems.query(limit=100)",
            "    if not result[\"succeeded\"]:",
            "        raise RuntimeError(result[\"error\"][\"message\"])",
            "    return None", ""]),
            "Python" => string.Join(Environment.NewLine, [
                $"def {entryName}(context):",
            "    # context.target、context.dateRange 和 context.workItem 来自上下文菜单。",
            "    # context.getDateRange() 在事项目标下返回 None。",
            "    context.log.debug(\"开始执行脚本\")",
            "    return None", ""]),
            _ => throw new InvalidOperationException($"不支持的脚本语言：{SelectedLanguage}"),
        };
    }

    private string CreateCSharpSource(ScriptScope scope)
    {
        var className = ToClassName(Id);
        var lines = new List<string>
        {
            "#nullable enable", "using System;", "using System.Collections.Generic;", "using System.Threading;", "using System.Threading.Tasks;", "using Diary.ScriptBase;",
        };
        if (SelectedTemplate == WorkItemQueryTemplate && scope == ScriptScope.Application)
            lines.Add("using Diary.ScriptHost;");
        var baseType = scope == ScriptScope.Editor ? "EditorScript" : "ApplicationScript";
        var contextType = scope == ScriptScope.Editor ? "IScriptEditorContext" : "IScriptApplicationContext";
        lines.AddRange([
            "",
            "namespace Diary.UserScripts;", "",
            $"public sealed class {className} : {baseType}", "{",
            $"    public override string Id => \"{Escape(Id)}\";",
            $"    public override string Name => \"{Escape(Name)}\";",
            $"    public override string? Description => \"{Escape(Description)}\";",
        ]);
        if (IsEditorTargetTemplate)
            lines.Add($"    public override IReadOnlyList<ScriptEditorTargetKind>? SupportedTargets => [ScriptEditorTargetKind.{GetSupportedEditorTargets()!.Single()}];");
        lines.AddRange([
            "",
            $"    public override {(SelectedTemplate == WorkItemQueryTemplate || IsEditorTargetTemplate ? "async " : string.Empty)}ValueTask<ScriptExecutionResult> ExecuteAsync(",
            $"        {contextType} context,",
            "        CancellationToken cancellationToken = default)", "    {",
        ]);
        if (SelectedTemplate == WorkItemQueryTemplate && scope == ScriptScope.Editor)
        {
            lines.AddRange([
                "        if (context is not IScriptEditorContext editor)",
                "            return new ScriptExecutionResult(ScriptExecutionStatus.Rejected, []);",
                "        if (context.GetDateRange() is not null)",
                "        {",
                "            await foreach (var item in context.StreamItemsAsync(cancellationToken))",
                "            {",
                "                _ = item;",
                "            }",
                "        }",
                "        else if (context.WorkItem is not null)",
                "        {",
                "            _ = context.WorkItem;",
                "        }",
                "        return ScriptExecutionResult.Succeeded();",
            ]);
        }
        else if (IsEditorTargetTemplate)
        {
            AddCSharpEditorTargetTemplate(lines);
        }
        else if (SelectedTemplate == WorkItemQueryTemplate)
        {
            lines.AddRange([
                "        var api = context.GetApi<IDiaryApi>();",
                "        if (api is null)",
                "            return new ScriptExecutionResult(ScriptExecutionStatus.Rejected, []);",
                "        var result = await api.QueryAsync(new ScriptWorkItemQuery { Limit = 100 }, cancellationToken);",
                "        return result.Succeeded",
                "            ? ScriptExecutionResult.Succeeded()",
                "            : new ScriptExecutionResult(ScriptExecutionStatus.Failed, []);",
            ]);
        }
        else
        {
            lines.Add("        return ValueTask.FromResult(ScriptExecutionResult.Succeeded());");
        }
        lines.AddRange(["    }", "}", ""]);
        var source = string.Join(Environment.NewLine, lines);
        if (GetSupportedEditorTargets() is { } supportedTargets)
        {
            var descriptorEnd = $"        \"{Escape(Description)}\");";
            var targetList = string.Join(", ", supportedTargets.Select(target => $"ScriptEditorTargetKind.{target}"));
            source = source.Replace(
                descriptorEnd,
                $"        \"{Escape(Description)}\", SupportedEditorTargets: [{targetList}]);",
                StringComparison.Ordinal);
        }
        return source;
    }

    private bool IsEditorTargetTemplate =>
        IsEditorScope && SelectedTemplate is not BlankTemplate and not WorkItemQueryTemplate;

    private void AddCSharpEditorTargetTemplate(List<string> lines)
    {
        lines.AddRange([
            "        if (context is not IScriptEditorContext editor)",
            "            return new ScriptExecutionResult(ScriptExecutionStatus.Rejected, []);",
        ]);
        if (SelectedTemplate == WorkItemTargetTemplate)
        {
            lines.AddRange([
                "        if (editor.WorkItem is null)",
                "            return new ScriptExecutionResult(ScriptExecutionStatus.Rejected, []);",
                "        _ = editor.WorkItem;",
            ]);
        }
        else
        {
            lines.AddRange([
                "        if (editor.GetDateRange() is null)",
                "            return new ScriptExecutionResult(ScriptExecutionStatus.Rejected, []);",
                "        await foreach (var item in editor.StreamItemsAsync(cancellationToken))",
                "            _ = item;",
            ]);
        }
        lines.Add("        return ScriptExecutionResult.Succeeded();");
    }

    private string CreateLuaTargetSource(ScriptScope scope)
    {
        var entryName = scope == ScriptScope.Editor ? "editor_main" : "application_main";
        return string.Join(Environment.NewLine,
        SelectedTemplate == WorkItemTargetTemplate
            ? [
$"function {entryName}(context)",
                "    if context.workItem == nil then error('需要当前事项目标') end",
                "    print(context.workItem.comment)",
                "end", "",
            ]
            : [
                $"function {entryName}(context)",
                "    if context.getDateRange() == nil then error('需要日期目标') end",
                "    for item in context.items.stream() do",
                "        print(item.date .. ': ' .. item.comment)",
                "    end",
                "end", "",
            ]);
    }

    private string CreatePythonTargetSource(ScriptScope scope)
    {
        var entryName = scope == ScriptScope.Editor ? "editor_main" : "application_main";
        return string.Join(Environment.NewLine,
        SelectedTemplate == WorkItemTargetTemplate
            ? [
$"def {entryName}(context):",
                "    if context.workItem is None:",
                "        raise RuntimeError('需要当前事项目标')",
                "    print(context.workItem['comment'])",
                "    return None", "",
            ]
            : [
                $"def {entryName}(context):",
                "    if context.getDateRange() is None:",
                "        raise RuntimeError('需要日期目标')",
                "    for item in context.items.stream():",
                "        print(item['date'], item['comment'])",
                "    return None", "",
            ]);
    }

    private IReadOnlyList<ScriptEditorTargetKind>? GetSupportedEditorTargets() =>
        !IsEditorScope || SelectedTemplate is BlankTemplate or WorkItemQueryTemplate
            ? null
            : SelectedTemplate switch
            {
                DayTargetTemplate => [ScriptEditorTargetKind.Day],
                MonthTargetTemplate => [ScriptEditorTargetKind.Month],
                QuarterTargetTemplate => [ScriptEditorTargetKind.Quarter],
                YearTargetTemplate => [ScriptEditorTargetKind.Year],
                WorkItemTargetTemplate => [ScriptEditorTargetKind.WorkItem],
                _ => null,
            };


    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");

    private static async Task WriteFilesAtomicallyAsync(
        string sourcePath,
        string source,
        string metadataPath,
        string metadata)
    {
        var sourceTempPath = sourcePath + $".{Guid.NewGuid():N}.tmp";
        var metadataTempPath = metadataPath + $".{Guid.NewGuid():N}.tmp";
        var sourceCreated = false;
        var metadataCreated = false;
        try
        {
            await File.WriteAllTextAsync(sourceTempPath, source);
            await File.WriteAllTextAsync(metadataTempPath, metadata);
            File.Move(sourceTempPath, sourcePath);
            sourceCreated = true;
            File.Move(metadataTempPath, metadataPath);
            metadataCreated = true;
        }
        finally
        {
            DeleteIfExists(sourceTempPath);
            DeleteIfExists(metadataTempPath);
            if (sourceCreated && !metadataCreated)
                DeleteIfExists(sourcePath);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
