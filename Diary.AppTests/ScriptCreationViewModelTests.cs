using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Diary.App.ViewModels.Dialogs;
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
                     ("C#", ".cs", "csharp", "IScriptProgramV1"),
                     ("Lua", ".lua", "lua", "function main(context)"),
                     ("Python", ".py", "python", "def main(context):"),
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
                Assert.AreEqual(engine, metadata.Engine);
                Assert.AreEqual(ScriptScope.Editor, metadata.Scope);
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
                      ("C#", "csharp", "GetApi<IDiaryApi>()"),
                     ("Lua", "lua", "diary.workItems.query"),
                     ("Python", "python", "context.diary.workItems.query"),
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
}
