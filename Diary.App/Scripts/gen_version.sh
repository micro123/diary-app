#!/usr/bin/env bash

set -euo pipefail

if [ "$#" -ne 3 ]; then
	echo "Usage: $0 <project> <output_dir> <file_name>"
	exit 1
fi

project="$1"
output_dir="$2"
file_name="$3"

echo "Generating version info for project: $project"
echo "Output directory: $output_dir"
echo "File name: $file_name"

run_command() {
	# Run a command, suppress errors, and trim leading/trailing whitespace
	local out
	out=$("$@" 2>/dev/null || true)
	printf '%s' "$out" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//'
}

hash_full="unknown"
hash_short="unknown"
branch="unknown"
commit_count="0"
commit_message="unknown"

repo_dir=$(run_command git rev-parse --show-toplevel)

if [ -n "$repo_dir" ]; then
	pushd "$repo_dir"

	dirty_check=$(run_command git status --porcelain)
	hash_full=$(run_command git rev-parse HEAD)
	hash_short=$(run_command git rev-parse --short HEAD)
	branch=$(run_command git rev-parse --abbrev-ref HEAD)
	commit_count=$(run_command git rev-list --count HEAD)
	commit_message=$(run_command git log -1 --pretty=%s)

	if [ -n "$dirty_check" ]; then
		hash_full+="-dirty"
		hash_short+="-dirty"
	fi
    
    popd
fi

build_sequence="$commit_count"
if [ -n "${DIARY_BUILD_SEQUENCE:-}" ]; then
	if [[ ! "$DIARY_BUILD_SEQUENCE" =~ ^[0-9]+$ ]]; then
		echo "DIARY_BUILD_SEQUENCE must be a non-negative integer." >&2
		exit 1
	fi
	build_sequence="$DIARY_BUILD_SEQUENCE"
fi
build_channel="${DIARY_BUILD_CHANNEL:-release}"
if [[ ! "$build_channel" =~ ^[a-z0-9][a-z0-9-]{0,31}$ ]]; then
	echo "DIARY_BUILD_CHANNEL must use lowercase letters, digits, or hyphens." >&2
	exit 1
fi

# Escape commit message for embedding in a C# string literal.
# 已只取 subject 首行，不含换行，故仅需转义反斜杠与双引号。
escape_cs_string() {
	printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'
}

commit_message_escaped=$(escape_cs_string "$commit_message")

mkdir -p "$output_dir"

output_path="$output_dir/$file_name"

tee "$output_path" <<EOF
using Diary.Core;
namespace ${project};

internal static partial class VersionInfo
{
	  private const string GitVersionFull = "${hash_full}";
	  private const string GitVersionShort = "${hash_short}";
	  private const string CommitCount = "${commit_count}";
	  private const string BuildSequence = "${build_sequence}";
	  private const string BuildChannel = "${build_channel}";
	  private const string Branch = "${branch}";
	  private const string LastCommitMessage = "${commit_message_escaped}";

	  static partial void GetVersionStringImpl(ref string versionString)
	  {
		    versionString = $"{DataVersion.VersionString}-r{BuildSequence}";
	  }

    static partial void GetSequenceImpl(ref long sequence)
    {
        sequence = long.Parse(BuildSequence, System.Globalization.CultureInfo.InvariantCulture);
    }

    static partial void GetBuildChannelImpl(ref string buildChannel)
    {
        buildChannel = BuildChannel;
    }

    static partial void GetVersionDetailImpl(ref string versionString)
    {
        versionString =
              $"""
               数据版本：{DataVersion.VersionString} (0x{DataVersion.VersionCode:X8})
               编译增量：{CommitCount}
               更新序号：{BuildSequence}
               构建频道：{BuildChannel}
               Git分支：{Branch}
               Git提交：{GitVersionShort}
               提交消息：{LastCommitMessage}
               """;
    }
}
EOF

exit 0
