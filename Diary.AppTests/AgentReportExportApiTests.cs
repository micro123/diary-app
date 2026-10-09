using System.Collections.Immutable;
using Diary.App.Services;
using Diary.Export.Csv;
using Diary.ScriptBase;
using Diary.ScriptHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.AppTests;

[TestClass]
public sealed class AgentReportExportApiTests
{
    [TestMethod]
    public async Task WeeklyReportExportsCsvWithoutLocalNotesAndIsIdempotent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DiaryApp-agent-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string localNote = "LOCAL_NOTE_MUST_NOT_BE_EXPORTED";
            var queryApi = new FixedWorkItemQueryApi([
                new ScriptWorkItem(1, "2026-10-05", "周一事项", 1.5, 1, localNote, []),
                new ScriptWorkItem(2, "2026-10-09", "周五事项", 2.0, 2, localNote, []),
            ]);
            var service = CreateCsvService(directory);
            var api = new AgentReportExportApi(queryApi, service, directory);
            var command = new AgentReportExportCommand(
                AgentReportPeriod.Week,
                "2026-10-09",
                "csv",
                "weekly.csv",
                "weekly-idempotency");

            var preview = await api.PreviewAsync(command);
            Assert.IsTrue(preview.Succeeded, preview.ErrorMessage);
            var first = await api.ExportAsync(preview.Command!);
            var duplicate = await api.ExportAsync(preview.Command!);

            Assert.AreEqual("2026-10-05", preview.StartDate);
            Assert.AreEqual("2026-10-11", preview.EndDate);
            Assert.AreEqual(2, preview.ItemCount);
            Assert.AreEqual(3.5, preview.TotalHours);
            Assert.IsTrue(first.Succeeded, first.ErrorMessage);
            Assert.IsFalse(first.Duplicate);
            Assert.IsTrue(duplicate.Duplicate);
            var content = await File.ReadAllTextAsync(Path.Combine(directory, first.FileName!));
            StringAssert.Contains(content, "周一事项");
            StringAssert.Contains(content, "周五事项");
            Assert.IsFalse(content.Contains(localNote, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ChangedReportDataInvalidatesPreview()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DiaryApp-agent-report-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var queryApi = new FixedWorkItemQueryApi([
                new ScriptWorkItem(1, "2026-10-09", "原事项", 1, 1, null, []),
            ]);
            var api = new AgentReportExportApi(queryApi, CreateCsvService(directory), directory);
            var preview = await api.PreviewAsync(new AgentReportExportCommand(
                AgentReportPeriod.Day,
                "2026-10-09",
                "csv",
                "daily.csv",
                "daily-stale"));
            Assert.IsTrue(preview.Succeeded, preview.ErrorMessage);
            queryApi.Items = [new ScriptWorkItem(1, "2026-10-09", "已变化", 1, 1, null, [])];

            var result = await api.ExportAsync(preview.Command!);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("preview_stale", result.ErrorCode);
            Assert.IsFalse(File.Exists(Path.Combine(directory, "daily.csv")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ScriptExportService CreateCsvService(string directory)
    {
        var plugin = new CsvExportPlugin();
        var catalog = new ExportTemplateCatalog(
            NullLogger<ExportTemplateCatalog>.Instance,
            plugin.GetTemplateHandlers(),
            Path.Combine(directory, "templates"));
        return new ScriptExportService(
            NullLogger<ScriptExportService>.Instance,
            catalog,
            plugin.GetExportHandlers());
    }

    private sealed class FixedWorkItemQueryApi(ImmutableArray<ScriptWorkItem> items) : IWorkItemQueryScriptApi
    {
        public ImmutableArray<ScriptWorkItem> Items { get; set; } = items;

        public ValueTask<ScriptWorkItemQueryResult> QueryAsync(
            ScriptWorkItemQuery query,
            CancellationToken cancellationToken = default)
        {
            var page = Items
                .Where(item => (query.StartDate is null || string.CompareOrdinal(item.Date, query.StartDate) >= 0)
                               && (query.EndDate is null || string.CompareOrdinal(item.Date, query.EndDate) <= 0))
                .Skip(query.Offset)
                .Take(query.Limit ?? WorkItemQueryScriptApi.DefaultLimit)
                .ToImmutableArray();
            return ValueTask.FromResult(ScriptWorkItemQueryResult.Success(page, query));
        }
    }
}
