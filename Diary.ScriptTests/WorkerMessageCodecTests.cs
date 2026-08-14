using Diary.Script.Runtime;
using Diary.ScriptBase;
using System.Text.Json;

namespace Diary.ScriptTests;

[TestClass]
public sealed class WorkerMessageCodecTests
{
    [TestMethod]
    public async Task Codec_RoundTripsPayloadNearDefaultMessageLimit()
    {
        var content = new string('x', 3 * 1024 * 1024);
        var message = new WorkerMessage<object>(
            WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.HostResult,
            "large", "exec", new { content });
        await using var stream = new MemoryStream();

        await WorkerMessageCodec.WriteAsync(stream, message);
        Assert.IsTrue(stream.Length > 3 * 1024 * 1024);
        stream.Position = 0;
        var result = await WorkerMessageCodec.ReadAsync<JsonElement>(stream);

        Assert.AreEqual(content.Length, result.Payload.GetProperty("content").GetString()!.Length);
    }

    [TestMethod]
    public async Task WriteAndRead_RoundTripsUtf8Message()
    {
        var message = new WorkerMessage<WorkerHelloPayload>(
            WorkerProtocol.Name,
            WorkerProtocol.Version,
            WorkerMessageType.Hello,
            "hello-1",
            null,
            new WorkerHelloPayload("csharp", "版本一", [ScriptApiVersion.V1], ["workItems.query"], 1));
        await using var stream = new MemoryStream();

        await WorkerMessageCodec.WriteAsync(stream, message);
        stream.Position = 0;
        var restored = await WorkerMessageCodec.ReadAsync<WorkerHelloPayload>(stream);

        Assert.AreEqual("版本一", restored.Payload.WorkerVersion);
        Assert.AreEqual(WorkerMessageType.Hello, restored.Type);
    }

    [TestMethod]
    public async Task WriteAsync_RejectsOversizedMessage()
    {
        await using var stream = new MemoryStream();
        var message = new WorkerMessage<string>(WorkerProtocol.Name, 1, WorkerMessageType.Error, "1", null, "123456");

        await Assert.ThrowsExactlyAsync<WorkerMessageTooLargeException>(() =>
            WorkerMessageCodec.WriteAsync(stream, message, maxMessageBytes: 10).AsTask());
    }

    [TestMethod]
    public async Task ReadAsync_RejectsOversizedMessage()
    {
        await using var stream = new MemoryStream("123456\n"u8.ToArray());

        await Assert.ThrowsExactlyAsync<WorkerMessageTooLargeException>(() =>
            WorkerMessageCodec.ReadAsync<WorkerHelloPayload>(stream, maxMessageBytes: 4).AsTask());
    }

    [TestMethod]
    public async Task ReadAsync_RejectsInvalidJsonAndMissingNewline()
    {
        await using var invalid = new MemoryStream("not-json\n"u8.ToArray());
        await Assert.ThrowsExactlyAsync<WorkerInvalidMessageException>(() =>
            WorkerMessageCodec.ReadAsync<WorkerHelloPayload>(invalid).AsTask());

        await using var missingNewline = new MemoryStream("{}"u8.ToArray());
        await Assert.ThrowsExactlyAsync<WorkerInvalidMessageException>(() =>
            WorkerMessageCodec.ReadAsync<WorkerHelloPayload>(missingNewline).AsTask());
    }
}
