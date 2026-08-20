using Diary.App.Services;
using Diary.Export.Csv;
using Diary.Export.Docx;
using Diary.Export.Mustache;
using Diary.Export.Xlsx;
using Diary.ScriptHost;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.AppTests;

[TestClass]
public sealed class MustacheExportTests
{
    [TestMethod]
    public async Task MustacheTemplate_RendersSectionsEscapingAndMatrixRows()
    {
        var directory = CreateDirectory();
        try
        {
            var templatePath = Path.Combine(directory, "report.mustache");
            await File.WriteAllTextAsync(
                templatePath,
                "{{title}}\n{{#show_details}}已显示明细\n{{/show_details}}{{#items}}\n{{name}},{{id}},{{{description}}}\n{{/items}}\n{{^missing}}无缺失数据\n{{/missing}}\n{{#columns}}{{name}},{{/columns}}\n{{#items}}{{#cells}}{{.}},{{/cells}}\n{{/items}}共 {{item_count}} 条",
                new System.Text.UTF8Encoding(false));

            var (service, catalog) = CreateService(directory);
            var imported = await catalog.ImportAsync(templatePath);
            Assert.IsTrue(imported.Succeeded, imported.ErrorMessage);
            Assert.AreEqual("mustache.report", imported.Descriptor!.TemplateId);

            var context = CreateContext();
            service.RegisterDirectory("directory", directory, context);
            var result = await service.ExportAsync(new ExportRequest
            {
                FormatId = "mustache",
                DirectorySelectionId = "directory",
                FileName = "rendered",
                Template = new ExportTemplateSource
                {
                    TemplateId = "mustache.report",
                    TemplateVersion = "1.0.0",
                    Values = new Dictionary<string, object?>
                    {
                        ["title"] = "加班报告",
                        ["show_details"] = true,
                    },
                    Tables = new Dictionary<string, ExportTableContent>
                    {
                        ["items"] = new()
                        {
                            Columns = [new ExportColumn("name"), new ExportColumn("id"), new ExportColumn("description")],
                            Rows =
                            [
                                ["唐国利", "00000399", "项目 <支持>"],
                                ["李明", "00000401", "故障处理"],
                            ],
                        },
                        ["columns"] = new()
                        {
                            Columns = [new ExportColumn("name")],
                            Rows = [["姓名"], ["编号"]],
                        },
                    },
                },
            }, context);

            Assert.IsTrue(result.Succeeded, result.Error?.Message);
            Assert.AreEqual("rendered.txt", result.FileName);
            var text = await File.ReadAllTextAsync(Path.Combine(directory, result.FileName!));
            StringAssert.Contains(text, "加班报告");
            StringAssert.Contains(text, "已显示明细");
            StringAssert.Contains(text, "唐国利,00000399,项目 <支持>");
            StringAssert.Contains(text, "李明,00000401,故障处理");
            StringAssert.Contains(text, "无缺失数据");
            StringAssert.Contains(text, "姓名,编号,");
            StringAssert.Contains(text, "共 4 条");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task MustacheTemplate_RejectsUnclosedSectionsAndSupportsMarkdownOutputExtension()
    {
        var directory = CreateDirectory();
        try
        {
            var templatePath = Path.Combine(directory, "invalid.mustache");
            await File.WriteAllTextAsync(templatePath, "{{#items}}{{name}}", new System.Text.UTF8Encoding(false));
            var (_, catalog) = CreateService(directory);
            var imported = await catalog.ImportAsync(templatePath);
            Assert.IsFalse(imported.Succeeded);
            Assert.IsTrue(imported.Diagnostics?.Any(item => item.Code == "EXPORT_TEMPLATE_STRUCTURE_INVALID"));

            var validPath = Path.Combine(directory, "markdown.mustache");
            await File.WriteAllTextAsync(validPath, "# {{title}}\n", new System.Text.UTF8Encoding(false));
            var (service, validCatalog) = CreateService(directory);
            var validImport = await validCatalog.ImportAsync(validPath);
            Assert.IsTrue(validImport.Succeeded, validImport.ErrorMessage);
            var context = CreateContext();
            service.RegisterDirectory("directory", directory, context);
            var result = await service.ExportAsync(new ExportRequest
            {
                FormatId = "mustache",
                DirectorySelectionId = "directory",
                FileName = "report.md",
                Template = new ExportTemplateSource
                {
                    TemplateId = "mustache.markdown",
                    TemplateVersion = "1.0.0",
                    Values = new Dictionary<string, object?> { ["title"] = "报告" },
                },
            }, context);

            Assert.IsTrue(result.Succeeded, result.Error?.Message);
            Assert.AreEqual("report.md", result.FileName);
            Assert.AreEqual("# 报告\n", await File.ReadAllTextAsync(Path.Combine(directory, result.FileName!)));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static (ScriptExportService Service, ExportTemplateCatalog Catalog) CreateService(string directory)
    {
        IExportPlugin[] plugins =
        [
            new MustacheExportPlugin(),
            new XlsxExportPlugin(),
            new CsvExportPlugin(),
            new DocxExportPlugin(),
        ];
        var catalog = new ExportTemplateCatalog(
            NullLogger<ExportTemplateCatalog>.Instance,
            plugins.SelectMany(plugin => plugin.GetTemplateHandlers()),
            Path.Combine(directory, "templates"));
        var service = new ScriptExportService(
            NullLogger<ScriptExportService>.Instance,
            catalog,
            plugins.SelectMany(plugin => plugin.GetExportHandlers()));
        return (service, catalog);
    }

    private static ScriptHostCallContext CreateContext() => new(
        "execution", "worker", "mustache-test", ScriptEntryKind.Application, ScriptExecutionSource.Manual);

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DiaryApp-mustache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
