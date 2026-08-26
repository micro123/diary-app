using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.ViewModels;
using Diary.Script.CSharp;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App.ViewModels;

public sealed class ScriptEditorDiagnosticItem
{
    public ScriptEditorDiagnosticItem(
        string severityLabel,
        string code,
        string message,
        string location,
        int? line,
        int? column,
        Action jump)
    {
        SeverityLabel = severityLabel;
        Code = code;
        Message = message;
        Location = location;
        Line = line;
        Column = column;
        JumpCommand = new RelayCommand(jump);
    }

    public string SeverityLabel { get; }
    public string Code { get; }
    public string Message { get; }
    public string Location { get; }
    public int? Line { get; }
    public int? Column { get; }
    public ICommand JumpCommand { get; }
    public string Summary => string.IsNullOrWhiteSpace(Location)
        ? $"[{Code}] {Message}"
        : $"[{Code}] {Location} {Message}";
}

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
    private readonly CSharpLanguageService _languageService = new();
    private CSharpLanguageAnalysis? _languageAnalysis;
    private string _languageAnalysisText = string.Empty;
    private CancellationTokenSource? _languageAnalysisCancellation;
    private int _languageAnalysisVersion;

    public ObservableCollection<ScriptEditorDiagnosticItem> Diagnostics { get; } = new();

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
    [NotifyCanExecuteChangedFor(nameof(SaveAsCommand))]
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
    public event EventHandler? SaveAsRequested;
    public event Action<ScriptEditorDiagnosticItem>? DiagnosticSelected;

    partial void OnTextChanged(string value)
    {
        if (!_loading)
        {
            IsDirty = !string.Equals(value, _savedText, StringComparison.Ordinal);
            ScheduleLanguageAnalysis(value);
        }
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

    public void Initialize(string sourcePath, string? scriptRoot = null)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("脚本文件不存在。", sourcePath);

        _sourcePath = Path.GetFullPath(sourcePath);
        _scriptRoot = Path.GetFullPath(scriptRoot
            ?? Path.Combine(FsTools.GetApplicationConfigDirectory(), "scripts"));
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
        ScheduleLanguageAnalysis(_savedText);
    }

    public async Task<IReadOnlyList<CSharpLanguageCompletionItem>> GetCSharpCompletionsAsync(
        int caretOffset,
        CancellationToken cancellationToken = default)
    {
        if (!IsCSharpSource)
            return [];

        var source = Text;
        var analysis = _languageAnalysis;
        if (analysis is null || !string.Equals(_languageAnalysisText, source, StringComparison.Ordinal))
        {
            analysis = await Task.Run(
                () => _languageService.Analyze(source, _sourcePath, cancellationToken),
                cancellationToken);
            if (string.Equals(Text, source, StringComparison.Ordinal))
            {
                _languageAnalysis = analysis;
                _languageAnalysisText = source;
            }
        }
        return analysis.GetCompletions(caretOffset);
    }

    public CSharpLanguageHover? GetCSharpHover(int caretOffset) =>
        IsCSharpSource
            && _languageAnalysis is not null
            && string.Equals(_languageAnalysisText, Text, StringComparison.Ordinal)
            ? _languageAnalysis.GetHover(caretOffset)
            : null;

    private bool IsCSharpSource =>
        _sourcePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private void ScheduleLanguageAnalysis(string source)
    {
        if (!IsCSharpSource || string.IsNullOrWhiteSpace(_sourcePath))
            return;

        _languageAnalysisCancellation?.Cancel();
        _languageAnalysisCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _languageAnalysisCancellation = cancellation;
        var version = ++_languageAnalysisVersion;
        _ = AnalyzeLanguageAsync(source, version, cancellation.Token);
    }

    private async Task AnalyzeLanguageAsync(
        string source,
        int version,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(250, cancellationToken);
            var analysis = await Task.Run(
                () => _languageService.Analyze(source, _sourcePath, cancellationToken),
                cancellationToken);
            if (cancellationToken.IsCancellationRequested
                || version != _languageAnalysisVersion
                || !string.Equals(Text, source, StringComparison.Ordinal))
                return;

            _languageAnalysis = analysis;
            _languageAnalysisText = source;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Diagnostics.Clear();
                foreach (var diagnostic in analysis.Diagnostics)
                    Diagnostics.Add(FormatLanguageDiagnostic(diagnostic));
                OnPropertyChanged(nameof(HasDiagnostics));
                var errors = analysis.Diagnostics.Count(item =>
                    item.Severity == CSharpLanguageDiagnosticSeverity.Error);
                Status = errors == 0
                    ? "实时语义检查通过"
                    : $"实时诊断：{errors} 个错误";
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "C# 实时语义分析失败：{SourcePath}", _sourcePath);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save() => await SaveCore(false);

    [RelayCommand(CanExecute = nameof(CanOverwriteSave))]
    private async Task OverwriteSave() => await SaveCore(true);

    private async Task<bool> SaveCore(bool overwriteExternalChange)
    {
        if (!IsDirty || string.IsNullOrWhiteSpace(_sourcePath))
            return false;
        if (ExternalChangeDetected && !overwriteExternalChange)
        {
            Error = "文件已被外部修改，请先重新加载外部版本或选择覆盖保存。";
            return false;
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
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "保存脚本失败：{SourcePath}", _sourcePath);
            Error = $"保存失败：{exception.Message}";
            Status = "脚本保存失败";
            return false;
        }
        finally
        {
            _writing = false;
            Busy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveAs))]
    private void SaveAs() => SaveAsRequested?.Invoke(this, EventArgs.Empty);

    public async Task<bool> SaveAsAsync(string targetPath)
    {
        if (Busy || string.IsNullOrWhiteSpace(_sourcePath))
            return false;

        var originalPath = _sourcePath;
        var originalDirectory = Path.GetDirectoryName(originalPath)!;
        var target = Path.GetFullPath(targetPath);
        var targetDirectory = Path.GetDirectoryName(target)!;
        if (!PathsEqual(originalDirectory, targetDirectory)
            || !ScriptCreationPolicy.IsInsideDirectory(target, _scriptRoot)
            || !target.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            Error = "另存为只能选择当前脚本目录中的 C# 源文件。";
            return false;
        }
        if (PathsEqual(originalPath, target))
            return await SaveCore(ExternalChangeDetected);
        if (File.Exists(target) || File.Exists(target + ".json"))
        {
            Error = "目标脚本或 metadata 已存在，不能覆盖已有脚本。";
            return false;
        }

        Busy = true;
        Error = string.Empty;
        _writing = true;
        var sourceBackup = originalPath + $".{Guid.NewGuid():N}.move";
        var metadataPath = originalPath + ".json";
        var metadataBackup = metadataPath + $".{Guid.NewGuid():N}.move";
        var targetMetadata = target + ".json";
        var sourceMoved = false;
        var metadataMoved = false;
        var targetCreated = false;
        var targetMetadataCreated = false;
        try
        {
            File.Move(originalPath, sourceBackup);
            sourceMoved = true;
            if (File.Exists(metadataPath))
            {
                File.Move(metadataPath, metadataBackup);
                metadataMoved = true;
            }

            await WriteTextAtomicallyAsync(target, Text);
            targetCreated = true;
            if (metadataMoved)
            {
                File.Move(metadataBackup, targetMetadata);
                targetMetadataCreated = true;
            }

            File.Delete(sourceBackup);
            sourceMoved = false;
            if (metadataMoved)
                metadataMoved = false;

            _sourcePath = target;
            _savedText = Text;
            IsDirty = false;
            ExternalChangeDetected = false;
            ExternalChangeMessage = string.Empty;
            StartWatcher();
            OnPropertyChanged(nameof(SourcePath));
            OnPropertyChanged(nameof(FileName));
            OnPropertyChanged(nameof(WindowTitle));
            Status = $"已另存为 {FileName}";
            Saved?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "脚本另存为失败：{SourcePath} -> {TargetPath}", originalPath, target);
            Error = $"另存为失败：{exception.Message}";
            if (targetMetadataCreated && File.Exists(targetMetadata))
            {
                File.Move(targetMetadata, metadataPath);
                targetMetadataCreated = false;
                metadataMoved = false;
            }
            if (targetCreated)
                DeleteIfExists(target);
            if (metadataMoved && File.Exists(metadataBackup))
                File.Move(metadataBackup, metadataPath);
            if (sourceMoved && File.Exists(sourceBackup))
                File.Move(sourceBackup, originalPath);
            return false;
        }
        finally
        {
            DeleteIfExists(sourceBackup);
            DeleteIfExists(metadataBackup);
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
            var result = await Task.Run(async () =>
                await directoryLoader.LoadAsync(_scriptRoot).ConfigureAwait(false));
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

    private bool CanSaveAs() => !Busy && !string.IsNullOrWhiteSpace(_sourcePath);

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

    protected override void Cleanup()
    {
        _languageAnalysisCancellation?.Cancel();
        _languageAnalysisCancellation?.Dispose();
        _languageAnalysisCancellation = null;
        _watcher?.Dispose();
        _watcher = null;
        base.Cleanup();
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private bool CanDiscard() => IsDirty;

    private bool CanReloadExternal() => HasExternalChange;

    private ScriptEditorDiagnosticItem FormatLanguageDiagnostic(
        CSharpLanguageDiagnostic diagnostic)
    {
        ScriptEditorDiagnosticItem? item = null;
        item = new(
            diagnostic.Severity switch
            {
                CSharpLanguageDiagnosticSeverity.Error => "错误",
                CSharpLanguageDiagnosticSeverity.Warning => "警告",
                _ => "信息",
            },
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.SourcePath is null
                ? string.Empty
                : $"{diagnostic.SourcePath}:{diagnostic.Line}:{diagnostic.Column}",
            diagnostic.Line,
            diagnostic.Column,
            () => DiagnosticSelected?.Invoke(item!));
        return item;
    }

    private ScriptEditorDiagnosticItem FormatDiagnostic(ScriptDiagnostic diagnostic)
    {
        ScriptEditorDiagnosticItem? item = null;
        item = new(
            diagnostic.Severity switch
            {
                ScriptDiagnosticSeverity.Error => "错误",
                ScriptDiagnosticSeverity.Warning => "警告",
                _ => "信息",
            },
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.SourcePath is null ? string.Empty : $"{diagnostic.SourcePath}:{diagnostic.Line}:{diagnostic.Column}",
            diagnostic.Line,
            diagnostic.Column,
            () => DiagnosticSelected?.Invoke(item!));
        return item;
    }

    private static async Task WriteTextAtomicallyAsync(string path, string text)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, text);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            DeleteIfExists(temporaryPath);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
