#!/usr/bin/env bash

set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd -- "$script_directory/.." && pwd)"
state_directory="$repository_root/.build-tmp/ui-test"
state_path="$state_directory/current.json"
app_path="$repository_root/Diary.App/bin/Debug/net10.0/Diary.App"

command_name="${1:-start}"
if (($# > 0)); then
    shift
fi

suite_name=""
if [[ "$command_name" == "run" && $# -gt 0 && "$1" != --* ]]; then
    suite_name="$1"
    shift
fi

port=9222
no_build=false
with_plugins=false
scenario="default"
seed_profile=""
profile_base="$state_directory/profiles"
display_value="${DISPLAY:-}"
force_xvfb=false

usage() {
    cat <<'EOF'
用法：
  Tools/ui-test.sh start [选项]
  Tools/ui-test.sh status
  Tools/ui-test.sh smoke
  Tools/ui-test.sh run <suite-name>
  Tools/ui-test.sh stop

start 选项：
  --port <port>             CDP 监听端口，默认 9222
  --no-build               跳过 Debug restore/build
  --with-plugins            加载 Tracker 插件
  --scenario <name>         default、extended、survey、database-error、extra-fields、date-performance、navigation-performance 或 plugins
  --seed-profile <path>     复制已有 profile 的加密配置文件
  --profile-base <path>     将隔离 profile 创建到指定磁盘目录，性能测试可指向 HDD
  --display <display>       使用指定 X11 DISPLAY，例如 :0
  --xvfb                    强制启动独立 Xvfb；未设置 DISPLAY 时会自动尝试

run 的 suite-name 可写 ui-core-full 或 ui-core-full.mjs。
EOF
}

fail() {
    echo "错误：$*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || fail "找不到 $1。"
}

while (($# > 0)); do
    case "$1" in
        --port)
            (($# >= 2)) || fail "--port 缺少值。"
            port="$2"
            shift 2
            ;;
        --no-build)
            no_build=true
            shift
            ;;
        --with-plugins)
            with_plugins=true
            shift
            ;;
        --scenario)
            (($# >= 2)) || fail "--scenario 缺少值。"
            scenario="$2"
            shift 2
            ;;
        --seed-profile)
            (($# >= 2)) || fail "--seed-profile 缺少值。"
            seed_profile="$2"
            shift 2
            ;;
        --profile-base)
            (($# >= 2)) || fail "--profile-base 缺少值。"
            profile_base="$2"
            shift 2
            ;;
        --display)
            (($# >= 2)) || fail "--display 缺少值。"
            display_value="$2"
            shift 2
            ;;
        --xvfb)
            force_xvfb=true
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            fail "无法识别的参数：$1"
            ;;
    esac
done

case "$command_name" in
    start|stop|status|smoke|run) ;;
    -h|--help)
        usage
        exit 0
        ;;
    *) fail "无法识别的命令：$command_name" ;;
esac

if [[ ! "$port" =~ ^[0-9]+$ ]] || ((port < 1024 || port > 65535)); then
    fail "端口必须在 1024 到 65535 之间。"
fi

case "$scenario" in
    default|extended|survey|database-error|extra-fields|date-performance|navigation-performance|plugins) ;;
    *) fail "不支持的测试场景：$scenario" ;;
esac

json_value() {
    local key="$1"
    python3 - "$state_path" "$key" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    value = json.load(stream).get(sys.argv[2])
if value is not None:
    print(value)
PY
}

owned_app_running() {
    [[ -f "$state_path" ]] || return 1
    local pid expected actual
    pid="$(json_value processId)"
    expected="$(json_value appPath)"
    [[ "$pid" =~ ^[0-9]+$ ]] || return 1
    kill -0 "$pid" 2>/dev/null || return 1
    actual="$(readlink -f "/proc/$pid/exe" 2>/dev/null || true)"
    expected="$(readlink -f "$expected" 2>/dev/null || true)"
    [[ -n "$actual" && "$actual" == "$expected" ]] \
        || fail "PID $pid 不属于当前 UI 测试程序：${actual:-未知可执行文件}"
}

stop_pid() {
    local pid="$1"
    kill -0 "$pid" 2>/dev/null || return 0
    kill "$pid" 2>/dev/null || true
    for _ in {1..100}; do
        kill -0 "$pid" 2>/dev/null || return 0
        sleep 0.1
    done
    kill -KILL "$pid" 2>/dev/null || true
}

stop_xvfb_from_state() {
    [[ -f "$state_path" ]] || return 0
    local pid executable
    pid="$(json_value displayProcessId)"
    [[ "$pid" =~ ^[0-9]+$ ]] || return 0
    kill -0 "$pid" 2>/dev/null || return 0
    executable="$(readlink -f "/proc/$pid/exe" 2>/dev/null || true)"
    [[ "${executable##*/}" == "Xvfb" ]] \
        || fail "PID $pid 不是当前工具启动的 Xvfb：${executable:-未知可执行文件}"
    stop_pid "$pid"
}

wait_for_cdp() {
    local target_port="$1"
    local app_pid="$2"
    python3 - "$target_port" "$app_pid" <<'PY'
import json
import os
import sys
import time
import urllib.error
import urllib.request

port = int(sys.argv[1])
pid = int(sys.argv[2])
deadline = time.monotonic() + 60
while time.monotonic() < deadline:
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        raise SystemExit("应用在 CDP 就绪前退出")
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/json", timeout=2) as response:
            targets = json.load(response)
        if targets:
            print(json.dumps(targets, ensure_ascii=False))
            raise SystemExit(0)
    except (OSError, urllib.error.URLError, json.JSONDecodeError):
        time.sleep(0.2)
raise SystemExit(f"CDP 服务 60 秒内未就绪：http://127.0.0.1:{port}")
PY
}

check_port_available() {
    python3 - "$port" <<'PY'
import socket
import sys

try:
    with socket.create_connection(("127.0.0.1", int(sys.argv[1])), timeout=0.5):
        raise SystemExit("CDP 端口已有服务监听")
except (ConnectionRefusedError, TimeoutError, OSError):
    pass
PY
}

start_xvfb() {
    require_command Xvfb
    local number socket_path
    for number in $(seq 90 199); do
        socket_path="/tmp/.X11-unix/X$number"
        [[ -e "$socket_path" || -e "/tmp/.X$number-lock" ]] && continue
        display_value=":$number"
        nohup setsid Xvfb "$display_value" -screen 0 1920x1080x24 -nolisten tcp \
            >"$xvfb_log_path" 2>&1 &
        xvfb_pid=$!
        for _ in {1..100}; do
            [[ -S "$socket_path" ]] && return 0
            kill -0 "$xvfb_pid" 2>/dev/null \
                || fail "Xvfb 启动失败，日志：$xvfb_log_path"
            sleep 0.05
        done
        stop_pid "$xvfb_pid"
        fail "Xvfb 未创建显示套接字：$socket_path"
    done
    fail "找不到可用的 Xvfb display。"
}

start_ui_test() {
    require_command python3
    require_command readlink
    require_command nohup
    require_command setsid

    if owned_app_running; then
        fail "UI 测试程序已运行：PID=$(json_value processId)，端口=$(json_value port)"
    fi
    if [[ -f "$state_path" ]]; then
        stop_xvfb_from_state
        rm -f -- "$state_path"
    fi

    check_port_available
    if [[ "$no_build" == false ]]; then
        require_command dotnet
        dotnet restore "$repository_root/Diary.App/Diary.App.csproj" -p:Configuration=Debug
        dotnet build "$repository_root/Diary.App/Diary.App.csproj" \
            --configuration Debug --no-restore
    fi
    [[ -x "$app_path" ]] || fail "找不到 Linux Debug App：$app_path"

    mkdir -p -- "$state_directory/profiles" "$state_directory/logs"
    local run_id profile app_log_path started_ms targets_json startup_ready_ms
    run_id="$(date +%Y%m%d%H%M%S)-$(python3 -c 'import uuid; print(uuid.uuid4().hex[:8])')"
    mkdir -p -- "$profile_base"
    profile_base="$(readlink -f "$profile_base")"
    profile="$profile_base/$run_id"
    app_log_path="$state_directory/logs/$run_id-app.log"
    xvfb_log_path="$state_directory/logs/$run_id-xvfb.log"
    mkdir -p -- "$profile"

    if [[ -n "$seed_profile" ]]; then
        seed_profile="$(readlink -f "$seed_profile")"
        [[ -d "$seed_profile/config" ]] \
            || fail "UI 测试种子 profile 缺少 config 目录：$seed_profile"
        mkdir -p -- "$profile/config"
        find "$seed_profile/config" -maxdepth 1 -type f -exec cp -- '{}' "$profile/config/" \;
    fi

    xvfb_pid=""
    app_pid=""
    cleanup_start_failure() {
        local exit_code=$?
        if ((exit_code != 0)); then
            [[ "$app_pid" =~ ^[0-9]+$ ]] && stop_pid "$app_pid"
            [[ "$xvfb_pid" =~ ^[0-9]+$ ]] && stop_pid "$xvfb_pid"
            echo "应用日志：$app_log_path" >&2
            [[ -s "$xvfb_log_path" ]] && echo "Xvfb 日志：$xvfb_log_path" >&2
        fi
        exit "$exit_code"
    }
    trap cleanup_start_failure EXIT

    if [[ "$force_xvfb" == true ]]; then
        display_value=""
        start_xvfb
    elif [[ -z "$display_value" ]]; then
        command -v Xvfb >/dev/null 2>&1 \
            || fail "未设置 DISPLAY 且找不到 Xvfb；请设置 --display 或安装 Xvfb。"
        start_xvfb
    fi

    local -a app_arguments=()
    if [[ "$with_plugins" == false ]]; then
        app_arguments+=(--core-only)
    fi
    started_ms="$(date +%s%3N)"
    (
        cd -- "$(dirname -- "$app_path")"
        exec nohup setsid env \
            DISPLAY="$display_value" \
            DIARY_CDP_PORT="$port" \
            DIARY_UI_TEST_ROOT="$profile" \
            DIARY_UI_TEST_SCENARIO="$([[ "$scenario" == default ]] || echo "$scenario")" \
            "$app_path" "${app_arguments[@]}"
    ) >"$app_log_path" 2>&1 </dev/null &
    app_pid=$!

    targets_json="$(wait_for_cdp "$port" "$app_pid")"
    startup_ready_ms=$(($(date +%s%3N) - started_ms))

    UI_PROCESS_ID="$app_pid" UI_APP_PATH="$app_path" UI_PORT="$port" \
    UI_PROFILE="$profile" UI_PROCESS_STARTED_MS="$started_ms" UI_STARTUP_MS="$startup_ready_ms" UI_PLUGINS="$with_plugins" \
    UI_SCENARIO="$scenario" UI_SEEDED="$([[ -n "$seed_profile" ]] && echo true || echo false)" \
    UI_DISPLAY="$display_value" UI_DISPLAY_PID="${xvfb_pid:-}" UI_LOG_PATH="$app_log_path" \
    UI_TARGETS="$targets_json" python3 - "$state_path" <<'PY'
import datetime
import json
import os
import sys

def boolean(name):
    return os.environ[name].lower() == "true"

state = {
    "processId": int(os.environ["UI_PROCESS_ID"]),
    "appPath": os.environ["UI_APP_PATH"],
    "port": int(os.environ["UI_PORT"]),
    "profile": os.environ["UI_PROFILE"],
    "startedAt": datetime.datetime.now().astimezone().isoformat(),
    "processStartedAtUnixMs": int(os.environ["UI_PROCESS_STARTED_MS"]),
    "startupReadyMs": int(os.environ["UI_STARTUP_MS"]),
    "withPlugins": boolean("UI_PLUGINS"),
    "scenario": os.environ["UI_SCENARIO"],
    "seeded": boolean("UI_SEEDED"),
    "platform": "linux",
    "display": os.environ["UI_DISPLAY"],
    "displayProcessId": int(os.environ["UI_DISPLAY_PID"]) if os.environ["UI_DISPLAY_PID"] else None,
    "logPath": os.environ["UI_LOG_PATH"],
}
with open(sys.argv[1], "w", encoding="utf-8") as stream:
    json.dump(state, stream, ensure_ascii=False, indent=2)

targets = json.loads(os.environ["UI_TARGETS"])
summary = {
    "status": "ready",
    **state,
    "targetCount": len(targets),
    "title": targets[0].get("title"),
    "webSocketDebuggerUrl": targets[0].get("webSocketDebuggerUrl"),
}
print(json.dumps(summary, ensure_ascii=False, indent=2))
PY
    trap - EXIT
}

stop_ui_test() {
    require_command python3
    require_command readlink
    if [[ ! -f "$state_path" ]]; then
        echo "UI 测试程序未运行。"
        return
    fi
    local pid
    pid="$(json_value processId)"
    if owned_app_running; then
        stop_pid "$pid"
    fi
    stop_xvfb_from_state
    rm -f -- "$state_path"
    echo "UI 测试程序已停止：PID=$pid"
}

show_status() {
    require_command python3
    require_command readlink
    if ! owned_app_running; then
        echo "UI 测试程序未运行。"
        return
    fi
    python3 - "$state_path" <<'PY'
import json
import sys
import urllib.request

with open(sys.argv[1], encoding="utf-8") as stream:
    state = json.load(stream)
with urllib.request.urlopen(f"http://127.0.0.1:{state['port']}/json", timeout=3) as response:
    targets = json.load(response)
print(json.dumps({
    "status": "ready",
    "processId": state["processId"],
    "port": state["port"],
    "startupReadyMs": state["startupReadyMs"],
    "profile": state["profile"],
    "display": state.get("display"),
    "targets": len(targets),
    "logPath": state.get("logPath"),
}, ensure_ascii=False, indent=2))
PY
}

run_suite() {
    require_command python3
    require_command readlink
    require_command node
    owned_app_running || fail "UI 测试程序未运行，请先执行 Tools/ui-test.sh start。"
    local node_version node_major node_minor suite_file
    node_version="$(node -p 'process.versions.node')"
    IFS=. read -r node_major node_minor _ <<<"$node_version"
    ((node_major > 22 || (node_major == 22 && node_minor >= 5))) \
        || fail "UI 测试需要 Node.js 22.5 或更高版本。"
    [[ -n "$suite_name" ]] || fail "run 命令缺少 suite-name。"
    [[ "$suite_name" != */* && "$suite_name" =~ ^ui-[a-z0-9-]+(\.mjs)?$ ]] \
        || fail "无效的 suite-name：$suite_name"
    suite_file="$suite_name"
    [[ "$suite_file" == *.mjs ]] || suite_file+=".mjs"
    [[ -f "$script_directory/$suite_file" ]] || fail "找不到 UI 套件：$suite_file"
    node "$script_directory/$suite_file" --state "$state_path"
}

case "$command_name" in
    start) start_ui_test ;;
    stop) stop_ui_test ;;
    status) show_status ;;
    smoke)
        suite_name="ui-smoke.mjs"
        run_suite
        ;;
    run) run_suite ;;
esac
