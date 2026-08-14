using Diary.Script.CSharp;
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
}
