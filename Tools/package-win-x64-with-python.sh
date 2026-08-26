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
        "Diary.Updater.exe"
        "Diary.Mcp.exe"
        "Diary.Mcp.dll"
        "Diary.Mcp.deps.json"
        "Diary.Mcp.runtimeconfig.json"
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
        "Docs/UserManual/DiaryApp-User-Manual.html"
        "Docs/UserManual/DiaryApp-User-Manual.pdf"
    )

    for required_file in "${required_files[@]}"; do
        if [ ! -f "$publish_directory/$required_file" ]; then
            printf '发布目录缺少必需文件：%s\n' "$required_file" >&2
            exit 1
        fi
    done
}

add_user_manual() {
    local publish_directory="$1"
    local manual_project_directory="$repository_root/Docs/UserManual"
    local manual_output_directory="$manual_project_directory/_output"
    local manual_destination_directory="$publish_directory/Docs/UserManual"
    local html_source="$manual_output_directory/DiaryApp-User-Manual.html"
    local pdf_source="$manual_output_directory/DiaryApp-User-Manual.pdf"

    printf '正在渲染用户手册……\n'
    quarto render "$manual_project_directory"
    if [ ! -s "$html_source" ] || ! grep -i -m 1 -q '<html' "$html_source"; then
        printf '用户手册 HTML 缺失或格式无效：%s\n' "$html_source" >&2
        exit 1
    fi
    if [ ! -s "$pdf_source" ] || ! head -c 5 "$pdf_source" | grep -q '%PDF-'; then
        printf '用户手册 PDF 缺失或格式无效：%s\n' "$pdf_source" >&2
        exit 1
    fi

    mkdir -p -- "$manual_destination_directory"
    cp -- "$html_source" "$manual_destination_directory/DiaryApp-User-Manual.html"
    cp -- "$pdf_source" "$manual_destination_directory/DiaryApp-User-Manual.pdf"
}

remove_unrelated_runtime_assets() {
    local publish_directory="$1"
    local rid="$2"
    local runtimes_directory="$publish_directory/runtimes"
    local target_runtime_directory="$runtimes_directory/$rid"
    local runtime_directory runtime_name

    if [ ! -d "$target_runtime_directory" ]; then
        printf '发布目录缺少目标 RID 运行时目录：runtimes/%s\n' "$rid" >&2
        exit 1
    fi
    if [ ! -d "$runtimes_directory/any" ]; then
        printf '发布目录缺少 RID 无关运行时目录：runtimes/any\n' >&2
        exit 1
    fi

    printf '正在保留 runtimes/%s、runtimes/any 并移除其他运行时目录……\n' "$rid"
    for runtime_directory in "$runtimes_directory"/*; do
        if [ ! -d "$runtime_directory" ]; then
            continue
        fi
        runtime_name=$(basename -- "$runtime_directory")
        if [ "$runtime_name" != "$rid" ] && [ "$runtime_name" != "any" ]; then
            rm -rf -- "$runtime_directory"
        fi
    done

    if [ ! -d "$target_runtime_directory" ]; then
        printf '清理后缺少目标 RID 运行时目录：runtimes/%s\n' "$rid" >&2
        exit 1
    fi
    if [ ! -d "$runtimes_directory/any" ]; then
        printf '清理后缺少 RID 无关运行时目录：runtimes/any\n' >&2
        exit 1
    fi
    for runtime_directory in "$runtimes_directory"/*; do
        if [ ! -d "$runtime_directory" ]; then
            continue
        fi
        runtime_name=$(basename -- "$runtime_directory")
        if [ "$runtime_name" != "$rid" ] && [ "$runtime_name" != "any" ]; then
            printf '无法移除非目标运行时目录：%s\n' "$runtime_directory" >&2
            exit 1
        fi
    done
}

verify_archive_entry() {
    local archive="$1"
    local entry="$2"

    if ! unzip -Z1 "$archive" | grep -Fx -- "$entry" >/dev/null; then
        printf '最终压缩包缺少必需条目：%s\n' "$entry" >&2
        exit 1
    fi
}

verify_archive_runtime_directories() {
    local archive="$1"
    local rid="$2"
    local validation_output validation_status

    if validation_output=$(unzip -Z1 "$archive" | awk -v rid="$rid" '
        BEGIN {
            runtimes_prefix = "runtimes/"
            target_prefix = runtimes_prefix rid "/"
        }
        index($0, target_prefix) == 1 {
            found_target = 1
            next
        }
        index($0, "runtimes/any/") == 1 {
            found_any = 1
            next
        }
        index($0, runtimes_prefix) == 1 && $0 != runtimes_prefix {
            print $0
            found_unexpected = 1
        }
        END {
            if (found_unexpected) exit 2
            if (!found_target) exit 3
            if (!found_any) exit 4
        }
    '); then
        return
    else
        validation_status=$?
    fi

    if [ "$validation_status" -eq 2 ]; then
        printf '最终压缩包包含非目标 RID 运行时条目：%s\n' "$validation_output" >&2
    elif [ "$validation_status" -eq 4 ]; then
        printf '最终压缩包缺少 RID 无关运行时目录：runtimes/any\n' >&2
    else
        printf '最终压缩包缺少目标 RID 运行时目录：runtimes/%s\n' "$rid" >&2
    fi
    exit 1
}

prepare_python_archive() {
    local cache_directory="$repository_root/artifacts/cache/python"
    local cached_archive="$cache_directory/$python_archive_name"
    local temporary_download

    mkdir -p -- "$cache_directory"
    if [ -f "$cached_archive" ] && printf '%s  %s\n' "$PYTHON_SHA256" "$cached_archive" | sha256sum --check --status; then
        printf '使用缓存的 Python %s embeddable runtime：%s\n' "$PYTHON_VERSION" "$cached_archive"
        python_archive="$cached_archive"
        return
    fi

    if [ -f "$cached_archive" ]; then
        printf 'Python 缓存校验失败，将重新下载：%s\n' "$cached_archive"
    else
        printf '缓存中没有 Python %s embeddable runtime。\n' "$PYTHON_VERSION"
    fi

    temporary_download=$(mktemp "$cache_directory/.${python_archive_name}.download.XXXXXX")
    curl --fail --location --retry 3 --output "$temporary_download" "$python_uri"
    printf '%s  %s\n' "$PYTHON_SHA256" "$temporary_download" | sha256sum --check --status
    mv -f -- "$temporary_download" "$cached_archive"
    printf 'Python 包已下载并缓存：%s\n' "$cached_archive"
    python_archive="$cached_archive"
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
        retrieve_code=$(sed -n \
            's/.*"detail"[[:space:]]*:[[:space:]]*{[^}]*"code"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p' \
            "$response_file" | head -n 1)
    fi
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

for command_name in dotnet git curl sha256sum unzip zip mktemp python3 quarto; do
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
updater_publish_directory="$temporary_directory/updater"
python_archive=""
temporary_archive="$temporary_directory/$archive_name"

mkdir -p -- "$publish_directory" "$updater_publish_directory" "$output_directory"

printf '正在还原 %s 依赖……\n' "$RID"
dotnet restore "$repository_root/DiaryApp.sln" --runtime "$RID" -p:Configuration="$CONFIGURATION"
dotnet restore "$repository_root/Diary.Script.Worker/Diary.Script.Worker.csproj" \
    --runtime "$RID" -p:Configuration="$CONFIGURATION"

printf '正在发布 %s 自包含应用……\n' "$RID"
dotnet publish "$repository_root/Diary.App/Diary.App.csproj" \
    --configuration "$CONFIGURATION" \
    --runtime "$RID" \
    --self-contained true \
    --no-restore \
    --output "$publish_directory"

printf '正在发布 %s 自包含单文件更新器……\n' "$RID"
dotnet publish "$repository_root/Diary.Updater/Diary.Updater.csproj" \
    --configuration "$CONFIGURATION" \
    --runtime "$RID" \
    --self-contained true \
    --no-restore \
    --output "$updater_publish_directory"
cp -- "$updater_publish_directory/Diary.Updater.exe" "$publish_directory/Diary.Updater.exe"

find "$publish_directory" -type f -iname '*.pdb' -delete

add_user_manual "$publish_directory"
verify_publish_output "$publish_directory"
remove_unrelated_runtime_assets "$publish_directory" "$RID"

prepare_python_archive
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
verify_archive_entry "$temporary_archive" "Diary.Updater.exe"
verify_archive_entry "$temporary_archive" "Diary.Mcp.exe"
verify_archive_entry "$temporary_archive" "Diary.Mcp.dll"
verify_archive_entry "$temporary_archive" "Diary.Mcp.deps.json"
verify_archive_entry "$temporary_archive" "Diary.Mcp.runtimeconfig.json"
verify_archive_entry "$temporary_archive" "nng.dll"
verify_archive_entry "$temporary_archive" "nng.NET.dll"
verify_archive_entry "$temporary_archive" "Docs/UserManual/DiaryApp-User-Manual.html"
verify_archive_entry "$temporary_archive" "Docs/UserManual/DiaryApp-User-Manual.pdf"
verify_archive_entry "$temporary_archive" "python/python.exe"
verify_archive_entry "$temporary_archive" "python/python${PYTHON_SERIES}.dll"
verify_archive_runtime_directories "$temporary_archive" "$RID"
python3 "$repository_root/Tools/validate-release-package.py" \
    --archive "$temporary_archive" \
    --rid "$RID" \
    --flavor python313 \
    --require-user-manual \
    --require-script-api

mv -f -- "$temporary_archive" "$archive_path"

printf '\n打包完成：%s\n' "$archive_path"
printf '文件大小：%s\n' "$(du -h "$archive_path" | cut -f1)"
printf 'SHA-256：%s\n' "$(sha256sum "$archive_path" | cut -d' ' -f1)"

if [ "$upload_filecodebox" -eq 1 ]; then
    upload_to_filecodebox "$archive_path"
fi
