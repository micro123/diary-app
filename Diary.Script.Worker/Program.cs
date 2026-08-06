using System.Text.Json;
using Diary.Script.CSharp;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Diary.ScriptHost;

var worker = new CSharpWorker(Console.OpenStandardInput(), Console.OpenStandardOutput());
await worker.RunAsync();

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
            new("csharp", "0.1", [ScriptApiVersion.V1], [], Environment.ProcessId));
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
            var build = await _engine.BuildAsync(new ScriptBuildRequest(payload.SourcePath, payload.Source));
            if (!build.Succeeded || build.Program is null)
            {
                await WriteResultAsync(message, new(ScriptExecutionStatus.Failed, build.Diagnostics));
                return;
            }

            var executionId = Guid.TryParse(message.ExecutionId, out var parsedId) ? parsedId : Guid.NewGuid();
            var metadata = new ScriptExecutionMetadata(executionId, DateTimeOffset.UtcNow, ScriptExecutionSource.Editor, payload.ScriptId);
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

internal sealed record WorkerExecutePayload(
    string ScriptId,
    string SourcePath,
    string Source,
    ScriptExecutionRequest Request,
    ScriptCapability GrantedCapabilities = ScriptCapability.None);

internal sealed record WorkerExecuteEnvelope(string ScriptId, JsonElement Payload);
