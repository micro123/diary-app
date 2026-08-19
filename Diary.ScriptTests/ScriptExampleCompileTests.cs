using Diary.Script.CSharp;
using Diary.Script.Lua;
using Diary.Script.Py;
using Diary.ScriptBase;

namespace Diary.ScriptTests;

[TestClass]
public sealed class ScriptExampleCompileTests
{
    [TestMethod]
    public async Task CSharpExamples_Compile()
    {
        var examplesDirectory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../Docs/ScriptApi/Examples"));
        Assert.IsTrue(Directory.Exists(examplesDirectory), $"示例目录不存在：{examplesDirectory}");

        var engine = new CSharpEngine();
        foreach (var path in Directory.EnumerateFiles(examplesDirectory, "*.cs").Order(StringComparer.Ordinal))
        {
            var source = await File.ReadAllTextAsync(path);
            var result = await engine.BuildAsync(new ScriptBuildRequest(path, source));
            Assert.IsTrue(
                result.Succeeded,
                $"{Path.GetFileName(path)} 编译失败：{string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))}");
        }
    }

    [TestMethod]
    public async Task LuaExamples_Parse()
    {
        var examplesDirectory = GetExamplesDirectory();
        var engine = new LuaEngine();
        foreach (var path in Directory.EnumerateFiles(examplesDirectory, "*.lua").Order(StringComparer.Ordinal))
        {
            var source = await File.ReadAllTextAsync(path);
            var result = await engine.BuildAsync(new ScriptBuildRequest(
                path,
                source,
                DescriptorHint: CreateDescriptorHint("lua")));
            Assert.IsTrue(
                result.Succeeded,
                $"{Path.GetFileName(path)} 解析失败：{string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))}");
        }
    }

    [TestMethod]
    public async Task PythonExamples_Parse()
    {
        var examplesDirectory = GetExamplesDirectory();
        var engine = new PythonEngine(descriptorHint: CreateDescriptorHint("python"));
        foreach (var path in Directory.EnumerateFiles(examplesDirectory, "*.py").Order(StringComparer.Ordinal))
        {
            var source = await File.ReadAllTextAsync(path);
            var result = await engine.BuildAsync(new ScriptBuildRequest(path, source));
            Assert.IsTrue(
                result.Succeeded,
                $"{Path.GetFileName(path)} 解析失败：{string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Message))}");
        }
    }

    private static string GetExamplesDirectory() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "../../../../Docs/ScriptApi/Examples"));

    private static ScriptDescriptorHint CreateDescriptorHint(string engineName) => new(
        Id: "example",
        Name: "Example",
        Scope: ScriptScope.Editor,
        EngineName: engineName,
        EntryKind: ScriptEntryKind.Editor);
}
