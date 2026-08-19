using Diary.App.ViewModels;

namespace Diary.AppTests;

[TestClass]
public sealed class ScriptApiReferenceViewModelTests
{
    [TestMethod]
    public void SelectingLanguage_LoadsLanguageBlocks()
    {
        var root = Path.Combine(Path.GetTempPath(), $"diary-api-reference-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "CSharp.md"), "# C# API\n\n说明文字。\n\n```csharp\nvar value = 1;\n```");
            File.WriteAllText(Path.Combine(root, "Lua.md"), "# Lua API\n\n```lua\nfunction application_main(context)\nend\n```");
            File.WriteAllText(Path.Combine(root, "Python.md"), "# Python API\n\n```python\ndef application_main(context):\n    return None\n```");

            var viewModel = new ScriptApiReferenceViewModel(root);

            Assert.AreEqual("C# API Reference", viewModel.Title);
            Assert.IsFalse(viewModel.HasReference);
            Assert.AreEqual(0, viewModel.Blocks.Count);

            viewModel.EnsureLoaded();

            Assert.IsTrue(viewModel.HasReference);
            Assert.IsTrue(viewModel.Blocks.Any(block => block.IsHeading && block.Text == "C# API"));
            Assert.IsTrue(viewModel.Blocks.Any(block => block.IsCode && block.Text.Contains("var value = 1;", StringComparison.Ordinal)));

            viewModel.SelectedLanguage = "Python";

            Assert.AreEqual("Python API Reference", viewModel.Title);
            Assert.IsTrue(viewModel.Blocks.Any(block => block.IsCode && block.Text.Contains("def application_main(context):", StringComparison.Ordinal)));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
