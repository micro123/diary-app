using System.Diagnostics;
using System.Text.Json;
using Diary.AiContext;

namespace Diary.ScriptTests;

[TestClass]
public sealed class AiContextMcpStdioTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task StdioServer_ListsOnlyReadOnlyToolsAndQueriesSnapshot()
    {
        var root = FindRepositoryRoot();
        var snapshotPath = Path.Combine(Path.GetTempPath(), $"diary-mcp-{Guid.NewGuid():N}.json");
        await AiContextSerializer.SaveAsync(snapshotPath, CreateSnapshot());
        using var process = StartServer(root, snapshotPath);
        try
        {
            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-11-25",
                    capabilities = new { },
                    clientInfo = new { name = "diary-tests", version = "1.0" },
                },
            });
            using var initialize = await ReadResponseAsync(process, 1);
            Assert.IsTrue(initialize.RootElement.TryGetProperty("result", out _));

            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                method = "notifications/initialized",
                @params = new { },
            });
            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/list",
                @params = new { },
            });
            using var toolsResponse = await ReadResponseAsync(process, 2);
            var toolNames = toolsResponse.RootElement.GetProperty("result").GetProperty("tools")
                .EnumerateArray()
                .Select(tool => tool.GetProperty("name").GetString())
                .Order(StringComparer.Ordinal)
                .ToArray();
            CollectionAssert.AreEqual(new[]
            {
                "diary_list_extra_fields",
                "diary_list_tags",
                "diary_list_templates",
                "diary_list_tracker_instances",
                "diary_query_work_items",
                "diary_summarize_work_items",
            }, toolNames);

            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "tools/call",
                @params = new
                {
                    name = "diary_query_work_items",
                    arguments = new { tagIds = new[] { 2 }, limit = 10 },
                },
            });
            using var queryResponse = await ReadResponseAsync(process, 3);
            var content = queryResponse.RootElement.GetProperty("result").GetProperty("content")[0]
                .GetProperty("text").GetString();
            StringAssert.Contains(content!, "second");
            Assert.IsFalse(content!.Contains("first", StringComparison.Ordinal));
        }
        finally
        {
            await process.StandardInput.DisposeAsync();
            if (!process.WaitForExit(5_000))
                process.Kill(true);
            File.Delete(snapshotPath);
        }
    }

    private static Process StartServer(string root, string snapshotPath)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var serverPath = Path.Combine(root, "Diary.Mcp", "bin", configuration, "net10.0", "Diary.Mcp.dll");
        Assert.IsTrue(File.Exists(serverPath), $"找不到 MCP 测试产物：{serverPath}");
        return Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { serverPath, "--snapshot", snapshotPath },
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("无法启动 Diary.Mcp 测试进程。");
    }

    private static async Task SendAsync(Process process, object message)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await process.StandardInput.FlushAsync();
    }

    private static async Task<JsonDocument> ReadResponseAsync(Process process, int id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null)
            {
                var error = await process.StandardError.ReadToEndAsync(timeout.Token);
                Assert.Fail($"MCP 进程提前退出。stderr: {error}");
            }
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line!);
            }
            catch (JsonException exception)
            {
                Assert.Fail($"MCP stdout 包含非 JSON 内容：{line}；{exception.Message}");
                throw;
            }
            if (document.RootElement.TryGetProperty("id", out var responseId)
                && responseId.ValueKind == JsonValueKind.Number
                && responseId.GetInt32() == id)
                return document;
            document.Dispose();
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DiaryApp.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("找不到 DiaryApp.sln。");
    }

    private static AiContextSnapshot CreateSnapshot() => new()
    {
        Disclosure = new AiContextDisclosure(true, true, true, true, false, true, true),
        Tags =
        [
            new AiContextTag(1, "work", 0, "Primary", false),
            new AiContextTag(2, "project", 0, "Secondary", false),
        ],
        WorkItems =
        [
            new AiContextWorkItem(1, "2026-08-01", "first", 1, 0, null, [1], []),
            new AiContextWorkItem(2, "2026-08-02", "second", 2, 1, null, [2], []),
        ],
        Audit = new AiContextAudit(["tags", "work_items"], 2, 0, 0, 0, 0, 2, 0),
    };
}
