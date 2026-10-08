using System.Collections.Immutable;
using System.Text.Json;
using Diary.Agent.Tools;
using Diary.ScriptBase;
using Diary.ScriptHost;

namespace Diary.AgentTests;

[TestClass]
public sealed class AgentToolTests
{
    [TestMethod]
    public async Task QueryToolNeverSerializesLocalNote()
    {
        const string localNote = "LOCAL_NOTE_MUST_NOT_LEAVE_HOST";
        var item = new ScriptWorkItem(
            7,
            "2026-10-08",
            "实现 AI 模块",
            2.5,
            3,
            localNote,
            [new ScriptWorkTag(1, "开发", 0x112233, 0, false)]);
        var tool = new QueryWorkItemsTool(new FakeWorkItemApi([item]));
        using var argumentsDocument = JsonDocument.Parse("{\"range\":\"today\",\"limit\":50}");

        var result = await tool.InvokeAsync(
            argumentsDocument.RootElement,
            new AgentToolInvocationContext(Guid.NewGuid(), Guid.NewGuid(), EmptyServiceProvider.Instance));

        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(result.Content.Contains(localNote, StringComparison.Ordinal));
        Assert.IsFalse(result.Content.Contains("note", StringComparison.OrdinalIgnoreCase));
        using var resultDocument = JsonDocument.Parse(result.Content);
        Assert.AreEqual("实现 AI 模块", resultDocument.RootElement.GetProperty("items")[0].GetProperty("Title").GetString());
        Assert.IsFalse(typeof(AgentWorkItemDto).GetProperties()
            .Any(property => property.Name.Contains("note", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task SummaryToolDoesNotReturnIndividualContentOrNotes()
    {
        const string localNote = "PRIVATE_NOTE_42";
        var items = ImmutableArray.Create(
            new ScriptWorkItem(1, "2026-10-08", "事项 A", 1.5, 1, localNote, []),
            new ScriptWorkItem(2, "2026-10-08", "事项 B", 2.0, 1, localNote, []));
        var tool = new SummarizeWorkItemsTool(new FakeWorkItemApi(items));
        using var argumentsDocument = JsonDocument.Parse("{}");

        var result = await tool.InvokeAsync(
            argumentsDocument.RootElement,
            new AgentToolInvocationContext(Guid.NewGuid(), Guid.NewGuid(), EmptyServiceProvider.Instance));

        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(result.Content.Contains(localNote, StringComparison.Ordinal));
        Assert.IsFalse(result.Content.Contains("事项 A", StringComparison.Ordinal));
        StringAssert.Contains(result.Content, "3.5");
    }

    [TestMethod]
    public void RegistryRejectsConflictsAndSnapshotIsImmutable()
    {
        var registry = new AgentToolRegistry();
        Assert.IsTrue(registry.TryRegister(new EchoTool("one", "echo_tool", "one")));
        var firstSnapshot = registry.CreateSnapshot();
        Assert.IsFalse(registry.TryRegister(new EchoTool("two", "echo_tool", "two")));
        Assert.IsTrue(registry.TryRegister(new EchoTool("three", "another_tool", "three")));
        var secondSnapshot = registry.CreateSnapshot();

        Assert.AreEqual(1, firstSnapshot.Descriptors.Count);
        Assert.AreEqual(2, secondSnapshot.Descriptors.Count);
        Assert.AreEqual(1, registry.RegistrationErrors.Count);
    }

    [TestMethod]
    public async Task ExecutorEnforcesInputAndResultBudgets()
    {
        var registry = new AgentToolRegistry();
        registry.TryRegister(new EchoTool("echo", "echo_tool", new string('中', 100)));
        var snapshot = registry.CreateSnapshot();
        var executor = new AgentToolExecutor(maxInputBytes: 16, maxResultBytes: 32);
        using var smallArguments = JsonDocument.Parse("{}");
        var context = new AgentToolInvocationContext(Guid.NewGuid(), Guid.NewGuid(), EmptyServiceProvider.Instance);

        var truncated = await executor.InvokeAsync(snapshot, "echo_tool", smallArguments.RootElement, context);
        Assert.IsTrue(truncated.Succeeded);
        Assert.IsTrue(truncated.IsTruncated);
        Assert.IsTrue(truncated.Content.EndsWith("[结果已截断]", StringComparison.Ordinal));

        using var largeArguments = JsonDocument.Parse("{\"value\":\"01234567890123456789\"}");
        var rejected = await executor.InvokeAsync(snapshot, "echo_tool", largeArguments.RootElement, context);
        Assert.AreEqual("tool_input_too_large", rejected.ErrorCode);
    }

    [TestMethod]
    public async Task QueryToolRejectsMoreThanOneHundredItems()
    {
        var tool = new QueryWorkItemsTool(new FakeWorkItemApi([]));
        using var arguments = JsonDocument.Parse("{\"limit\":101}");
        var result = await tool.InvokeAsync(
            arguments.RootElement,
            new AgentToolInvocationContext(Guid.NewGuid(), Guid.NewGuid(), EmptyServiceProvider.Instance));
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("invalid_arguments", result.ErrorCode);
        StringAssert.Contains(result.Content, "100");
    }

    private sealed class FakeWorkItemApi(ImmutableArray<ScriptWorkItem> items) : IWorkItemQueryScriptApi
    {
        public ValueTask<ScriptWorkItemQueryResult> QueryAsync(
            ScriptWorkItemQuery query,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ScriptWorkItemQueryResult.Success(items, query));
    }

    private sealed class EchoTool(string id, string modelName, string value) : IAgentTool
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();

        public AgentToolDescriptor Descriptor { get; } = new(
            id,
            modelName,
            id,
            id,
            Schema,
            AgentToolOrigin.BuiltIn,
            AgentToolRisk.ReadOnly,
            "tests");

        public ValueTask<AgentToolResult> InvokeAsync(
            JsonElement arguments,
            AgentToolInvocationContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(AgentToolResult.Success(value));
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
