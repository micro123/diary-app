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
        StringAssert.Contains(result.Content, "averageHours");
        StringAssert.Contains(result.Content, "byPriority");
    }

    [TestMethod]
    public async Task DetailToolUsesExactIdAndNeverReturnsLocalNote()
    {
        const string localNote = "PRIVATE_DETAIL_NOTE";
        var api = new FakeWorkItemApi([
            new ScriptWorkItem(42, "2026-10-09", "详情事项", 1.25, 2, localNote, []),
        ]);
        var tool = new GetWorkItemDetailTool(api);
        using var arguments = JsonDocument.Parse("{\"workItemId\":42}");

        var result = await tool.InvokeAsync(
            arguments.RootElement,
            new AgentToolInvocationContext(Guid.NewGuid(), Guid.NewGuid(), EmptyServiceProvider.Instance));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(42, api.LastQuery?.WorkItemId);
        Assert.IsFalse(result.Content.Contains(localNote, StringComparison.Ordinal));
        using var resultDocument = JsonDocument.Parse(result.Content);
        Assert.AreEqual("详情事项", resultDocument.RootElement.GetProperty("Title").GetString());
    }

    [TestMethod]
    public async Task ExportOptionsToolReturnsFormatsAndFilteredTemplates()
    {
        var api = new FakeExportApi();
        var tool = new ListExportOptionsTool(api);
        using var arguments = JsonDocument.Parse("{\"formatId\":\"csv\"}");

        var result = await tool.InvokeAsync(
            arguments.RootElement,
            new AgentToolInvocationContext(Guid.NewGuid(), Guid.NewGuid(), EmptyServiceProvider.Instance));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("csv", api.LastTemplateFormatId);
        using var resultDocument = JsonDocument.Parse(result.Content);
        Assert.AreEqual("CSV", resultDocument.RootElement.GetProperty("formats")[0].GetProperty("DisplayName").GetString());
        Assert.AreEqual("日报模板", resultDocument.RootElement.GetProperty("templates")[0].GetProperty("DisplayName").GetString());
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

    [TestMethod]
    public async Task ListExtraFieldsFiltersTagsAndDisabledDefinitions()
    {
        var api = new FakeExtraFieldApi([
            new ScriptTagExtraFieldInfo("field-1", "project.code", 1, "项目", "项目代码", "Text", "", 0, [], "", true),
            new ScriptTagExtraFieldInfo("field-2", "project.closed", 1, "项目", "已关闭", "Boolean", "", 1, [], "false", false),
            new ScriptTagExtraFieldInfo("field-3", "client.name", 2, "客户", "客户", "Text", "", 0, [], "", true),
        ]);
        var tool = new ListExtraFieldsTool(api);
        using var arguments = JsonDocument.Parse("{\"tagIds\":[1]}");

        var result = await tool.InvokeAsync(arguments.RootElement, CreateContext());

        Assert.IsTrue(result.Succeeded);
        using var document = JsonDocument.Parse(result.Content);
        Assert.AreEqual(1, document.RootElement.GetProperty("count").GetInt32());
        Assert.AreEqual("project.code", document.RootElement.GetProperty("fields")[0].GetProperty("FieldKey").GetString());
    }

    [TestMethod]
    public async Task WorkLogQualityFindsMissingDayDuplicatesAndUntaggedItems()
    {
        var items = ImmutableArray.Create(
            new ScriptWorkItem(1, "2026-10-05", "实现功能", 2, 1, null, []),
            new ScriptWorkItem(2, "2026-10-05", "实现功能", 3, 1, null, []),
            new ScriptWorkItem(3, "2026-10-07", "测试", 8, 2, null,
                [new ScriptWorkTag(9, "停用", 0, 0, true)]));
        var tool = new AnalyzeWorkLogQualityTool(new FakeWorkItemApi(items));
        using var arguments = JsonDocument.Parse("""
            {"startDate":"2026-10-05","endDate":"2026-10-07","expectedDailyHours":8}
            """);

        var result = await tool.InvokeAsync(arguments.RootElement, CreateContext());

        Assert.IsTrue(result.Succeeded);
        using var document = JsonDocument.Parse(result.Content);
        Assert.AreEqual("2026-10-06", document.RootElement.GetProperty("missingDates")[0].GetString());
        Assert.AreEqual(2, document.RootElement.GetProperty("duplicates")[0].GetProperty("workItemIds").GetArrayLength());
        Assert.AreEqual(2, document.RootElement.GetProperty("untaggedWorkItemIds").GetArrayLength());
        Assert.AreEqual(1, document.RootElement.GetProperty("disabledTagReferences").GetArrayLength());
    }

    [TestMethod]
    public async Task PeriodComparisonAndCalendarOverviewReturnLocalAggregates()
    {
        var items = ImmutableArray.Create(
            new ScriptWorkItem(1, "2026-09-28", "上周", 4, 1, null, []),
            new ScriptWorkItem(2, "2026-10-05", "本周 A", 6, 1, null, []),
            new ScriptWorkItem(3, "2026-10-05", "本周 B", 2, 2, null, []));
        var api = new FilteringWorkItemApi(items);
        var compare = new CompareWorkPeriodsTool(api);
        using var compareArguments = JsonDocument.Parse("""
            {"leftStartDate":"2026-09-28","leftEndDate":"2026-09-28","rightStartDate":"2026-10-05","rightEndDate":"2026-10-05"}
            """);

        var compareResult = await compare.InvokeAsync(compareArguments.RootElement, CreateContext());

        Assert.IsTrue(compareResult.Succeeded);
        using var comparison = JsonDocument.Parse(compareResult.Content);
        Assert.AreEqual(4, comparison.RootElement.GetProperty("delta").GetProperty("totalHours").GetDouble());

        var calendar = new GetCalendarOverviewTool(api);
        using var calendarArguments = JsonDocument.Parse("""
            {"startDate":"2026-10-05","endDate":"2026-10-06"}
            """);
        var calendarResult = await calendar.InvokeAsync(calendarArguments.RootElement, CreateContext());
        using var overview = JsonDocument.Parse(calendarResult.Content);
        Assert.AreEqual(1, overview.RootElement.GetProperty("recordedDays").GetInt32());
        Assert.AreEqual(1, overview.RootElement.GetProperty("missingDays").GetInt32());
        Assert.AreEqual(8, overview.RootElement.GetProperty("totalHours").GetDouble());
    }

    private sealed class FakeWorkItemApi(ImmutableArray<ScriptWorkItem> items) : IWorkItemQueryScriptApi
    {
        public ScriptWorkItemQuery? LastQuery { get; private set; }

        public ValueTask<ScriptWorkItemQueryResult> QueryAsync(
            ScriptWorkItemQuery query,
            CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            var page = items.Skip(query.Offset).Take(query.Limit ?? items.Length).ToImmutableArray();
            return ValueTask.FromResult(ScriptWorkItemQueryResult.Success(page, query));
        }
    }

    private sealed class FilteringWorkItemApi(ImmutableArray<ScriptWorkItem> items) : IWorkItemQueryScriptApi
    {
        public ValueTask<ScriptWorkItemQueryResult> QueryAsync(
            ScriptWorkItemQuery query,
            CancellationToken cancellationToken = default)
        {
            var filtered = items.Where(item =>
                    (query.StartDate is null || string.CompareOrdinal(item.Date, query.StartDate) >= 0)
                    && (query.EndDate is null || string.CompareOrdinal(item.Date, query.EndDate) <= 0))
                .Skip(query.Offset)
                .Take(query.Limit ?? items.Length)
                .ToImmutableArray();
            return ValueTask.FromResult(ScriptWorkItemQueryResult.Success(filtered, query));
        }
    }

    private sealed class FakeExtraFieldApi(IReadOnlyList<ScriptTagExtraFieldInfo> fields) : ITagExtraFieldScriptApi
    {
        public IReadOnlyList<ScriptTagExtraFieldInfo> List(bool includeDisabled = false) =>
            fields.Where(field => includeDisabled || field.Enabled).ToArray();
    }

    private static AgentToolInvocationContext CreateContext() =>
        new(Guid.NewGuid(), Guid.NewGuid(), EmptyServiceProvider.Instance);

    private sealed class FakeExportApi : IExportApi
    {
        public string? LastTemplateFormatId { get; private set; }

        public ValueTask<ExportResult> ExportAsync(
            ExportRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<ExportFormatDescriptor>> ListFormatsAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<ExportFormatDescriptor>>([
                new ExportFormatDescriptor(
                    "csv",
                    "CSV",
                    ".csv",
                    [".csv"],
                    [new ExportContentCapabilities(ExportContentKind.Table, [ExportFeature.UnicodeText])]),
            ]);

        public ValueTask<IReadOnlyList<ExportTemplateDescriptor>> ListTemplatesAsync(
            string? formatId = null,
            CancellationToken cancellationToken = default)
        {
            LastTemplateFormatId = formatId;
            return ValueTask.FromResult<IReadOnlyList<ExportTemplateDescriptor>>([
                new ExportTemplateDescriptor(
                    "daily",
                    "1",
                    "tests",
                    "csv",
                    ".csv",
                    "日报模板",
                    null,
                    [],
                    []),
            ]);
        }
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
