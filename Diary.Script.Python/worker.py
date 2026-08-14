import ast
import calendar
from datetime import datetime, timedelta
import inspect
import json
import os
import sys
import threading
import traceback
import uuid


PROTOCOL = "diary.script.worker"
VERSION = 1
# 消息大小层级：协议消息默认 4MB，执行结果 16MB（HelloAccepted 协商下发，
# 见 run() 中的 current_max_message_bytes / current_max_result_message_bytes）。
MAX_MESSAGE_BYTES = 4 * 1024 * 1024
PROTOCOL_OUTPUT = sys.stdout
OUTPUT_LOCK = threading.Lock()

current_max_message_bytes = MAX_MESSAGE_BYTES
current_max_result_message_bytes = MAX_MESSAGE_BYTES


class CancelledExecution(Exception):
    pass


def normalize_host_error_code(code):
    return {
        "InvalidInput": "INVALID_ARGUMENT",
        "PermissionDenied": "PERMISSION_DENIED",
        "DatabaseUnavailable": "SCRIPT_API_HOST_NOT_CONFIGURED",
        "ProviderFailure": "PROVIDER_FAILURE",
        "Cancelled": "CANCELLED",
        "InstanceUnavailable": "INSTANCE_UNAVAILABLE",
    }.get(code, code if isinstance(code, str) and code.isupper() else "PROVIDER_FAILURE")


class HostCallError(Exception):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = normalize_host_error_code(code)


class ExecutionState:
    def __init__(self, message):
        self.message = message
        self.cancelled = threading.Event()
        self.done = threading.Event()
        self.host_lock = threading.Lock()
        self.host_responses = {}

    def set_host_response(self, request_id, payload):
        with self.host_lock:
            self.host_responses[request_id] = payload

    def wait_host_response(self, request_id):
        while not self.cancelled.wait(0.05):
            with self.host_lock:
                if request_id in self.host_responses:
                    return self.host_responses.pop(request_id)
        raise CancelledExecution()


class WorkItemsApi:
    def __init__(self, state):
        self.state = state

    def query(self, params=None, **kwargs):
        if params is None:
            params = {}
        if kwargs:
            if not isinstance(params, dict):
                raise HostCallError("InvalidInput", "Query parameters must be an object.")
            params = dict(params)
            params.update(kwargs)
        if not isinstance(params, dict):
            raise HostCallError("InvalidInput", "Query parameters must be an object.")
        if self.state.cancelled.is_set():
            raise CancelledExecution()
        request_id = new_id()
        send_message("HostCall", request_id, get_execution_id(self.state.message), {
            "method": "workItems.query",
            "params": json_safe(params),
        })
        response = self.state.wait_host_response(request_id)
        if response.get("success"):
            return response.get("result")
        error = response.get("error") or {}
        raise HostCallError(error.get("code", "ProviderFailure"), error.get("message", "Host call failed."))

    def stream(self, params=None, pageSize=500, **kwargs):
        if pageSize < 1 or pageSize > 500:
            raise ValueError("pageSize must be between 1 and 500")
        query = {} if params is None else dict(params)
        query.update(kwargs)
        query.pop("pageSize", None)
        offset = query.get("offset", 0)
        while True:
            query["limit"] = pageSize
            query["offset"] = offset
            result = self.query(query)
            if not result.get("succeeded"):
                error = result.get("error") or {}
                raise HostCallError(error.get("code", "ProviderFailure"), error.get("message", "Host call failed."))
            items = result.get("items") or []
            for item in items:
                yield item
            if len(items) < pageSize:
                return
            offset += len(items)


class HostApi:
    def __init__(self, state, method):
        self.state = state
        self.method = method

    def __call__(self, params=None, **kwargs):
        params = {} if params is None else params
        if kwargs:
            if not isinstance(params, dict):
                raise HostCallError("InvalidInput", "Host parameters must be an object.")
            params = dict(params)
            params.update(kwargs)
        request_id = new_id()
        send_message("HostCall", request_id, get_execution_id(self.state.message), {
            "method": self.method, "params": json_safe(params),
        })
        response = self.state.wait_host_response(request_id)
        if response.get("success"):
            return response.get("result")
        error = response.get("error") or {}
        raise HostCallError(error.get("code", "ProviderFailure"), error.get("message", "Host call failed."))


class LogApi:
    def __init__(self, state):
        self._write = HostApi(state, "log.write")

    def _log(self, level, message):
        return self._write({"level": level, "message": str(message)})

    def debug(self, message):
        return self._log("Debug", message)

    def info(self, message):
        return self._log("Info", message)

    def warning(self, message):
        return self._log("Warning", message)

    def error(self, message):
        return self._log("Error", message)


class DiaryApi:
    def __init__(self, state):
        self.workItems = WorkItemsApi(state)
        self.logItems = type("LogItemsApi", (), {"create": HostApi(state, "logItems.create")})()
        self.templateLogItems = type("TemplateLogItemsApi", (), {"create": HostApi(state, "templateLogItems.create")})()
        self.trackerInstances = type("TrackerInstancesApi", (), {
            "get": HostApi(state, "trackerInstances.get"),
            "list": HostApi(state, "trackerInstances.list"),
        })()
        self.templates = type("TemplatesApi", (), {"list": HostApi(state, "templates.list")})()
        self.host = type("HostApi", (), {"list": HostApi(state, "host.capabilities.list")})()
        self.clipboard = ClipboardApi(state)
        self.ui = UiApi(state)
        self.log = LogApi(state)


class TargetItemsApi:
    def __init__(self, context):
        self.context = context

    def stream(self, params=None, **kwargs):
        date_range = self.context.dateRange
        if not date_range:
            raise HostCallError("InvalidInput", "当前目标没有日期范围。")
        query = {} if params is None else dict(params)
        query.update(kwargs)
        query["startDate"] = date_range["startDate"]
        query["endDate"] = date_range["endDate"]
        return self.context.diary.workItems.stream(query)


class ProgressApi:
    def __init__(self, state):
        self._report = HostApi(state, "script.progress")

    def report(self, fraction, message):
        if not isinstance(fraction, (int, float)) or fraction < 0 or fraction > 1:
            raise ValueError("fraction must be between 0 and 1")
        if not isinstance(message, str) or not message.strip():
            raise ValueError("message must not be empty")
        return self._report({"fraction": fraction, "message": message})


class ClipboardApi:
    def __init__(self, state):
        self._get = HostApi(state, "clipboard.get")
        self._set = HostApi(state, "clipboard.set")

    def get(self):
        return self._get({})

    def set(self, text):
        return self._set({"text": text})


class UiApi:
    def __init__(self, state):
        self._notify = HostApi(state, "ui.notify")
        self._confirm = HostApi(state, "ui.confirm")

    def notify(self, title, body):
        return self._notify({"title": title, "body": body})

    def confirm(self, title, body):
        return self._confirm({"title": title, "body": body})


class ScriptContext:
    def __init__(self, state, request):
        self.state = state
        self.request = request if isinstance(request, dict) else {}
        self.arguments = self.request.get("arguments") or {}
        self.target = self.request.get("target")
        self.source = self.request.get("source", "Unknown")
        self.entryKind = self.request.get("entryKind", "Application")
        self.idempotencyKey = self.request.get("idempotencyKey")
        self.preview = bool(self.request.get("preview", False))
        self.diary = DiaryApi(state)
        self.log = self.diary.log
        self.progress = ProgressApi(state)
        self.dateRange = resolve_date_range(self.target)
        self.workItem = self.target.get("workItem") if isinstance(self.target, dict) else None
        self.items = TargetItemsApi(self)
        trigger = "Scheduled" if self.source == "Automation" else "Startup" if self.source == "Startup" else "Unknown"
        self.automation = {
            "trigger": trigger,
            "eventData": dict(self.arguments or {}),
            "idempotencyKey": self.idempotencyKey,
        }

    def getDateRange(self):
        return self.dateRange

    def isCancelled(self):
        return self.state.cancelled.is_set()

    def __getitem__(self, key):
        if key == "diary":
            return self.diary
        if key == "request":
            return self.request
        if key == "arguments":
            return self.arguments
        if key == "target":
            return self.target
        if key == "source":
            return self.source
        if key == "entryKind":
            return self.entryKind
        if key == "idempotencyKey":
            return self.idempotencyKey
        if key == "preview":
            return self.preview
        if key == "dateRange":
            return self.dateRange
        if key == "workItem":
            return self.workItem
        if key == "items":
            return self.items
        if key == "log":
            return self.log
        if key == "progress":
            return self.progress
        if key == "automation":
            return self.automation
        raise KeyError(key)


def resolve_date_range(target):
    if not isinstance(target, dict):
        return None
    kind = target.get("kind")
    year = target.get("year")
    if kind == "Year" and isinstance(year, int):
        return {"startDate": f"{year:04d}-01-01", "endDate": f"{year:04d}-12-31"}
    if kind == "Quarter" and isinstance(year, int) and isinstance(target.get("quarter"), int):
        quarter = target["quarter"]
        if quarter not in range(1, 5):
            return None
        start_month = (quarter - 1) * 3 + 1
        end_month = start_month + 2
        return {
            "startDate": f"{year:04d}-{start_month:02d}-01",
            "endDate": f"{year:04d}-{end_month:02d}-{calendar.monthrange(year, end_month)[1]:02d}",
        }
    if kind == "Month" and isinstance(year, int) and isinstance(target.get("month"), int):
        month = target["month"]
        if month not in range(1, 13):
            return None
        return {
            "startDate": f"{year:04d}-{month:02d}-01",
            "endDate": f"{year:04d}-{month:02d}-{calendar.monthrange(year, month)[1]:02d}",
        }
    if kind == "Day" and isinstance(target.get("date"), str):
        return {"startDate": target["date"], "endDate": target["date"]}
    if kind == "Week" and isinstance(target.get("weekStart"), str):
        try:
            week_start = datetime.strptime(target["weekStart"], "%Y-%m-%d").date()
        except ValueError:
            return None
        if week_start.weekday() != 0:  # 周目标起始日期必须是周一
            return None
        week_end = week_start + timedelta(days=6)
        return {"startDate": target["weekStart"], "endDate": week_end.strftime("%Y-%m-%d")}
    return None


SAFE_BUILTINS = {
    "abs": abs,
    "all": all,
    "any": any,
    "bool": bool,
    "dict": dict,
    "enumerate": enumerate,
    "Exception": Exception,
    "HostCallError": HostCallError,
    "float": float,
    "int": int,
    "isinstance": isinstance,
    "len": len,
    "list": list,
    "max": max,
    "min": min,
    "next": next,
    "print": print,
    "range": range,
    "set": set,
    "sorted": sorted,
    "str": str,
    "sum": sum,
    "tuple": tuple,
    "type": type,
    "ValueError": ValueError,
    "RuntimeError": RuntimeError,
    "zip": zip,
}


FORBIDDEN_NAMES = {
    "__builtins__",
    "__import__",
    "__loader__",
    "__spec__",
    "breakpoint",
    "compile",
    "delattr",
    "eval",
    "exec",
    "getattr",
    "globals",
    "help",
    "input",
    "locals",
    "memoryview",
    "open",
    "quit",
    "setattr",
    "vars",
}


def new_id():
    return uuid.uuid4().hex


def get_execution_id(message):
    return message.get("executionId")


def json_safe(value):
    json.dumps(value)
    return value


def send_message(message_type, request_id, execution_id, payload, max_bytes=None):
    message = {
        "protocol": PROTOCOL,
        "version": VERSION,
        "type": message_type,
        "requestId": request_id,
        "executionId": execution_id,
        "payload": payload,
    }
    encoded = (json.dumps(message, ensure_ascii=True, separators=(",", ":")) + "\n").encode("utf-8")
    limit = max_bytes if max_bytes is not None else current_max_message_bytes
    if len(encoded) > limit:
        raise ValueError("Worker message is too large.")
    with OUTPUT_LOCK:
        PROTOCOL_OUTPUT.buffer.write(encoded)
        PROTOCOL_OUTPUT.flush()


def read_message():
    line = sys.stdin.buffer.readline(current_max_message_bytes + 1)
    if not line:
        return None
    if len(line) > current_max_message_bytes or not line.endswith(b"\n"):
        raise ValueError("Worker message is too large or missing a newline.")
    try:
        message = json.loads(line.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ValueError("Worker message is not valid JSON.") from error
    if not isinstance(message, dict):
        raise ValueError("Worker message must be an object.")
    return message


def validate_message(message):
    if message.get("protocol") != PROTOCOL or message.get("version") != VERSION:
        raise ValueError("Worker protocol mismatch.")
    if not isinstance(message.get("type"), str):
        raise ValueError("Worker message type is invalid.")


def source_diagnostics(source, source_path):
    try:
        tree = ast.parse(source, filename=source_path)
    except SyntaxError as error:
        return [{
            "code": "PYTHON_SYNTAX_ERROR",
            "message": error.msg,
            "severity": "Error",
            "category": "Syntax",
            "sourcePath": source_path,
            "line": error.lineno,
            "column": error.offset,
        }]

    diagnostics = []
    for node in ast.walk(tree):
        if isinstance(node, (ast.Import, ast.ImportFrom)):
            diagnostics.append({
                "code": "PYTHON_API_FORBIDDEN",
                "message": "Python scripts cannot import modules.",
                "severity": "Error",
                "category": "Security",
                "sourcePath": source_path,
                "line": node.lineno,
                "column": node.col_offset + 1,
            })
        elif isinstance(node, ast.Name) and node.id in FORBIDDEN_NAMES:
            diagnostics.append({
                "code": "PYTHON_API_FORBIDDEN",
                "message": "The Python script uses a forbidden runtime API.",
                "severity": "Error",
                "category": "Security",
                "sourcePath": source_path,
                "line": node.lineno,
                "column": node.col_offset + 1,
            })
        elif isinstance(node, ast.Attribute) and node.attr.startswith("__"):
            diagnostics.append({
                "code": "PYTHON_API_FORBIDDEN",
                "message": "The Python script uses a forbidden runtime attribute.",
                "severity": "Error",
                "category": "Security",
                "sourcePath": source_path,
                "line": node.lineno,
                "column": node.col_offset + 1,
            })
    unique = []
    seen = set()
    for diagnostic in diagnostics:
        key = (diagnostic["code"], diagnostic["line"], diagnostic["column"])
        if key not in seen:
            seen.add(key)
            unique.append(diagnostic)
    return unique


def create_trace(state):
    def trace(frame, event, argument):
        if state.cancelled.is_set():
            raise CancelledExecution()
        return trace
    return trace


def execute_script(state):
    message = state.message
    try:
        envelope = message.get("payload") or {}
        payload = envelope.get("payload") if isinstance(envelope, dict) else None
        if not isinstance(payload, dict):
            raise ValueError("Execute payload is invalid.")
        source = payload.get("source", "")
        source_path = payload.get("sourcePath", "<python-script>")
        if not isinstance(source, str) or not isinstance(source_path, str):
            raise ValueError("Execute source is invalid.")
        descriptor = payload.get("descriptorHint") or {}
        if not isinstance(descriptor, dict):
            send_result(message, "Rejected", [diagnostic("SCRIPT_DESCRIPTOR_INVALID", "Worker execution is missing a valid descriptor.", source_path, "Validation")])
            return
        request = payload.get("request") or {}
        target = request.get("target") if isinstance(request, dict) else None
        entry_kind = descriptor.get("entryKind")
        if not isinstance(entry_kind, str):
            entry_kind = "Editor" if descriptor.get("scope") == "Editor" else "Application"
        request_entry_kind = request.get("entryKind") if isinstance(request, dict) else None
        if request_entry_kind is not None and request_entry_kind != entry_kind:
            send_result(message, "Rejected", [diagnostic("SCRIPT_ENTRY_KIND_MISMATCH", "The execution entry does not match the script descriptor.", source_path, "Validation")])
            return
        if (entry_kind == "Editor") != isinstance(target, dict):
            send_result(message, "Rejected", [diagnostic("SCRIPT_TARGET_INVALID", "The execution target does not match the script entry.", source_path, "Validation")])
            return
        diagnostics = source_diagnostics(source, source_path)
        if diagnostics:
            send_result(message, "Failed", diagnostics)
            return

        namespace = {
            "__builtins__": SAFE_BUILTINS,
            "__name__": "__diary_script__",
        }
        code = compile(source, source_path, "exec")
        context = ScriptContext(state, payload.get("request"))
        entry_names = {
            "Application": "application_main",
            "Editor": "editor_main",
            "Automation": "automation_main",
            "Query": "query_main",
        }
        entry_name = entry_names.get(entry_kind)
        if entry_name is None:
            send_result(message, "Rejected", [diagnostic("SCRIPT_ENTRY_KIND_INVALID", "The script entry kind is invalid.", source_path, "Validation")])
            return
        with redirect_script_output(state):
            exec(code, namespace, namespace)
            entry = namespace.get(entry_name)
            if not callable(entry):
                send_result(message, "Failed", [{
                    "code": "PYTHON_ENTRYPOINT_MISSING",
                    "message": f"Python scripts must define {entry_name}(context).",
                    "severity": "Error",
                    "category": "Validation",
                    "sourcePath": source_path,
                }])
                return
            sys.settrace(create_trace(state))
            try:
                value = entry(context)
                if inspect.isawaitable(value):
                    raise RuntimeError("Async Python entry points are not supported by this worker.")
            finally:
                sys.settrace(None)
        json_safe(value)
        effects = value.get("effects") if isinstance(value, dict) else None
        send_result(message, "Succeeded", [], value, effects)
    except CancelledExecution:
        send_result(message, "Cancelled", [])
    except Exception as error:
        traceback.print_exc(file=sys.stderr)
        source_path = get_source_path(message)
        line, column = exception_location(error, source_path)
        diagnostic = {
            "code": "PYTHON_EXECUTION_FAILED",
            "message": str(error) or error.__class__.__name__,
            "severity": "Error",
            "category": "Runtime",
            "sourcePath": source_path,
            "line": line,
            "column": column,
        }
        if isinstance(error, HostCallError):
            diagnostic["code"] = "PYTHON_HOST_CALL_FAILED"
            diagnostic["category"] = "Host"
        send_result(message, "Failed", [diagnostic])
    finally:
        state.done.set()


def get_source_path(message):
    envelope = message.get("payload") or {}
    payload = envelope.get("payload") if isinstance(envelope, dict) else None
    return payload.get("sourcePath", "<python-script>") if isinstance(payload, dict) else "<python-script>"


def exception_location(error, source_path):
    if isinstance(error, SyntaxError):
        return error.lineno, error.offset
    locations = traceback.extract_tb(error.__traceback__)
    for location in reversed(locations):
        if location.filename == source_path:
            return location.lineno, None
    return None, None


MAX_SCRIPT_OUTPUT_BYTES = 1 * 1024 * 1024


class ScriptPrintWriter:
    """将脚本 print 按行转发到宿主脚本日志（Info 级）；总量 1MB 上限作安全兜底。"""

    def __init__(self, state):
        self.state = state
        self._log = LogApi(state)
        self._buffer = ""
        self._bytes = 0

    def write(self, text):
        if not isinstance(text, str):
            text = str(text)
        self._bytes += len(text.encode("utf-8"))
        if self._bytes > MAX_SCRIPT_OUTPUT_BYTES:
            raise ValueError("Script output exceeded the size limit.")
        self._buffer += text
        while "\n" in self._buffer:
            line, self._buffer = self._buffer.split("\n", 1)
            self._forward(line)

    def flush(self):
        if self._buffer:
            self._forward(self._buffer)
            self._buffer = ""

    def _forward(self, line):
        line = line.rstrip("\r")
        if not line:
            return
        # print 转发是尽力而为：log.write 未配置/失败时不因此让脚本失败。
        try:
            self._log.info(line)
        except Exception:
            pass


class redirect_script_output:
    def __init__(self, state):
        self.state = state

    def __enter__(self):
        self.previous = sys.stdout
        sys.stdout = ScriptPrintWriter(self.state)
        return self

    def __exit__(self, exception_type, exception, value):
        try:
            sys.stdout.flush()
        except Exception:
            pass
        sys.stdout = self.previous
        return False


def send_result(message, status, diagnostics, value=None, effects=None):
    payload = {"status": status, "diagnostics": diagnostics, "value": value}
    if effects is not None:
        payload["effects"] = effects
    try:
        send_message("ExecuteResult", message.get("requestId"), message.get("executionId"),
                     payload, current_max_result_message_bytes)
    except ValueError:
        send_message("ExecuteResult", message.get("requestId"), message.get("executionId"), {
            "status": "Failed",
            "diagnostics": [diagnostic("WORKER_RESULT_TOO_LARGE",
                                       "Worker execution result is too large.",
                                       get_source_path(message), "Runtime")],
            "value": None,
        })


def diagnostic(code, message, source_path, category):
    return {
        "code": code,
        "message": message,
        "severity": "Error",
        "category": category,
        "sourcePath": source_path,
    }


def run():
    send_message("Hello", new_id(), None, {
        "language": "python",
        "workerVersion": "0.3",
        "supportedApiVersions": ["V1"],
        "supportedHostApis": ["workItems.query", "logItems.create", "templateLogItems.create", "templates.list", "trackerInstances.get", "trackerInstances.list", "clipboard.get", "clipboard.set", "ui.notify", "ui.confirm", "log.write", "script.progress", "host.capabilities.list"],
        "processId": os.getpid(),
    })
    accepted = read_message()
    if accepted is None or accepted.get("type") != "HelloAccepted":
        return
    global current_max_message_bytes, current_max_result_message_bytes
    if isinstance(accepted.get("maxMessageBytes"), int) and accepted["maxMessageBytes"] > 0:
        current_max_message_bytes = accepted["maxMessageBytes"]
    if isinstance(accepted.get("maxResultMessageBytes"), int) and accepted["maxResultMessageBytes"] > 0:
        current_max_result_message_bytes = accepted["maxResultMessageBytes"]

    active = None
    while True:
        if active is not None and active.done.is_set():
            active = None
        message = read_message()
        if message is None:
            return
        validate_message(message)
        message_type = message["type"]
        if message_type == "Ping":
            send_message("Pong", message.get("requestId"), None, {})
        elif message_type == "Cancel":
            if active is not None and active.message.get("executionId") == message.get("executionId"):
                active.cancelled.set()
            else:
                send_result(message, "Cancelled", [])
        elif message_type == "HostResult":
            if active is not None and active.message.get("executionId") == message.get("executionId"):
                active.set_host_response(message.get("requestId"), message.get("payload") or {})
        elif message_type == "Execute":
            if active is not None and active.done.is_set():
                active = None
            if active is not None:
                send_message("Error", message.get("requestId"), message.get("executionId"), {
                    "code": "PYTHON_WORKER_BUSY",
                    "message": "The Python worker is already executing a script.",
                })
                continue
            active = ExecutionState(message)
            threading.Thread(target=execute_script, args=(active,), daemon=True).start()
        else:
            send_message("Error", message.get("requestId"), message.get("executionId"), {
                "code": "WORKER_PROTOCOL_UNSUPPORTED",
                "message": "The Python worker does not support this message type.",
            })
            return


if __name__ == "__main__":
    try:
        run()
    except Exception:
        traceback.print_exc(file=sys.stderr)
