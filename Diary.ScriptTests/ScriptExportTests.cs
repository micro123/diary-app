using System.Text.Json;
using Diary.Script.Runtime;
using Diary.ScriptHost;
using Diary.ScriptBase;

namespace Diary.ScriptTests;

[TestClass]
public sealed class ScriptExportTests
{
    [TestMethod]
    public void ExportJson_UsesLowerSnakeCaseAndRoundTripsTableContent()
    {
        var request = new ExportRequest
        {
            FormatId = "xlsx",
            DirectorySelectionId = "directory",
            FileName = "report.xlsx",
            Content = new ExportTableContent
            {
                Style = ExportTableStyle.Report,
                Columns = [new ExportColumn("时长", ExportColumnType.Duration)],
                Rows = [["01:30:00"]],
            },
        };

        var json = JsonSerializer.Serialize(request, ExportJson.Options);
        StringAssert.Contains(json, "\"kind\":\"table\"");
        StringAssert.Contains(json, "\"type\":\"duration\"");
        StringAssert.Contains(json, "\"style\":\"report\"");

        var roundTrip = JsonSerializer.Deserialize<ExportRequest>(json, ExportJson.Options);
        Assert.IsNotNull(roundTrip);
        Assert.AreEqual(ExportContentKind.Table, roundTrip!.Content.Kind);
        Assert.AreEqual(ExportColumnType.Duration, ((ExportTableContent)roundTrip.Content).Columns[0].Type);
        Assert.AreEqual("01:30:00", ((ExportTableContent)roundTrip.Content).Rows[0][0]);
    }

    [TestMethod]
    public void ExportRequestValidator_RejectsUnsafeFileNameAndTimeAggregation()
    {
        var descriptor = new ExportFormatDescriptor(
            "xlsx", "Excel", ".xlsx", [".xlsx"],
            [new ExportContentCapabilities(ExportContentKind.Table, [ExportFeature.TypedValues])]);
        var request = new ExportRequest
        {
            FormatId = "xlsx",
            DirectorySelectionId = "directory",
            FileName = "../report.xlsx",
            Content = new ExportTableContent
            {
                Columns = [new ExportColumn("时间", ExportColumnType.Time)],
                Rows = [["09:00:00"]],
                Aggregates = [new ExportAggregateColumn("时间")],
            },
        };

        var error = ExportRequestValidator.Validate(request, descriptor);
        Assert.IsNotNull(error);
        Assert.AreEqual("EXPORT_INVALID_REQUEST", error!.Code);
    }

    [TestMethod]
    public void CsvTextSafety_ProtectsFormulaPrefixesAndEscapesFields()
    {
        Assert.AreEqual("'=1+1", CsvTextSafety.ProtectFormulaText("=1+1"));
        Assert.AreEqual("'@cmd", CsvTextSafety.ProtectFormulaText("@cmd"));
        Assert.AreEqual("\"'=-1,\"\"x\"\"\"", CsvTextSafety.Escape("=-1,\"x\""));
        Assert.AreEqual("普通文本", CsvTextSafety.Escape("普通文本"));
    }

    [TestMethod]
    public void ExportRequestValidator_RejectsOverlappingMerges()
    {
        var descriptor = new ExportFormatDescriptor(
            "xlsx", "Excel", ".xlsx", [".xlsx"],
            [new ExportContentCapabilities(ExportContentKind.Table, [ExportFeature.MergeCells])]);
        var request = new ExportRequest
        {
            FormatId = "xlsx",
            DirectorySelectionId = "directory",
            FileName = "report.xlsx",
            Content = new ExportTableContent
            {
                Columns = [new ExportColumn("A"), new ExportColumn("B")],
                Rows = [["1", "2"], ["3", "4"]],
                Merges = [new TableCellMerge(1, 1, 1, 2), new TableCellMerge(1, 2, 1, 1)],
            },
        };

        var error = ExportRequestValidator.Validate(request, descriptor);
        Assert.IsNotNull(error);
        StringAssert.Contains(error!.Message, "重叠");
    }

    [TestMethod]
    public async Task Dispatcher_RejectsInteractiveHostCallOutsideManualScope()
    {
        var dispatcher = new WorkItemQueryWorkerDispatcher(
            () => new StubQueryApi(),
            fileInteractionApiFactory: _ => new FakeFileInteractionApi());

        var result = await dispatcher.DispatchAsync(
            new ScriptHostCallContext(
                "execution",
                "worker",
                "script",
                ScriptEntryKind.Automation,
                ScriptExecutionSource.Automation),
            new("ui.directory.pick", JsonSerializer.SerializeToElement(new DirectoryPickerOptions(), ExportJson.Options)));

        Assert.IsFalse(result.Success);
        Assert.AreEqual("HOSTCALL_SCOPE_NOT_SUPPORTED", result.Error!.Code);
    }

    private sealed class StubQueryApi : IWorkItemQueryScriptApi
    {
        public ValueTask<ScriptWorkItemQueryResult> QueryAsync(
            ScriptWorkItemQuery query,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ScriptWorkItemQueryResult.Success([], query));
    }

    private sealed class FakeFileInteractionApi : IFileInteractionApi
    {
        public ValueTask<OptionDialogResult> SelectOptionAsync(OptionDialogRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new OptionDialogResult(OptionDialogStatus.Cancelled));

        public ValueTask<DirectorySelection?> PickDirectoryAsync(DirectoryPickerOptions options, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<DirectorySelection?>(null);

        public ValueTask<OpenExportedFileResult> AskToOpenExportedFileAsync(string fileId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new OpenExportedFileResult(OpenExportedFileStatus.UserDeclined));
    }
}
