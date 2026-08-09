using System.Collections.Immutable;
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
             new("csharp", "0.4", [ScriptApiVersion.V1], ["workItems.query", "logItems.create", "templateLogItems.create", "trackerInstances.get", "clipboard.get", "clipboard.set", "ui.notify", "ui.confirm", "log.write"], Environment.ProcessId));
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
            var entryKind = payload.Request.EntryKind
                ?? ScriptEntryKindResolver.Resolve(build.Program.Descriptor);
            var metadata = new ScriptExecutionMetadata(
                executionId,
                DateTimeOffset.UtcNow,
                payload.Request.Source,
                payload.ScriptId,
                entryKind,
                payload.Request.IdempotencyKey,
                payload.Request.Preview);
            IWorkItemQueryScriptApi queryApi = new WorkerWorkItemQueryProxy(CallHostAsync);
            var context = new ScriptExecutionContext(
                metadata,
                payload.Request.Target,
                payload.Request.Arguments,
                (range, cancellationToken) => queryApi.StreamAsync(new ScriptWorkItemQuery
                {
                    StartDate = range.StartDate,
                    EndDate = range.EndDate,
                }, cancellationToken: cancellationToken),
                new ScriptAutomationContext(
                    payload.Request.Source == ScriptExecutionSource.Automation
                        ? ScriptAutomationTriggerKind.Scheduled
                        : ScriptAutomationTriggerKind.Unknown,
                    payload.Request.Arguments ?? ImmutableDictionary<string, string>.Empty,
                    payload.Request.IdempotencyKey),
                async update =>
                {
                    var response = await CallHostAsync(new WorkerHostCallPayload(
                        "script.progress",
                        JsonSerializer.SerializeToElement(update, WorkerProtocol.JsonOptions)),
                        CancellationToken.None);
                    if (!response.Success)
                        throw new InvalidOperationException(response.Error?.Message ?? "脚本进度报告失败。");
                },
                CancellationToken.None);
            context.RegisterApi<IWorkItemQueryScriptApi>(queryApi);
            context.RegisterApi<ITrackerInstanceScriptApi>(new TrackerInstanceWorkerProxy(CallHostAsync));
            context.RegisterApi<ILogItemScriptApi>(new WorkerLogItemProxy(CallHostAsync));
            context.RegisterApi<ITemplateLogItemScriptApi>(new WorkerTemplateLogItemProxy(CallHostAsync));
            context.RegisterApi<ILogApi>(new WorkerScriptLogApi(CallHostAsync));
            context.RegisterApi<IClipboardScriptApi>(new WorkerClipboardProxy(CallHostAsync));
            context.RegisterApi<IUserInteractionScriptApi>(new WorkerUserInteractionProxy(CallHostAsync));
            context.RegisterApi<IDiaryApi>(new WorkerDiaryApiProxy(
                context.GetApi<IWorkItemQueryScriptApi>()!,
                context.GetApi<ILogItemScriptApi>()!,
                context.GetApi<ITemplateLogItemScriptApi>()!));
            context.RegisterApi<ITrackerApi>(new WorkerTrackerApiProxy(context.GetApi<ITrackerInstanceScriptApi>()!));
            context.RegisterApi<SysApi>(new WorkerSystemInteractionApiProxy(
                context.GetApi<IClipboardScriptApi>()!, context.GetApi<IUserInteractionScriptApi>()!));
            var outcome = await _executor.ExecuteAsync(build.Program, payload.Request, context, cancellationToken: CancellationToken.None, executionId: executionId);
            await WriteResultAsync(message, new(outcome.Result.Status, outcome.Result.Diagnostics, DurationMilliseconds: (long)outcome.Duration.TotalMilliseconds, Effects: outcome.Result.Effects));
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
