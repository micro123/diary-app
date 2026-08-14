using Diary.ScriptHost;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Diary.Script.Lua;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using NLua;
using LuaState = NLua.Lua;

internal sealed class LuaWorker(Stream input, Stream output)
{
    private const string BootstrapResourceName = "Diary.Script.Worker.lua-bootstrap.lua";
    private static readonly Lazy<string> Bootstrap = new(LoadBootstrap);

    private readonly LuaEngine _engine = new();
    private readonly WorkerHostCallRouter _hostCalls = new(output);
    private Task? _activeExecution;
    private CancellationTokenSource? _activeCancellation;
    private string? _activeExecutionId;
    private int _maxMessageBytes = WorkerProtocol.DefaultMaxMessageBytes;
    private int _maxResultMessageBytes = WorkerProtocol.DefaultMaxResultMessageBytes;
    private ScriptApiVersion _negotiatedApiVersion = ScriptApiVersion.V1;

    public async Task RunAsync()
    {
        var hello = new WorkerMessage<WorkerHelloPayload>(
            WorkerProtocol.Name,
            WorkerProtocol.Version,
            WorkerMessageType.Hello,
            Guid.NewGuid().ToString("N"),
            null,
            new("lua", "0.4", [ScriptApiVersion.V1], ScriptHostApiCatalog.All, Environment.ProcessId));
        Console.SetOut(new BoundedTextWriter(1 * 1024 * 1024));
        // 防止脚本读取 Worker stdin（协议通道）：Console 输入一律视为空流。
        Console.SetIn(TextReader.Null);
        await _hostCalls.WriteAsync(hello);
        var accepted = await WorkerMessageCodec.ReadAsync<WorkerHelloAcceptedPayload>(input);
        if (accepted.Type != WorkerMessageType.HelloAccepted)
            return;
        _maxMessageBytes = accepted.Payload.MaxMessageBytes;
        _maxResultMessageBytes = accepted.Payload.MaxResultMessageBytes;
        _negotiatedApiVersion = accepted.Payload.ApiVersion;
        _hostCalls.MaxMessageBytes = _maxMessageBytes;

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
            await Task.Run(
                () => ExecuteAsync(message, cancellation.Token),
                CancellationToken.None);
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
        try
        {
            var envelope = message.Payload.Deserialize<WorkerExecuteEnvelope>(WorkerProtocol.JsonOptions)
                ?? throw new InvalidDataException("Worker 执行载荷为空。");
            var payload = envelope.Payload.Deserialize<WorkerExecutePayload>(WorkerProtocol.JsonOptions)
                ?? throw new InvalidDataException("Worker 执行参数为空。");
            var hint = payload.DescriptorHint;
            if (hint is null || hint.Scope is null)
            {
                await WriteResultAsync(message, new(ScriptExecutionStatus.Rejected, [new ScriptDiagnostic(
                    "SCRIPT_DESCRIPTOR_INVALID",
                    "Worker 执行缺少有效的脚本 descriptor。",
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCategory.Validation,
                    payload.SourcePath)]));
                return;
            }
            var entryKind = hint.EntryKind
                ?? (hint.Scope == ScriptScope.Editor ? ScriptEntryKind.Editor : ScriptEntryKind.Application);
            if (payload.Request.EntryKind is { } requestEntryKind && requestEntryKind != entryKind)
            {
                await WriteResultAsync(message, new(ScriptExecutionStatus.Rejected, [new ScriptDiagnostic(
                    "SCRIPT_ENTRY_KIND_MISMATCH",
                    "执行入口与脚本 descriptor 不匹配。",
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCategory.Validation,
                    payload.SourcePath)]));
                return;
            }
            if ((entryKind == ScriptEntryKind.Editor) != (payload.Request.Target is not null))
            {
                await WriteResultAsync(message, new(ScriptExecutionStatus.Rejected, [new ScriptDiagnostic(
                    "SCRIPT_TARGET_INVALID",
                    "执行目标与脚本入口不匹配。",
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCategory.Validation,
                    payload.SourcePath)]));
                return;
            }
            var build = await _engine.BuildAsync(new ScriptBuildRequest(
                payload.SourcePath,
                payload.Source,
                ApiVersion: _negotiatedApiVersion,
                DescriptorHint: payload.DescriptorHint));
            if (!build.Succeeded || build.Program is null)
            {
                await WriteResultAsync(message, new(ScriptExecutionStatus.Failed, build.Diagnostics));
                return;
            }

            var executionId = Guid.TryParse(message.ExecutionId, out var parsedId) ? parsedId : Guid.NewGuid();
            var status = await ExecuteLuaAsync(payload, executionId, entryKind, cancellationToken);
            await WriteResultAsync(message, status);
        }
        catch (Exception exception)
        {
            await WriteResultAsync(message, new(ScriptExecutionStatus.Failed, [new ScriptDiagnostic(
                "LUA_EXECUTION_FAILED", exception.Message, ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Runtime)]));
        }
    }

    private async ValueTask<WorkerExecutionResultPayload> ExecuteLuaAsync(
        WorkerExecutePayload payload,
        Guid executionId,
        ScriptEntryKind entryKind,
        CancellationToken cancellationToken)
    {
        using var lua = CreateLua(executionId, payload.Request, cancellationToken);
        try
        {
            // NLua 的 string 重载按系统 ANSI 编码转换，中文会被替换成 ?；统一用 UTF-8 byte[] 重载。
            var sourceBytes = Encoding.UTF8.GetBytes(payload.Source);
            lua.LoadString(sourceBytes, payload.SourcePath);
            lua.DoString(sourceBytes, payload.SourcePath);
            var entry = lua.GetFunction(GetEntryFunctionName(entryKind));
            if (entry is null)
            {
                return new(ScriptExecutionStatus.Failed, [new ScriptDiagnostic(
                    "LUA_ENTRYPOINT_MISSING",
                    $"Lua scripts must define {GetEntryFunctionName(entryKind)}(context).",
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCategory.Validation,
                    payload.SourcePath)]);
            }

            var context = (LuaTable?)lua["__diary_context"]
                ?? throw new InvalidOperationException("Lua 执行上下文初始化失败。");
            context["request"] = JsonToLua(JsonSerializer.SerializeToElement(payload.Request, WorkerProtocol.JsonOptions));
            context["arguments"] = JsonToLua(JsonSerializer.SerializeToElement(payload.Request.Arguments, WorkerProtocol.JsonOptions));
            var returns = entry.Call(context);
            return new(ScriptExecutionStatus.Succeeded, [], Effects: ExtractEffects(returns));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(ScriptExecutionStatus.Cancelled, []);
        }
        catch (Exception exception)
        {
            var location = ParseLocation(exception.Message);
            return new(ScriptExecutionStatus.Failed, [new ScriptDiagnostic(
                "LUA_EXECUTION_FAILED",
                exception.Message,
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Runtime,
                payload.SourcePath,
                location.Line,
                location.Column)]);
        }
    }

    private LuaState CreateLua(Guid executionId, ScriptExecutionRequest request, CancellationToken cancellationToken)
    {
        var lua = new LuaState();
        // KeraLua 默认使用 ASCII 做字符串双向转换，中文会被替换成 ?；统一改为 UTF-8。
        lua.State.Encoding = Encoding.UTF8;
        // 沙箱限制与 API 门面统一在嵌入资源 lua-bootstrap.lua 中，注册完成后一次性执行。
        var bridge = new LuaHostBridge(CallHostAsync, executionId, cancellationToken);
        var target = request.Target is null
            ? null
            : JsonToLua(JsonSerializer.SerializeToElement(request.Target, WorkerProtocol.JsonOptions));
        var dateRange = request.Target is null
            ? null
            : JsonToLua(JsonSerializer.SerializeToElement(
                ScriptEditorTargetResolver.GetDateRange(request.Target), WorkerProtocol.JsonOptions));
        var workItem = request.Target?.WorkItem is null
            ? null
            : JsonToLua(JsonSerializer.SerializeToElement(request.Target.WorkItem, WorkerProtocol.JsonOptions));
        var arguments = JsonToLua(JsonSerializer.SerializeToElement(request.Arguments, WorkerProtocol.JsonOptions));
        var automation = ScriptAutomationContextFactory.FromRequest(request);
        lua["__diary_context_target"] = target;
        lua["__diary_context_date_range"] = dateRange;
        lua["__diary_context_work_item"] = workItem;
        lua["__diary_context_arguments"] = arguments;
        lua["__diary_context_entry_kind"] = request.EntryKind?.ToString() ?? "Application";
        lua["__diary_context_idempotency_key"] = request.IdempotencyKey;
        lua["__diary_context_preview"] = request.Preview;
        lua["__diary_context_automation_trigger"] = automation.Trigger.ToString();
        lua["__diary_context_automation_event_data"] = JsonToLua(
            JsonSerializer.SerializeToElement(automation.EventData, WorkerProtocol.JsonOptions));
        lua["__diary_context_automation_idempotency_key"] = automation.IdempotencyKey;
        lua.RegisterFunction("__diary_work_items_query", bridge, bridge.GetType().GetMethod(nameof(LuaHostBridge.Query))!);
        lua.RegisterFunction("__diary_log_items_create", bridge, bridge.GetType().GetMethod(nameof(LuaHostBridge.CreateLogItem))!);
        lua.RegisterFunction("__diary_template_log_items_create", bridge, bridge.GetType().GetMethod(nameof(LuaHostBridge.CreateTemplateLogItem))!);
        lua.RegisterFunction("__diary_tracker_get", bridge, bridge.GetType().GetMethod(nameof(LuaHostBridge.GetTracker))!);
        lua.RegisterFunction("__diary_tracker_list", bridge, bridge.GetType().GetMethod(nameof(LuaHostBridge.ListTrackers))!);
        lua.RegisterFunction("__diary_templates_list", bridge, bridge.GetType().GetMethod(nameof(LuaHostBridge.ListTemplates))!);
        lua.RegisterFunction("__diary_host_capabilities_list", bridge, bridge.GetType().GetMethod(nameof(LuaHostBridge.ListHostCapabilities))!);
        lua.RegisterFunction("__diary_clipboard_get", bridge, bridge.GetType().GetMethod(nameof(LuaHostBridge.GetClipboard))!);
        lua.RegisterFunction("__diary_clipboard_set", bridge, bridge.GetType().GetMethod(nameof(LuaHostBridge.SetClipboard))!);
        lua.RegisterFunction("__diary_ui_notify", bridge, bridge.GetType().GetMethod(nameof(LuaHostBridge.Notify))!);
        lua.RegisterFunction("__diary_ui_confirm", bridge, bridge.GetType().GetMethod(nameof(LuaHostBridge.Confirm))!);
        lua.RegisterFunction("__diary_log_write", bridge, bridge.GetType().GetMethod(nameof(LuaHostBridge.Log))!);
        lua.RegisterFunction("__diary_progress_report", bridge, bridge.GetType().GetMethod(nameof(LuaHostBridge.Progress))!);
        lua.RegisterFunction("__diary_is_cancelled", bridge, bridge.GetType().GetMethod(nameof(LuaHostBridge.IsCancelled))!);
        // NLua 的 string 重载按系统 ANSI 编码转换，中文会被替换成 ?；统一用 UTF-8 byte[] 重载。
        lua.DoString(Encoding.UTF8.GetBytes(Bootstrap.Value), BootstrapResourceName);
        return lua;
    }

    private static string LoadBootstrap()
    {
        using var stream = typeof(LuaWorker).Assembly.GetManifestResourceStream(BootstrapResourceName)
            ?? throw new InvalidOperationException("Lua 引导脚本资源缺失。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>入口返回值若为宿主 API 结果表（含 effects 键），提取其中的 effects 供宿主展示。</summary>
    private static ScriptEffectSummary? ExtractEffects(object[]? returns)
    {
        if (returns is not { Length: > 0 })
            return null;
        var normalized = NormalizeLuaValue(returns[0]);
        if (normalized is not Dictionary<string, object?> table
            || !table.TryGetValue("effects", out var effects)
            || effects is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<ScriptEffectSummary>(
                JsonSerializer.SerializeToElement(effects, WorkerProtocol.JsonOptions),
                WorkerProtocol.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>把 NLua 返回的 LuaTable/数组规范化为 Dictionary 与 object[]，便于 JSON 序列化。</summary>
    private static object? NormalizeLuaValue(object? value)
    {
        switch (value)
        {
            case LuaTable table:
                var keys = table.Keys.Cast<object>().ToArray();
                var isArray = keys.Length > 0
                    && keys.All(key => key is double or long or int)
                    && keys.Select(key => Convert.ToInt32(key)).OrderBy(index => index)
                        .SequenceEqual(Enumerable.Range(1, keys.Length));
                if (isArray)
                    return keys.OrderBy(key => Convert.ToInt32(key))
                        .Select(key => NormalizeLuaValue(table[key]))
                        .ToArray();
                var dictionary = new Dictionary<string, object?>();
                foreach (var key in keys)
                    dictionary[key?.ToString() ?? string.Empty] = NormalizeLuaValue(table[key]);
                return dictionary;
            case object[] array:
                return array.Select(NormalizeLuaValue).ToArray();
            case Dictionary<string, object?> nested:
                return nested.ToDictionary(pair => pair.Key, pair => NormalizeLuaValue(pair.Value));
            default:
                return value;
        }
    }

    private async ValueTask WriteResultAsync(WorkerMessage<JsonElement> request, WorkerExecutionResultPayload payload)
    {
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

    private ValueTask<WorkerHostResultPayload> CallHostAsync(
        WorkerHostCallPayload call,
        string executionId,
        CancellationToken cancellationToken = default) =>
        _hostCalls.CallAsync(call, executionId, cancellationToken);

    private static object? JsonToLua(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(item => item.Name, item => JsonToLua(item.Value)),
        JsonValueKind.Array => value.EnumerateArray().Select(JsonToLua).ToArray(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static string GetEntryFunctionName(ScriptEntryKind entryKind) =>
        entryKind switch
        {
            ScriptEntryKind.Application => "application_main",
            ScriptEntryKind.Editor => "editor_main",
            ScriptEntryKind.Automation => "automation_main",
            ScriptEntryKind.Query => "query_main",
            _ => throw new ArgumentOutOfRangeException(nameof(entryKind)),
        };

    private static (int? Line, int? Column) ParseLocation(string message)
    {
        var match = Regex.Match(message, @":(?<line>\d+)(?::(?<column>\d+))?(?:[:\s]|$)", RegexOptions.CultureInvariant);
        return match.Success
            ? (int.Parse(match.Groups["line"].Value), match.Groups["column"].Success
                ? int.Parse(match.Groups["column"].Value)
                : null)
            : (null, null);
    }

    private sealed class LuaHostBridge(
        Func<WorkerHostCallPayload, string, CancellationToken, ValueTask<WorkerHostResultPayload>> callHost,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        public object? Query(object? parameters)
            => Call("workItems.query", parameters is null ? new { } : parameters);

        public object? CreateLogItem(object? parameters) => Call("logItems.create", parameters ?? new { });
        public object? CreateTemplateLogItem(object? parameters) => Call("templateLogItems.create", parameters ?? new { });
        public object? GetTracker(object? parameters) => Call("trackerInstances.get", parameters ?? new { });
        public object? ListTrackers() => Call("trackerInstances.list", new { });
        public object? ListTemplates() => Call("templates.list", new { });
        public object? ListHostCapabilities() => Call("host.capabilities.list", new { });
        public object? GetClipboard() => Call("clipboard.get", new { });
        public object? SetClipboard(object? text) => Call("clipboard.set", new { text = text?.ToString() ?? string.Empty });
        public object? Notify(object? title, object? body) => Call("ui.notify", new { title = title?.ToString() ?? string.Empty, body = body?.ToString() ?? string.Empty });
        public object? Confirm(object? title, object? body) => Call("ui.confirm", new { title = title?.ToString() ?? string.Empty, body = body?.ToString() ?? string.Empty });
        public object? Log(object? level, object? message) => Call("log.write", new { level = level?.ToString() ?? "Info", message = message?.ToString() ?? string.Empty });
        public object? Progress(object? fraction, object? message) => Call("script.progress", new { fraction = Convert.ToDouble(fraction ?? 0), message = message?.ToString() ?? string.Empty });
        public bool IsCancelled() => cancellationToken.IsCancellationRequested;

        private static string NormalizeHostErrorCode(string? code) => code switch
        {
            "InvalidInput" => "INVALID_ARGUMENT",
            "PermissionDenied" => "PERMISSION_DENIED",
            "DatabaseUnavailable" => "SCRIPT_API_HOST_NOT_CONFIGURED",
            "ProviderFailure" => "PROVIDER_FAILURE",
            "Cancelled" => "CANCELLED",
            "InstanceUnavailable" => "INSTANCE_UNAVAILABLE",
            _ when !string.IsNullOrWhiteSpace(code) && code!.ToUpperInvariant() == code => code,
            _ => "PROVIDER_FAILURE",
        };

        private object? Call(string method, object parameters)
        {
            var json = JsonSerializer.SerializeToElement(parameters, WorkerProtocol.JsonOptions);
            var response = callHost(
                new WorkerHostCallPayload(method, json),
                executionId.ToString("N"),
                cancellationToken).GetAwaiter().GetResult();
            if (!response.Success)
            {
                var code = NormalizeHostErrorCode(response.Error?.Code);
                var message = response.Error?.Message ?? "Worker 宿主调用失败。";
                throw new InvalidOperationException($"[{code}] {message}");
            }
            return response.Result is { } result ? JsonToLua(result) : null;
        }
    }
}
