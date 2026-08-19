using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Diary.App.ViewModels.Dialogs;
using Diary.Script.CSharp;
using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.AppTests;

[TestClass]
public sealed class ScriptCreationViewModelTests
{
    [TestMethod]
    public async Task CreateCommand_GeneratesLoadableTemplateForEachLanguage()
    {
        foreach (var (language, extension, engine, marker) in new[]
                 {
                     ("C#", ".cs", "csharp", "EditorScript"),
                     ("Lua", ".lua", "lua", "function editor_main(context)"),
                     ("Python", ".py", "python", "def editor_main(context):"),
                 })
        {
            var root = Path.Combine(Path.GetTempPath(), $"diary-app-script-create-{Guid.NewGuid():N}");
            try
            {
                var viewModel = new ScriptCreationViewModel(root)
                {
                    Name = "示例脚本",
                    Id = $"sample-{engine}",
                    SelectedLanguage = language,
                    SelectedScope = "编辑器脚本",
                };
                object? createdPath = null;
                viewModel.RequestClose += (_, value) => createdPath = value;

                var command = Assert.IsInstanceOfType<IAsyncRelayCommand>(viewModel.CreateCommand);
                await command.ExecuteAsync(null);

                var sourcePath = Assert.IsInstanceOfType<string>(createdPath);
                Assert.AreEqual(extension, Path.GetExtension(sourcePath));
                StringAssert.Contains(await File.ReadAllTextAsync(sourcePath), marker);
                var metadata = JsonSerializer.Deserialize<ScriptFileMetadata>(
                    await File.ReadAllTextAsync(sourcePath + ".json"));
                Assert.IsNotNull(metadata);
                if (language == "C#")
                {
                    Assert.IsNull(metadata.Engine);
                    Assert.IsNull(metadata.Scope);
                }
                else
                {
                    Assert.AreEqual(engine, metadata.Engine);
                    Assert.AreEqual(ScriptScope.Editor, metadata.Scope);
                }
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task CreateCommand_GeneratesWorkItemQueryTemplateForEachLanguage()
    {
        foreach (var (language, engine, marker) in new[]
                 {
                      ("C#", "csharp", "context.Api().Diary"),
                     ("Lua", "lua", "diary.work_items.query"),
                     ("Python", "python", "context.diary.work_items.query"),
                 })
        {
            var root = Path.Combine(Path.GetTempPath(), $"diary-app-script-sample-{Guid.NewGuid():N}");
            try
            {
                var viewModel = new ScriptCreationViewModel(root)
                {
                    Name = "查询示例",
                    Id = $"query-{engine}",
                    SelectedLanguage = language,
                    SelectedTemplate = "查询工作项",
                };
                object? createdPath = null;
                viewModel.RequestClose += (_, value) => createdPath = value;

                await Assert.IsInstanceOfType<IAsyncRelayCommand>(viewModel.CreateCommand).ExecuteAsync(null);

                var sourcePath = Assert.IsInstanceOfType<string>(createdPath);
                StringAssert.Contains(await File.ReadAllTextAsync(sourcePath), marker);
                var metadata = JsonSerializer.Deserialize<ScriptFileMetadata>(
                    await File.ReadAllTextAsync(sourcePath + ".json"));
                Assert.IsNotNull(metadata);
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task CreateCommand_GeneratesQueryScriptTemplateForEachLanguage()
    {
        foreach (var (language, engine, marker) in new[]
                 {
                     ("C#", "csharp", ": QueryScript"),
                     ("Lua", "lua", "function query_main(context)"),
                     ("Python", "python", "def query_main(context):"),
                 })
        {
            var root = Path.Combine(Path.GetTempPath(), $"diary-app-script-query-{Guid.NewGuid():N}");
            try
            {
                var viewModel = new ScriptCreationViewModel(root)
                {
                    Name = "查询脚本",
                    Id = $"query-entry-{engine}",
                    SelectedLanguage = language,
                    SelectedTemplate = "查询脚本",
                };
                object? createdPath = null;
                viewModel.RequestClose += (_, value) => createdPath = value;

                await Assert.IsInstanceOfType<IAsyncRelayCommand>(viewModel.CreateCommand).ExecuteAsync(null);

                var sourcePath = Assert.IsInstanceOfType<string>(createdPath);
                var source = await File.ReadAllTextAsync(sourcePath);
                StringAssert.Contains(source, marker);
                var metadata = JsonSerializer.Deserialize<ScriptFileMetadata>(
                    await File.ReadAllTextAsync(sourcePath + ".json"));
                Assert.IsNotNull(metadata);
                if (language == "C#")
                {
                    Assert.IsNull(metadata.EntryKind);
                    Assert.IsNull(metadata.Scope);
                    var build = await new CSharpEngine().BuildAsync(new ScriptBuildRequest(sourcePath, source));
                    Assert.IsTrue(
                        build.Succeeded,
                        string.Join(Environment.NewLine, build.Diagnostics.Select(diagnostic => diagnostic.Message)));
                }
                else
                {
                    Assert.AreEqual(ScriptEntryKind.Query, metadata.EntryKind);
                    Assert.AreEqual(ScriptScope.Application, metadata.Scope);
                }
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task CreateCommand_GeneratesAutomationScriptTemplateForEachLanguage()
    {
        foreach (var (language, engine, marker) in new[]
                 {
                     ("C#", "csharp", ": AutomationScript"),
                     ("Lua", "lua", "function automation_main(context)"),
                     ("Python", "python", "def automation_main(context):"),
                 })
        {
            var root = Path.Combine(Path.GetTempPath(), $"diary-app-script-auto-{Guid.NewGuid():N}");
            try
            {
                var viewModel = new ScriptCreationViewModel(root)
                {
                    Name = "自动化脚本",
                    Id = $"auto-entry-{engine}",
                    SelectedLanguage = language,
                    SelectedTemplate = "自动化脚本",
                    ScheduleText = "daily 22:15",
                    RunOnStartup = true,
                };
                object? createdPath = null;
                viewModel.RequestClose += (_, value) => createdPath = value;

                await Assert.IsInstanceOfType<IAsyncRelayCommand>(viewModel.CreateCommand).ExecuteAsync(null);

                var sourcePath = Assert.IsInstanceOfType<string>(createdPath);
                var source = await File.ReadAllTextAsync(sourcePath);
                StringAssert.Contains(source, marker);
                var metadata = JsonSerializer.Deserialize<ScriptFileMetadata>(
                    await File.ReadAllTextAsync(sourcePath + ".json"));
                Assert.IsNotNull(metadata);
                Assert.AreEqual("daily 22:15", metadata.Schedule);
                Assert.IsTrue(metadata.RunOnStartup);
                if (language == "C#")
                {
                    Assert.IsNull(metadata.EntryKind);
                    Assert.IsNull(metadata.Scope);
                    var build = await new CSharpEngine().BuildAsync(new ScriptBuildRequest(sourcePath, source));
                    Assert.IsTrue(
                        build.Succeeded,
                        string.Join(Environment.NewLine, build.Diagnostics.Select(diagnostic => diagnostic.Message)));
                }
                else
                {
                    Assert.AreEqual(ScriptEntryKind.Automation, metadata.EntryKind);
                    Assert.AreEqual(ScriptScope.Application, metadata.Scope);
                }
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task CreateCommand_ValidatesAutomationScheduleAndAllowsEmpty()
    {
        var invalid = new ScriptCreationViewModel(CreateRoot())
        {
            Name = "自动化脚本",
            Id = "auto-invalid",
            SelectedTemplate = "自动化脚本",
            ScheduleText = "hourly",
        };
        var command = Assert.IsInstanceOfType<IAsyncRelayCommand>(invalid.CreateCommand);
        Assert.IsFalse(command.CanExecute(null));

        var emptySchedule = new ScriptCreationViewModel(CreateRoot())
        {
            Name = "自动化脚本",
            Id = "auto-empty",
            SelectedTemplate = "自动化脚本",
            ScheduleText = "",
        };
        object? createdPath = null;
        emptySchedule.RequestClose += (_, value) => createdPath = value;
        await Assert.IsInstanceOfType<IAsyncRelayCommand>(emptySchedule.CreateCommand).ExecuteAsync(null);
        var metadata = JsonSerializer.Deserialize<ScriptFileMetadata>(
            await File.ReadAllTextAsync(Assert.IsInstanceOfType<string>(createdPath) + ".json"));
        Assert.IsNull(metadata!.Schedule);
    }

    private static string CreateRoot() =>
        Path.Combine(Path.GetTempPath(), $"diary-app-script-create-{Guid.NewGuid():N}");

    [TestMethod]
    public async Task CreateCommand_GeneratesEditorTargetTemplateMetadata()
    {
        foreach (var (template, target) in new[]
                 {
                     ("日目标脚本", ScriptEditorTargetKind.Day),
                     ("周目标脚本", ScriptEditorTargetKind.Week),
                     ("月目标脚本", ScriptEditorTargetKind.Month),
                     ("季度目标脚本", ScriptEditorTargetKind.Quarter),
                     ("年目标脚本", ScriptEditorTargetKind.Year),
                     ("当前事项脚本", ScriptEditorTargetKind.WorkItem),
                 })
        {
            var root = Path.Combine(Path.GetTempPath(), $"diary-app-script-target-{Guid.NewGuid():N}");
            try
            {
                var viewModel = new ScriptCreationViewModel(root)
                {
                    Name = "目标脚本",
                    Id = $"target-{target}",
                    SelectedScope = "编辑器脚本",
                    SelectedTemplate = template,
                };
                object? createdPath = null;
                viewModel.RequestClose += (_, value) => createdPath = value;

                await Assert.IsInstanceOfType<IAsyncRelayCommand>(viewModel.CreateCommand).ExecuteAsync(null);

                var sourcePath = Assert.IsInstanceOfType<string>(createdPath);
                var source = await File.ReadAllTextAsync(sourcePath);
                var metadata = JsonSerializer.Deserialize<ScriptFileMetadata>(
                    await File.ReadAllTextAsync(sourcePath + ".json"));
                Assert.IsNotNull(metadata);
                Assert.IsNull(metadata.SupportedEditorTargets);
                Assert.IsNull(metadata.Scope);
                StringAssert.Contains(source, $"ScriptEditorTargetKind.{target}");
                var build = await new CSharpEngine().BuildAsync(new ScriptBuildRequest(sourcePath, source));
                Assert.IsTrue(build.Succeeded, string.Join(Environment.NewLine, build.Diagnostics.Select(item => item.Message)));
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public void Templates_ChangeWithScope()
    {
        var viewModel = new ScriptCreationViewModel();

        Assert.IsFalse(viewModel.Templates.Contains("日目标脚本"));

        viewModel.SelectedScope = "编辑器脚本";
        Assert.IsTrue(viewModel.Templates.Contains("日目标脚本"));
        Assert.IsTrue(viewModel.Templates.Contains("周目标脚本"));

        viewModel.SelectedTemplate = "日目标脚本";
        viewModel.SelectedScope = "应用脚本";
        Assert.AreEqual("空白脚本", viewModel.SelectedTemplate);
    }
}
