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
    private readonly string _scriptRoot;

    public ScriptCreationViewModel(string? scriptRoot = null)
    {
        _scriptRoot = scriptRoot ?? Path.Combine(FsTools.GetApplicationConfigDirectory(), "scripts");
    }

    public IReadOnlyList<string> Scopes { get; } = ["应用脚本", "编辑器脚本"];
    public IReadOnlyList<string> Languages { get; } = ["C#", "Lua", "Python"];
    public IReadOnlyList<string> Templates { get; } = [BlankTemplate, WorkItemQueryTemplate];

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
    [ObservableProperty] private bool _readDiary;
    [ObservableProperty] private bool _writeDiary;
    [ObservableProperty] private bool _userInteraction;
    [ObservableProperty] private bool _clipboard;
    [ObservableProperty] private bool _tracker;
    [ObservableProperty] private string _error = string.Empty;
    [ObservableProperty] private bool _creating;

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnSelectedTemplateChanged(string value)
    {
        if (value == WorkItemQueryTemplate)
            ReadDiary = true;
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
                Capabilities: SelectedCapabilities,
                Engine: GetEngineName(),
                Scope: scope), new JsonSerializerOptions { WriteIndented = true });
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

    private string CreateSource(ScriptScope scope) => SelectedLanguage switch
    {
        "C#" => CreateCSharpSource(scope),
        "Lua" when SelectedTemplate == WorkItemQueryTemplate => string.Join(Environment.NewLine, [
            "function main(context)",
            "    local result = diary.workItems.query({ limit = 100 })",
            "    if not result.succeeded then",
            "        error(result.error.message)",
            "    end",
            "end", ""]),
        "Lua" => string.Join(Environment.NewLine, [
            "function main(context)",
            "    -- context.request 包含本次执行目标。",
            "end", ""]),
        "Python" when SelectedTemplate == WorkItemQueryTemplate => string.Join(Environment.NewLine, [
            "def main(context):",
            "    result = context.diary.workItems.query(limit=100)",
            "    if not result[\"succeeded\"]:",
            "        raise RuntimeError(result[\"error\"][\"message\"])",
            "    return None", ""]),
        "Python" => string.Join(Environment.NewLine, [
            "def main(context):",
            "    # context.target 包含本次执行目标。",
            "    return None", ""]),
        _ => throw new InvalidOperationException($"不支持的脚本语言：{SelectedLanguage}"),
    };

    private string CreateCSharpSource(ScriptScope scope)
    {
        var className = ToClassName(Id);
        var lines = new List<string>
        {
            "using System;", "using System.Threading;", "using System.Threading.Tasks;", "using Diary.ScriptBase;",
        };
        if (SelectedTemplate == WorkItemQueryTemplate)
            lines.Add("using Diary.ScriptHost;");
        lines.AddRange([
            "",
            "namespace Diary.UserScripts;", "",
            $"public sealed class {className} : IScriptProgramV1", "{",
            "    public ScriptDescriptor Descriptor { get; } = new(",
            $"        \"{Escape(Id)}\",", $"        \"{Escape(Name)}\",",
            "        ScriptApiVersion.V1,", $"        ScriptScope.{scope},",
            $"        {FormatCapabilities()},", $"        \"{Escape(Description)}\");", "",
            $"    public {(SelectedTemplate == WorkItemQueryTemplate ? "async " : string.Empty)}ValueTask<ScriptExecutionResult> ExecuteAsync(",
            "        ScriptExecutionRequest request,",
            "        IScriptExecutionContext context,",
            "        CancellationToken cancellationToken = default)", "    {",
        ]);
        if (SelectedTemplate == WorkItemQueryTemplate)
        {
            lines.AddRange([
                "        var api = context.GetApi<IWorkItemQueryScriptApi>();",
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
        return string.Join(Environment.NewLine, lines);
    }

    private ScriptCapability SelectedCapabilities =>
        (ReadDiary ? ScriptCapability.ReadDiary : ScriptCapability.None)
        | (WriteDiary ? ScriptCapability.WriteDiary : ScriptCapability.None)
        | (UserInteraction ? ScriptCapability.UserInteraction : ScriptCapability.None)
        | (Clipboard ? ScriptCapability.Clipboard : ScriptCapability.None)
        | (Tracker ? ScriptCapability.Tracker : ScriptCapability.None);

    private string FormatCapabilities()
    {
        if (SelectedCapabilities == ScriptCapability.None)
            return "ScriptCapability.None";
        return string.Join(" | ", Enum.GetValues<ScriptCapability>()
            .Where(capability => capability != ScriptCapability.None && SelectedCapabilities.HasFlag(capability))
            .Select(capability => $"ScriptCapability.{capability}"));
    }

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
