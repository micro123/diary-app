using ClosedXML.Excel;
using Diary.App.Services;
using Diary.Export.Csv;
using Diary.Export.Docx;
using Diary.Export.Mustache;
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
                var worksheet = workbook.Worksheets.Add("明细");
                worksheet.Cell("A1").FormulaA1 = "WEBSERVICE(\"https://example.test/data\")";
                worksheet.Cell("A2").Value = "{{title}}";
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
            new[] { "csv", "docx", "mustache", "xlsx" },
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
                new[] { "csv", "docx", "mustache", "xlsx" },
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
    public async Task CsvTemplate_CanBeImportedAndRendered()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var templatePath = Path.Combine(directory, "report.csv");
            await File.WriteAllTextAsync(templatePath,
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
                    TemplateId = "csv.report",
                    TemplateVersion = "1.0.0",
                    Values = new Dictionary<string, object?> { ["period"] = "current_month" },
                },
            }, context);

            Assert.IsTrue(result.Succeeded, result.Error?.Message);
            var text = await File.ReadAllTextAsync(Path.Combine(directory, result.FileName!));
            StringAssert.Contains(text, "周期,current_month");
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
                    TemplateId = "csv.report",
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
    public async Task CsvTemplate_ExpandsRowAndColumnLoops()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var templatePath = Path.Combine(directory, "loop.csv");
            await File.WriteAllTextAsync(
                templatePath,
                "姓名,工时\n{{items.name}},{{items.hours}}\n横向,{{items.name|column}}\n",
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
                FileName = "loop-output.csv",
                Template = new ExportTemplateSource
                {
                    TemplateId = "csv.loop",
                    TemplateVersion = "1.0.0",
                    Tables = new Dictionary<string, ExportTableContent>
                    {
                        ["items"] = new()
                        {
                            Columns = [new ExportColumn("name"), new ExportColumn("hours", ExportColumnType.Integer)],
                            Rows = [["唐国利", 2], ["李明", 3]],
                        },
                    },
                },
            }, context);

            Assert.IsTrue(result.Succeeded, result.Error?.Message);
            var text = await File.ReadAllTextAsync(Path.Combine(directory, result.FileName!));
            Assert.AreEqual("姓名,工时\r\n唐国利,2\r\n李明,3\r\n横向,唐国利,李明\r\n", text);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task CsvTemplate_ExpandsMatrixBetweenFixedRows()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var templatePath = Path.Combine(directory, "matrix.csv");
            await File.WriteAllTextAsync(
                templatePath,
                "固定表头\n{{items|matrix}}\n固定表底\n",
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
                FileName = "matrix-output.csv",
                Template = new ExportTemplateSource
                {
                    TemplateId = "csv.matrix",
                    TemplateVersion = "1.0.0",
                    Tables = new Dictionary<string, ExportTableContent>
                    {
                        ["items"] = new()
                        {
                            Columns = [new ExportColumn("姓名"), new ExportColumn("工时", ExportColumnType.Integer)],
                            Rows = [["唐国利", 2], ["李明", 3]],
                        },
                    },
                },
            }, context);

            Assert.IsTrue(result.Succeeded, result.Error?.Message);
            var text = await File.ReadAllTextAsync(Path.Combine(directory, result.FileName!));
            Assert.AreEqual("固定表头\r\n唐国利,2\r\n李明,3\r\n固定表底\r\n", text);
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
                    TemplateId = "csv.required",
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
                var worksheet = workbook.Worksheets.Add("明细");
                worksheet.Cell("A1").Value = "{{items.quantity}}";
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
                    Columns = [new ExportColumn("quantity", ExportColumnType.Integer)],
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
                    Columns = [new ExportColumn("quantity")],
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
    public async Task XlsxTemplate_ExpandsRowsAndColumnsAndPreservesDateTimeFormat()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var templatePath = Path.Combine(directory, "overtime.xlsx");
            using (var workbook = new XLWorkbook())
            {
                var rows = workbook.Worksheets.Add("明细");
                rows.Cell("A1").Value = "姓名";
                rows.Cell("B1").Value = "开始时间";
                rows.Cell("A2").Value = "{{items.name}}";
                rows.Cell("B2").Value = "{{items.start_time}}";
                rows.Cell("B2").Style.DateFormat.Format = "yyyy/mm/dd-hh:mm:ss";
                var columns = workbook.Worksheets.Add("横向");
                columns.Cell("A1").Value = "说明";
                columns.Cell("B1").Value = "{{items.description|column}}";
                workbook.SaveAs(templatePath);
            }
            var service = CreateService(directory, out var catalog);
            var imported = await catalog.ImportAsync(templatePath);
            Assert.IsTrue(imported.Succeeded, imported.ErrorMessage);

            var context = CreateContext();
            service.RegisterDirectory("directory", directory, context);
            var result = await service.ExportAsync(new ExportRequest
            {
                FormatId = "xlsx",
                DirectorySelectionId = "directory",
                FileName = "overtime-output.xlsx",
                Template = new ExportTemplateSource
                {
                    TemplateId = "xlsx.overtime",
                    TemplateVersion = "1.0.0",
                    Tables = new Dictionary<string, ExportTableContent>
                    {
                        ["items"] = new()
                        {
                            Columns =
                            [
                                new ExportColumn("name"),
                                new ExportColumn("start_time", ExportColumnType.DateTime),
                                new ExportColumn("description"),
                            ],
                            Rows =
                            [
                                ["唐国利", new DateTime(2026, 7, 6, 13, 0, 0), "项目支持"],
                                ["李明", new DateTime(2026, 7, 7, 18, 30, 0), "故障处理"],
                            ],
                        },
                    },
                },
            }, context);

            Assert.IsTrue(result.Succeeded, result.Error?.Message);
            using var output = new XLWorkbook(Path.Combine(directory, result.FileName!));
            Assert.AreEqual("唐国利", output.Worksheet("明细").Cell("A2").GetString());
            Assert.AreEqual("李明", output.Worksheet("明细").Cell("A3").GetString());
            Assert.AreEqual(new DateTime(2026, 7, 6, 13, 0, 0), output.Worksheet("明细").Cell("B2").GetDateTime());
            Assert.AreEqual("yyyy/mm/dd-hh:mm:ss", output.Worksheet("明细").Cell("B3").Style.DateFormat.Format);
            Assert.AreEqual("项目支持", output.Worksheet("横向").Cell("B1").GetString());
            Assert.AreEqual("故障处理", output.Worksheet("横向").Cell("C1").GetString());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task XlsxTemplate_ExpandsMatrixBetweenFixedRowsAndColumns()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var templatePath = Path.Combine(directory, "matrix.xlsx");
            using (var workbook = new XLWorkbook())
            {
                var matrixSheet = workbook.Worksheets.Add("明细");
                matrixSheet.Cell("A1").Value = "固定表头";
                matrixSheet.Cell("A2").Value = "{{items|matrix}}";
                matrixSheet.Cell("A3").Value = "固定表底";
                workbook.SaveAs(templatePath);
            }
            var service = CreateService(directory, out var catalog);
            var imported = await catalog.ImportAsync(templatePath);
            Assert.IsTrue(imported.Succeeded, imported.ErrorMessage);

            var context = CreateContext();
            service.RegisterDirectory("directory", directory, context);
            var result = await service.ExportAsync(new ExportRequest
            {
                FormatId = "xlsx",
                DirectorySelectionId = "directory",
                FileName = "matrix-output.xlsx",
                Template = new ExportTemplateSource
                {
                    TemplateId = "xlsx.matrix",
                    TemplateVersion = "1.0.0",
                    Tables = new Dictionary<string, ExportTableContent>
                    {
                        ["items"] = new()
                        {
                            Columns = [new ExportColumn("姓名"), new ExportColumn("工时", ExportColumnType.Integer)],
                            Rows = [["唐国利", 2], ["李明", 3]],
                        },
                    },
                },
            }, context);

            Assert.IsTrue(result.Succeeded, result.Error?.Message);
            using var output = new XLWorkbook(Path.Combine(directory, result.FileName!));
            var worksheet = output.Worksheet("明细");
            Assert.AreEqual("固定表头", worksheet.Cell("A1").GetString());
            Assert.AreEqual("唐国利", worksheet.Cell("A2").GetString());
            Assert.AreEqual(2, worksheet.Cell("B2").GetValue<int>());
            Assert.AreEqual("李明", worksheet.Cell("A3").GetString());
            Assert.AreEqual(3, worksheet.Cell("B3").GetValue<int>());
            Assert.AreEqual("固定表底", worksheet.Cell("A4").GetString());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task ExportAsync_ValidateOnlySkipsDirectoryAndFileCreation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DiaryApp-docx-matrix-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
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
        TemplateId = "xlsx.table_template",
        TemplateVersion = "1.0.0",
        Tables = new Dictionary<string, ExportTableContent> { ["items"] = table },
    };

    private static ScriptExportService CreateService(string directory) => CreateService(directory, out _);

    private static ScriptExportService CreateService(string directory, out ExportTemplateCatalog catalog)
    {
        IExportPlugin[] plugins =
            [new XlsxExportPlugin(), new CsvExportPlugin(), new DocxExportPlugin(), new MustacheExportPlugin()];
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
                                new DocumentFormat.OpenXml.Wordprocessing.Text("{{title}}"))),
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
                                new DocumentFormat.OpenXml.Wordprocessing.Text("标题：{{title}}"))),
                        new DocumentFormat.OpenXml.Wordprocessing.Table(
                            new DocumentFormat.OpenXml.Wordprocessing.TableRow(
                                new DocumentFormat.OpenXml.Wordprocessing.TableCell(
                                    new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                                        new DocumentFormat.OpenXml.Wordprocessing.Run(
                                            new DocumentFormat.OpenXml.Wordprocessing.Text("{{items.name}}")))),
                                new DocumentFormat.OpenXml.Wordprocessing.TableCell(
                                    new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                                        new DocumentFormat.OpenXml.Wordprocessing.Run(
                                            new DocumentFormat.OpenXml.Wordprocessing.Text("{{items.hours}}"))))),
                            new DocumentFormat.OpenXml.Wordprocessing.TableRow(
                                new DocumentFormat.OpenXml.Wordprocessing.TableCell(
                                    new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                                        new DocumentFormat.OpenXml.Wordprocessing.Run(
                                            new DocumentFormat.OpenXml.Wordprocessing.Text("横向")))),
                                new DocumentFormat.OpenXml.Wordprocessing.TableCell(
                                    new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                                        new DocumentFormat.OpenXml.Wordprocessing.Run(
                                            new DocumentFormat.OpenXml.Wordprocessing.Text("{{items.name|column}}"))))))));
                main.Document.Save();
            }

            var plugins = new IExportPlugin[] { new XlsxExportPlugin(), new CsvExportPlugin(), new DocxExportPlugin() };
            var catalog = new ExportTemplateCatalog(
                NullLogger<ExportTemplateCatalog>.Instance,
                plugins.SelectMany(plugin => plugin.GetTemplateHandlers()),
                Path.Combine(directory, "templates"));
            var imported = await catalog.ImportAsync(templatePath);
            Assert.IsTrue(imported.Succeeded, imported.ErrorMessage);
            Assert.AreEqual("docx.report", imported.Descriptor!.TemplateId);

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
                    TemplateId = "docx.report",
                    TemplateVersion = "1.0.0",
                    Values = new Dictionary<string, object?> { ["title"] = "完成报告" },
                    Tables = new Dictionary<string, ExportTableContent>
                    {
                        ["items"] = new()
                        {
                            Columns = [new ExportColumn("name"), new ExportColumn("hours", ExportColumnType.Integer)],
                            Rows = [["唐国利", 2], ["李明", 3]],
                        },
                    },
                },
            }, context);

            Assert.IsTrue(result.Succeeded, result.Error?.Message);
            using var archive = System.IO.Compression.ZipFile.OpenRead(Path.Combine(directory, result.FileName!));
            var entry = archive.GetEntry("word/document.xml");
            Assert.IsNotNull(entry);
            using var reader = new StreamReader(entry.Open());
            var xml = await reader.ReadToEndAsync();
            StringAssert.Contains(xml, "完成报告");
            StringAssert.Contains(xml, "唐国利");
            StringAssert.Contains(xml, "李明");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task DocxTemplate_ExpandsMatrixBetweenFixedRows()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DiaryApp-docx-matrix-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var templatePath = Path.Combine(directory, "matrix.docx");
            using (var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(
                       templatePath,
                       DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = document.AddMainDocumentPart();
                static DocumentFormat.OpenXml.Wordprocessing.TableCell Cell(string text) =>
                    new(
                        new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                            new DocumentFormat.OpenXml.Wordprocessing.Run(
                                new DocumentFormat.OpenXml.Wordprocessing.Text(text))));
                main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                    new DocumentFormat.OpenXml.Wordprocessing.Body(
                        new DocumentFormat.OpenXml.Wordprocessing.Table(
                            new DocumentFormat.OpenXml.Wordprocessing.TableRow(Cell("固定表头")),
                            new DocumentFormat.OpenXml.Wordprocessing.TableRow(Cell("{{items|matrix}}")),
                            new DocumentFormat.OpenXml.Wordprocessing.TableRow(Cell("固定表底")))));
                main.Document.Save();
            }

            IExportPlugin[] plugins = [new XlsxExportPlugin(), new CsvExportPlugin(), new DocxExportPlugin()];
            var catalog = new ExportTemplateCatalog(
                NullLogger<ExportTemplateCatalog>.Instance,
                plugins.SelectMany(plugin => plugin.GetTemplateHandlers()),
                Path.Combine(directory, "templates"));
            var service = new ScriptExportService(
                NullLogger<ScriptExportService>.Instance,
                catalog,
                plugins.SelectMany(plugin => plugin.GetExportHandlers()));
            var imported = await catalog.ImportAsync(templatePath);
            Assert.IsTrue(imported.Succeeded, imported.ErrorMessage);
            var context = new ScriptHostCallContext(
                "execution", "worker", "script", ScriptEntryKind.Application, ScriptExecutionSource.Manual);
            service.RegisterDirectory("directory", directory, context);
            var result = await service.ExportAsync(new ExportRequest
            {
                FormatId = "docx",
                DirectorySelectionId = "directory",
                FileName = "matrix-output.docx",
                Template = new ExportTemplateSource
                {
                    TemplateId = "docx.matrix",
                    TemplateVersion = "1.0.0",
                    Tables = new Dictionary<string, ExportTableContent>
                    {
                        ["items"] = new()
                        {
                            Columns = [new ExportColumn("姓名"), new ExportColumn("工时", ExportColumnType.Integer)],
                            Rows = [["唐国利", 2], ["李明", 3]],
                        },
                    },
                },
            }, context);

            Assert.IsTrue(result.Succeeded, result.Error?.Message);
            using var archive = System.IO.Compression.ZipFile.OpenRead(Path.Combine(directory, result.FileName!));
            var entry = archive.GetEntry("word/document.xml");
            Assert.IsNotNull(entry);
            using var reader = new StreamReader(entry.Open());
            var xml = await reader.ReadToEndAsync();
            StringAssert.Contains(xml, "固定表头");
            StringAssert.Contains(xml, "唐国利");
            StringAssert.Contains(xml, "李明");
            StringAssert.Contains(xml, "固定表底");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
