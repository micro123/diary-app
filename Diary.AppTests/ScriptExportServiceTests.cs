using ClosedXML.Excel;
using Diary.App.Services;
using Diary.ScriptHost;
using Diary.Script.Runtime;
using Diary.ScriptBase;
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
            var service = new ScriptExportService(NullLogger<ScriptExportService>.Instance);
            var context = new ScriptHostCallContext(
                "execution",
                "worker",
                "script",
                ScriptEntryKind.Application,
                ScriptExecutionSource.Manual);
            service.RegisterDirectory("directory", directory, context);
            var result = await service.ExportAsync(new ExportRequest
            {
                FormatId = "xlsx",
                DirectorySelectionId = "directory",
                FileName = "report",
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
                        new ExportAggregateColumn("时长"),
                        new ExportAggregateColumn("数值"),
                    ],
                },
            }, context);

            Assert.IsTrue(result.Succeeded, result.Error?.Message);
            Assert.IsNotNull(result.FileId);
            Assert.AreEqual(2, result.ItemCount);
            Assert.IsTrue(File.Exists(Path.Combine(directory, result.FileName!)));

            using var workbook = new XLWorkbook(Path.Combine(directory, result.FileName!));
            var sheet = workbook.Worksheet("明细");
            Assert.AreEqual("测试报告", sheet.Cell("A1").GetString());
            Assert.AreEqual("日期", sheet.Cell("A2").GetString());
            Assert.AreEqual("2026-08-19", sheet.Cell("A3").GetDateTime().ToString("yyyy-MM-dd"));
            Assert.AreEqual("SUM(B3:B4)", sheet.Cell("B5").FormulaA1);
            Assert.AreEqual("SUM(C3:C4)", sheet.Cell("C5").FormulaA1);
            Assert.AreEqual("[h]:mm:ss", sheet.Cell("B3").Style.NumberFormat.Format);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
