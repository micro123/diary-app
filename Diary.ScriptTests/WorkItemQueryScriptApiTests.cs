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
        var primary = db.CreateWorkTag("primary", true, 10);
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
        Assert.AreEqual(0, result.Items.Single(item => item.Id == second.Id).Tags.Length);
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
