#!/usr/bin/env bash

set -euo pipefail

readonly RID="win-x64"
readonly CONFIGURATION="Release"
readonly PYTHON_VERSION="3.13.15"
readonly PYTHON_SERIES="313"
readonly PYTHON_SHA256="d1f04d990aee1253d8569e8e5104e30fa9f5fa830899f14843448872d936a2cf"
readonly FILECODEBOX_URL="http://192.168.1.40:12345"
readonly FILECODEBOX_EXPIRE_VALUE="3"
readonly FILECODEBOX_EXPIRE_STYLE="hour"

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(cd -- "$script_directory/.." && pwd)
output_directory="$repository_root/artifacts/packages"
temporary_directory=""

cleanup() {
    if [ -n "$temporary_directory" ] && [ -d "$temporary_directory" ]; then
        rm -rf -- "$temporary_directory"
    fi
}

trap cleanup EXIT

require_command() {
    if ! command -v "$1" >/dev/null 2>&1; then
        printf '缺少必需命令：%s\n' "$1" >&2
        exit 1
    fi
}

print_usage() {
    printf '用法：%s [--upload-filecodebox] [版本标签]\n' "$0"
    printf '示例：%s --upload-filecodebox v1.0.0-test1\n' "$0"
    printf '默认只生成本地 ZIP；指定 --upload-filecodebox 后，打包完成会上传到局域网 FileCodeBox。\n'
}

default_package_version() {
    local commit_count short_hash dirty_suffix

    commit_count=$(git -C "$repository_root" rev-list --count HEAD)
    short_hash=$(git -C "$repository_root" rev-parse --short HEAD)
    dirty_suffix=""
    if [ -n "$(git -C "$repository_root" status --porcelain)" ]; then
        dirty_suffix="-dirty"
    fi

    printf 'r%s-%s-local%s' "$commit_count" "$short_hash" "$dirty_suffix"
}

verify_publish_output() {
    local publish_directory="$1"
    local required_file
    local required_files=(
        "Diary.App.exe"
        "Diary.App.dll"
        "Diary.Script.Worker.dll"
        "Diary.Script.Worker.exe"
        "Diary.Script.Worker.deps.json"
        "Diary.Script.Worker.runtimeconfig.json"
        "Microsoft.Diagnostics.NETCore.Client.dll"
        "Diary.RedMine.dll"
        "Diary.RedMine.UI.dll"
        "Diary.RedMine.SQLite.dll"
        "Diary.RedMine.PostgreSQL.dll"
        "Diary.Jira.dll"
        "Diary.Jira.UI.dll"
        "Diary.Jira.SQLite.dll"
        "Diary.Jira.PostgreSQL.dll"
        "nng.NET.dll"
        "nng.NET.Shared.dll"
        "nng.dll"
        "mbedcrypto.dll"
        "mbedtls.dll"
        "mbedx509.dll"
    )

    for required_file in "${required_files[@]}"; do
        if [ ! -f "$publish_directory/$required_file" ]; then
            printf '发布目录缺少必需文件：%s\n' "$required_file" >&2
            exit 1
        fi
    done
}

remove_redundant_runtime_assets() {
    local publish_directory="$1"
    local runtimes_directory="$publish_directory/runtimes"

    if [ -d "$runtimes_directory" ]; then
        printf '正在移除 NuGet 额外复制的冗余 runtimes 目录……\n'
        rm -rf -- "$runtimes_directory"
    fi
    if [ -e "$runtimes_directory" ]; then
        printf '无法移除冗余运行时目录：%s\n' "$runtimes_directory" >&2
        exit 1
    fi
}

verify_archive_entry() {
    local archive="$1"
    local entry="$2"

    if ! unzip -Z1 "$archive" | grep -Fx -- "$entry" >/dev/null; then
        printf '最终压缩包缺少必需条目：%s\n' "$entry" >&2
        exit 1
    fi
}

verify_archive_has_no_runtimes_directory() {
    local archive="$1"

    if unzip -Z1 "$archive" | awk '
        $0 == "runtimes/" || index($0, "runtimes/") == 1 { found = 1 }
        END { exit found ? 0 : 1 }
    '; then
        printf '最终压缩包不应包含 runtimes 目录。\n' >&2
        exit 1
    fi
}

upload_to_filecodebox() {
    local archive="$1"
    local response_file="$temporary_directory/filecodebox-response.json"
    local http_status retrieve_code

    printf '正在上传到 FileCodeBox：%s\n' "$FILECODEBOX_URL"
    if ! http_status=$(curl --silent --show-error --location --max-time 300 \
        --form "file=@$archive;type=application/zip" \
        --form "expire_value=$FILECODEBOX_EXPIRE_VALUE" \
        --form "expire_style=$FILECODEBOX_EXPIRE_STYLE" \
        --output "$response_file" \
        --write-out '%{http_code}' \
        "$FILECODEBOX_URL/share/file/"); then
        printf 'FileCodeBox 上传请求失败；本地压缩包仍保留：%s\n' "$archive" >&2
        exit 1
    fi

    if [ "$http_status" != "200" ]; then
        printf 'FileCodeBox 返回 HTTP %s；本地压缩包仍保留：%s\n' "$http_status" "$archive" >&2
        sed -n '1,5p' "$response_file" >&2
        exit 1
    fi

    retrieve_code=$(sed -n \
        's/.*"detail"[[:space:]]*:[[:space:]]*{[^}]*"code"[[:space:]]*:[[:space:]]*"\([^"}]*\)".*/\1/p' \
        "$response_file" | head -n 1)
    if [ -z "$retrieve_code" ]; then
        printf 'FileCodeBox 响应中没有找到取件码；本地压缩包仍保留：%s\n' "$archive" >&2
        sed -n '1,5p' "$response_file" >&2
        exit 1
    fi

    printf 'FileCodeBox 上传完成。\n'
    printf '取件地址：%s\n' "$FILECODEBOX_URL"
    printf '取件码：%s\n' "$retrieve_code"
    printf '有效期：%s 小时\n' "$FILECODEBOX_EXPIRE_VALUE"
}

upload_filecodebox=0
package_version=""
while [ "$#" -gt 0 ]; do
    case "$1" in
        -h|--help)
            print_usage
            exit 0
            ;;
        --upload-filecodebox)
            upload_filecodebox=1
            ;;
        --*)
            printf '未知参数：%s\n' "$1" >&2
            print_usage >&2
            exit 1
            ;;
        *)
            if [ -n "$package_version" ]; then
                printf '只能指定一个版本标签：%s\n' "$1" >&2
                print_usage >&2
                exit 1
            fi
            package_version="$1"
            ;;
    esac
    shift
done

for command_name in dotnet git curl sha256sum unzip zip mktemp; do
    require_command "$command_name"
done

if [ -z "$package_version" ]; then
    package_version=$(default_package_version)
fi
if [[ ! "$package_version" =~ ^[A-Za-z0-9._-]+$ ]]; then
    printf '版本标签只能包含字母、数字、点、下划线和连字符：%s\n' "$package_version" >&2
    exit 1
fi

archive_name="DiaryAppNG-${package_version}-${RID}-python${PYTHON_SERIES}.zip"
archive_path="$output_directory/$archive_name"
python_archive_name="python-${PYTHON_VERSION}-embed-amd64.zip"
python_uri="https://www.python.org/ftp/python/${PYTHON_VERSION}/${python_archive_name}"

temporary_directory=$(mktemp -d -t diaryapp-win-package.XXXXXX)
publish_directory="$temporary_directory/publish"
python_archive="$temporary_directory/$python_archive_name"
temporary_archive="$temporary_directory/$archive_name"

mkdir -p -- "$publish_directory" "$output_directory"

printf '正在还原 %s 依赖……\n' "$RID"
dotnet restore "$repository_root/DiaryApp.sln" --runtime "$RID"

printf '正在发布 %s 自包含应用……\n' "$RID"
dotnet publish "$repository_root/Diary.App/Diary.App.csproj" \
    --configuration "$CONFIGURATION" \
    --runtime "$RID" \
    --self-contained true \
    --no-restore \
    --output "$publish_directory"

verify_publish_output "$publish_directory"
remove_redundant_runtime_assets "$publish_directory"

printf '正在下载 Python %s embeddable runtime……\n' "$PYTHON_VERSION"
curl --fail --location --retry 3 --output "$python_archive" "$python_uri"

printf '%s  %s\n' "$PYTHON_SHA256" "$python_archive" | sha256sum --check --status
printf 'Python 包 SHA-256 校验通过。\n'

mkdir -p -- "$publish_directory/python"
unzip -q "$python_archive" -d "$publish_directory/python"

for python_file in python.exe "python${PYTHON_SERIES}.dll" "python${PYTHON_SERIES}.zip"; do
    if [ ! -f "$publish_directory/python/$python_file" ]; then
        printf 'Python embeddable runtime 缺少文件：python/%s\n' "$python_file" >&2
        exit 1
    fi
done

printf '正在生成压缩包……\n'
(
    cd "$publish_directory"
    zip -q -r "$temporary_archive" .
)

unzip -tq "$temporary_archive" >/dev/null
verify_archive_entry "$temporary_archive" "Diary.App.dll"
verify_archive_entry "$temporary_archive" "Diary.App.exe"
verify_archive_entry "$temporary_archive" "Diary.Script.Worker.exe"
verify_archive_entry "$temporary_archive" "nng.dll"
verify_archive_entry "$temporary_archive" "nng.NET.dll"
verify_archive_entry "$temporary_archive" "python/python.exe"
verify_archive_entry "$temporary_archive" "python/python${PYTHON_SERIES}.dll"
verify_archive_has_no_runtimes_directory "$temporary_archive"

mv -f -- "$temporary_archive" "$archive_path"

printf '\n打包完成：%s\n' "$archive_path"
printf '文件大小：%s\n' "$(du -h "$archive_path" | cut -f1)"
printf 'SHA-256：%s\n' "$(sha256sum "$archive_path" | cut -d' ' -f1)"

if [ "$upload_filecodebox" -eq 1 ]; then
    upload_to_filecodebox "$archive_path"
fi
