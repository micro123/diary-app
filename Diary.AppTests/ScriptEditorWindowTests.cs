using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using AvaloniaEdit;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Diagnostics;
using Diary.App.ViewModels;
using Diary.App.Views;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.AppTests;

[TestClass]
[DoNotParallelize]
public sealed class ScriptEditorWindowTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Initialize(TestContext context)
    {
        _session = HeadlessUnitTestSession.StartNew(typeof(TestApplication), AvaloniaTestIsolationLevel.PerAssembly);
    }

    [ClassCleanup]
    public static void Cleanup() => _session.Dispose();

    [TestMethod]
    public async Task EditorWindow_SavesTextThroughBoundButtonCommand()
    {
        var root = CreateScriptDirectory(out var sourcePath);
        try
        {
            await File.WriteAllTextAsync(sourcePath, "initial");
            await _session.Dispatch(async () =>
            {
                var viewModel = CreateViewModel(new EmptyDirectoryLoader());
                viewModel.Initialize(sourcePath, root);
                var window = new ScriptEditorWindow(viewModel);
                window.Show();

                try
                {
                    var editor = window.FindControl<TextEditor>("Editor");
                    var saveButton = window.FindControl<Button>("SaveButton");
                    Assert.IsNotNull(editor);
                    Assert.IsNotNull(saveButton);
                    editor.Text = "changed";

                    var command = Assert.IsInstanceOfType<IAsyncRelayCommand>(saveButton.Command);
                    await command.ExecuteAsync(null);

                    Assert.AreEqual("changed", await File.ReadAllTextAsync(sourcePath));
                    Assert.IsFalse(viewModel.IsDirty);
                }
                finally
                {
                    if (window.IsVisible)
                    {
                        if (viewModel.IsDirty)
                            viewModel.DiscardCommand.Execute(null);
                        else
                            window.Close();
                    }
                }
            }, CancellationToken.None);
        }
        finally
        {
            await DeleteDirectoryAsync(root);
        }
    }

    [TestMethod]
    public async Task EditorViewModel_SavesAsAndMovesMetadata()
    {
        var root = CreateScriptDirectory(out var sourcePath);
        var targetPath = Path.Combine(Path.GetDirectoryName(sourcePath)!, "renamed.cs");
        try
        {
            await File.WriteAllTextAsync(sourcePath, "source");
            await File.WriteAllTextAsync(sourcePath + ".json", "{\"id\":\"sample\"}");
            var viewModel = CreateViewModel(new EmptyDirectoryLoader());
            viewModel.Initialize(sourcePath, root);
            viewModel.Text = "renamed source";

            Assert.IsTrue(await viewModel.SaveAsAsync(targetPath));
            Assert.IsFalse(File.Exists(sourcePath));
            Assert.IsFalse(File.Exists(sourcePath + ".json"));
            Assert.AreEqual("renamed source", await File.ReadAllTextAsync(targetPath));
            Assert.AreEqual("{\"id\":\"sample\"}", await File.ReadAllTextAsync(targetPath + ".json"));
            Assert.AreEqual(Path.GetFullPath(targetPath), viewModel.SourcePath);
            Assert.IsFalse(viewModel.IsDirty);
            viewModel.Dispose();
        }
        finally
        {
            await DeleteDirectoryAsync(root);
        }
    }

    [TestMethod]
    public async Task EditorWindow_DiagnosticCommandMovesCaretToLineAndColumn()
    {
        var root = CreateScriptDirectory(out var sourcePath);
        try
        {
            await File.WriteAllTextAsync(sourcePath, "line one\nline two\nline three");
            var diagnostic = new ScriptDiagnostic(
                "CS1002",
                "应输入分号",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Syntax,
                sourcePath,
                2,
                3);
            var loader = new FixedDirectoryLoader(new ScriptDirectoryLoadResult(
                [new ScriptDirectoryEntry(
                    sourcePath,
                    ScriptScope.Editor,
                    ScriptBuildResult.Failure(diagnostic))],
                []));
            await _session.Dispatch(async () =>
            {
                var viewModel = CreateViewModel(loader);
                viewModel.Initialize(sourcePath, root);
                var window = new ScriptEditorWindow(viewModel);
                window.Show();

                try
                {
                    var checkButton = window.FindControl<Button>("CheckButton");
                    var editor = window.FindControl<TextEditor>("Editor");
                    Assert.IsNotNull(checkButton);
                    Assert.IsNotNull(editor);
                    var command = Assert.IsInstanceOfType<IAsyncRelayCommand>(checkButton.Command);
                    await command.ExecuteAsync(null);

                    Assert.AreEqual(1, viewModel.Diagnostics.Count);
                    var item = viewModel.Diagnostics.Single();
                    item.JumpCommand.Execute(null);

                    Assert.AreEqual(2, editor.TextArea.Caret.Line);
                    Assert.AreEqual(3, editor.TextArea.Caret.Column);
                }
                finally
                {
                    if (window.IsVisible)
                        window.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            await DeleteDirectoryAsync(root);
        }
    }

    [TestMethod]
    public async Task EditorWindow_BlocksClosingDirtyDocumentUntilDiscarded()
    {
        var root = CreateScriptDirectory(out var sourcePath);
        try
        {
            await File.WriteAllTextAsync(sourcePath, "initial");
            await _session.Dispatch(() =>
            {
                var viewModel = CreateViewModel(new EmptyDirectoryLoader());
                viewModel.Initialize(sourcePath, root);
                var window = new ScriptEditorWindow(viewModel);
                window.Show();
                try
                {
                    var editor = window.FindControl<TextEditor>("Editor");
                    Assert.IsNotNull(editor);
                    editor.Text = "dirty";

                    window.Close();

                    Assert.IsTrue(window.IsVisible);
                    Assert.IsTrue(viewModel.HasError);
                    viewModel.DiscardCommand.Execute(null);
                    Assert.IsFalse(window.IsVisible);
                }
                finally
                {
                    if (window.IsVisible)
                    {
                        if (viewModel.IsDirty)
                            viewModel.DiscardCommand.Execute(null);
                        else
                            window.Close();
                    }
                }
            }, CancellationToken.None);
        }
        finally
        {
            await DeleteDirectoryAsync(root);
        }
    }


    [TestMethod]
    public async Task CrashReporterWindow_ShowsBriefDetailsAndOpenFolderAction()
    {
        var directory = CreateScriptDirectory(out _);
        try
        {
            var request = new CrashReportRequest(
                123,
                "Diary.App",
                "1.0.0",
                DateTimeOffset.UtcNow,
                "System.InvalidOperationException",
                "brief failure",
                directory,
                Path.Combine(directory, "sample.dmp"),
                Path.Combine(directory, "sample.json"),
                true);
            var result = new CrashReportResult(request, true, 2048, null);

            await _session.Dispatch(() =>
            {
                var window = new CrashReporterWindow(result);
                window.Show();
                try
                {
                    var exceptionType = window.FindControl<TextBlock>("ExceptionTypeText");
                    var message = window.FindControl<TextBlock>("ExceptionMessageText");
                    var status = window.FindControl<TextBlock>("DumpStatusText");
                    var openFolder = window.FindControl<Button>("OpenDumpFolderButton");

                    Assert.IsNotNull(exceptionType);
                    Assert.IsNotNull(message);
                    Assert.IsNotNull(status);
                    Assert.IsNotNull(openFolder);
                    StringAssert.Contains(exceptionType.Text, "InvalidOperationException");
                    Assert.AreEqual("brief failure", message.Text);
                    StringAssert.Contains(status.Text, "2 KB");
                    Assert.AreEqual("打开 Dump 文件夹", openFolder.Content);
                }
                finally
                {
                    window.Close();
                }
            }, CancellationToken.None);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    private static ScriptEditorViewModel CreateViewModel(IScriptDirectoryLoader loader) =>
        new(loader, NullLogger.Instance);

    private static string CreateScriptDirectory(out string sourcePath)
    {
        var root = Path.Combine(Path.GetTempPath(), $"diary-app-editor-tests-{Guid.NewGuid():N}");
        var editorDirectory = Path.Combine(root, "editor");
        Directory.CreateDirectory(editorDirectory);
        sourcePath = Path.Combine(editorDirectory, "sample.cs");
        return root;
    }

    private static async Task DeleteDirectoryAsync(string path)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (!Directory.Exists(path))
                return;
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch (Exception exception) when (
                attempt < maxAttempts
                && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt));
            }
        }
    }

    private sealed class EmptyDirectoryLoader : IScriptDirectoryLoader
    {
        public ValueTask<ScriptDirectoryLoadResult> LoadAsync(
            string rootDirectory,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ScriptDirectoryLoadResult([], []));
    }

    private sealed class FixedDirectoryLoader(ScriptDirectoryLoadResult result) : IScriptDirectoryLoader
    {
        public ValueTask<ScriptDirectoryLoadResult> LoadAsync(
            string rootDirectory,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(result);
    }

    private sealed class TestApplication : Application
    {
    }
}
