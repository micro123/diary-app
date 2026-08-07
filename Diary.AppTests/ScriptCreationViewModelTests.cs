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
}
