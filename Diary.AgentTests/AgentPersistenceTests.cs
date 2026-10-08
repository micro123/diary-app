using System.Text.Json;
using Diary.Agent.Protocols;
using Diary.Agent.Runtime;

namespace Diary.AgentTests;

[TestClass]
public sealed class AgentPersistenceTests
{
    [TestMethod]
    public void ArgumentSummaryRedactsNotesCredentialsAndNestedSecrets()
    {
        using var document = JsonDocument.Parse("""
            {
              "title":"visible",
              "note":"local-note-secret",
              "apiToken":"token-secret",
              "nested":{"password":"password-secret","value":"safe"}
            }
            """);

        var summary = AgentDataRedactor.CreateArgumentsSummary(document.RootElement);

        StringAssert.Contains(summary, "visible");
        StringAssert.Contains(summary, "safe");
        Assert.IsFalse(summary.Contains("local-note-secret", StringComparison.Ordinal));
        Assert.IsFalse(summary.Contains("token-secret", StringComparison.Ordinal));
        Assert.IsFalse(summary.Contains("password-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ConversationStoreRoundTripsVersionedSafeSummaryAndDeletes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"diary-agent-conversations-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "conversations.json");
        try
        {
            var store = new AgentConversationStore(path);
            var id = Guid.NewGuid();
            var record = new AgentConversationRecord(
                id,
                "测试会话",
                "connection-1",
                "model-1",
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow,
                [
                    new AgentConversationMessage("user", "查询今天事项"),
                    new AgentConversationMessage("assistant", "已完成"),
                ],
                [new AgentConversationToolCall(
                    "diary_query_work_items",
                    "{\"date\":\"2026-10-08\",\"note\":\"[已隐藏]\"}",
                    "已完成",
                    null,
                    "12 ms")],
                "Completed",
                new AgentUsage(10, 5, 15));

            await store.SaveAsync(record);
            var loaded = await store.LoadAsync();

            Assert.AreEqual(1, loaded.Count);
            Assert.AreEqual(id, loaded[0].Id);
            Assert.AreEqual("connection-1", loaded[0].ConnectionId);
            var raw = await File.ReadAllTextAsync(path);
            StringAssert.Contains(raw, "\"schemaVersion\": 1");
            Assert.IsFalse(raw.Contains("local-note-secret", StringComparison.Ordinal));
            Assert.IsFalse(raw.Contains("api-key-secret", StringComparison.Ordinal));

            await store.DeleteAsync(id);
            Assert.AreEqual(0, (await store.LoadAsync()).Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ConversationStoreDoesNotOverwriteUnreadableFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"diary-agent-conversations-corrupt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "conversations.json");
        const string corrupt = "{broken-json";
        try
        {
            await File.WriteAllTextAsync(path, corrupt);
            var store = new AgentConversationStore(path);
            var record = new AgentConversationRecord(
                Guid.NewGuid(), "测试", "connection", "model", DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, [], [], "Completed", null);

            Assert.AreEqual(0, (await store.LoadAsync()).Count);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await store.SaveAsync(record));
            Assert.AreEqual(corrupt, await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AuditStoreWritesOnlyStructuredMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"diary-agent-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "audit.jsonl");
        try
        {
            var store = new AgentAuditStore(path);
            await store.RecordRunAsync(new AgentRunAuditRecord(
                Guid.NewGuid(),
                "connection-1",
                "model-1",
                "OpenAiResponses",
                DateTimeOffset.UtcNow.AddSeconds(-1),
                DateTimeOffset.UtcNow,
                "Completed",
                2,
                1,
                null,
                new AgentUsage(10, 5, 15)));
            await store.RecordToolAsync(new AgentToolAuditRecord(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "diary.work-items.create",
                "diary_create_work_item",
                "BuiltIn",
                "Write",
                DateTimeOffset.UtcNow,
                20,
                true,
                null,
                false,
                false,
                32,
                "已创建事项 42。"));

            var raw = await File.ReadAllTextAsync(path);
            StringAssert.Contains(raw, "diary_create_work_item");
            var lines = raw.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            using var toolLine = JsonDocument.Parse(lines[1]);
            Assert.AreEqual(
                "已创建事项 42。",
                toolLine.RootElement.GetProperty("record").GetProperty("effectSummary").GetString());
            Assert.IsFalse(raw.Contains("local-note-secret", StringComparison.Ordinal));
            Assert.IsFalse(raw.Contains("api-key-secret", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AuditStoreRotatesBeforeExceedingConfiguredBudget()
    {
        var root = Path.Combine(Path.GetTempPath(), $"diary-agent-audit-rotation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "audit.jsonl");
        try
        {
            var store = new AgentAuditStore(path, maxBytes: 1, maxArchiveFiles: 2);
            for (var index = 0; index < 4; index++)
            {
                await store.RecordRunAsync(new AgentRunAuditRecord(
                    Guid.NewGuid(), "connection", "model", "OpenAiResponses",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Completed", 1, 0, null, null));
            }

            Assert.IsTrue(File.Exists(path));
            Assert.IsTrue(File.Exists(path + ".1"));
            Assert.IsTrue(File.Exists(path + ".2"));
            Assert.AreEqual(3, Directory.GetFiles(root, "audit.jsonl*").Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
