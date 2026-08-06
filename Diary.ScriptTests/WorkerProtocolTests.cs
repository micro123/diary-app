using System.Text.Json;
using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.ScriptTests;

[TestClass]
public sealed class WorkerProtocolTests
{
    private static WorkerHandshakeOptions Options => new(
        "csharp",
        [ScriptApiVersion.V1],
        ["workItems.query"]);

    [TestMethod]
    public void Negotiate_AcceptsCommonVersionAndHostApis()
    {
        var result = WorkerHandshake.Negotiate(
            new WorkerMessage<WorkerHelloPayload>(
                WorkerProtocol.Name,
                WorkerProtocol.Version,
                WorkerMessageType.Hello,
                "hello-1",
                null,
                new WorkerHelloPayload("csharp", "1.0", [ScriptApiVersion.V1], ["workItems.query", "unsupported"], 12)),
            Options);

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(ScriptApiVersion.V1, result.AcceptedPayload!.ApiVersion);
        CollectionAssert.AreEqual(new[] { "workItems.query" }, result.AcceptedPayload.HostApis!.ToArray());
    }

    [TestMethod]
    [DataRow("wrong.protocol", 1)]
    [DataRow("diary.script.worker", 2)]
    public void Negotiate_RejectsProtocolMismatch(string protocol, int version)
    {
        var result = WorkerHandshake.Negotiate(CreateHello(protocol, version), Options);

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual("WORKER_HANDSHAKE_FAILED", result.Diagnostic!.Code);
    }

    [TestMethod]
    public void Negotiate_RejectsLanguageAndMissingApi()
    {
        Assert.IsFalse(WorkerHandshake.Negotiate(CreateHello(language: "python"), Options).Accepted);
        Assert.IsFalse(WorkerHandshake.Negotiate(CreateHello(apis: []), Options).Accepted);
    }

    [TestMethod]
    public void Message_RoundTripsAsJson()
    {
        var message = CreateHello();
        var json = JsonSerializer.Serialize(message, WorkerProtocol.JsonOptions);
        var restored = JsonSerializer.Deserialize<WorkerMessage<WorkerHelloPayload>>(json, WorkerProtocol.JsonOptions);

        Assert.AreEqual(message.Protocol, restored!.Protocol);
        Assert.AreEqual(message.Type, restored.Type);
        Assert.AreEqual("csharp", restored.Payload.Language);
    }

    private static WorkerMessage<WorkerHelloPayload> CreateHello(
        string protocol = WorkerProtocol.Name,
        int version = WorkerProtocol.Version,
        string language = "csharp",
        IReadOnlyCollection<ScriptApiVersion>? apis = null) =>
        new(protocol, version, WorkerMessageType.Hello, "hello-1", null,
            new WorkerHelloPayload(language, "1.0", apis ?? [ScriptApiVersion.V1], ["workItems.query"], 12));
}
