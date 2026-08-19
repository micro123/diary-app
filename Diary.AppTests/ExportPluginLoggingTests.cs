using Diary.App.Services;
using Diary.Export.Xlsx;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.AppTests;

[TestClass]
public sealed class ExportPluginLoggingTests
{
    [TestMethod]
    public void ScriptExportService_LogsRegisteredFormatsAndSummary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DiaryApp-export-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var plugin = new XlsxExportPlugin();
            var catalog = new ExportTemplateCatalog(
                NullLogger<ExportTemplateCatalog>.Instance,
                plugin.GetTemplateHandlers(),
                directory);
            var logger = new RecordingLogger<ScriptExportService>();

            _ = new ScriptExportService(logger, catalog, plugin.GetExportHandlers());

            Assert.IsTrue(logger.Entries.Any(entry =>
                entry.Level == LogLevel.Information
                && entry.Message.Contains("导出格式注册成功", StringComparison.Ordinal)
                && entry.Message.Contains("xlsx", StringComparison.Ordinal)));
            Assert.IsTrue(logger.Entries.Any(entry =>
                entry.Level == LogLevel.Information
                && entry.Message.Contains("导出格式注册完成", StringComparison.Ordinal)
                && entry.Message.Contains("1", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
