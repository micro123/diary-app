using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using Diary.ScriptBase;

namespace Diary.Script.Runtime;

public static class WorkerProtocol
{
    // 消息大小层级：
    // - 协议消息默认上限 4MB（DefaultMaxMessageBytes）：握手、HostCall、HostResult、控制消息共用；
    // - 执行结果上限 16MB（DefaultMaxResultMessageBytes）：结果可能携带大批量 JSON 明细；
    // - Worker 进程 stderr 与脚本原生控制台输出上限 1MB（ProcessWorkerTransport.MaxStderrBytes / Worker 侧 BoundedTextWriter）：仅作安全兜底。
    public const string Name = "diary.script.worker";
    public const int Version = 1;
    public const int DefaultMaxMessageBytes = 4 * 1024 * 1024;
    public const int DefaultMaxResultMessageBytes = 16 * 1024 * 1024;
    public const int DefaultHeartbeatSeconds = 30;
    public const int DefaultHandshakeTimeoutSeconds = 10;
    public const int DefaultHeartbeatTimeoutSeconds = 15;
    public const int DefaultHostCallTimeoutSeconds = 30;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static int GetMessageSize<TPayload>(WorkerMessage<TPayload> message) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(message, JsonOptions)) + 1;
}

/// <summary>Worker 通道数据错误基类（JSON 无效、缺少换行、超限等）。</summary>
public class WorkerProtocolDataException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>Worker 消息超过大小上限（读端、写端均可能抛出）。</summary>
public sealed class WorkerMessageTooLargeException(string message) : WorkerProtocolDataException(message);

/// <summary>Worker 消息不是有效 JSON、缺少换行结束符或载荷为空。</summary>
public sealed class WorkerInvalidMessageException(string message, Exception? innerException = null)
    : WorkerProtocolDataException(message, innerException);

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
    int MaxResultMessageBytes = WorkerProtocol.DefaultMaxResultMessageBytes,
    int HeartbeatSeconds = WorkerProtocol.DefaultHeartbeatSeconds,
    IReadOnlyCollection<string>? HostApis = null);

public sealed record WorkerHandshakeOptions(
    string Language,
    IReadOnlyCollection<ScriptApiVersion> SupportedApiVersions,
    IReadOnlyCollection<string> AllowedHostApis,
    int MaxMessageBytes = WorkerProtocol.DefaultMaxMessageBytes,
    int MaxResultMessageBytes = WorkerProtocol.DefaultMaxResultMessageBytes,
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
            return WorkerHandshakeResult.Failure(
                $"Worker 协议名称或主版本不匹配（期望 {WorkerProtocol.Name} v{WorkerProtocol.Version}，实际 {hello.Protocol} v{hello.Version}）。");
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
            options.MaxResultMessageBytes,
            options.HeartbeatSeconds,
            hostApis));
    }
}

public static class WorkerMessageCodec
{
    public static async ValueTask WriteAsync<TPayload>(
        Stream stream,
        WorkerMessage<TPayload> message,
        int maxMessageBytes = WorkerProtocol.DefaultMaxMessageBytes,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(message, WorkerProtocol.JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        if (bytes.Length > maxMessageBytes)
            throw new WorkerMessageTooLargeException("Worker 消息超过大小限制。");
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async ValueTask<WorkerMessage<TPayload>> ReadAsync<TPayload>(
        Stream stream,
        int maxMessageBytes = WorkerProtocol.DefaultMaxMessageBytes,
        CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        var oneByte = new byte[1];
        while (true)
        {
            var count = await stream.ReadAsync(oneByte, cancellationToken);
            if (count == 0)
            {
                if (buffer.Length == 0)
                    throw new EndOfStreamException("Worker 通道已关闭。");
                throw new WorkerInvalidMessageException("Worker 消息缺少换行结束符。");
            }
            if (oneByte[0] == (byte)'\n')
                break;
            buffer.WriteByte(oneByte[0]);
            if (buffer.Length + 1 > maxMessageBytes)
                throw new WorkerMessageTooLargeException("Worker 消息超过大小限制。");
        }

        var json = Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
        try
        {
            return JsonSerializer.Deserialize<WorkerMessage<TPayload>>(json, WorkerProtocol.JsonOptions)
                ?? throw new WorkerInvalidMessageException("Worker 消息为空。");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new WorkerInvalidMessageException("Worker 消息不是有效 JSON。", exception);
        }
    }
}
