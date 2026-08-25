using Diary.Script.CSharp;
using Diary.ScriptBase;
using Diary.ScriptHost;

namespace Diary.ScriptTests;

[TestClass]
public sealed class CSharpEngineTests
{
    private readonly CSharpEngine _engine = new();

    [TestMethod]
    public async Task ValidateAsync_CompilesWithoutLoadingOrInstantiatingProgram()
    {
        var source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Diary.ScriptBase;
            public sealed class ExplodingProgram : IScriptProgramV1
            {
                static ExplodingProgram() => throw new InvalidOperationException("must not run");
                public ScriptDescriptor Descriptor => new("test", "Test", ScriptApiVersion.V1, ScriptScope.Application);
                public ValueTask<ScriptExecutionResult> ExecuteAsync(ScriptExecutionRequest request, IScriptExecutionContext context, CancellationToken cancellationToken = default)
                    => ValueTask.FromResult(ScriptExecutionResult.Succeeded());
            }
            """;

        var result = await _engine.ValidateAsync(new ScriptValidationRequest("validate.cs", source));

        Assert.IsTrue(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.AreEqual("csharp", result.EngineName);
    }

    [TestMethod]
    public async Task ValidateAsync_ReturnsCompilerDiagnostics()
    {
        var result = await _engine.ValidateAsync(new ScriptValidationRequest(
            "broken.cs",
            "public sealed class Broken { public void Run( }"));

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Diagnostics.Any(diagnostic =>
            diagnostic.Category == ScriptDiagnosticCategory.Syntax
            && diagnostic.Line is > 0
            && diagnostic.Column is > 0));
    }

    [TestMethod]
    public async Task BuildAsync_CompilesAndCreatesV1Program()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Diary.ScriptBase;
            public sealed class TestProgram : IScriptProgramV1
            {
                public ScriptDescriptor Descriptor => new("test", "Test", ScriptApiVersion.V1, ScriptScope.Application);
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
    public async Task BuildAsync_DiscoversV2ParametersFromTypedBaseClass()
    {
        var source = """
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Diary.ScriptBase;
            public sealed class ParameterizedProgram : ApplicationScriptV2
            {
                public override string Id => "parameterized-csharp";
                public override string Name => "Parameterized C#";
                public override IReadOnlyList<ScriptParameterDefinition> Parameters =>
                [
                    new("limit", "Limit", ScriptParameterType.Integer, Required: true),
                    new("enabled", "Enabled", ScriptParameterType.Boolean, DefaultValue: "false"),
                ];
                public override ValueTask<ScriptExecutionResult> ExecuteAsync(
                    IScriptApplicationContext context,
                    CancellationToken cancellationToken = default) =>
                    ValueTask.FromResult(ScriptExecutionResult.Succeeded());
            }
            """;

        var result = await _engine.BuildAsync(new ScriptBuildRequest("parameterized.cs", source));

        Assert.IsTrue(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.AreEqual(ScriptApiVersion.V2, result.Program!.Descriptor.ApiVersion);
        Assert.AreEqual(2, result.Program.Descriptor.Parameters!.Count);
        Assert.AreEqual("limit", result.Program.Descriptor.Parameters[0].Name);
        Assert.IsTrue(result.Program.Descriptor.Parameters[0].Required);
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
                public ScriptDescriptor Descriptor => new("query", "Query", ScriptApiVersion.V1, ScriptScope.Application);
                public async ValueTask<ScriptExecutionResult> ExecuteAsync(ScriptExecutionRequest request, IScriptExecutionContext context, CancellationToken cancellationToken = default)
                {
                    var api = context.Api();
                    await api.Diary.QueryAsync(new ScriptWorkItemQuery { Limit = 1 }, cancellationToken);
                    return ScriptExecutionResult.Succeeded();
                }
            }
        """;
        var result = await _engine.BuildAsync(new ScriptBuildRequest("query.cs", source));
        Assert.IsTrue(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var api = new RecordingQueryApi();
        var context = new Diary.Script.Runtime.ScriptExecutionContext();
        context.RegisterApi<IDiaryApi>(new DiaryApi(api, new NoopLogItemApi(), new NoopTemplateLogItemApi()));

        var execution = await result.Program!.ExecuteAsync(
            new ScriptExecutionRequest(),
            context);

        Assert.AreEqual(ScriptExecutionStatus.Succeeded, execution.Status);
        Assert.IsTrue(api.Called);
    }

    [TestMethod]
    public async Task BuildAsync_UsesCacheAndRebuildsChangedOrCorruptedSource()
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), $"diary-script-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheDirectory);
        try
        {
            var source = """
                using System.Threading;
                using System.Threading.Tasks;
                using Diary.ScriptBase;
                public sealed class CachedProgram : IScriptProgramV1
                {
                    public ScriptDescriptor Descriptor => new("cached", "Cached", ScriptApiVersion.V1, ScriptScope.Application);
                    public ValueTask<ScriptExecutionResult> ExecuteAsync(ScriptExecutionRequest request, IScriptExecutionContext context, CancellationToken cancellationToken = default)
                        => ValueTask.FromResult(ScriptExecutionResult.Succeeded());
                }
                """;
            var engine = new CSharpEngine(cacheDirectory);
            var first = await engine.BuildAsync(new ScriptBuildRequest("cached.cs", source));
            (first.Program as IDisposable)?.Dispose();
            var second = await engine.BuildAsync(new ScriptBuildRequest("cached.cs", source));
            (second.Program as IDisposable)?.Dispose();

            var result = await engine.BuildAsync(new ScriptBuildRequest("cached.cs", source));

            Assert.IsTrue(result.Succeeded);
            Assert.IsTrue(result.Diagnostics.Any(item => item.Code == "SCRIPT_CACHE_HIT"));
            var cachePath = Directory.EnumerateFiles(cacheDirectory, "*.dll").Single();
            var changed = await engine.BuildAsync(new ScriptBuildRequest(
                "cached.cs",
                source.Replace("\"Cached\"", "\"Changed\"", StringComparison.Ordinal)));
            Assert.IsTrue(changed.Succeeded);
            Assert.IsFalse(changed.Diagnostics.Any(item => item.Code == "SCRIPT_CACHE_HIT"));
            (changed.Program as IDisposable)?.Dispose();
            await File.WriteAllTextAsync(cachePath, "broken");
            var rebuilt = await engine.BuildAsync(new ScriptBuildRequest("cached.cs", source));
            Assert.IsTrue(rebuilt.Succeeded);
            Assert.IsFalse(rebuilt.Diagnostics.Any(item => item.Code == "SCRIPT_CACHE_HIT"));
            (rebuilt.Program as IDisposable)?.Dispose();
        }
        finally
        {
            if (Directory.Exists(cacheDirectory))
                Directory.Delete(cacheDirectory, true);
        }
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
    public async Task BuildAsync_RejectsDangerousProcessApi()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Diary.ScriptBase;
            public sealed class DangerousProgram : IScriptProgramV1
            {
                    public ScriptDescriptor Descriptor => new("dangerous", "Dangerous", ScriptApiVersion.V1, ScriptScope.Application);
                public ValueTask<ScriptExecutionResult> ExecuteAsync(ScriptExecutionRequest request, IScriptExecutionContext context, CancellationToken cancellationToken = default)
                {
                    _ = System.Environment.ProcessPath;
                    return ValueTask.FromResult(ScriptExecutionResult.Succeeded());
                }
            }
            """;

        var result = await _engine.BuildAsync(new ScriptBuildRequest("dangerous.cs", source));

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Diagnostics.Any(item =>
            item.Code == "CSHARP_API_FORBIDDEN"
            && item.Category == ScriptDiagnosticCategory.Security
            && item.Line is > 0));
    }

    [TestMethod]
    [DataRow("dynamic value = new object(); value.ToString();")]
    [DataRow("_ = new object().GetType();")]
    [DataRow("_ = Task.Run(() => { });")]
    public async Task BuildAsync_RejectsExecutionEscapeHatches(string statement)
    {
        var source = $$"""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Diary.ScriptBase;
            public sealed class EscapeProgram : IScriptProgramV1
            {
                    public ScriptDescriptor Descriptor => new("escape", "Escape", ScriptApiVersion.V1, ScriptScope.Application);
                public ValueTask<ScriptExecutionResult> ExecuteAsync(ScriptExecutionRequest request, IScriptExecutionContext context, CancellationToken cancellationToken = default)
                {
                    {{statement}}
                    return ValueTask.FromResult(ScriptExecutionResult.Succeeded());
                }
            }
            """;

        var result = await _engine.BuildAsync(new ScriptBuildRequest("escape.cs", source));

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Diagnostics.Any(item =>
            item.Code == "CSHARP_API_FORBIDDEN"
            && item.Category == ScriptDiagnosticCategory.Security));
    }

    [TestMethod]
    public async Task BuildAsync_RejectsMissingEntrypoint()
    {
        var result = await _engine.BuildAsync(new ScriptBuildRequest("empty.cs", "public sealed class Empty { }"));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("CSHARP_ENTRYPOINT_COUNT", result.Diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task BuildAsync_CompilesQueryScriptBaseClass()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Diary.ScriptBase;
            public sealed class DemoQuery : QueryScript
            {
                public override string Id => "demo-query";
                public override string Name => "DemoQuery";
                public override ValueTask<ScriptExecutionResult> ExecuteAsync(IScriptApplicationContext context, CancellationToken cancellationToken = default)
                    => ValueTask.FromResult(ScriptExecutionResult.Succeeded());
            }
            """;

        var result = await _engine.BuildAsync(new ScriptBuildRequest("query.cs", source));

        Assert.IsTrue(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        Assert.AreEqual(ScriptEntryKind.Query, result.Program!.Descriptor.EntryKind);
        Assert.IsTrue(ScriptProgramAdapter.TryAdapt(result.Program, out var adapted));
        Assert.IsNotNull(adapted);

        var context = new Diary.Script.Runtime.ScriptExecutionContext();
        var outcome = await adapted!.ExecuteAsync(
            new ScriptExecutionRequest(EntryKind: ScriptEntryKind.Query),
            context);
        Assert.AreEqual(ScriptExecutionStatus.Succeeded, outcome.Status);
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

    private sealed class NoopLogItemApi : ILogItemScriptApi
    {
        public ValueTask<ScriptLogItemResult> CreateAsync(ScriptLogItemRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ScriptLogItemResult.Failure(ScriptLogItemErrorCode.ProviderFailure, "测试未实现。"));
    }

    private sealed class NoopTemplateLogItemApi : ITemplateLogItemScriptApi
    {
        public ValueTask<ScriptLogItemResult> CreateAsync(ScriptTemplateLogItemRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ScriptLogItemResult.Failure(ScriptLogItemErrorCode.ProviderFailure, "测试未实现。"));
    }
}
