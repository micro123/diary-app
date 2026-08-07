using System.Text.Json;
using System.Text;
using Diary.Script.CSharp;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Diary.ScriptHost;

var language = GetLanguage(args);
if (language == "csharp")
{
    await new CSharpWorker(Console.OpenStandardInput(), Console.OpenStandardOutput()).RunAsync();
}
else if (language == "lua")
{
    await new LuaWorker(Console.OpenStandardInput(), Console.OpenStandardOutput()).RunAsync();
}
else
{
    throw new ArgumentException($"Unsupported worker language: {language}");
}

static string GetLanguage(string[] args)
{
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (string.Equals(args[index], "--language", StringComparison.OrdinalIgnoreCase))
            return args[index + 1].ToLowerInvariant();
    }

    return "csharp";
}

internal sealed class CSharpWorker(Stream input, Stream output)
{
    private readonly CSharpEngine _engine = new();
    private readonly ScriptExecutor _executor = new();

    public async Task RunAsync()
    {
        var hello = new WorkerMessage<WorkerHelloPayload>(
            WorkerProtocol.Name,
            WorkerProtocol.Version,
            WorkerMessageType.Hello,
            Guid.NewGuid().ToString("N"),
            null,
            new("csharp", "0.1", [ScriptApiVersion.V1], ["workItems.query"], Environment.ProcessId));
        Console.SetOut(new BoundedTextWriter(1 * 1024 * 1024));
        await WorkerMessageCodec.WriteAsync(output, hello);
        var accepted = await WorkerMessageCodec.ReadAsync<WorkerHelloAcceptedPayload>(input);
        if (accepted.Type != WorkerMessageType.HelloAccepted)
            return;

        while (true)
        {
            WorkerMessage<JsonElement> message;
            try
            {
                message = await WorkerMessageCodec.ReadAsync<JsonElement>(input);
            }
            catch (EndOfStreamException)
            {
                return;
            }

            switch (message.Type)
            {
                case WorkerMessageType.Ping:
                    await WorkerMessageCodec.WriteAsync(output, new WorkerMessage<object>(
                        WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.Pong,
                        message.RequestId, null, new { }));
                    break;
                case WorkerMessageType.Cancel:
                    await WorkerMessageCodec.WriteAsync(output, new WorkerMessage<WorkerExecutionResultPayload>(
                        WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.ExecuteResult,
                        message.RequestId, message.ExecutionId,
                        new(ScriptExecutionStatus.Cancelled, [])));
                    break;
                case WorkerMessageType.Execute:
                    await ExecuteAsync(message);
                    break;
                default:
                    await WorkerMessageCodec.WriteAsync(output, new WorkerMessage<WorkerErrorPayload>(
                        WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.Error,
                        message.RequestId, message.ExecutionId,
                        new("WORKER_PROTOCOL_UNSUPPORTED", "Worker 不支持此消息类型。")));
                    return;
            }
        }
    }

    private async Task ExecuteAsync(WorkerMessage<JsonElement> message)
    {
        try
        {
            var envelope = message.Payload.Deserialize<WorkerExecuteEnvelope>(WorkerProtocol.JsonOptions)
                ?? throw new InvalidDataException("Worker 执行载荷为空。");
            var payload = envelope.Payload.Deserialize<WorkerExecutePayload>(WorkerProtocol.JsonOptions)
                ?? throw new InvalidDataException("Worker 执行参数为空。");
            var build = await _engine.BuildAsync(new ScriptBuildRequest(
                payload.SourcePath,
                payload.Source,
                DescriptorHint: payload.DescriptorHint));
            if (!build.Succeeded || build.Program is null)
            {
                await WriteResultAsync(message, new(ScriptExecutionStatus.Failed, build.Diagnostics));
                return;
            }

            var executionId = Guid.TryParse(message.ExecutionId, out var parsedId) ? parsedId : Guid.NewGuid();
            var metadata = new ScriptExecutionMetadata(executionId, DateTimeOffset.UtcNow, payload.Request.Source, payload.ScriptId);
            var context = new ScriptExecutionContext(payload.GrantedCapabilities, metadata);
            if ((payload.GrantedCapabilities & ScriptCapability.ReadDiary) != 0)
                context.RegisterApi<IWorkItemQueryScriptApi>(new WorkerWorkItemQueryProxy(CallHostAsync), ScriptCapability.ReadDiary);
            var outcome = await _executor.ExecuteAsync(build.Program, payload.Request, context, cancellationToken: CancellationToken.None, executionId: executionId);
            await WriteResultAsync(message, new(outcome.Result.Status, outcome.Result.Diagnostics, DurationMilliseconds: (long)outcome.Duration.TotalMilliseconds));
        }
        catch (Exception exception)
        {
            await WriteResultAsync(message, new(ScriptExecutionStatus.Failed, [new ScriptDiagnostic(
                "WORKER_EXECUTION_FAILED", exception.Message, ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Runtime)]));
        }
    }

    private Task WriteResultAsync(WorkerMessage<JsonElement> request, WorkerExecutionResultPayload payload) =>
        WorkerMessageCodec.WriteAsync(output, new WorkerMessage<WorkerExecutionResultPayload>(
            WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.ExecuteResult,
            request.RequestId, request.ExecutionId, payload)).AsTask();

    private async ValueTask<WorkerHostResultPayload> CallHostAsync(WorkerHostCallPayload call, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        await WorkerMessageCodec.WriteAsync(output, new WorkerMessage<WorkerHostCallPayload>(
            WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.HostCall,
            requestId, null, call), cancellationToken: cancellationToken);
        while (true)
        {
            var response = await WorkerMessageCodec.ReadAsync<WorkerHostResultPayload>(input, cancellationToken: cancellationToken);
            if (response.Type == WorkerMessageType.HostResult && response.RequestId == requestId)
                return response.Payload;
        }
    }
}

internal sealed record WorkerExecuteEnvelope(string ScriptId, JsonElement Payload);

internal sealed class BoundedTextWriter(int maxBytes) : TextWriter
{
    private int _bytes;
    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value) => Add(value.ToString());
    public override void Write(string? value)
    {
        if (value is not null)
            Add(value);
    }

    private void Add(string value)
    {
        var bytes = Encoding.GetByteCount(value);
        if (Interlocked.Add(ref _bytes, bytes) > maxBytes)
            throw new InvalidDataException("Worker 标准输出超过大小限制。");
    }
}
