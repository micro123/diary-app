using ClosedXML.Excel;
using Diary.App.Services;
using Diary.Export.Csv;
using Diary.Export.Docx;
using Diary.Export.Xlsx;
using Diary.ScriptHost;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Diary.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.AppTests;

[TestClass]
public sealed class ScriptExportServiceTests
{
    [TestMethod]
    public async Task ExportAsync_WritesTypedXlsxAndRegistersFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DiaryApp-export-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var (service, context) = CreateXlsxService(directory);
            service.RegisterDirectory("directory", directory, context);
            var result = await service.ExportAsync(new ExportRequest
            {
                FormatId = "xlsx",
                DirectorySelectionId = "directory",
                FileName = "report",
                FormatOptions = new ExportFormatOptions(
                    "xlsx",
                    new Dictionary<string, object?> { ["sheet_name"] = "加班明细" }),
                Content = new ExportTableContent
                {
                    Title = "测试报告",
                    Columns =
                    [
                        new ExportColumn("日期", ExportColumnType.Date),
                        new ExportColumn("时长", ExportColumnType.Duration),
                        new ExportColumn("数值", ExportColumnType.Decimal),
                    ],
                    Rows =
                    [
                        ["2026-08-19", "25:30:00", 1.5m],
                        ["2026-08-20", "01:30:00", 2.5m],
                    ],
                    Aggregates =
                    [
                        new ExportAggregateColumn("时长", Label: "总计"),
                        new ExportAggregateColumn("数值"),
                    ],
                    Style = ExportTableStyle.Report,
                },
            }, context);

            Assert.IsTrue(result.Succeeded, result.Error?.Message);
            Assert.IsNotNull(result.FileId);
            Assert.AreEqual(2, result.ItemCount);
            Assert.IsTrue(File.Exists(Path.Combine(directory, result.FileName!)));

            using var workbook = new XLWorkbook(Path.Combine(directory, result.FileName!));
            var sheet = workbook.Worksheet("加班明细");
            Assert.AreEqual("测试报告", sheet.Cell("A1").GetString());
            Assert.AreEqual("日期", sheet.Cell("A2").GetString());
            Assert.AreEqual("2026-08-19", sheet.Cell("A3").GetDateTime().ToString("yyyy-MM-dd"));
            Assert.AreEqual("SUM(B3:B4)", sheet.Cell("B5").FormulaA1);
            Assert.AreEqual("SUM(C3:C4)", sheet.Cell("C5").FormulaA1);
            Assert.AreEqual("总计", sheet.Cell("A5").GetString());
            Assert.AreEqual(XLAlignmentHorizontalValues.Center, sheet.Cell("A2").Style.Alignment.Horizontal);
            Assert.AreEqual("[h]:mm:ss", sheet.Cell("B3").Style.NumberFormat.Format);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExportAsync_RejectsCamelCaseXlsxSheetNameOption()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DiaryApp-export-option-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var (service, context) = CreateXlsxService(directory);
            service.RegisterDirectory("directory", directory, context);
            var result = await service.ExportAsync(new ExportRequest
            {
                FormatId = "xlsx",
                DirectorySelectionId = "directory",
                FileName = "report",
                FormatOptions = new ExportFormatOptions(
                    "xlsx",
                    new Dictionary<string, object?> { ["sheetName"] = "旧键名" }),
                Content = new ExportTableContent
                {
                    Columns = [new ExportColumn("内容")],
                    Rows = [["测试"]],
                },
            }, context);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("EXPORT_FORMAT_OPTION_UNKNOWN", result.Error?.Code);
            Assert.AreEqual(ScriptErrorCategory.Validation, result.Error?.Category);
            Assert.IsFalse(File.Exists(Path.Combine(directory, "report.xlsx")));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExportAsync_AppliesCompactXlsxStyle()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DiaryApp-export-compact-style-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var (service, context) = CreateXlsxService(directory);
            service.RegisterDirectory("directory", directory, context);
            var result = await service.ExportAsync(new ExportRequest
            {
                FormatId = "xlsx",
                DirectorySelectionId = "directory",
                FileName = "compact",
                Content = new ExportTableContent
                {
                    Columns = [new ExportColumn("内容")],
                    Rows = [["测试"]],
                    Style = ExportTableStyle.Compact,
                },
            }, context);

            Assert.IsTrue(result.Succeeded, result.Error?.Message);
            using var workbook = new XLWorkbook(Path.Combine(directory, result.FileName!));
            var header = workbook.Worksheet("明细").Cell("A1");
            Assert.AreEqual(10d, header.Style.Font.FontSize);
            Assert.AreEqual(XLBorderStyleValues.Thin, header.Style.Border.BottomBorder);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static (ScriptExportService Service, ScriptHostCallContext Context) CreateXlsxService(string directory)
    {
        var xlsxPlugin = new XlsxExportPlugin();
        var catalog = new ExportTemplateCatalog(
            NullLogger<ExportTemplateCatalog>.Instance,
            xlsxPlugin.GetTemplateHandlers(),
            Path.Combine(directory, "templates"));
        var service = new ScriptExportService(
            NullLogger<ScriptExportService>.Instance,
            catalog,
            xlsxPlugin.GetExportHandlers());
        var context = new ScriptHostCallContext(
            "execution",
            "worker",
            "script",
            ScriptEntryKind.Application,
            ScriptExecutionSource.Manual);
        return (service, context);
    }
}

[TestClass]
public sealed class ExportTemplateTests
{
    [TestMethod]
    public void BindingValidator_UsesDefaultValueWhenBindingIsOmitted()
    {
        var descriptor = new ExportTemplateDescriptor(
            "xlsx.work_report",
            "1.0.0",
            "xlsx",
            "xlsx",
            ".xlsx",
            "工作报表",
            null,
            [new ExportBindingDescriptor(
                "period",
                ExportBindingKind.Scalar,
                ExportScalarType.Text,
                Required: true,
                HasDefaultValue: true,
                DefaultValue: "current_month")],
            []);
        var source = new ExportTemplateSource
        {
            TemplateId = descriptor.TemplateId,
            TemplateVersion = descriptor.TemplateVersion,
        };

        var valid = ExportTemplateBindingValidator.TryApplyDefaults(
            source,
            descriptor,
            out var normalized,
            out var diagnostics);

        Assert.IsTrue(valid);
        Assert.AreEqual("current_month", normalized.Values["period"]);
        Assert.AreEqual(0, diagnostics.Count);
    }

    [TestMethod]
    public void BindingValidator_RejectsRequiredBindingWithoutDefault()
    {
        var descriptor = new ExportTemplateDescriptor(
            "xlsx.work_report",
            "1.0.0",
            "xlsx",
            "xlsx",
            ".xlsx",
            "工作报表",
            null,
            [new ExportBindingDescriptor("period", ExportBindingKind.Scalar, ExportScalarType.Text)],
            []);

        var valid = ExportTemplateBindingValidator.TryApplyDefaults(
            new ExportTemplateSource
            {
                TemplateId = descriptor.TemplateId,
                TemplateVersion = descriptor.TemplateVersion,
            },
            descriptor,
            out _,
            out var diagnostics);

        Assert.IsFalse(valid);
        Assert.AreEqual("EXPORT_TEMPLATE_REQUIRED_BINDING_MISSING", diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task Catalog_ImportsTemplateAndBuildsPluginQualifiedId()
    {
        var root = Path.Combine(Path.GetTempPath(), "DiaryApp-template-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "work.demo");
        await File.WriteAllTextAsync(sourcePath, "template");
        var storagePath = Path.Combine(root, "storage");
        try
        {
            var catalog = new ExportTemplateCatalog(
                NullLogger<ExportTemplateCatalog>.Instance,
                [new FakeTemplateHandler()],
                storagePath);
            var result = await catalog.ImportAsync(sourcePath);

            Assert.IsTrue(result.Succeeded, result.ErrorMessage);
            Assert.AreEqual("xlsx.work_report", result.Descriptor!.TemplateId);
            Assert.AreEqual(1, catalog.List("xlsx").Count);
            Assert.IsTrue(catalog.TryResolve("xlsx.work_report", "1.0.0", out var registration));
            Assert.IsTrue(File.Exists(registration.TemplateFilePath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task XlsxTemplate_RejectsDangerousExternalFormula()
    {
        var root = Path.Combine(Path.GetTempPath(), "DiaryApp-xlsx-template-safety-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "unsafe.xlsx");
        try
        {
            using (var workbook = new XLWorkbook())
            {
                var metadata = workbook.Worksheets.Add("__diary_template");
                metadata.Cell("A1").Value = "diary.export.template";
                metadata.Cell("A2").Value = "unsafe_report";
                metadata.Cell("A3").Value = "1.0.0";
                var worksheet = workbook.Worksheets.Add("明细");
                worksheet.Cell("A1").FormulaA1 = "WEBSERVICE(\"https://example.test/data\")";
                workbook.SaveAs(sourcePath);
            }

            var plugin = new XlsxExportPlugin();
            var catalog = new ExportTemplateCatalog(
                NullLogger<ExportTemplateCatalog>.Instance,
                plugin.GetTemplateHandlers(),
                Path.Combine(root, "storage"));

            var result = await catalog.ImportAsync(sourcePath);

            Assert.IsFalse(result.Succeeded);
            Assert.IsTrue(result.Diagnostics?.Any(item => item.Code == "EXPORT_TEMPLATE_UNSUPPORTED"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private sealed class FakeTemplateHandler : IExportTemplateHandler
    {
        public string PluginId => "xlsx";
        public string FormatId => "xlsx";
        public IReadOnlyList<string> SupportedTemplateExtensions => [".demo"];

        public ValueTask<ExportTemplateValidationResult> ValidateAsync(
            Stream templateStream,
            ExportTemplateValidationContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExportTemplateValidationResult(
                true,
                "work_report",
                "工作报表",
                null,
                "1.0.0",
                [new ExportBindingDescriptor(
                    "period",
                    ExportBindingKind.Scalar,
                    ExportScalarType.Text,
                    Required: true,
                    HasDefaultValue: true,
                    DefaultValue: "current_month")],
                [],
                []));

        public async ValueTask<ExportRenderResult> RenderAsync(
            ExportRequest request,
            ExportExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            await File.WriteAllTextAsync(context.OutputPath, request.Template!.Values["period"]!.ToString(), cancellationToken);
            return new ExportRenderResult(1);
        }
    }
}

[TestClass]
public sealed class ExportPluginIntegrationTests
{
    [TestMethod]
    public void TypeLoader_DiscoversExportPluginsFromApplicationOutput()
    {
        var outputDirectory = Path.GetDirectoryName(typeof(CsvExportPlugin).Assembly.Location)!;

        var plugins = TypeLoader.GetImplementations<IExportPlugin>(
                outputDirectory,
                "Diary.Export.*.dll")
            .ToArray();

        CollectionAssert.AreEquivalent(
            new[] { "csv", "docx", "xlsx" },
            plugins.Select(plugin => plugin.Manifest.Id).ToArray());
    }

    [TestMethod]
    public async Task ListFormatsAndCsvExport_UseDiscoveredPluginHandlers()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var service = CreateService(directory);
            var formats = await service.ListFormatsAsync();
            CollectionAssert.AreEquivalent(
                new[] { "csv", "docx", "xlsx" },
                formats.Select(format => format.FormatId).ToArray());
            var xlsx = formats.Single(format => format.FormatId == "xlsx");
            var sheetNameOption = xlsx.FormatOptions?.Single();
            Assert.IsNotNull(sheetNameOption);
            Assert.AreEqual("sheet_name", sheetNameOption.Key);
            Assert.AreEqual("明细", sheetNameOption.DefaultValue);
            Assert.IsFalse(xlsx.ContentCapabilities.Single().Features.Contains(ExportFeature.BackgroundColor));
            Assert.IsTrue(xlsx.ContentCapabilities.Single().Features.Contains(ExportFeature.NumberFormat));
            Assert.AreEqual(0, formats.Single(format => format.FormatId == "csv").FormatOptions?.Count);

            var context = CreateContext();
            service.RegisterDirectory("directory", directory, context);
            var result = await service.ExportAsync(new ExportRequest
            {
                FormatId = "csv",
                DirectorySelectionId = "directory",
                FileName = "report.csv",
                Content = new ExportTableContent
                {
                    Columns =
                    [
                        new ExportColumn("内容"),
                        new ExportColumn("时长", ExportColumnType.Duration),
                    ],
                    Rows = [["=1+1", "25:30:00"]],
                    Aggregates = [new ExportAggregateColumn("时长", Label: "总时长")],
                },
            }, context);

            Assert.IsTrue(result.Succeeded, result.Error?.Message);
            var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, result.FileName!));
            CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            StringAssert.Contains(text, "'=1+1");
            StringAssert.Contains(text, "1.01:30:00");
            StringAssert.Contains(text, "总时长");
            StringAssert.Contains(text, "\r\n");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task DocxExport_WritesHeadingParagraphAndTable()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var service = CreateService(directory);
            var context = CreateContext();
            service.RegisterDirectory("directory", directory, context);
            var result = await service.ExportAsync(new ExportRequest
            {
                FormatId = "docx",
                DirectorySelectionId = "directory",
                FileName = "report.docx",
                Content = new ExportDocumentContent
                {
                    Title = "工作报告",
                    Blocks =
                    [
                        new ExportHeadingBlock("摘要", 1),
                        new ExportParagraphBlock("本月工作完成。"),
                        new ExportTableBlock(new ExportTableContent
                        {
                            Columns = [new ExportColumn("项目"), new ExportColumn("耗时", ExportColumnType.Decimal)],
                            Rows = [["Diary", 2.5m]],
                            Aggregates = [new ExportAggregateColumn("耗时", Label: "总耗时")],
                            Style = ExportTableStyle.Report,
                        }),
                    ],
                },
            }, context);

            Assert.IsTrue(result.Succeeded, result.Error?.Message);
            using var archive = System.IO.Compression.ZipFile.OpenRead(Path.Combine(directory, result.FileName!));
            var documentEntry = archive.GetEntry("word/document.xml");
            Assert.IsNotNull(documentEntry);
            using var reader = new StreamReader(documentEntry.Open());
            var xml = await reader.ReadToEndAsync();
            StringAssert.Contains(xml, "工作报告");
            StringAssert.Contains(xml, "本月工作完成。");
            StringAssert.Contains(xml, "Diary");
            StringAssert.Contains(xml, "总耗时");
            StringAssert.Contains(xml, "w:shd");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task CsvTemplate_CanBeImportedAndRenderedWithDefaultValue()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var templatePath = Path.Combine(directory, "report.csv");
            await File.WriteAllTextAsync(templatePath,
                "# diary.export.template\n" +
                "# template_name: work_report\n" +
                "# version: 1.0.0\n" +
                "# binding: period|scalar|text|true|current_month\n" +
                "周期,{{period}}\n",
                new System.Text.UTF8Encoding(true));
            var service = CreateService(directory, out var catalog);
            var imported = await catalog.ImportAsync(templatePath);
            Assert.IsTrue(imported.Succeeded, imported.ErrorMessage);

            var context = CreateContext();
            service.RegisterDirectory("directory", directory, context);
            var result = await service.ExportAsync(new ExportRequest
            {
                FormatId = "csv",
                DirectorySelectionId = "directory",
                FileName = "rendered.csv",
                Template = new ExportTemplateSource
                {
                    TemplateId = "csv.work_report",
                    TemplateVersion = "1.0.0",
                },
            }, context);

            Assert.IsTrue(result.Succeeded, result.Error?.Message);
            var text = await File.ReadAllTextAsync(Path.Combine(directory, result.FileName!));
            StringAssert.Contains(text, "周期,current_month");
            Assert.IsFalse(text.Contains("diary.export.template", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task CsvTemplate_EscapesInsertedDelimitersQuotesNewlinesAndFormulaPrefixes()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var templatePath = Path.Combine(directory, "report.csv");
            await File.WriteAllTextAsync(templatePath,
                "# diary.export.template\n" +
                "# template_name: work_report\n" +
                "# version: 1.0.0\n" +
                "# binding: value|scalar|text|true\n" +
                "周期,{{value}}\n",
                new System.Text.UTF8Encoding(true));
            var service = CreateService(directory, out var catalog);
            var imported = await catalog.ImportAsync(templatePath);
            Assert.IsTrue(imported.Succeeded, imported.ErrorMessage);

            var context = CreateContext();
            service.RegisterDirectory("directory", directory, context);
            var result = await service.ExportAsync(new ExportRequest
            {
                FormatId = "csv",
                DirectorySelectionId = "directory",
                FileName = "rendered.csv",
                Template = new ExportTemplateSource
                {
                    TemplateId = "csv.work_report",
                    TemplateVersion = "1.0.0",
                    Values = new Dictionary<string, object?>
                    {
                        ["value"] = "=1+1,\"引用\"\r\n下一行",
                    },
                },
            }, context);

            Assert.IsTrue(result.Succeeded, result.Error?.Message);
            var text = await File.ReadAllTextAsync(Path.Combine(directory, result.FileName!));
            Assert.AreEqual("周期,\"'=1+1,\"\"引用\"\"\r\n下一行\"\r\n", text);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task ExportAsync_ReturnsStructuredNonRetryableValueError()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var service = CreateService(directory);
            var context = CreateContext();
            service.RegisterDirectory("directory", directory, context);
            var result = await service.ExportAsync(new ExportRequest
            {
                FormatId = "xlsx",
                DirectorySelectionId = "directory",
                FileName = "invalid.xlsx",
                Content = new ExportTableContent
                {
                    Columns = [new ExportColumn("数量", ExportColumnType.Integer)],
                    Rows = [["abc"]],
                },
            }, context);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("EXPORT_VALUE_INVALID", result.Error?.Code);
            Assert.AreEqual(ScriptErrorCategory.Validation, result.Error?.Category);
            Assert.IsFalse(result.Error?.Retryable);
            Assert.AreEqual(1, result.Error?.Details?["row"]);
            Assert.AreEqual("数量", result.Error?.Details?["column"]);
            Assert.AreEqual("integer", result.Error?.Details?["expected_type"]);
            Assert.AreEqual(false, result.Error?.Details?["value_was_null"]);
            Assert.IsFalse(File.Exists(Path.Combine(directory, "invalid.xlsx")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task TemplateExport_ReturnsStructuredBindingDiagnostics()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var templatePath = Path.Combine(directory, "required.csv");
            await File.WriteAllTextAsync(templatePath,
                "# diary.export.template\n" +
                "# template_name: required_report\n" +
                "# version: 1.0.0\n" +
                "# binding: customer_name|scalar|text|true\n" +
                "客户,{{customer_name}}\n",
                new System.Text.UTF8Encoding(true));
            var service = CreateService(directory, out var catalog);
            var imported = await catalog.ImportAsync(templatePath);
            Assert.IsTrue(imported.Succeeded, imported.ErrorMessage);

            var context = CreateContext();
            service.RegisterDirectory("directory", directory, context);
            var result = await service.ExportAsync(new ExportRequest
            {
                FormatId = "csv",
                DirectorySelectionId = "directory",
                FileName = "required-output.csv",
                Template = new ExportTemplateSource
                {
                    TemplateId = "csv.required_report",
                    TemplateVersion = "1.0.0",
                },
            }, context);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("EXPORT_TEMPLATE_BINDING_INVALID", result.Error?.Code);
            var diagnostics = result.Error?.Details?["diagnostics"] as IReadOnlyList<ExportDiagnostic>;
            Assert.IsNotNull(diagnostics);
            Assert.AreEqual("EXPORT_TEMPLATE_REQUIRED_BINDING_MISSING", diagnostics.Single().Code);
            Assert.AreEqual("customer_name", diagnostics.Single().BindingKey);
            var json = System.Text.Json.JsonSerializer.Serialize(result, ExportJson.Options);
            StringAssert.Contains(json, "\"diagnostics\"");
            StringAssert.Contains(json, "\"binding_key\":\"customer_name\"");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task XlsxTemplateTableBinding_ValidatesFieldsAndCellValues()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var templatePath = Path.Combine(directory, "table-template.xlsx");
            using (var workbook = new XLWorkbook())
            {
                var metadata = workbook.Worksheets.Add("__diary_template");
                metadata.Cell("A1").Value = "diary.export.template";
                metadata.Cell("A2").Value = "table_report";
                metadata.Cell("A3").Value = "1.0.0";
                metadata.Cell("A8").Value = "items";
                metadata.Cell("B8").Value = "table";
                metadata.Cell("D8").Value = "true";
                metadata.Cell("F8").Value = "明细!A1";
                workbook.Worksheets.Add("明细");
                workbook.SaveAs(templatePath);
            }
            var service = CreateService(directory, out var catalog);
            var imported = await catalog.ImportAsync(templatePath);
            Assert.IsTrue(imported.Succeeded, imported.ErrorMessage);

            var context = CreateContext();
            service.RegisterDirectory("directory", directory, context);
            var invalidValue = await service.ExportAsync(new ExportRequest
            {
                FormatId = "xlsx",
                DirectorySelectionId = "directory",
                FileName = "invalid-value.xlsx",
                Template = CreateTableTemplateSource(new ExportTableContent
                {
                    Columns = [new ExportColumn("数量", ExportColumnType.Integer)],
                    Rows = [["abc"]],
                }),
            }, context);
            var unsupportedStyle = await service.ExportAsync(new ExportRequest
            {
                FormatId = "xlsx",
                DirectorySelectionId = "directory",
                FileName = "unsupported-style.xlsx",
                Template = CreateTableTemplateSource(new ExportTableContent
                {
                    Columns = [new ExportColumn("内容")],
                    Rows = [["测试"]],
                    Style = ExportTableStyle.Report,
                }),
            }, context);

            Assert.AreEqual("EXPORT_VALUE_INVALID", invalidValue.Error?.Code);
            Assert.AreEqual("items", invalidValue.Error?.Details?["binding_key"]);
            Assert.AreEqual(1, invalidValue.Error?.Details?["row"]);
            Assert.AreEqual("EXPORT_UNSUPPORTED_FEATURE", unsupportedStyle.Error?.Code);
            Assert.AreEqual("items", unsupportedStyle.Error?.Details?["binding_key"]);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task ExportAsync_ValidateOnlySkipsDirectoryAndFileCreation()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var service = CreateService(directory);
            var context = new ScriptHostCallContext(
                "execution", "worker", "query-script",
                ScriptEntryKind.Query, ScriptExecutionSource.Manual, Preview: true);

            var result = await service.ExportAsync(new ExportRequest
            {
                FormatId = "csv",
                DirectorySelectionId = string.Empty,
                FileName = "preview.csv",
                ValidateOnly = true,
                Content = new ExportTableContent
                {
                    Columns = [new ExportColumn("title")],
                    Rows = [new object?[] { "Diary" }],
                },
            }, context);

            Assert.IsTrue(result.Succeeded, result.Error?.Message);
            Assert.IsTrue(result.ValidatedOnly);
            Assert.IsNull(result.FileId);
            Assert.IsNull(result.FileName);
            Assert.AreEqual(1, result.ItemCount);
            Assert.AreEqual(0, Directory.EnumerateFiles(directory, "*.csv", SearchOption.AllDirectories).Count());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static ExportTemplateSource CreateTableTemplateSource(ExportTableContent table) => new()
    {
        TemplateId = "xlsx.table_report",
        TemplateVersion = "1.0.0",
        Tables = new Dictionary<string, ExportTableContent> { ["items"] = table },
    };

    private static ScriptExportService CreateService(string directory) => CreateService(directory, out _);

    private static ScriptExportService CreateService(string directory, out ExportTemplateCatalog catalog)
    {
        IExportPlugin[] plugins = [new XlsxExportPlugin(), new CsvExportPlugin(), new DocxExportPlugin()];
        catalog = new ExportTemplateCatalog(
            NullLogger<ExportTemplateCatalog>.Instance,
            plugins.SelectMany(plugin => plugin.GetTemplateHandlers()),
            Path.Combine(directory, "templates"));
        return new ScriptExportService(
            NullLogger<ScriptExportService>.Instance,
            catalog,
            plugins.SelectMany(plugin => plugin.GetExportHandlers()));
    }

    private static ScriptHostCallContext CreateContext() => new(
        "execution",
        "worker",
        "script",
        ScriptEntryKind.Application,
        ScriptExecutionSource.Manual);

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DiaryApp-export-plugin-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}

[TestClass]
public sealed class DocxTemplateIntegrationTests
{
    [TestMethod]
    public async Task DocxTemplate_RejectsExternalFieldInstruction()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DiaryApp-docx-template-safety-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var templatePath = Path.Combine(directory, "unsafe.docx");
            using (var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(
                       templatePath,
                       DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = document.AddMainDocumentPart();
                main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                    new DocumentFormat.OpenXml.Wordprocessing.Body(
                        new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                            new DocumentFormat.OpenXml.Wordprocessing.Run(
                                new DocumentFormat.OpenXml.Wordprocessing.Text("[[diary.export.template]]"))),
                        new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                            new DocumentFormat.OpenXml.Wordprocessing.Run(
                                new DocumentFormat.OpenXml.Wordprocessing.Text("template_name: unsafe_report"))),
                        new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                            new DocumentFormat.OpenXml.Wordprocessing.Run(
                                new DocumentFormat.OpenXml.Wordprocessing.Text("version: 1.0.0"))),
                        new DocumentFormat.OpenXml.Wordprocessing.SimpleField(
                            new DocumentFormat.OpenXml.Wordprocessing.Run(
                                new DocumentFormat.OpenXml.Wordprocessing.Text("外部内容")))
                        {
                            Instruction = "INCLUDETEXT https://example.test/data",
                        }));
                main.Document.Save();
            }

            var plugin = new DocxExportPlugin();
            var catalog = new ExportTemplateCatalog(
                NullLogger<ExportTemplateCatalog>.Instance,
                plugin.GetTemplateHandlers(),
                Path.Combine(directory, "templates"));

            var imported = await catalog.ImportAsync(templatePath);

            Assert.IsFalse(imported.Succeeded);
            Assert.IsTrue(imported.Diagnostics?.Any(item => item.Code == "EXPORT_TEMPLATE_UNSUPPORTED"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task DocxTemplate_CanBeImportedAndRendered()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DiaryApp-docx-template-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var templatePath = Path.Combine(directory, "report.docx");
            using (var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(
                       templatePath,
                       DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = document.AddMainDocumentPart();
                main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                    new DocumentFormat.OpenXml.Wordprocessing.Body(
                        new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                            new DocumentFormat.OpenXml.Wordprocessing.Run(
                                new DocumentFormat.OpenXml.Wordprocessing.Text("[[diary.export.template]]"))),
                        new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                            new DocumentFormat.OpenXml.Wordprocessing.Run(
                                new DocumentFormat.OpenXml.Wordprocessing.Text("template_name: work_report"))),
                        new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                            new DocumentFormat.OpenXml.Wordprocessing.Run(
                                new DocumentFormat.OpenXml.Wordprocessing.Text("version: 1.0.0"))),
                        new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                            new DocumentFormat.OpenXml.Wordprocessing.Run(
                                new DocumentFormat.OpenXml.Wordprocessing.Text("标题：{{title}}")))));
                main.Document.Save();
            }

            var plugins = new IExportPlugin[] { new XlsxExportPlugin(), new CsvExportPlugin(), new DocxExportPlugin() };
            var catalog = new ExportTemplateCatalog(
                NullLogger<ExportTemplateCatalog>.Instance,
                plugins.SelectMany(plugin => plugin.GetTemplateHandlers()),
                Path.Combine(directory, "templates"));
            var imported = await catalog.ImportAsync(templatePath);
            Assert.IsTrue(imported.Succeeded, imported.ErrorMessage);
            Assert.AreEqual("docx.work_report", imported.Descriptor!.TemplateId);

            var service = new ScriptExportService(
                NullLogger<ScriptExportService>.Instance,
                catalog,
                plugins.SelectMany(plugin => plugin.GetExportHandlers()));
            var context = new ScriptHostCallContext("execution", "worker", "script", ScriptEntryKind.Application, ScriptExecutionSource.Manual);
            service.RegisterDirectory("directory", directory, context);
            var result = await service.ExportAsync(new ExportRequest
            {
                FormatId = "docx",
                DirectorySelectionId = "directory",
                FileName = "rendered.docx",
                Template = new ExportTemplateSource
                {
                    TemplateId = "docx.work_report",
                    TemplateVersion = "1.0.0",
                    Values = new Dictionary<string, object?> { ["title"] = "完成报告" },
                },
            }, context);

            Assert.IsTrue(result.Succeeded, result.Error?.Message);
            using var archive = System.IO.Compression.ZipFile.OpenRead(Path.Combine(directory, result.FileName!));
            var entry = archive.GetEntry("word/document.xml");
            Assert.IsNotNull(entry);
            using var reader = new StreamReader(entry.Open());
            StringAssert.Contains(await reader.ReadToEndAsync(), "完成报告");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
