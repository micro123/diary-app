using Avalonia.Controls;
using Avalonia.Platform.Storage;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using System.ComponentModel;
using Diary.App.ViewModels;
using TextMateSharp.Grammars;
using Ursa.Controls;

namespace Diary.App.Views;

public partial class ScriptEditorWindow : UrsaWindow
{
    private readonly ScriptEditorViewModel? _viewModel;
    private TextEditor? _editor;
    private bool _syncingEditor;
    private TextMate.Installation? _textMateInstallation;

    public ScriptEditorWindow()
    {
        InitializeComponent();
    }

    public ScriptEditorWindow(ScriptEditorViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Closing += OnClosing;
        _viewModel.RequestClose += OnRequestClose;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_viewModel is null)
            return;
        _editor = this.FindControl<TextEditor>("Editor");
        if (_editor is null)
            return;
        _editor.Text = _viewModel.Text;
        _editor.TextChanged += OnEditorTextChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.SaveAsRequested += OnSaveAsRequested;
        _viewModel.DiagnosticSelected += OnDiagnosticSelected;
        var registryOptions = new RegistryOptions(ThemeName.DarkPlus);
        var installation = _editor.InstallTextMate(registryOptions);
        installation.SetGrammar(registryOptions.GetScopeByLanguageId("csharp"));
        _textMateInstallation = installation;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_viewModel?.IsDirty == true)
        {
            e.Cancel = true;
            _viewModel.NotifyCloseBlocked();
        }
    }

    private void OnRequestClose(object? sender, EventArgs e) => Close();

    private async void OnSaveAsRequested(object? sender, EventArgs e)
    {
        if (_viewModel is null)
            return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "脚本另存为",
                SuggestedFileName = _viewModel.FileName,
                DefaultExtension = "cs",
                FileTypeChoices =
                [
                    new FilePickerFileType("C# 源码") { Patterns = ["*.cs"] },
                ],
            });
            if (file is not null)
                await _viewModel.SaveAsAsync(file.Path.LocalPath);
        }
        catch (Exception exception)
        {
            _viewModel.Error = $"另存为失败：{exception.Message}";
        }
    }

    private void OnDiagnosticSelected(ScriptEditorDiagnosticItem diagnostic)
    {
        if (_editor?.Document is null || diagnostic.Line is not { } line || line < 1)
            return;
        var lineNumber = Math.Min(line, _editor.Document.LineCount);
        var documentLine = _editor.Document.GetLineByNumber(lineNumber);
        var column = Math.Max(1, diagnostic.Column ?? 1);
        _editor.TextArea.Caret.Offset = Math.Min(
            documentLine.Offset + column - 1,
            documentLine.EndOffset);
        _editor.TextArea.Caret.BringCaretToView();
        _editor.TextArea.Focus();
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_syncingEditor || _editor is null || _viewModel is null)
            return;
        _viewModel.Text = _editor.Text;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null
            || e.PropertyName != nameof(ScriptEditorViewModel.Text)
            || _editor is null
            || string.Equals(_editor.Text, _viewModel.Text, StringComparison.Ordinal))
        {
            return;
        }

        _syncingEditor = true;
        _editor.Text = _viewModel.Text;
        _syncingEditor = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is null)
        {
            base.OnClosed(e);
            return;
        }
        _viewModel.RequestClose -= OnRequestClose;
        _viewModel.SaveAsRequested -= OnSaveAsRequested;
        _viewModel.DiagnosticSelected -= OnDiagnosticSelected;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (_editor is not null)
            _editor.TextChanged -= OnEditorTextChanged;
        _textMateInstallation?.Dispose();
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
