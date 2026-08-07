import ast
import inspect
import json
import os
import sys
import threading
import traceback
import uuid


PROTOCOL = "diary.script.worker"
VERSION = 1
MAX_MESSAGE_BYTES = 4 * 1024 * 1024
PROTOCOL_OUTPUT = sys.stdout
OUTPUT_LOCK = threading.Lock()


class CancelledExecution(Exception):
    pass


class HostCallError(Exception):
    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


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


class DiaryApi:
    def __init__(self, state):
        self.workItems = WorkItemsApi(state)


class ScriptContext:
    def __init__(self, state, request):
        self.request = request if isinstance(request, dict) else {}
        self.arguments = self.request.get("arguments") or {}
        self.target = self.request.get("target")
        self.source = self.request.get("source", "Unknown")
        self.diary = DiaryApi(state)

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
        raise KeyError(key)


SAFE_BUILTINS = {
    "abs": abs,
    "all": all,
    "any": any,
    "bool": bool,
    "dict": dict,
    "enumerate": enumerate,
    "Exception": Exception,
    "float": float,
    "int": int,
    "isinstance": isinstance,
    "len": len,
    "list": list,
    "max": max,
    "min": min,
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


def send_message(message_type, request_id, execution_id, payload):
    message = {
        "protocol": PROTOCOL,
        "version": VERSION,
        "type": message_type,
        "requestId": request_id,
        "executionId": execution_id,
        "payload": payload,
    }
    encoded = (json.dumps(message, ensure_ascii=True, separators=(",", ":")) + "\n").encode("utf-8")
    if len(encoded) > MAX_MESSAGE_BYTES:
        raise ValueError("Worker message is too large.")
    with OUTPUT_LOCK:
        PROTOCOL_OUTPUT.buffer.write(encoded)
        PROTOCOL_OUTPUT.flush()


def read_message():
    line = sys.stdin.buffer.readline(MAX_MESSAGE_BYTES + 1)
    if not line:
        return None
    if len(line) > MAX_MESSAGE_BYTES or not line.endswith(b"\n"):
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
        if not isinstance(target, dict) or target.get("scope") != descriptor.get("scope"):
            send_result(message, "Rejected", [diagnostic("SCRIPT_TARGET_INVALID", "The execution target does not match the script descriptor.", source_path, "Validation")])
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
        with redirect_script_output():
            exec(code, namespace, namespace)
            entry = namespace.get("main") or namespace.get("execute")
            if not callable(entry):
                send_result(message, "Failed", [{
                    "code": "PYTHON_ENTRYPOINT_MISSING",
                    "message": "Python scripts must define main(context) or execute(context).",
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
        send_result(message, "Succeeded", [], value)
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


class redirect_script_output:
    def __enter__(self):
        self.previous = sys.stdout
        sys.stdout = sys.stderr
        return self

    def __exit__(self, exception_type, exception, value):
        sys.stdout = self.previous
        return False


def send_result(message, status, diagnostics, value=None):
    send_message("ExecuteResult", message.get("requestId"), message.get("executionId"), {
        "status": status,
        "diagnostics": diagnostics,
        "value": value,
    })


def diagnostic(code, message, source_path, category):
    return {
        "code": code,
        "message": message,
        "severity": "Error",
        "category": category,
        "sourcePath": source_path,
    }


def capability_flags(value):
    if isinstance(value, int):
        return value
    if not isinstance(value, str):
        return 0
    flags = 0
    for name in value.replace(",", " ").split():
        flags |= {
            "None": 0,
            "ReadDiary": 1,
            "WriteDiary": 2,
            "UserInteraction": 4,
            "Clipboard": 8,
            "Tracker": 16,
        }.get(name, 0)
    return flags


def run():
    send_message("Hello", new_id(), None, {
        "language": "python",
        "workerVersion": "0.1",
        "supportedApiVersions": ["V1"],
        "supportedHostApis": ["workItems.query"],
        "processId": os.getpid(),
    })
    accepted = read_message()
    if accepted is None or accepted.get("type") != "HelloAccepted":
        return

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
