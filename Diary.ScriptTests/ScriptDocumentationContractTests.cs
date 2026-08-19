namespace Diary.ScriptTests;

[TestClass]
public sealed class ScriptDocumentationContractTests
{
    [TestMethod]
    public void LanguageReferencesLinkToCompleteExamplesAndUseStableEntrypoints()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Docs/ScriptApi"));
        var references = new[]
        {
            (File: "CSharp.md", Example: "Examples/CSharpQuickStart.md", Entry: "ApplicationScript"),
            (File: "Lua.md", Example: "Examples/LuaQuickStart.md", Entry: "application_main(context)"),
            (File: "Python.md", Example: "Examples/PythonQuickStart.md", Entry: "application_main(context)"),
        };

        foreach (var reference in references)
        {
            var referencePath = Path.Combine(root, reference.File);
            Assert.IsTrue(File.Exists(referencePath), referencePath);
            var content = File.ReadAllText(referencePath);
            StringAssert.Contains(content, reference.Example, reference.File);
            StringAssert.Contains(content, reference.Entry, reference.File);

            var examplePath = Path.Combine(root, reference.Example.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(examplePath), examplePath);
            var example = File.ReadAllText(examplePath);
            StringAssert.Contains(example.ToLowerInvariant(), "idempot", reference.Example);
            StringAssert.Contains(example.ToLowerInvariant(), "preview", reference.Example);
        }
    }

    [TestMethod]
    public void CompletedWorkArchivesStage910AndTodosDoesNotKeepItActive()
    {
        var docsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Docs"));
        var completed = File.ReadAllText(Path.Combine(docsRoot, "CompletedWork.md"));
        var todos = File.ReadAllText(Path.Combine(docsRoot, "TODOS.md"));

        StringAssert.Contains(completed, "阶段 9.10：脚本 API 用户体验和功能入口优化");
        Assert.IsFalse(todos.Contains("## 阶段 9.10：脚本 API 用户体验和功能入口优化", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ExportReferencesShareContractAndLinkRunnableOvertimeExamples()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Docs/ScriptApi"));
        Assert.IsTrue(File.Exists(Path.Combine(root, "Export.md")));

        var references = new[]
        {
            (File: "CSharp.md", Example: "Examples/OvertimeExport.cs"),
            (File: "Lua.md", Example: "Examples/OvertimeExport.lua"),
            (File: "Python.md", Example: "Examples/OvertimeExport.py"),
        };
        foreach (var reference in references)
        {
            var content = File.ReadAllText(Path.Combine(root, reference.File));
            StringAssert.Contains(content, "Export.md");
            StringAssert.Contains(content, reference.Example);
            Assert.IsTrue(File.Exists(Path.Combine(root, reference.Example.Replace('/', Path.DirectorySeparatorChar))));
        }
    }
}
