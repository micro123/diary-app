using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Models;
using Diary.GUIBase.ViewModels;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels.Dialogs;

public sealed record ScriptRunOptions(
    ImmutableDictionary<string, string> Arguments,
    string? IdempotencyKey,
    TimeSpan Timeout,
    bool Preview,
    ScriptApiVersion ApiVersion = ScriptApiVersion.V1,
    string? LegacyArgumentsText = null);

[DiAutoRegister]
public partial class ScriptRunDialogViewModel : ViewModelBase, IDialogContext
{
    [ObservableProperty] private string _scriptName = string.Empty;
    [ObservableProperty] private string _argumentsText = string.Empty;
    [ObservableProperty] private string _idempotencyKey = string.Empty;
    [ObservableProperty] private int _timeoutSeconds = 300;
    [ObservableProperty] private bool _preview;
    [ObservableProperty] private string _error = string.Empty;
    [ObservableProperty] private ScriptParameterFormViewModel? _parameterForm;
    [ObservableProperty] private bool _showExecutionOptions = true;

    public bool IsV1 => ParameterForm is null;
    public bool IsV2 => ParameterForm is not null;
    public bool HasParameters => ParameterForm?.HasFields == true;

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public event EventHandler<object?>? RequestClose;

    public void Initialize(string scriptName, ScriptFileMetadata? metadata)
    {
        var descriptor = new ScriptDescriptor(
            metadata?.Id ?? "legacy-script",
            scriptName,
            ScriptApiVersion.V1,
            metadata?.Scope ?? ScriptScope.Application);
        Initialize(descriptor, metadata, null, null);
    }

    public void Initialize(
        ScriptDescriptor descriptor,
        ScriptFileMetadata? metadata,
        IReadOnlyDictionary<string, string>? lastArguments,
        string? legacyArgumentsText,
        bool showExecutionOptions = true,
        Func<ValueTask>? clearRememberedArguments = null)
    {
        ScriptName = descriptor.Name;
        ShowExecutionOptions = showExecutionOptions;
        ParameterForm = descriptor.ApiVersion == ScriptApiVersion.V2
            ? new ScriptParameterFormViewModel(
                descriptor,
                metadata?.DefaultArguments,
                lastArguments,
                clearRememberedArguments)
            : null;
        ArgumentsText = legacyArgumentsText ?? string.Join(
                Environment.NewLine,
                metadata?.DefaultArguments?
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Key}={pair.Value}")
                ?? []);
        TimeoutSeconds = metadata?.TimeoutSeconds is > 0 and <= 3600
            ? metadata.TimeoutSeconds.Value
            : 300;
        IdempotencyKey = string.Empty;
        Preview = false;
        Error = string.Empty;
        OnPropertyChanged(nameof(IsV1));
        OnPropertyChanged(nameof(IsV2));
        OnPropertyChanged(nameof(HasParameters));
    }

    partial void OnParameterFormChanged(ScriptParameterFormViewModel? value)
    {
        OnPropertyChanged(nameof(IsV1));
        OnPropertyChanged(nameof(IsV2));
        OnPropertyChanged(nameof(HasParameters));
    }

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    [RelayCommand]
    private void Run()
    {
        Error = string.Empty;
        if (TimeoutSeconds is <= 0 or > 3600)
        {
            Error = "超时时间必须在 1 到 3600 秒之间。";
            return;
        }
        ImmutableDictionary<string, string> arguments;
        ScriptApiVersion apiVersion;
        if (ParameterForm is not null)
        {
            var binding = ParameterForm.ValidateAndBuild();
            if (!binding.Succeeded)
            {
                Error = binding.Issues.Any(issue => issue.ParameterName is not null)
                    ? "请修正参数表单中的错误。"
                    : binding.Diagnostics.FirstOrDefault()?.Message ?? "参数校验失败。";
                return;
            }
            arguments = binding.Arguments;
            apiVersion = ScriptApiVersion.V2;
        }
        else if (!TryParseArguments(ArgumentsText, out arguments, out var error))
        {
            Error = error;
            return;
        }
        else
        {
            apiVersion = ScriptApiVersion.V1;
        }

        RequestClose?.Invoke(this, new ScriptRunOptions(
            arguments,
            string.IsNullOrWhiteSpace(IdempotencyKey) ? null : IdempotencyKey.Trim(),
            TimeSpan.FromSeconds(TimeoutSeconds),
            Preview,
            apiVersion,
            apiVersion == ScriptApiVersion.V1 ? ArgumentsText : null));
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, null);

    public void Close() => Cancel();

    public static bool TryParseArguments(
        string text,
        out ImmutableDictionary<string, string> arguments,
        out string error)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                arguments = ImmutableDictionary<string, string>.Empty;
                error = $"第 {index + 1} 行必须使用 key=value 格式。";
                return false;
            }
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length == 0 || !builder.TryAdd(key, value))
            {
                arguments = ImmutableDictionary<string, string>.Empty;
                error = key.Length == 0
                    ? $"第 {index + 1} 行的参数名不能为空。"
                    : $"参数名重复：{key}。";
                return false;
            }
        }

        arguments = builder.ToImmutable();
        error = string.Empty;
        return true;
    }
}
