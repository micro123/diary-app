using Diary.ScriptHost;
using System.Text.Json;
using System.Text.RegularExpressions;
using Diary.Script.Lua;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using NLua;
using LuaState = NLua.Lua;

internal sealed class LuaWorker(Stream input, Stream output)
{
    private readonly LuaEngine _engine = new();
    private readonly WorkerHostCallRouter _hostCalls = new(output);
    private Task? _activeExecution;
    private CancellationTokenSource? _activeCancellation;
    private string? _activeExecutionId;

    public async Task RunAsync()
    {
        var hello = new WorkerMessage<WorkerHelloPayload>(
            WorkerProtocol.Name,
            WorkerProtocol.Version,
            WorkerMessageType.Hello,
            Guid.NewGuid().ToString("N"),
            null,
            new("lua", "0.3", [ScriptApiVersion.V1], ScriptHostApiCatalog.All, Environment.ProcessId));
        Console.SetOut(new BoundedTextWriter(1 * 1024 * 1024));
        await _hostCalls.WriteAsync(hello);
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
                _activeCancellation?.Cancel();
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
            lua.LoadString(payload.Source, payload.SourcePath);
            lua.DoString(payload.Source, payload.SourcePath);
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

            lua.NewTable("__diary_context");
            var context = (LuaTable)lua["__diary_context"];
            context["request"] = JsonToLua(JsonSerializer.SerializeToElement(payload.Request, WorkerProtocol.JsonOptions));
            context["arguments"] = JsonToLua(JsonSerializer.SerializeToElement(payload.Request.Arguments, WorkerProtocol.JsonOptions));
            entry.Call(context);
            return new(ScriptExecutionStatus.Succeeded, []);
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
        lua.DoString("io = nil; os = nil; debug = nil; package = nil; require = nil; dofile = nil; loadfile = nil; load = nil; loadstring = nil; import = nil; luanet = nil; clr = nil");
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
        lua["__diary_context_target"] = target;
        lua["__diary_context_date_range"] = dateRange;
        lua["__diary_context_work_item"] = workItem;
        lua["__diary_context_arguments"] = arguments;
        lua["__diary_context_entry_kind"] = request.EntryKind?.ToString() ?? "Application";
        lua["__diary_context_idempotency_key"] = request.IdempotencyKey;
        lua["__diary_context_preview"] = request.Preview;
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
        lua.DoString("diary = { workItems = { query = function(params) return __diary_work_items_query(params) end }, logItems = { create = function(params) return __diary_log_items_create(params) end }, templateLogItems = { create = function(params) return __diary_template_log_items_create(params) end }, templates = { list = function() return __diary_templates_list() end }, host = { list = function() return __diary_host_capabilities_list() end }, trackerInstances = { get = function(params) return __diary_tracker_get(params) end, list = function() return __diary_tracker_list() end }, clipboard = { get = function() return __diary_clipboard_get() end, set = function(text) return __diary_clipboard_set(text) end }, ui = { notify = function(title, body) return __diary_ui_notify(title, body) end, confirm = function(title, body) return __diary_ui_confirm(title, body) end }, log = { debug = function(message) return __diary_log_write('Debug', message) end, info = function(message) return __diary_log_write('Info', message) end, warning = function(message) return __diary_log_write('Warning', message) end, error = function(message) return __diary_log_write('Error', message) end } }; diary.workItems.stream = function(params) params = params or {}; local pageSize = params.pageSize or 500; if pageSize < 1 or pageSize > 500 then error('pageSize must be between 1 and 500') end; local offset = params.offset or 0; local page = {}; local index = 1; local finished = false; params.pageSize = nil; return function() while true do if index <= #page then local item = page[index]; index = index + 1; return item end; if finished then return nil end; params.limit = pageSize; params.offset = offset; local result = __diary_work_items_query(params); if not result.succeeded then error(result.error.message) end; page = result.items or {}; index = 1; offset = offset + #page; finished = #page < pageSize; end end end; __diary_context = {}; __diary_context.target = __diary_context_target; __diary_context.dateRange = __diary_context_date_range; __diary_context.workItem = __diary_context_work_item; __diary_context.arguments = __diary_context_arguments or {}; __diary_context.log = diary.log; __diary_context.progress = { report = function(fraction, message) return __diary_progress_report(fraction, message) end }; __diary_context.getDateRange = function() return __diary_context_date_range end; __diary_context.items = { stream = function(params) local range = __diary_context_date_range; if range == nil then error('当前目标没有日期范围') end; params = params or {}; params.startDate = range.startDate; params.endDate = range.endDate; return diary.workItems.stream(params) end };");
        lua.DoString("__diary_context.entryKind = __diary_context_entry_kind; __diary_context.idempotencyKey = __diary_context_idempotency_key; __diary_context.preview = __diary_context_preview;");
        return lua;
    }

    private ValueTask WriteResultAsync(WorkerMessage<JsonElement> request, WorkerExecutionResultPayload payload) =>
        _hostCalls.WriteAsync(new WorkerMessage<WorkerExecutionResultPayload>(
            WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.ExecuteResult,
            request.RequestId, request.ExecutionId, payload));

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
