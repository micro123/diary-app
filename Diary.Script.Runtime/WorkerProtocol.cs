using System.Text.Json;
using System.Text.Json.Serialization;
using Diary.ScriptBase;

namespace Diary.Script.Runtime;

public static class WorkerProtocol
{
    public const string Name = "diary.script.worker";
    public const int Version = 1;
    public const int DefaultMaxMessageBytes = 4 * 1024 * 1024;
    public const int DefaultHeartbeatSeconds = 30;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}

public enum WorkerMessageType
{
    Hello,
    HelloAccepted,
    Execute,
    ExecuteAccepted,
    ExecuteRejected,
    ExecuteResult,
    HostCall,
    HostResult,
    Cancel,
    Ping,
    Pong,
    Error,
}

public sealed record WorkerMessage<TPayload>(
    string Protocol,
    int Version,
    WorkerMessageType Type,
    string? RequestId,
    string? ExecutionId,
    TPayload Payload);

public sealed record WorkerHelloPayload(
    string Language,
    string WorkerVersion,
    IReadOnlyCollection<ScriptApiVersion> SupportedApiVersions,
    IReadOnlyCollection<string> SupportedHostApis,
    int ProcessId);

public sealed record WorkerHelloAcceptedPayload(
    ScriptApiVersion ApiVersion,
    int MaxMessageBytes = WorkerProtocol.DefaultMaxMessageBytes,
    int HeartbeatSeconds = WorkerProtocol.DefaultHeartbeatSeconds,
    IReadOnlyCollection<string>? HostApis = null);

public sealed record WorkerHandshakeOptions(
    string Language,
    IReadOnlyCollection<ScriptApiVersion> SupportedApiVersions,
    IReadOnlyCollection<string> AllowedHostApis,
    int MaxMessageBytes = WorkerProtocol.DefaultMaxMessageBytes,
    int HeartbeatSeconds = WorkerProtocol.DefaultHeartbeatSeconds);

public sealed record WorkerHandshakeResult(
    bool Accepted,
    WorkerHelloAcceptedPayload? AcceptedPayload,
    ScriptDiagnostic? Diagnostic)
{
    public static WorkerHandshakeResult Success(WorkerHelloAcceptedPayload payload) => new(true, payload, null);

    public static WorkerHandshakeResult Failure(string message) => new(
        false,
        null,
        new ScriptDiagnostic(
            "WORKER_HANDSHAKE_FAILED",
            message,
            ScriptDiagnosticSeverity.Error,
            ScriptDiagnosticCategory.Validation));
}

public static class WorkerHandshake
{
    public static WorkerHandshakeResult Negotiate(
        WorkerMessage<WorkerHelloPayload> hello,
        WorkerHandshakeOptions options)
    {
        if (hello.Protocol != WorkerProtocol.Name || hello.Version != WorkerProtocol.Version)
            return WorkerHandshakeResult.Failure("Worker 协议名称或主版本不匹配。");
        if (hello.Type != WorkerMessageType.Hello || string.IsNullOrWhiteSpace(hello.RequestId))
            return WorkerHandshakeResult.Failure("Worker 握手消息类型或 requestId 无效。");
        if (!string.Equals(hello.Payload.Language, options.Language, StringComparison.OrdinalIgnoreCase))
            return WorkerHandshakeResult.Failure("Worker 语言与启动配置不匹配。");

        var apiVersion = hello.Payload.SupportedApiVersions
            .Intersect(options.SupportedApiVersions)
            .OrderByDescending(version => version)
            .FirstOrDefault();
        if (!options.SupportedApiVersions.Contains(apiVersion)
            || !hello.Payload.SupportedApiVersions.Contains(apiVersion))
        {
            return WorkerHandshakeResult.Failure("Worker 与宿主没有共同的 Script API 版本。");
        }

        var hostApis = hello.Payload.SupportedHostApis
            .Intersect(options.AllowedHostApis, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return WorkerHandshakeResult.Success(new WorkerHelloAcceptedPayload(
            apiVersion,
            options.MaxMessageBytes,
            options.HeartbeatSeconds,
            hostApis));
    }
}
