using Diary.Script.CSharp;
using Diary.ScriptBase;
using Diary.ScriptHost;

namespace Diary.ScriptTests;

[TestClass]
public sealed class CSharpEngineTests
{
    private readonly CSharpEngine _engine = new();

    [TestMethod]
    public async Task BuildAsync_CompilesAndCreatesV1Program()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Diary.ScriptBase;
            public sealed class TestProgram : IScriptProgramV1
            {
                public ScriptDescriptor Descriptor => new("test", "Test", ScriptApiVersion.V1, ScriptScope.Application, ScriptCapability.ReadDiary);
                public ValueTask<ScriptExecutionResult> ExecuteAsync(ScriptExecutionRequest request, IScriptExecutionContext context, CancellationToken cancellationToken = default)
                    => ValueTask.FromResult(ScriptExecutionResult.Succeeded());
            }
            """;

        var result = await _engine.BuildAsync(new ScriptBuildRequest("test.cs", source));

        Assert.IsTrue(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.AreEqual("test", result.Program!.Descriptor.Id);
    }

    [TestMethod]
    public async Task BuildAsync_CompiledProgramCanUseReadOnlyHostApi()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Diary.ScriptBase;
            using Diary.ScriptHost;
            public sealed class QueryProgram : IScriptProgramV1
            {
                public ScriptDescriptor Descriptor => new("query", "Query", ScriptApiVersion.V1, ScriptScope.Application, ScriptCapability.ReadDiary);
                public async ValueTask<ScriptExecutionResult> ExecuteAsync(ScriptExecutionRequest request, IScriptExecutionContext context, CancellationToken cancellationToken = default)
                {
                    var api = context.GetApi<IWorkItemQueryScriptApi>();
                    await api!.QueryAsync(new ScriptWorkItemQuery { Limit = 1 }, cancellationToken);
                    return ScriptExecutionResult.Succeeded();
                }
            }
        """;
        var result = await _engine.BuildAsync(new ScriptBuildRequest("query.cs", source));
        Assert.IsTrue(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var api = new RecordingQueryApi();
        var context = new Diary.Script.Runtime.ScriptExecutionContext(ScriptCapability.ReadDiary);
        context.RegisterApi<IWorkItemQueryScriptApi>(api, ScriptCapability.ReadDiary);

        var execution = await result.Program!.ExecuteAsync(
            new ScriptExecutionRequest(new ScriptTarget(ScriptScope.Application)),
            context);

        Assert.AreEqual(ScriptExecutionStatus.Succeeded, execution.Status);
        Assert.IsTrue(api.Called);
    }

    [TestMethod]
    public async Task BuildAsync_ReturnsSourceLocationForSyntaxError()
    {
        var result = await _engine.BuildAsync(new ScriptBuildRequest("broken.cs", "public class Broken {"));

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Diagnostics.Any(diagnostic =>
            diagnostic.Severity == ScriptDiagnosticSeverity.Error
            && diagnostic.SourcePath == "broken.cs"
            && diagnostic.Line is > 0
            && diagnostic.Column is > 0));
    }

    [TestMethod]
    public async Task BuildAsync_RejectsMissingEntrypoint()
    {
        var result = await _engine.BuildAsync(new ScriptBuildRequest("empty.cs", "public sealed class Empty { }"));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("CSHARP_ENTRYPOINT_COUNT", result.Diagnostics.Single().Code);
    }

    [TestMethod]
    public void Match_IsCaseInsensitiveAndRejectsOtherExtensions()
    {
        Assert.IsTrue(_engine.Match(new ScriptMatchRequest("test.CS")).IsMatch);
        Assert.IsFalse(_engine.Match(new ScriptMatchRequest("test.lua")).IsMatch);
    }

    private sealed class RecordingQueryApi : IWorkItemQueryScriptApi
    {
        public bool Called { get; private set; }

        public ValueTask<ScriptWorkItemQueryResult> QueryAsync(
            ScriptWorkItemQuery query,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            return ValueTask.FromResult(ScriptWorkItemQueryResult.Success([], query));
        }
    }
}
