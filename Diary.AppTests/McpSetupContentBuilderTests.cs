using System.Text.Json;
using Diary.App.Services;

namespace Diary.AppTests;

[TestClass]
public sealed class McpSetupContentBuilderTests
{
    [TestMethod]
    public void CreateGenericConfiguration_ProducesStdioCommandAndSnapshotArguments()
    {
        var executablePath = AbsoluteTestPath("Diary App", OperatingSystem.IsWindows() ? "Diary.Mcp.exe" : "Diary.Mcp");
        var snapshotPath = AbsoluteTestPath("diary profile", "mcp-snapshot.json");

        var content = McpSetupContentBuilder.CreateGenericConfiguration(executablePath, snapshotPath);
        using var document = JsonDocument.Parse(content);
        var diary = document.RootElement.GetProperty("mcpServers").GetProperty("diary");

        Assert.AreEqual(executablePath, diary.GetProperty("command").GetString());
        var arguments = diary.GetProperty("args").EnumerateArray().Select(item => item.GetString()).ToArray();
        CollectionAssert.AreEqual(new[] { "--snapshot", snapshotPath }, arguments);
        Assert.IsFalse(diary.TryGetProperty("env", out _));
    }

    [TestMethod]
    public void CreateAiInstructions_ExplainsReadOnlyStdioSetupWithoutSecrets()
    {
        var executablePath = AbsoluteTestPath("diary", OperatingSystem.IsWindows() ? "Diary.Mcp.exe" : "Diary.Mcp");
        var snapshotPath = AbsoluteTestPath("diary", "mcp-snapshot.json");

        var content = McpSetupContentBuilder.CreateAiInstructions(executablePath, snapshotPath);

        StringAssert.Contains(content, "stdio MCP Server");
        StringAssert.Contains(content, executablePath);
        StringAssert.Contains(content, snapshotPath);
        StringAssert.Contains(content, "diary_list_tags");
        StringAssert.Contains(content, "diary_query_work_items");
        StringAssert.Contains(content, "diary_validate_script");
        StringAssert.Contains(content, "不要注入数据库密码");
        StringAssert.Contains(content, "不会查询数据库");
        StringAssert.Contains(content, "不会执行脚本");
    }

    [TestMethod]
    public void CreateGenericConfiguration_RejectsRelativePaths()
    {
        var executablePath = AbsoluteTestPath("diary", OperatingSystem.IsWindows() ? "Diary.Mcp.exe" : "Diary.Mcp");
        var snapshotPath = AbsoluteTestPath("diary", "mcp-snapshot.json");
        Assert.ThrowsExactly<ArgumentException>(() =>
            McpSetupContentBuilder.CreateGenericConfiguration("Diary.Mcp", snapshotPath));
        Assert.ThrowsExactly<ArgumentException>(() =>
            McpSetupContentBuilder.CreateGenericConfiguration(executablePath, "mcp-snapshot.json"));
    }

    private static string AbsoluteTestPath(params string[] parts) =>
        Path.GetFullPath(Path.Combine([Path.GetTempPath(), .. parts]));
}
