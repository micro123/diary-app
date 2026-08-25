using System.Text.Json;
using System.Text;
using System.Threading.Channels;
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
    private readonly WorkerHostCallRouter _hostCalls = new(output);
    private Task? _activeExecution;
    private CancellationTokenSource? _activeCancellation;
    private string? _activeExecutionId;
    private int _maxMessageBytes = WorkerProtocol.DefaultMaxMessageBytes;
    private int _maxResultMessageBytes = WorkerProtocol.DefaultMaxResultMessageBytes;
    private readonly BoundedTextWriter _console = new(1 * 1024 * 1024);

    public async Task RunAsync()
    {
        var hello = new WorkerMessage<WorkerHelloPayload>(
            WorkerProtocol.Name,
            WorkerProtocol.Version,
            WorkerMessageType.Hello,
            Guid.NewGuid().ToString("N"),
            null,
            new("csharp", "0.6", [ScriptApiVersion.V1, ScriptApiVersion.V2], ScriptHostApiCatalog.All, Environment.ProcessId));
        Console.SetOut(_console);
        // 防止脚本读取 Worker stdin（协议通道）：Console 输入一律视为空流。
        Console.SetIn(TextReader.Null);
        await _hostCalls.WriteAsync(hello);
        var accepted = await WorkerMessageCodec.ReadAsync<WorkerHelloAcceptedPayload>(input);
        if (accepted.Type != WorkerMessageType.HelloAccepted)
            return;
        _maxMessageBytes = accepted.Payload.MaxMessageBytes;
        _maxResultMessageBytes = accepted.Payload.MaxResultMessageBytes;
        _hostCalls.MaxMessageBytes = _maxMessageBytes;

        var drainTask = DrainConsoleOutputAsync();
        try
        {
            await RunLoopAsync();
        }
        finally
        {
            _console.CompleteOutput();
            await drainTask;
        }
    }

    /// <summary>后台把脚本打印按行转发到宿主脚本日志；写入与宿主调用都在独立异步流上，避免阻塞读循环。</summary>
    private async Task DrainConsoleOutputAsync()
    {
        await foreach (var line in _console.Lines.ReadAllAsync())
        {
            try
            {
                var sink = _console.LineSink;
                if (sink is not null)
                    await sink(line);
            }
            catch (Exception)
            {
                // 打印转发尽力而为：失败不影响脚本执行。
            }
            finally
            {
                _console.MarkLineProcessed();
            }
        }
    }

    private async Task RunLoopAsync()
    {
        while (true)
        {
            WorkerMessage<JsonElement> message;
            try
            {
                message = await WorkerMessageCodec.ReadAsync<JsonElement>(input, _maxMessageBytes);
            }
            catch (EndOfStreamException)
            {
                _activeCancellation?.Cancel();
                return;
            }
            catch (WorkerProtocolDataException)
            {
                // 宿主违反协商上限或消息格式错误：直接退出，由宿主按通道断开处理。
                return;
            }

            switch (message.Type)
            {
                case WorkerMessageType.Ping:
                    await _hostCalls.WriteAsync(new WorkerMessage<object>(
                        WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.Pong,
                        message.RequestId, null, new { }));
                    break;
                case WorkerMessageType.HostResult:
                    _hostCalls.TryComplete(message);
                    break;
                case WorkerMessageType.Cancel:
                    if (_activeExecutionId == message.ExecutionId)
                    {
                        _activeCancellation?.Cancel();
                        _hostCalls.CancelExecution(message.ExecutionId ?? string.Empty);
                    }
                    break;
                case WorkerMessageType.Execute:
                    if (_activeExecution is { IsCompleted: false })
                    {
                        await _hostCalls.WriteAsync(new WorkerMessage<WorkerErrorPayload>(
                            WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.Error,
                            message.RequestId, message.ExecutionId,
                            new("WORKER_BUSY", "Worker 当前已有执行中的脚本。")));
                        break;
                    }
                    _activeExecutionId = message.ExecutionId;
                    _activeCancellation?.Dispose();
                    _activeCancellation = new CancellationTokenSource();
                    var cancellation = _activeCancellation;
                    _activeExecution = RunExecutionAsync(message, cancellation);
                    break;
                default:
                    await _hostCalls.WriteAsync(new WorkerMessage<WorkerErrorPayload>(
                        WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.Error,
                        message.RequestId, message.ExecutionId,
                        new("WORKER_PROTOCOL_UNSUPPORTED", "Worker 不支持此消息类型。")));
                    return;
            }
        }
    }

    private async Task RunExecutionAsync(WorkerMessage<JsonElement> message, CancellationTokenSource cancellation)
    {
        try
        {
            await ExecuteAsync(message, cancellation.Token);
        }
        finally
        {
            if (ReferenceEquals(_activeCancellation, cancellation))
            {
                _activeCancellation = null;
                _activeExecution = null;
                _activeExecutionId = null;
            }
            cancellation.Dispose();
        }
    }

    private async Task ExecuteAsync(WorkerMessage<JsonElement> message, CancellationToken cancellationToken)
    {
        // 执行期间脚本的 Console 输出经 Channel 转发到宿主脚本日志（Info 级），
        // 结果写出前冲刷并等待转发完成（WriteResultAsync 内）。
        _console.SetLineSink(ForwardConsoleLine);
        try
        {
            await ExecuteCoreAsync(message, cancellationToken);
        }
        finally
        {
            _console.SetLineSink(null);
        }
    }

    private async ValueTask ForwardConsoleLine(string line)
    {
        await CallHostAsync(new WorkerHostCallPayload(
            "log.write",
            JsonSerializer.SerializeToElement(new { level = "Info", message = line }, WorkerProtocol.JsonOptions)),
            CancellationToken.None);
    }

    private async Task ExecuteCoreAsync(WorkerMessage<JsonElement> message, CancellationToken cancellationToken)
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
                ApiVersion: payload.ApiVersion,
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
                ScriptAutomationContextFactory.FromRequest(payload.Request),
                async update =>
                {
                    var response = await CallHostAsync(new WorkerHostCallPayload(
                        "script.progress",
                        JsonSerializer.SerializeToElement(update, WorkerProtocol.JsonOptions)),
                        cancellationToken);
                    if (!response.Success)
                        throw new InvalidOperationException(response.Error?.Message ?? "脚本进度报告失败。");
                },
                cancellationToken);
            context.RegisterApi<IWorkItemQueryScriptApi>(queryApi);
            context.RegisterApi<ITrackerInstanceScriptApi>(new TrackerInstanceWorkerProxy(CallHostAsync));
            context.RegisterApi<ILogItemScriptApi>(new WorkerLogItemProxy(CallHostAsync));
            context.RegisterApi<ITemplateLogItemScriptApi>(new WorkerTemplateLogItemProxy(CallHostAsync));
            context.RegisterApi<ITemplateScriptApi>(new WorkerTemplateDiscoveryProxy(CallHostAsync));
            context.RegisterApi<IHostCapabilitiesScriptApi>(new WorkerHostCapabilitiesProxy(CallHostAsync));
            context.RegisterApi<ILogApi>(new WorkerScriptLogApi(CallHostAsync));
            context.RegisterApi<IClipboardScriptApi>(new WorkerClipboardProxy(CallHostAsync));
            context.RegisterApi<IUserInteractionScriptApi>(new WorkerUserInteractionProxy(CallHostAsync));
            context.RegisterApi<IFileInteractionApi>(new WorkerFileInteractionProxy(CallHostAsync));
            context.RegisterApi<IExportApi>(new WorkerExportProxy(CallHostAsync));
            context.RegisterApi<IDiaryApi>(new WorkerDiaryApiProxy(
                context.GetApi<IWorkItemQueryScriptApi>()!,
                context.GetApi<ILogItemScriptApi>()!,
                context.GetApi<ITemplateLogItemScriptApi>()!,
                context.GetApi<ITemplateScriptApi>()!,
                context.GetApi<IHostCapabilitiesScriptApi>()!));
            context.RegisterApi<ITrackerApi>(new WorkerTrackerApiProxy(context.GetApi<ITrackerInstanceScriptApi>()!));
            context.RegisterApi<SysApi>(new WorkerSystemInteractionApiProxy(
                context.GetApi<IClipboardScriptApi>()!,
                context.GetApi<IUserInteractionScriptApi>()!,
                context.GetApi<IFileInteractionApi>()!));
            var outcome = await _executor.ExecuteAsync(build.Program, payload.Request, context, cancellationToken: cancellationToken, executionId: executionId);
            await WriteResultAsync(message, new(outcome.Result.Status, outcome.Result.Diagnostics, DurationMilliseconds: (long)outcome.Duration.TotalMilliseconds, Effects: outcome.Result.Effects));
        }
        catch (Exception exception)
        {
            await WriteResultAsync(message, new(ScriptExecutionStatus.Failed, [new ScriptDiagnostic(
                "WORKER_EXECUTION_FAILED", exception.Message, ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Runtime)]));
        }
    }

    private async ValueTask WriteResultAsync(WorkerMessage<JsonElement> request, WorkerExecutionResultPayload payload)
    {
        // 先冲刷残余打印并等待转发完成，保证打印日志全部先于执行结果到达宿主。
        await _console.FlushAndDrainAsync();
        try
        {
            await _hostCalls.WriteAsync(new WorkerMessage<WorkerExecutionResultPayload>(
                WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.ExecuteResult,
                request.RequestId, request.ExecutionId, payload), _maxResultMessageBytes);
        }
        catch (WorkerMessageTooLargeException)
        {
            await _hostCalls.WriteAsync(new WorkerMessage<WorkerExecutionResultPayload>(
                WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.ExecuteResult,
                request.RequestId, request.ExecutionId,
                new(ScriptExecutionStatus.Failed, [new ScriptDiagnostic(
                    "WORKER_RESULT_TOO_LARGE", "Worker 执行结果超过大小限制。",
                    ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Runtime)])));
        }
    }

    private ValueTask<WorkerHostResultPayload> CallHostAsync(WorkerHostCallPayload call, CancellationToken cancellationToken) =>
        _hostCalls.CallAsync(call, _activeExecutionId ?? string.Empty, cancellationToken);
}

internal sealed record WorkerExecuteEnvelope(string ScriptId, JsonElement Payload);

/// <summary>
/// 替换 Worker 进程 Console.Out 的写入器：按行缓冲，脚本执行期间把完整行投递到
/// 内部 Channel，由后台 drainer 转发到宿主脚本日志（Info 级）；非执行期输出丢弃。
/// 转发必须异步化：脚本线程（可能是读循环线程）同步阻塞宿主调用会造成协议死锁。
/// 总量 1MB 上限作安全兜底。
/// </summary>
internal sealed class BoundedTextWriter(int maxBytes) : TextWriter
{
    private readonly StringBuilder _buffer = new();
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private Func<string, ValueTask>? _lineSink;
    private int _pendingLines;
    private int _bytes;
    public override Encoding Encoding => Encoding.UTF8;

    public ChannelReader<string> Lines => _lines.Reader;

    public Func<string, ValueTask>? LineSink
    {
        get
        {
            lock (_buffer)
            {
                return _lineSink;
            }
        }
    }

    /// <summary>设置行转发目标；null 表示丢弃输出（非执行期）。</summary>
    public void SetLineSink(Func<string, ValueTask>? sink)
    {
        lock (_buffer)
        {
            _lineSink = sink;
        }
    }

    public void CompleteOutput() => _lines.Writer.TryComplete();

    /// <summary>冲刷残余半行并等待 drainer 转发完所有已投递行（尽力而为，带时限）。</summary>
    public async Task FlushAndDrainAsync(TimeSpan? timeout = null)
    {
        lock (_buffer)
        {
            if (_buffer.Length > 0)
            {
                Enqueue(_buffer.ToString());
                _buffer.Clear();
            }
        }
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            lock (_buffer)
            {
                if (_pendingLines == 0)
                    return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(1));
        }
    }

    public override void Write(char value) => Add(value.ToString());

    public override void Write(string? value)
    {
        if (value is not null)
            Add(value);
    }

    public override void Flush()
    {
        lock (_buffer)
        {
            if (_buffer.Length > 0)
            {
                Enqueue(_buffer.ToString());
                _buffer.Clear();
            }
        }
    }

    public void MarkLineProcessed()
    {
        lock (_buffer)
        {
            if (_pendingLines > 0)
                _pendingLines--;
        }
    }

    private void Add(string value)
    {
        var bytes = Encoding.GetByteCount(value);
        if (Interlocked.Add(ref _bytes, bytes) > maxBytes)
            throw new InvalidDataException("Worker 标准输出超过大小限制。");

        lock (_buffer)
        {
            _buffer.Append(value);
            var text = _buffer.ToString();
            _buffer.Clear();
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n')
                    continue;
                var line = text[start..i].TrimEnd('\r');
                if (line.Length > 0)
                    Enqueue(line);
                start = i + 1;
            }
            if (start < text.Length)
                _buffer.Append(text, start, text.Length - start);
        }
    }

    private void Enqueue(string line)
    {
        if (_lines.Writer.TryWrite(line))
            _pendingLines++;
    }
}
