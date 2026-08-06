using System.Text.Json;
using System.Text.RegularExpressions;
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
        && Regex.IsMatch(Id, "^[a-z][a-z0-9._-]{1,63}$", RegexOptions.IgnoreCase);

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
            Directory.CreateDirectory(folder);
            var sourcePath = Path.Combine(folder, $"{Id}.cs");
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
            await File.WriteAllTextAsync(sourcePath, source);
            await File.WriteAllTextAsync(metadataPath, metadata);
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
}
