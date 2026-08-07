using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.ViewModels;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Diary.Utils;
using Microsoft.Extensions.Logging;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels;

[DiAutoRegister]
public partial class ScriptEditorViewModel(
    IScriptDirectoryLoader directoryLoader,
    ILogger logger) : ViewModelBase
{
    private FileSystemWatcher? _watcher;
    private string _sourcePath = string.Empty;
    private string _scriptRoot = string.Empty;
    private string _savedText = string.Empty;
    private bool _loading;
    private bool _writing;

    public ObservableCollection<ScriptDiagnosticListItem> Diagnostics { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(OverwriteSaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscardCommand))]
    private string _text = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(OverwriteSaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(DiscardCommand))]
    private bool _isDirty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(OverwriteSaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReloadExternalCommand))]
    private bool _externalChangeDetected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(OverwriteSaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    private bool _busy;

    [ObservableProperty] private string _status = "尚未打开脚本";
    [ObservableProperty] private string _error = string.Empty;
    [ObservableProperty] private string _externalChangeMessage = string.Empty;

    public string SourcePath => _sourcePath;
    public string FileName => Path.GetFileName(_sourcePath);
    public string WindowTitle => IsDirty ? $"* {FileName} - 脚本编辑器" : $"{FileName} - 脚本编辑器";
    public bool HasDiagnostics => Diagnostics.Count > 0;
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool HasExternalChange => ExternalChangeDetected;
    public bool CanSave => IsDirty && !Busy && !ExternalChangeDetected;
    public bool CanOverwriteSave => IsDirty && !Busy && ExternalChangeDetected;

    public event EventHandler? Saved;
    public event EventHandler? RequestClose;

    partial void OnTextChanged(string value)
    {
        if (!_loading)
            IsDirty = !string.Equals(value, _savedText, StringComparison.Ordinal);
        OnPropertyChanged(nameof(WindowTitle));
    }

    partial void OnIsDirtyChanged(bool value)
    {
        OnPropertyChanged(nameof(WindowTitle));
        SaveCommand.NotifyCanExecuteChanged();
        OverwriteSaveCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
    }

    partial void OnExternalChangeDetectedChanged(bool value)
    {
        OnPropertyChanged(nameof(HasExternalChange));
        SaveCommand.NotifyCanExecuteChanged();
        OverwriteSaveCommand.NotifyCanExecuteChanged();
    }

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    public void Initialize(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("脚本文件不存在。", sourcePath);

        _sourcePath = Path.GetFullPath(sourcePath);
        _scriptRoot = Path.Combine(FsTools.GetApplicationConfigDirectory(), "scripts");
        _savedText = File.ReadAllText(_sourcePath);
        _loading = true;
        Text = _savedText;
        _loading = false;
        IsDirty = false;
        ExternalChangeDetected = false;
        Error = string.Empty;
        Status = $"已打开 {FileName}";
        OnPropertyChanged(nameof(SourcePath));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(WindowTitle));
        StartWatcher();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task Save() => SaveCore(false);

    [RelayCommand(CanExecute = nameof(CanOverwriteSave))]
    private Task OverwriteSave() => SaveCore(true);

    private async Task SaveCore(bool overwriteExternalChange)
    {
        if (!IsDirty || string.IsNullOrWhiteSpace(_sourcePath))
            return;
        if (ExternalChangeDetected && !overwriteExternalChange)
        {
            Error = "文件已被外部修改，请先重新加载外部版本或选择覆盖保存。";
            return;
        }

        Busy = true;
        Error = string.Empty;
        _writing = true;
        try
        {
            var temporaryPath = _sourcePath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, Text);
                File.Move(temporaryPath, _sourcePath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }

            _savedText = Text;
            IsDirty = false;
            ExternalChangeDetected = false;
            ExternalChangeMessage = string.Empty;
            Status = overwriteExternalChange ? "已覆盖外部修改并保存" : "已保存脚本";
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "保存脚本失败：{SourcePath}", _sourcePath);
            Error = $"保存失败：{exception.Message}";
            Status = "脚本保存失败";
        }
        finally
        {
            _writing = false;
            Busy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private void Discard()
    {
        IsDirty = false;
        Error = string.Empty;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void Close() => RequestClose?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(CanReloadExternal))]
    private void ReloadExternal()
    {
        try
        {
            var content = File.ReadAllText(_sourcePath);
            _loading = true;
            Text = content;
            _savedText = content;
            _loading = false;
            IsDirty = false;
            ExternalChangeDetected = false;
            ExternalChangeMessage = string.Empty;
            Error = string.Empty;
            Status = "已重新加载外部版本";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "重新加载外部脚本失败：{SourcePath}", _sourcePath);
            Error = $"重新加载失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private async Task Check()
    {
        if (Busy || string.IsNullOrWhiteSpace(_scriptRoot))
            return;
        if (IsDirty)
        {
            Error = "当前内容尚未保存，请先保存后再进行编译检查。";
            Status = "编译检查未执行";
            return;
        }
        Busy = true;
        Error = string.Empty;
        try
        {
            var result = await directoryLoader.LoadAsync(_scriptRoot);
            var entry = result.Entries.FirstOrDefault(item => PathsEqual(item.SourcePath, _sourcePath));
            Diagnostics.Clear();
            foreach (var diagnostic in entry?.BuildResult?.Diagnostics ?? [])
                Diagnostics.Add(FormatDiagnostic(diagnostic));
            OnPropertyChanged(nameof(HasDiagnostics));
            if (entry?.BuildResult?.Succeeded == true)
                Status = "编译检查通过";
            else
                Status = "编译检查失败，请查看诊断";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "脚本编译检查失败：{SourcePath}", _sourcePath);
            Error = $"编译检查失败：{exception.Message}";
            Status = "编译检查失败";
        }
        finally
        {
            Busy = false;
        }
    }

    public void NotifyCloseBlocked() => Error = "存在未保存修改，请先保存或点击“放弃修改”。";

    private void StartWatcher()
    {
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(
            Path.GetDirectoryName(_sourcePath)!,
            Path.GetFileName(_sourcePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnWatchedFileChanged;
        _watcher.Created += OnWatchedFileChanged;
        _watcher.Deleted += OnWatchedFileChanged;
        _watcher.Renamed += OnWatchedFileRenamed;
    }

    private void OnWatchedFileChanged(object sender, FileSystemEventArgs args) =>
        Dispatcher.UIThread.Post(CheckExternalFile);

    private void OnWatchedFileRenamed(object sender, RenamedEventArgs args) =>
        Dispatcher.UIThread.Post(CheckExternalFile);

    private void CheckExternalFile()
    {
        if (_writing || string.IsNullOrWhiteSpace(_sourcePath))
            return;
        try
        {
            if (!File.Exists(_sourcePath))
            {
                ExternalChangeDetected = true;
                ExternalChangeMessage = "脚本文件已被外部删除，保存时将重新创建文件。";
                Status = "检测到脚本文件被删除";
                return;
            }

            var content = File.ReadAllText(_sourcePath);
            if (string.Equals(content, _savedText, StringComparison.Ordinal)
                || string.Equals(content, Text, StringComparison.Ordinal))
            {
                if (ExternalChangeDetected)
                {
                    ExternalChangeDetected = false;
                    ExternalChangeMessage = string.Empty;
                }
                return;
            }

            ExternalChangeDetected = true;
            ExternalChangeMessage = "文件已被外部修改，请选择重新加载或覆盖保存。";
            Status = "检测到外部修改";
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "检查脚本外部修改失败：{SourcePath}", _sourcePath);
        }
    }

    public override void Cleanup()
    {
        _watcher?.Dispose();
        _watcher = null;
        base.Cleanup();
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private bool CanDiscard() => IsDirty;

    private bool CanReloadExternal() => HasExternalChange;

    private static ScriptDiagnosticListItem FormatDiagnostic(ScriptDiagnostic diagnostic) =>
        new(
            diagnostic.Severity switch
            {
                ScriptDiagnosticSeverity.Error => "错误",
                ScriptDiagnosticSeverity.Warning => "警告",
                _ => "信息",
            },
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.SourcePath is null ? string.Empty : $"{diagnostic.SourcePath}:{diagnostic.Line}:{diagnostic.Column}");
}
