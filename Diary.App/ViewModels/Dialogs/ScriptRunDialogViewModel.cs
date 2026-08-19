using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.ViewModels;
using Diary.Script.Runtime;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels.Dialogs;

public sealed record ScriptRunOptions(
    ImmutableDictionary<string, string> Arguments,
    string? IdempotencyKey,
    TimeSpan Timeout,
    bool Preview);

[DiAutoRegister]
public partial class ScriptRunDialogViewModel : ViewModelBase, IDialogContext
{
    [ObservableProperty] private string _scriptName = string.Empty;
    [ObservableProperty] private string _argumentsText = string.Empty;
    [ObservableProperty] private string _idempotencyKey = string.Empty;
    [ObservableProperty] private int _timeoutSeconds = 300;
    [ObservableProperty] private bool _preview;
    [ObservableProperty] private string _error = string.Empty;

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public event EventHandler<object?>? RequestClose;

    public void Initialize(string scriptName, ScriptFileMetadata? metadata)
    {
        ScriptName = scriptName;
        ArgumentsText = string.Join(
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
        if (!TryParseArguments(ArgumentsText, out var arguments, out var error))
        {
            Error = error;
            return;
        }

        RequestClose?.Invoke(this, new ScriptRunOptions(
            arguments,
            string.IsNullOrWhiteSpace(IdempotencyKey) ? null : IdempotencyKey.Trim(),
            TimeSpan.FromSeconds(TimeoutSeconds),
            Preview));
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
