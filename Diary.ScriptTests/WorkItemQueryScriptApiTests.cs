using Diary.ScriptBase;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.Db.SQLite;
using Diary.ScriptHost;

namespace Diary.ScriptTests;

[TestClass]
public sealed class WorkItemQueryScriptApiTests
{
    [TestMethod]
    public async Task QueryAsync_NormalizesDefaultsAndReusableInput()
    {
        using var db = TestDatabase.Create();
        var api = CreateApi(() => db);

        var result = await api.QueryAsync(new ScriptWorkItemQuery
        {
            StartDate = " 2026-08-01 ",
            Text = " planning ",
            TagFilter = ScriptWorkItemTagFilter.Ignore,
            TagIds = [1, 1],
        });

        Assert.IsTrue(result.Succeeded);
        Assert.IsNotNull(result.NormalizedQuery);
        Assert.AreEqual("2026-08-01", result.NormalizedQuery.StartDate);
        Assert.AreEqual("planning", result.NormalizedQuery.Text);
        Assert.AreEqual(WorkItemQueryScriptApi.DefaultLimit, result.NormalizedQuery.Limit);
        Assert.AreEqual(0, result.NormalizedQuery.TagIds.Length);
    }

    [TestMethod]
    public async Task QueryAsync_UsesProviderWithoutPermissionGate()
    {
        var providerCalled = false;
        var api = new WorkItemQueryScriptApi(
            () =>
            {
                providerCalled = true;
                return null;
            });

        var result = await api.QueryAsync(new ScriptWorkItemQuery());

        AssertError(result, ScriptQueryErrorCode.DatabaseUnavailable);
        Assert.IsTrue(providerCalled);
    }

    [TestMethod]
    public async Task QueryAsync_ReportsDatabaseUnavailableAndProviderFailure()
    {
        var unavailable = await CreateApi(() => null).QueryAsync(new ScriptWorkItemQuery());
        var failed = await CreateApi(() => throw new InvalidOperationException("secret connection string"))
            .QueryAsync(new ScriptWorkItemQuery());

        AssertError(unavailable, ScriptQueryErrorCode.DatabaseUnavailable);
        AssertError(failed, ScriptQueryErrorCode.ProviderFailure);
        Assert.IsFalse(failed.Error!.Message.Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task QueryAsync_RejectsHostLimitsAndInvalidCoreInput()
    {
        ScriptWorkItemQuery[] invalid =
        [
            new() { Limit = WorkItemQueryScriptApi.MaxLimit + 1 },
            new() { Offset = WorkItemQueryScriptApi.MaxOffset + 1 },
            new() { TagIds = [.. Enumerable.Range(1, WorkItemQueryScriptApi.MaxTagCount + 1)] },
            new() { StartDate = "2026-8-01" },
            new() { TagFilter = ScriptWorkItemTagFilter.Any },
            new() { Priority = 10 },
        ];
        var providerCalled = false;
        var api = CreateApi(() =>
        {
            providerCalled = true;
            return null;
        });

        foreach (var query in invalid)
            AssertError(await api.QueryAsync(query), ScriptQueryErrorCode.InvalidInput);
        Assert.IsFalse(providerCalled);
    }

    [TestMethod]
    public async Task QueryAsync_ExposesCanonicalApiErrorCode()
    {
        var result = await CreateApi(() => null).QueryAsync(new ScriptWorkItemQuery
        {
            Limit = WorkItemQueryScriptApi.MaxLimit + 1,
        });

        AssertError(result, ScriptQueryErrorCode.InvalidInput);
        Assert.AreEqual(ScriptApiErrorCodes.InvalidArgument, result.ApiError!.Code);
        Assert.AreEqual(ScriptErrorCategory.Validation, result.ApiError.Category);
    }

    [TestMethod]
    public async Task QueryAsync_ReportsCancellationBeforeProviderAccess()
    {
        var providerCalled = false;
        var api = CreateApi(() =>
        {
            providerCalled = true;
            return null;
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await api.QueryAsync(new ScriptWorkItemQuery(), cancellation.Token);

        AssertError(result, ScriptQueryErrorCode.Cancelled);
        Assert.IsFalse(providerCalled);
    }

    [TestMethod]
    public async Task QueryAsync_SqliteResultEqualsDirectQueryAndIncludesCoreNotesAndTags()
    {
        using var db = TestDatabase.Create();
        var first = db.CreateWorkItem("2026-08-01", "planning alpha");
        first.Time = 1.5;
        first.Priority = WorkPriorities.P2;
        Assert.IsTrue(db.UpdateWorkItem(first));
        var second = db.CreateWorkItem("2026-08-02", "planning beta");
        db.CreateWorkItem("2026-08-03", "outside");
        var primary = db.CreateWorkTag("primary", true, 10,
            new Dictionary<string, string> { ["projectNumber"] = "PRJ-001" });
        var secondary = db.CreateWorkTag("secondary", false, 20);
        Assert.IsTrue(db.WorkItemAddTag(first, primary));
        Assert.IsTrue(db.WorkItemAddTag(first, secondary));
        db.WorkUpdateNote(first, "private diary note");

        var scriptQuery = new ScriptWorkItemQuery
        {
            StartDate = "2026-08-01",
            EndDate = "2026-08-02",
            Text = "planning",
            Limit = 20,
        };
        var result = await CreateApi(() => db).QueryAsync(scriptQuery);
        var direct = db.QueryWorkItems(new WorkItemQuery
        {
            StartDate = scriptQuery.StartDate,
            EndDate = scriptQuery.EndDate,
            Text = scriptQuery.Text,
            Limit = scriptQuery.Limit,
        });

        Assert.IsTrue(result.Succeeded, result.Error?.Message);
        CollectionAssert.AreEqual(direct.Select(item => item.Id).ToArray(), result.Items.Select(item => item.Id).ToArray());
        var mapped = result.Items.Single(item => item.Id == first.Id);
        Assert.AreEqual(first.CreateDate, mapped.Date);
        Assert.AreEqual(first.Comment, mapped.Comment);
        Assert.AreEqual(first.Time, mapped.Hours);
        Assert.AreEqual((int)first.Priority, mapped.Priority);
        Assert.AreEqual("private diary note", mapped.Note);
        CollectionAssert.AreEqual(new[] { "primary", "secondary" }, mapped.Tags.Select(tag => tag.Name).ToArray());
        Assert.AreEqual("PRJ-001", mapped.Tags[0].Metadata["projectNumber"]);
        Assert.AreEqual(0, result.Items.Single(item => item.Id == second.Id).Tags.Length);
    }

    [TestMethod]
    public async Task QueryAsync_ResolvesRangeShortcutsToDateRanges()
    {
        using var db = TestDatabase.Create();
        var api = CreateApi(() => db);

        var today = await api.QueryAsync(new ScriptWorkItemQuery { Range = "today" });
        Assert.IsTrue(today.Succeeded, today.Error?.Message);
        Assert.AreEqual(DateTime.Today.ToString("yyyy-MM-dd"), today.NormalizedQuery!.StartDate);
        Assert.AreEqual(today.NormalizedQuery.StartDate, today.NormalizedQuery.EndDate);

        var yesterday = await api.QueryAsync(new ScriptWorkItemQuery { Range = "yesterday" });
        Assert.IsTrue(yesterday.Succeeded, yesterday.Error?.Message);
        Assert.AreEqual(DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd"), yesterday.NormalizedQuery!.StartDate);
        Assert.AreEqual(yesterday.NormalizedQuery.StartDate, yesterday.NormalizedQuery.EndDate);

        var weekStart = DateTime.Today.Date;
        var dayOfWeek = (int)weekStart.DayOfWeek;
        if (dayOfWeek == 0)
            dayOfWeek = 7;
        weekStart = weekStart.AddDays(-dayOfWeek + 1);
        var week = await api.QueryAsync(new ScriptWorkItemQuery { Range = "thisWeek" });
        Assert.IsTrue(week.Succeeded, week.Error?.Message);
        Assert.AreEqual(weekStart.ToString("yyyy-MM-dd"), week.NormalizedQuery!.StartDate);
        Assert.AreEqual(weekStart.AddDays(6).ToString("yyyy-MM-dd"), week.NormalizedQuery.EndDate);

        var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var month = await api.QueryAsync(new ScriptWorkItemQuery { Range = "thisMonth" });
        Assert.IsTrue(month.Succeeded, month.Error?.Message);
        Assert.AreEqual(monthStart.ToString("yyyy-MM-dd"), month.NormalizedQuery!.StartDate);
        Assert.AreEqual(monthStart.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd"), month.NormalizedQuery.EndDate);
    }

    [TestMethod]
    public async Task QueryAsync_RangeOverridesExplicitDatesAndRejectsUnknownValues()
    {
        using var db = TestDatabase.Create();
        var api = CreateApi(() => db);

        var overridden = await api.QueryAsync(new ScriptWorkItemQuery
        {
            StartDate = "2020-01-01",
            EndDate = "2020-01-31",
            Range = "today",
        });
        Assert.IsTrue(overridden.Succeeded, overridden.Error?.Message);
        Assert.AreEqual(DateTime.Today.ToString("yyyy-MM-dd"), overridden.NormalizedQuery!.StartDate);

        var invalid = await api.QueryAsync(new ScriptWorkItemQuery { Range = "tomorrow" });
        AssertError(invalid, ScriptQueryErrorCode.InvalidInput);
    }

    private static WorkItemQueryScriptApi CreateApi(Func<DbInterfaceBase?> provider) =>
        new(provider);

    private static void AssertError(ScriptWorkItemQueryResult result, ScriptQueryErrorCode code)
    {
        Assert.IsFalse(result.Succeeded);
        Assert.IsNotNull(result.Error);
        Assert.AreEqual(code, result.Error.Code);
        Assert.AreEqual(0, result.Items.Length);
    }
}

internal sealed class TestDatabaseFactory : IDbFactory
{
    private readonly Config _config = new() { FilePath = ":memory:" };

    public string Name => "SQLite";
    public bool Usable => true;
    public DbInterfaceBase Create() => new SQLiteDb(this);
    public Migration? GetMigration(uint version) => null;
    public object GetConfig() => _config;
}

internal static class TestDatabase
{
    public static SQLiteDb Create()
    {
        var database = new SQLiteDb(new TestDatabaseFactory());
        Assert.IsTrue(database.Connect());
        Assert.IsTrue(database.Initialized());
        return database;
    }
}
