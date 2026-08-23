using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Media;
using AvaloniaEdit;
using CommunityToolkit.Mvvm.Input;
using Diary.App;
using Diary.App.Diagnostics;
using Diary.App.Fonts;
using Diary.App.ViewModels;
using Diary.App.Views;
using Diary.Core.Data.AppConfig;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Microsoft.Extensions.Logging.Abstractions;
using Optris.Icons.Avalonia;
using Optris.Icons.Avalonia.FontAwesome;
using Optris.Icons.Avalonia.MaterialDesign;

namespace Diary.AppTests;

[TestClass]
[DoNotParallelize]
public sealed class ScriptEditorWindowTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Initialize(TestContext context)
    {
        IconProvider.Current
            .Register<FontAwesomeIconProvider>()
            .Register<MaterialDesignIconProvider>();

        _session = HeadlessUnitTestSession.StartNew(typeof(TestApplication), AvaloniaTestIsolationLevel.PerAssembly);
    }

    [ClassCleanup]
    public static async Task Cleanup() => await _session.DisposeAsync();

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
                true,
                directory,
                Path.Combine(directory, "sample.logs.zip"));
            var result = new CrashReportResult(request, true, 2048, null, true, 1024, null);

            await _session.Dispatch(() =>
            {
                var window = new CrashReporterWindow(result);
                window.Show();
                try
                {
                    var exceptionType = window.FindControl<TextBlock>("ExceptionTypeText");
                    var message = window.FindControl<SelectableTextBlock>("ExceptionMessageText");
                    var status = window.FindControl<TextBlock>("DumpStatusText");
                    var logStatus = window.FindControl<TextBlock>("LogStatusText");
                    var openFolder = window.FindControl<Button>("OpenDumpFolderButton");

                    Assert.IsNotNull(exceptionType);
                    Assert.IsNotNull(message);
                    Assert.IsNotNull(status);
                    Assert.IsNotNull(logStatus);
                    Assert.IsNotNull(openFolder);
                    StringAssert.Contains(exceptionType.Text, "InvalidOperationException");
                    Assert.AreEqual("brief failure", message.Text);
                    StringAssert.Contains(status.Text, "2 KB");
                    StringAssert.Contains(logStatus.Text, "1 KB");
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

    [TestMethod]
    public async Task CrashReporterWindow_KeepsActionsVisibleForLongContent()
    {
        var directory = Path.Combine(Path.GetTempPath(), new string('d', 120));
        var request = new CrashReportRequest(
            123,
            "Diary.App",
            "1.0.0",
            DateTimeOffset.UtcNow,
            "System.InvalidOperationException",
            string.Join(' ', Enumerable.Repeat("Could not create glyphTypeface.", 24)),
            directory,
            Path.Combine(directory, new string('f', 120) + ".dmp"),
            Path.Combine(directory, "sample.json"),
            true,
            directory,
            Path.Combine(directory, new string('l', 120) + ".logs.zip"));
        var result = new CrashReportResult(request, true, 2048, null, true, 1024, null);

        await _session.Dispatch(() =>
        {
            var window = new CrashReporterWindow(result);
            window.Show();
            try
            {
                var root = window.FindControl<Grid>("RootGrid");
                var details = window.FindControl<ScrollViewer>("DetailsScrollViewer");
                var actions = window.FindControl<StackPanel>("ActionsPanel");
                var dumpPath = window.FindControl<SelectableTextBlock>("DumpPathText");
                var logPath = window.FindControl<SelectableTextBlock>("LogPathText");

                Assert.IsNotNull(root);
                Assert.IsNotNull(details);
                Assert.IsNotNull(actions);
                Assert.IsNotNull(dumpPath);
                Assert.IsNotNull(logPath);
                Assert.IsTrue(window.CanResize);
                Assert.IsTrue(details.Bounds.Height > 0);
                Assert.IsTrue(
                    actions.Bounds.Bottom <= root.Bounds.Height + 0.5,
                    $"操作区超出窗口内容：bottom={actions.Bounds.Bottom}, rootHeight={root.Bounds.Height}");
                Assert.AreEqual(request.DumpPath, dumpPath.Text);
                Assert.AreEqual(request.LogArchivePath, logPath.Text);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [TestMethod]
    public async Task MainWindow_ApplicationMenuContainsRestartCommand()
    {
        await _session.Dispatch(() =>
        {
            var window = new MainWindow();
            try
            {
                var menuButton = window.FindControl<Button>("ApplicationMenuButton");
                Assert.IsNotNull(menuButton);
                var flyout = Assert.IsInstanceOfType<MenuFlyout>(menuButton.Flyout);
                var restartItem = flyout.Items
                    .OfType<Avalonia.Controls.MenuItem>()
                    .SingleOrDefault(item => string.Equals(item.Header?.ToString(), "重启程序", StringComparison.Ordinal));

                Assert.IsNotNull(restartItem);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [TestMethod]
    public async Task MainWindow_UserManualMenuDefinesBindings()
    {
        await _session.Dispatch(() =>
        {
            var window = new MainWindow();
            try
            {
                var menuButton = window.FindControl<Button>("ApplicationMenuButton");
                Assert.IsNotNull(menuButton);
                var flyout = Assert.IsInstanceOfType<MenuFlyout>(menuButton.Flyout);
                var manualItem = flyout.Items
                    .OfType<Avalonia.Controls.MenuItem>()
                    .SingleOrDefault(item => string.Equals(item.Header?.ToString(), "用户手册", StringComparison.Ordinal));

                Assert.IsNotNull(manualItem);
                Assert.IsNotNull(BindingOperations.GetBindingExpressionBase(manualItem, Visual.IsVisibleProperty));
                Assert.IsNotNull(BindingOperations.GetBindingExpressionBase(
                    manualItem,
                    Avalonia.Controls.MenuItem.CommandProperty));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [TestMethod]
    public void Program_RestartRequestIsConsumedOnlyOnce()
    {
        _ = Program.ConsumeRestartRequest();

        Program.RequestRestart();

        Assert.IsTrue(Program.ConsumeRestartRequest());
        Assert.IsFalse(Program.ConsumeRestartRequest());
    }

    [TestMethod]
    public async Task UserFontCollection_LoadsExternalFontFile()
    {
        var fontPath = GetSourceFontPath(AppFontConfiguration.BundledFallbackFontFileName);
        Assert.IsTrue(File.Exists(fontPath), $"测试字体不存在：{fontPath}");
        Assert.IsTrue(AppFontConfiguration.TryInspectFontFile(fontPath, out var familyName, out var error), error);
        var settings = new ViewConfig
        {
            FontSource = AppFontSource.FontFile,
            FontFilePath = fontPath,
        };
        var resolved = AppFontConfiguration.Resolve(settings);

        Assert.IsNull(resolved.Warning);
        Assert.IsNotNull(resolved.Collection);
        Assert.IsNotNull(resolved.DefaultFamilyName);
        StringAssert.Contains(resolved.DefaultFamilyName, $"#{familyName}");

        await _session.Dispatch(() =>
        {
            FontManager.Current.AddFontCollection(resolved.Collection);
            try
            {
                var typeface = new Typeface(new FontFamily(resolved.DefaultFamilyName));
                Assert.IsTrue(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphTypeface));
                Assert.IsTrue(glyphTypeface.CharacterToGlyphMap.ContainsGlyph(0x4E2D));
            }
            finally
            {
                FontManager.Current.RemoveFontCollection(UserFontCollection.CollectionKey);
            }
        }, CancellationToken.None);
    }

    [TestMethod]
    public async Task AppFontService_AppliesSystemFontAtRuntime()
    {
        await _session.Dispatch(() =>
        {
            var application = Application.Current!;
            var service = new AppFontService(NullLogger<AppFontService>.Instance);
            var systemFamily = FontManager.Current.SystemFonts.First().Name;
            var settings = new ViewConfig
            {
                FontSource = AppFontSource.SystemFont,
                SystemFontFamily = systemFamily,
            };

            var result = service.Apply(application, settings);

            Assert.IsFalse(result.UsedFallback);
            Assert.AreEqual(systemFamily, result.FontFamily.Name);
            Assert.AreEqual(result.FontFamily, application.Resources[AppFontService.ResourceKey]);
        }, CancellationToken.None);
    }

    [TestMethod]
    public async Task AppFontService_AppliesExternalFontAtRuntimeAndFallsBackWhenMissing()
    {
        var fontPath = GetSourceFontPath(AppFontConfiguration.BundledFallbackFontFileName);
        Assert.IsTrue(File.Exists(fontPath), $"测试字体不存在：{fontPath}");
        Assert.IsTrue(AppFontConfiguration.TryInspectFontFile(fontPath, out var familyName, out var error), error);

        await _session.Dispatch(() =>
        {
            var application = Application.Current!;
            var service = new AppFontService(NullLogger<AppFontService>.Instance);
            try
            {
                var applied = service.Apply(application, new ViewConfig
                {
                    FontSource = AppFontSource.FontFile,
                    FontFilePath = fontPath,
                });

                Assert.IsFalse(applied.UsedFallback);
                StringAssert.Contains(applied.FontFamily.ToString(), $"#{familyName}");
                Assert.AreEqual(applied.FontFamily, application.Resources[AppFontService.ResourceKey]);
                var typeface = new Typeface(applied.FontFamily);
                Assert.IsTrue(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphTypeface));
                Assert.IsTrue(glyphTypeface.CharacterToGlyphMap.ContainsGlyph(0x4E2D));

                var fallback = service.Apply(application, new ViewConfig
                {
                    FontSource = AppFontSource.FontFile,
                    FontFilePath = Path.Combine(Path.GetTempPath(), $"missing-font-{Guid.NewGuid():N}.ttf"),
                });

                Assert.IsTrue(fallback.UsedFallback);
                StringAssert.Contains(fallback.Warning, "回退到应用后备字体");
                StringAssert.Contains(fallback.FontFamily.ToString(), $"#{familyName}");
                Assert.AreEqual(fallback.FontFamily, application.Resources[AppFontService.ResourceKey]);
            }
            finally
            {
                service.Apply(application, new ViewConfig
                {
                    FontSource = AppFontSource.SystemDefault,
                });
            }
        }, CancellationToken.None);
    }

    [TestMethod]
    public void AppFontSource_OptionsIncludeBundledDefault()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                AppFontSource.BundledDefault,
                AppFontSource.SystemDefault,
                AppFontSource.SystemFont,
                AppFontSource.FontFile,
            },
            AppFontSource.Options.ToArray());
    }

    [TestMethod]
    public void AppFontConfiguration_BundledDefaultLoadsBundledFont()
    {
        var resolved = AppFontConfiguration.Resolve(new ViewConfig());

        Assert.IsNotNull(resolved.DefaultFamilyName);
        Assert.IsNotNull(resolved.Collection);
        Assert.IsNull(resolved.Warning);
        StringAssert.Contains(resolved.DefaultFamilyName, "#Noto Sans Mono CJK SC");
    }

    [TestMethod]
    public void AppFontConfiguration_MissingBundledDefaultFallsBackToSystemDefault()
    {
        var missingBundledFont = Path.Combine(Path.GetTempPath(), "missing-bundled-default-font.otf");

        var resolved = AppFontConfiguration.Resolve(new ViewConfig(), missingBundledFont);

        Assert.IsNull(resolved.DefaultFamilyName);
        Assert.IsNull(resolved.Collection);
        StringAssert.Contains(resolved.Warning, "应用默认字体不可用");
        StringAssert.Contains(resolved.Warning, "回退到系统默认字体");
    }

    [TestMethod]
    public void AppFontConfiguration_SystemDefaultKeepsPlatformDefault()
    {
        var resolved = AppFontConfiguration.Resolve(new ViewConfig
        {
            FontSource = AppFontSource.SystemDefault,
        });

        Assert.IsNull(resolved.DefaultFamilyName);
        Assert.IsNull(resolved.Collection);
        Assert.IsNull(resolved.Warning);
    }

    [TestMethod]
    public void AppFontConfiguration_InvalidFontFileFallsBackToBundledFont()
    {
        var settings = new ViewConfig
        {
            FontSource = AppFontSource.FontFile,
            FontFilePath = Path.Combine(Path.GetTempPath(), "missing-font.ttf"),
        };

        var resolved = AppFontConfiguration.Resolve(settings);

        Assert.IsNotNull(resolved.DefaultFamilyName);
        Assert.IsNotNull(resolved.Collection);
        StringAssert.Contains(resolved.Warning, "回退到应用后备字体");
    }

    [TestMethod]
    public void AppFontConfiguration_InvalidSystemFontFallsBackToBundledFont()
    {
        var settings = new ViewConfig
        {
            FontSource = AppFontSource.SystemFont,
            SystemFontFamily = $"missing-font-{Guid.NewGuid():N}",
        };

        var resolved = AppFontConfiguration.Resolve(settings);

        Assert.IsNotNull(resolved.DefaultFamilyName);
        Assert.IsNotNull(resolved.Collection);
        StringAssert.Contains(resolved.Warning, "回退到应用后备字体");
    }

    [TestMethod]
    public void AppFontConfiguration_MissingBundledFontFallsBackToSystemDefault()
    {
        var settings = new ViewConfig
        {
            FontSource = AppFontSource.FontFile,
            FontFilePath = Path.Combine(Path.GetTempPath(), "missing-user-font.ttf"),
        };
        var missingBundledFont = Path.Combine(Path.GetTempPath(), "missing-bundled-font.ttf");

        var resolved = AppFontConfiguration.Resolve(settings, missingBundledFont);

        Assert.IsNull(resolved.DefaultFamilyName);
        Assert.IsNull(resolved.Collection);
        StringAssert.Contains(resolved.Warning, "应用后备字体不可用");
        StringAssert.Contains(resolved.Warning, "回退到系统默认字体");
    }

    [TestMethod]
    public void AppFontConfiguration_BundledFontIsCopiedToOutputDirectory()
    {
        Assert.IsTrue(
            File.Exists(AppFontConfiguration.BundledFallbackFontPath),
            $"应用后备字体未复制到输出目录：{AppFontConfiguration.BundledFallbackFontPath}");
    }

    [TestMethod]
    public async Task SettingFont_SavesValidatedExternalFontSelection()
    {
        var fontPath = GetSourceFontPath(AppFontConfiguration.BundledFallbackFontFileName);
        var config = new ViewConfig();

        await _session.Dispatch(() =>
        {
            var setting = new SettingFont("界面字体", "", config);
            setting.Load();
            setting.Source = AppFontSource.FontFile;
            setting.FontFilePath = fontPath;
            setting.Save();

            Assert.AreEqual(AppFontSource.FontFile, config.FontSource);
            Assert.AreEqual(Path.GetFullPath(fontPath), config.FontFilePath);
            StringAssert.Contains(setting.FontFileStatus, "Noto Sans Mono CJK SC");
        }, CancellationToken.None);
    }

    private static ScriptEditorViewModel CreateViewModel(IScriptDirectoryLoader loader) =>
        new(loader, NullLogger.Instance);

    private static string GetSourceFontPath(string fileName) =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "Diary.App",
            "Assets",
            "Fonts",
            fileName));

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
