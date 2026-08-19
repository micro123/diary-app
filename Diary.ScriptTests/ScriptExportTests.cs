using System.Text.Json;
using System.IO.Compression;
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
        Assert.AreEqual(ExportContentKind.Table, roundTrip!.Content!.Kind);
        Assert.AreEqual(ExportColumnType.Duration, ((ExportTableContent)roundTrip.Content).Columns[0].Type);
        Assert.AreEqual("01:30:00", ((ExportTableContent)roundTrip.Content).Rows[0][0]);
    }


    [TestMethod]
    public void ExportJson_RoundTripsDocumentContent()
    {
        var request = new ExportRequest
        {
            FormatId = "docx",
            DirectorySelectionId = "directory",
            FileName = "report.docx",
            Content = new ExportDocumentContent
            {
                Title = "报告",
                Blocks =
                [
                    new ExportHeadingBlock("摘要", 2),
                    new ExportParagraphBlock("正文"),
                    new ExportTableBlock(new ExportTableContent
                    {
                        Columns = [new ExportColumn("时长", ExportColumnType.Duration)],
                        Rows = [["01:30:00"]],
                    }),
                ],
            },
        };

        var json = JsonSerializer.Serialize(request, ExportJson.Options);
        StringAssert.Contains(json, "\"kind\":\"document\"");
        StringAssert.Contains(json, "\"kind\":\"heading\"");
        var roundTrip = JsonSerializer.Deserialize<ExportRequest>(json, ExportJson.Options);
        var document = roundTrip!.Content as ExportDocumentContent;
        Assert.IsNotNull(document);
        Assert.AreEqual(3, document.Blocks.Count);
        Assert.IsInstanceOfType<ExportTableBlock>(document.Blocks[2]);
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
    public void OpenXmlTemplateSafety_RejectsExternalRelationshipsAndMacros()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var relationship = archive.CreateEntry("word/_rels/document.xml.rels");
            using (var writer = new StreamWriter(relationship.Open()))
            {
                writer.Write(
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"test\" Target=\"https://example.test/data\" TargetMode=\"External\"/>" +
                    "</Relationships>");
            }
            archive.CreateEntry("word/vbaProject.bin");
        }
        stream.Position = 0;

        var diagnostics = OpenXmlTemplateSafety.ValidatePackage(stream);

        Assert.IsTrue(diagnostics.Count >= 2);
        Assert.IsTrue(diagnostics.Any(item => item.Message.Contains("外部关系", StringComparison.Ordinal)));
        Assert.IsTrue(diagnostics.Any(item => item.Message.Contains("宏", StringComparison.Ordinal)));
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
