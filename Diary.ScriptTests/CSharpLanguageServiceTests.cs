using Diary.Script.CSharp;

namespace Diary.ScriptTests;

[TestClass]
public sealed class CSharpLanguageServiceTests
{
    private const string Source = """
        using Diary.ScriptBase;
        using Diary.ScriptHost;

        public sealed class SampleScript : ApplicationScript
        {
            public override string Id => "sample";
            public override string Name => "Sample";

            public override async ValueTask<ScriptExecutionResult> ExecuteAsync(
                IScriptApplicationContext context,
                CancellationToken cancellationToken = default)
            {
                var diary = context.GetRequiredApi<IDiaryApi>();
                var result = await diary.QueryAsync(new ScriptWorkItemQuery(), cancellationToken);
                return ScriptExecutionResult.Succeeded();
            }
        }
        """;

    [TestMethod]
    public void Analyze_ReturnsSemanticDiagnostics()
    {
        var source = Source.Replace(
            "await diary.QueryAsync",
            "await diary.NotExistingAsync",
            StringComparison.Ordinal);
        var analysis = new CSharpLanguageService().Analyze(source, "sample.cs");

        Assert.IsTrue(analysis.Diagnostics.Any(diagnostic => diagnostic.Code == "CS1061"));
    }

    [TestMethod]
    public void GetCompletions_UsesSemanticTypeMembers()
    {
        var source = Source.Replace(
            "var result = await diary.QueryAsync",
            "var result = await diary.",
            StringComparison.Ordinal);
        var analysis = new CSharpLanguageService().Analyze(source, "sample.cs");
        var offset = source.IndexOf("var result", StringComparison.Ordinal) + "var result = await diary.".Length;

        var items = analysis.GetCompletions(offset);

        Assert.IsTrue(items.Any(item => item.Text == "QueryAsync"));
        Assert.IsTrue(items.Any(item => item.Text == "CreateLogItemAsync"));
    }

    [TestMethod]
    public void GetHover_ReturnsSymbolSignature()
    {
        var analysis = new CSharpLanguageService().Analyze(Source, "sample.cs");
        var offset = Source.IndexOf("QueryAsync", StringComparison.Ordinal);

        Assert.IsTrue(offset >= 0);
        Assert.AreEqual("QueryAsync", Source.Substring(offset, "QueryAsync".Length));
        var hover = analysis.GetHover(offset);

        Assert.IsNotNull(hover);
        StringAssert.Contains(hover!.Signature, "QueryAsync");
    }
}
