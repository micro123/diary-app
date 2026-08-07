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

    public async Task RunAsync()
    {
        var hello = new WorkerMessage<WorkerHelloPayload>(
            WorkerProtocol.Name,
            WorkerProtocol.Version,
            WorkerMessageType.Hello,
            Guid.NewGuid().ToString("N"),
            null,
            new("lua", "0.2", [ScriptApiVersion.V1], ["workItems.query", "logItems.create", "trackerInstances.get", "clipboard.get", "clipboard.set", "ui.notify", "ui.confirm"], Environment.ProcessId));
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
            if (payload.Request.Target is null || payload.Request.Target.Scope != hint.Scope.Value)
            {
                await WriteResultAsync(message, new(ScriptExecutionStatus.Rejected, [new ScriptDiagnostic(
                    "SCRIPT_TARGET_INVALID",
                    "执行目标与脚本 descriptor 不匹配。",
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
            var status = await ExecuteLuaAsync(payload, executionId);
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
        Guid executionId)
    {
        using var lua = CreateLua(executionId);
        try
        {
            lua.LoadString(payload.Source, payload.SourcePath);
            lua.DoString(payload.Source, payload.SourcePath);
            var entry = lua.GetFunction("main") ?? lua.GetFunction("execute");
            if (entry is null)
            {
                return new(ScriptExecutionStatus.Failed, [new ScriptDiagnostic(
                    "LUA_ENTRYPOINT_MISSING",
                    "Lua scripts must define main(context) or execute(context).",
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

    private LuaState CreateLua(Guid executionId)
    {
        var lua = new LuaState();
        lua.DoString("io = nil; os = nil; debug = nil; package = nil; require = nil; dofile = nil; loadfile = nil; load = nil; loadstring = nil; import = nil; luanet = nil; clr = nil");
        var bridge = new LuaHostBridge(CallHostAsync, executionId);
        lua.RegisterFunction("__diary_work_items_query", bridge, bridge.GetType().GetMethod(nameof(LuaHostBridge.Query))!);
        lua.RegisterFunction("__diary_log_items_create", bridge, bridge.GetType().GetMethod(nameof(LuaHostBridge.CreateLogItem))!);
        lua.RegisterFunction("__diary_tracker_get", bridge, bridge.GetType().GetMethod(nameof(LuaHostBridge.GetTracker))!);
        lua.RegisterFunction("__diary_clipboard_get", bridge, bridge.GetType().GetMethod(nameof(LuaHostBridge.GetClipboard))!);
        lua.RegisterFunction("__diary_clipboard_set", bridge, bridge.GetType().GetMethod(nameof(LuaHostBridge.SetClipboard))!);
        lua.RegisterFunction("__diary_ui_notify", bridge, bridge.GetType().GetMethod(nameof(LuaHostBridge.Notify))!);
        lua.RegisterFunction("__diary_ui_confirm", bridge, bridge.GetType().GetMethod(nameof(LuaHostBridge.Confirm))!);
        lua.DoString("diary = { workItems = { query = function(params) return __diary_work_items_query(params) end }, logItems = { create = function(params) return __diary_log_items_create(params) end }, trackerInstances = { get = function(params) return __diary_tracker_get(params) end }, clipboard = { get = function() return __diary_clipboard_get() end, set = function(text) return __diary_clipboard_set(text) end }, ui = { notify = function(title, body) return __diary_ui_notify(title, body) end, confirm = function(title, body) return __diary_ui_confirm(title, body) end } }; diary.workItems.stream = function(params) params = params or {}; local pageSize = params.pageSize or 500; if pageSize < 1 or pageSize > 500 then error('pageSize must be between 1 and 500') end; local offset = params.offset or 0; local page = {}; local index = 1; local finished = false; params.pageSize = nil; return function() while true do if index <= #page then local item = page[index]; index = index + 1; return item end; if finished then return nil end; params.limit = pageSize; params.offset = offset; local result = __diary_work_items_query(params); if not result.succeeded then error(result.error.message) end; page = result.items or {}; index = 1; offset = offset + #page; finished = #page < pageSize; end end end");
        return lua;
    }

    private Task WriteResultAsync(WorkerMessage<JsonElement> request, WorkerExecutionResultPayload payload) =>
        WorkerMessageCodec.WriteAsync(output, new WorkerMessage<WorkerExecutionResultPayload>(
            WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.ExecuteResult,
            request.RequestId, request.ExecutionId, payload)).AsTask();

    private async ValueTask<WorkerHostResultPayload> CallHostAsync(
        WorkerHostCallPayload call,
        string executionId,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        await WorkerMessageCodec.WriteAsync(output, new WorkerMessage<WorkerHostCallPayload>(
            WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.HostCall,
            requestId, executionId, call), cancellationToken: cancellationToken);
        while (true)
        {
            var response = await WorkerMessageCodec.ReadAsync<WorkerHostResultPayload>(input, cancellationToken: cancellationToken);
            if (response.Type == WorkerMessageType.HostResult && response.RequestId == requestId)
                return response.Payload;
        }
    }

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
        Guid executionId)
    {
        public object? Query(object? parameters)
            => Call("workItems.query", parameters is null ? new { } : parameters);

        public object? CreateLogItem(object? parameters) => Call("logItems.create", parameters ?? new { });
        public object? GetTracker(object? parameters) => Call("trackerInstances.get", parameters ?? new { });
        public object? GetClipboard() => Call("clipboard.get", new { });
        public object? SetClipboard(object? text) => Call("clipboard.set", new { text = text?.ToString() ?? string.Empty });
        public object? Notify(object? title, object? body) => Call("ui.notify", new { title = title?.ToString() ?? string.Empty, body = body?.ToString() ?? string.Empty });
        public object? Confirm(object? title, object? body) => Call("ui.confirm", new { title = title?.ToString() ?? string.Empty, body = body?.ToString() ?? string.Empty });

        private object? Call(string method, object parameters)
        {
            var json = JsonSerializer.SerializeToElement(parameters, WorkerProtocol.JsonOptions);
            var response = callHost(
                new WorkerHostCallPayload(method, json),
                executionId.ToString("N"),
                CancellationToken.None).GetAwaiter().GetResult();
            if (!response.Success)
                throw new InvalidOperationException(response.Error?.Message ?? "Worker 宿主查询失败。");
            return response.Result is { } result ? JsonToLua(result) : null;
        }
    }
}
