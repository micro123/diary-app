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
    private readonly string _scriptRoot = Path.Combine(FsTools.GetApplicationConfigDirectory(), "scripts");

    public IReadOnlyList<string> Scopes { get; } = ["应用脚本", "编辑器脚本"];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _name = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _id = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _selectedScope = "应用脚本";
    [ObservableProperty] private bool _readDiary;
    [ObservableProperty] private bool _writeDiary;
    [ObservableProperty] private bool _userInteraction;
    [ObservableProperty] private bool _clipboard;
    [ObservableProperty] private bool _tracker;
    [ObservableProperty] private string _error = string.Empty;
    [ObservableProperty] private bool _creating;

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

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
            var sourcePath = Path.GetFullPath(Path.Combine(fullFolder, $"{Id}.cs"));
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

            var className = ToClassName(Id);
            var source = string.Join(Environment.NewLine, [
                "using System;", "using System.Threading;", "using System.Threading.Tasks;", "using Diary.ScriptBase;", "",
                "namespace Diary.UserScripts;", "",
                $"public sealed class {className} : IScriptProgramV1", "{",
                "    public ScriptDescriptor Descriptor { get; } = new(",
                $"        \"{Escape(Id)}\",", $"        \"{Escape(Name)}\",",
                "        ScriptApiVersion.V1,", $"        ScriptScope.{scope},",
                $"        {FormatCapabilities()},", $"        \"{Escape(Description)}\");", "",
                "    public ValueTask<ScriptExecutionResult> ExecuteAsync(",
                "        ScriptExecutionRequest request,",
                "        IScriptExecutionContext context,",
                "        CancellationToken cancellationToken = default)", "    {",
                "        return ValueTask.FromResult(ScriptExecutionResult.Succeeded());",
                "    }", "}", ""]);
            var metadata = JsonSerializer.Serialize(new ScriptFileMetadata(
                ApiVersion: ScriptApiVersion.V1,
                Id: Id,
                Name: Name,
                Description: Description,
                Capabilities: SelectedCapabilities), new JsonSerializerOptions { WriteIndented = true });
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
