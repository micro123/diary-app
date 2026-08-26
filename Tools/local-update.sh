#!/usr/bin/env bash

set -euo pipefail

readonly RID="win-x64"
readonly FLAVOR="python313"
readonly CHANNEL="local"

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(cd -- "$script_directory/.." && pwd)
update_server_directory="$repository_root/UpdateServer"
server_url="${DIARY_UPDATE_SERVER_URL:-http://127.0.0.1:18080}"
token_file="${DIARY_UPDATE_PUBLISH_TOKEN_FILE:-$update_server_directory/publish_token.txt}"
sequence=""
ready_to_run=0
command_name="publish"
temporary_directory=""

cleanup() {
    if [ -n "$temporary_directory" ] && [ -d "$temporary_directory" ]; then
        rm -rf -- "$temporary_directory"
    fi
}

trap cleanup EXIT

print_usage() {
    printf '用法：%s [publish|all|server-start|server-stop|status] [选项]\n' "$0"
    printf '\n'
    printf '  publish       打包 win-x64 Python 版并发布到 local 通道（默认）\n'
    printf '  all           启动/更新本机 Docker 服务，然后打包并发布\n'
    printf '  server-start  构建并启动本机 Docker 更新服务\n'
    printf '  server-stop   停止本机 Docker 更新服务，保留数据卷\n'
    printf '  status        查看服务状态和 local 最新版本\n'
    printf '\n'
    printf '选项：\n'
    printf '  --server URL       更新服务器根地址，默认 %s\n' "$server_url"
    printf '  --token-file PATH  发布 Token 文件，默认 %s\n' "$token_file"
    printf '  --sequence NUMBER  显式指定更新序号；默认取 UTC 时间并保证大于服务器 latest\n'
    printf '  --ready-to-run     使用 ReadyToRun 构建实验包；包体积会明显增加\n'
    printf '  -h, --help         显示帮助\n'
}

require_command() {
    if ! command -v "$1" >/dev/null 2>&1; then
        printf '缺少必需命令：%s\n' "$1" >&2
        exit 1
    fi
}

normalize_server_url() {
    server_url=${server_url%/}
    if [[ ! "$server_url" =~ ^https?:// ]]; then
        printf '服务器地址必须是 HTTP/HTTPS 绝对地址：%s\n' "$server_url" >&2
        exit 1
    fi
}

read_publish_token() {
    if [ -n "${DIARY_UPDATE_PUBLISH_TOKEN:-}" ]; then
        printf '%s' "$DIARY_UPDATE_PUBLISH_TOKEN"
        return
    fi
    if [ ! -f "$token_file" ]; then
        printf '找不到发布 Token：%s\n' "$token_file" >&2
        printf '可先运行：%s server-start\n' "$0" >&2
        exit 1
    fi
    tr -d '\r\n' < "$token_file"
}

ensure_publish_token() {
    if [ -n "${DIARY_UPDATE_PUBLISH_TOKEN:-}" ]; then
        return
    fi
    if [ ! -s "$token_file" ]; then
        mkdir -p -- "$(dirname -- "$token_file")"
        umask 077
        od -An -N32 -tx1 /dev/urandom | tr -d ' \n' > "$token_file"
        printf '已生成本机发布 Token：%s\n' "$token_file"
    fi
    DIARY_UPDATE_PUBLISH_TOKEN=$(tr -d '\r\n' < "$token_file")
    export DIARY_UPDATE_PUBLISH_TOKEN
}

start_server() {
    require_command docker
    require_command curl
    require_command od
    require_command seq
    ensure_publish_token
    if [ -z "${DIARY_GITHUB_TOKEN:-}" ] && [ -s "$repository_root/github_token.txt" ]; then
        DIARY_GITHUB_TOKEN=$(tr -d '\r\n' < "$repository_root/github_token.txt")
        export DIARY_GITHUB_TOKEN
        printf '已从 github_token.txt 加载 GitHub Token。\n'
    fi
    printf '正在构建并启动本机更新服务器……\n'
    (
        cd "$update_server_directory"
        docker compose up -d --build
    )
    printf '等待更新服务器就绪：%s\n' "$server_url"
    for _attempt in $(seq 1 30); do
        if curl --silent --fail --max-time 3 "$server_url/health/ready" >/dev/null; then
            printf '更新服务器已就绪。\n'
            return
        fi
        sleep 1
    done
    printf '更新服务器在 30 秒内未就绪，请检查 docker compose logs。\n' >&2
    exit 1
}

stop_server() {
    require_command docker
    (
        cd "$update_server_directory"
        docker compose down
    )
}

latest_sequence() {
    local response_file="$temporary_directory/latest.json"
    local status
    status=$(curl --silent --show-error --max-time 15 \
        --output "$response_file" --write-out '%{http_code}' \
        "$server_url/api/v1/updates/latest?channel=$CHANNEL&rid=$RID&flavor=$FLAVOR") || {
        printf '无法连接更新服务器：%s\n' "$server_url" >&2
        exit 1
    }
    if [ "$status" = "404" ]; then
        printf '0'
        return
    fi
    if [ "$status" != "200" ]; then
        printf '读取 local latest 失败（HTTP %s）：\n' "$status" >&2
        cat "$response_file" >&2
        exit 1
    fi
    python3 -c 'import json,sys; print(int(json.load(open(sys.argv[1], encoding="utf-8"))["manifest"]["sequence"]))' \
        "$response_file"
}

resolve_sequence() {
    local current_latest candidate
    current_latest=$(latest_sequence)
    if [ -n "$sequence" ]; then
        if [[ ! "$sequence" =~ ^[0-9]+$ ]]; then
            printf 'sequence 必须是非负整数：%s\n' "$sequence" >&2
            exit 1
        fi
        if [ "$sequence" -le "$current_latest" ]; then
            printf 'sequence 必须大于服务器 local latest（%s）：%s\n' "$current_latest" "$sequence" >&2
            exit 1
        fi
        return
    fi
    candidate=$(date -u +%Y%m%d%H%M%S)
    if [ "$candidate" -le "$current_latest" ]; then
        candidate=$((current_latest + 1))
    fi
    sequence="$candidate"
}

read_data_version() {
    local source="$repository_root/Diary.Core/DataVersion.cs"
    local major minor patch
    major=$(sed -nE 's/.*Major = ([0-9]+).*/\1/p' "$source")
    minor=$(sed -nE 's/.*Minor = ([0-9]+).*/\1/p' "$source")
    patch=$(sed -nE 's/.*Patch = ([0-9]+).*/\1/p' "$source")
    if [ -z "$major" ] || [ -z "$minor" ] || [ -z "$patch" ]; then
        printf '无法从 %s 读取数据版本。\n' "$source" >&2
        exit 1
    fi
    printf '%s.%s.%s' "$major" "$minor" "$patch"
}

publish_package() {
    local publish_token data_version version_id package_label archive_path package_sha response_file latest_file status
    for required in curl python3 sha256sum date sed; do
        require_command "$required"
    done
    publish_token=$(read_publish_token)
    if [ -z "$publish_token" ]; then
        printf '发布 Token 不能为空。\n' >&2
        exit 1
    fi
    resolve_sequence
    data_version=$(read_data_version)
    version_id="$data_version-r$sequence"
    package_label="local-$sequence"
    archive_path="$repository_root/artifacts/packages/DiaryAppNG-$package_label-$RID-python313.zip"

    printf '开始构建 local 更新：sequence=%s, version=%s\n' "$sequence" "$version_id"
    local -a package_arguments=("$package_label")
    if [ "$ready_to_run" -eq 1 ]; then
        package_arguments=(--ready-to-run "${package_arguments[@]}")
    fi
    DIARY_BUILD_SEQUENCE="$sequence" DIARY_BUILD_CHANNEL="$CHANNEL" \
        "$script_directory/package-win-x64-with-python.sh" "${package_arguments[@]}"
    if [ ! -f "$archive_path" ]; then
        printf '打包脚本没有生成预期文件：%s\n' "$archive_path" >&2
        exit 1
    fi
    package_sha=$(sha256sum "$archive_path" | cut -d' ' -f1)
    response_file="$temporary_directory/publish-response.json"
    printf '正在上传到 %s 的 local 通道……\n' "$server_url"
    status=$(curl --silent --show-error --max-time 1800 \
        --output "$response_file" --write-out '%{http_code}' \
        --request POST \
        --header "Authorization: Bearer $publish_token" \
        --header 'Content-Type: application/zip' \
        --header 'Expect:' \
        --header "X-Diary-Channel: $CHANNEL" \
        --header "X-Diary-Sequence: $sequence" \
        --header "X-Diary-Version-Id: $version_id" \
        --header "X-Diary-Data-Version: $data_version" \
        --header "X-Diary-Rid: $RID" \
        --header "X-Diary-Flavor: $FLAVOR" \
        --header "X-Diary-Sha256: $package_sha" \
        --data-binary "@$archive_path" \
        "$server_url/api/v1/internal/publish/local") || {
        printf '上传 local 更新失败。\n' >&2
        exit 1
    }
    if [ "$status" != "200" ] && [ "$status" != "201" ]; then
        printf '服务器拒绝 local 更新（HTTP %s）：\n' "$status" >&2
        cat "$response_file" >&2
        exit 1
    fi
    python3 - "$response_file" "$sequence" "$package_sha" <<'PY'
import json
import sys

response = json.load(open(sys.argv[1], encoding="utf-8"))
expected_sequence = int(sys.argv[2])
expected_sha256 = sys.argv[3]
if response["release"]["sequence"] != expected_sequence:
    raise SystemExit("服务器返回的 sequence 不匹配。")
if response["fullPackage"]["sha256"] != expected_sha256:
    raise SystemExit("服务器返回的完整包 SHA-256 不匹配。")
print(json.dumps(response, ensure_ascii=False, indent=2))
PY

    latest_file="$temporary_directory/published-latest.json"
    curl --silent --show-error --fail --max-time 30 \
        --output "$latest_file" \
        "$server_url/api/v1/updates/latest?channel=$CHANNEL&rid=$RID&flavor=$FLAVOR"
    python3 - "$latest_file" "$sequence" "$package_sha" <<'PY'
import json
import sys

latest = json.load(open(sys.argv[1], encoding="utf-8"))
if latest["manifest"]["sequence"] != int(sys.argv[2]):
    raise SystemExit("latest 回读的 sequence 不匹配。")
if latest["fullPackage"]["sha256"] != sys.argv[3]:
    raise SystemExit("latest 回读的完整包 SHA-256 不匹配。")
PY

    printf '\nlocal 更新发布完成。\n'
    printf '  服务器：%s\n' "$server_url"
    printf '  频道：%s\n' "$CHANNEL"
    printf '  RID：%s\n' "$RID"
    printf '  包类型：%s\n' "$FLAVOR"
    printf '  版本：%s\n' "$version_id"
    printf '  sequence：%s\n' "$sequence"
    printf '  本地包：%s\n' "$archive_path"
    printf '  下载页：%s/downloads\n' "$server_url"
    if [[ "$server_url" == *"127.0.0.1"* ]] || [[ "$server_url" == *"localhost"* ]]; then
        local lan_ip client_server_url
        lan_ip=$(python3 - <<'PY' || true
import socket

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
try:
    sock.connect(("192.0.2.1", 9))
    print(sock.getsockname()[0])
finally:
    sock.close()
PY
        )
        if [ -n "$lan_ip" ]; then
            client_server_url=${server_url/127.0.0.1/$lan_ip}
            client_server_url=${client_server_url/localhost/$lan_ip}
            printf '  Windows 客户端可尝试：%s\n' "$client_server_url"
        fi
    fi
}

show_status() {
    local latest_file="$temporary_directory/status-latest.json"
    require_command curl
    require_command python3
    printf '服务状态：\n'
    curl --silent --show-error --fail "$server_url/health/status" | python3 -m json.tool
    printf '\nlocal latest：\n'
    if ! curl --silent --show-error --fail --output "$latest_file" \
        "$server_url/api/v1/updates/latest?channel=$CHANNEL&rid=$RID&flavor=$FLAVOR"; then
        printf '尚未发布。\n'
    else
        python3 - "$latest_file" <<'PY'
import json
import sys

latest = json.load(open(sys.argv[1], encoding="utf-8"))
manifest = latest["manifest"]
package = latest["fullPackage"]
print(json.dumps({
    "versionId": manifest["versionId"],
    "sequence": manifest["sequence"],
    "channel": manifest["channel"],
    "rid": manifest["rid"],
    "flavor": manifest["flavor"],
    "fileCount": len(manifest["files"]),
    "packageSize": package["size"],
    "packageSha256": package["sha256"],
}, ensure_ascii=False, indent=2))
PY
    fi
}

if [ "$#" -gt 0 ] && [[ "$1" != --* ]]; then
    command_name="$1"
    shift
fi

while [ "$#" -gt 0 ]; do
    case "$1" in
        --server)
            if [ "$#" -lt 2 ]; then
                printf '%s 缺少参数。\n' "$1" >&2
                exit 1
            fi
            server_url="${2:-}"
            shift 2
            ;;
        --token-file)
            if [ "$#" -lt 2 ]; then
                printf '%s 缺少参数。\n' "$1" >&2
                exit 1
            fi
            token_file="${2:-}"
            shift 2
            ;;
        --sequence)
            if [ "$#" -lt 2 ]; then
                printf '%s 缺少参数。\n' "$1" >&2
                exit 1
            fi
            sequence="${2:-}"
            shift 2
            ;;
        --ready-to-run)
            ready_to_run=1
            shift
            ;;
        -h|--help)
            print_usage
            exit 0
            ;;
        *)
            printf '未知参数：%s\n' "$1" >&2
            print_usage >&2
            exit 1
            ;;
    esac
done

normalize_server_url
temporary_directory=$(mktemp -d -t diaryapp-local-update.XXXXXX)

case "$command_name" in
    publish)
        publish_package
        ;;
    all)
        start_server
        publish_package
        ;;
    server-start)
        start_server
        ;;
    server-stop)
        stop_server
        ;;
    status)
        show_status
        ;;
    *)
        printf '未知命令：%s\n' "$command_name" >&2
        print_usage >&2
        exit 1
        ;;
esac
