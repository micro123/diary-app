#!/usr/bin/env bash
# 简体中文：将 gen_version.ps1 的逻辑翻译为 Bash

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

timestamp=$(date +"%Y/%m/%d %H:%M:%S")
hash_full="unknown"
hash_short="unknown"
branch="unknown"
commit_count="0"
commit_message="unknown"
commit_date="unknown"
hostname=$(run_command hostname)

repo_dir=$(run_command git rev-parse --show-toplevel)

if [ -n "$repo_dir" ]; then
	pushd "$repo_dir"

	dirty_check=$(run_command git status --porcelain)
	hash_full=$(run_command git rev-parse HEAD)
	hash_short=$(run_command git rev-parse --short HEAD)
	branch=$(run_command git rev-parse --abbrev-ref HEAD)
	commit_count=$(run_command git rev-list --count HEAD)
	commit_message=$(run_command git log -1 --pretty=%B)
	commit_date=$(run_command git log -1 --pretty=%cd --date=format:'%Y-%m-%d %H:%M:%S')

	if [ -n "$dirty_check" ]; then
		hash_full+="-dirty"
		hash_short+="-dirty"
	fi
    
    popd
fi

# Escape commit message for embedding in a C# string literal
escape_cs_string() {
	printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' -e ':a;N;$!ba;s/\n/\\n/g'
}

commit_message_escaped=$(escape_cs_string "$commit_message")

mkdir -p "$output_dir"

output_path="$output_dir/$file_name"

cat <<EOF
namespace ${project};

internal static class VersionInfo
{
	public static readonly string BuildTime = "${timestamp}";
	public static readonly string GitVersionFull = "${hash_full}";
	public static readonly string GitVersionShort = "${hash_short}";
	public static readonly string CommitCount = "${commit_count}";
	public static readonly string Branch = "${branch}";
	public static readonly string LastCommitMessage = "${commit_message_escaped}";
	public static readonly string LastCommitDate = "${commit_date}";
	public static readonly string HostName = "${hostname}";
}
EOF | tee "$output_path"

exit 0

