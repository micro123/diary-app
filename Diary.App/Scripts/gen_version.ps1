#!/usr/bin/env pwsh
# 确保有三个参数传入
# 用法: .\gen_version.ps1 <param1> <param2> <param3>

# 支持通过 $args 传入，也支持命名参数。优先使用显式参数，如果未通过 param 提供则使用 $args。
chcp 65001

if ($args.Count -ne 3) {
    Write-Error ".\gen_version.ps1 <project> <output_dir> <file_name>"
    exit 1
}

$project = $args[0]
$output_dir = $args[1]
$file_name = $args[2]

Write-Output "Generating version info for project: $project"
Write-Output "Output directory: $output_dir"
Write-Output "File name: $file_name"

function RunCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$Command,
        
        [Parameter(Mandatory = $false, Position = 1, ValueFromRemainingArguments = $true)]
        [object[]]$Arguments
    )
    
    try {
        if ($Arguments) {
            # 如果有参数，执行命令并传递参数
            $output = & $Command $Arguments
        }
        else {
            # 如果没有参数，直接执行命令
            $output = & $Command
        }
        if ($null -eq $output) {
            $output = ""
        }
    }
    catch {
        Write-Error "执行命令时出错: $($_.Exception.Message)"
        $output = ""
    }

    return $output.Trim()
}

# values
$hash_full = "unknown"
$hash_short = "unknown"
$branch = "unknown"
$commit_count = "0"
$commit_message = "unknown"


$repo_dir = RunCommand git rev-parse --show-toplevel

function EncodingTest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string]$text
    )
    
    $probes=@(
        [System.Text.Encoding]::UTF8, 
        [System.Text.Encoding]::ASCII, 
        [System.Text.Encoding]::Unicode, 
        [System.Text.Encoding]::GetEncoding("GBK"), 
        [System.Text.Encoding]::GetEncoding("GB2312")
    )
    
    foreach($probe in $probes) {
        $bytes = $probe.GetBytes($text)
        Write-Output "[$($probe.EncodingName)] Bytes: $($bytes -join ', ')"
        $decoded = $probe.GetString($bytes)
        Write-Output "Decoded: $decoded"
    }
}

if ($repo_dir -ne "") {
    Push-Location -Path $repo_dir

    $dirty_check = RunCommand git status --porcelain
    $hash_full = RunCommand git rev-parse HEAD
    $hash_short = RunCommand git rev-parse --short HEAD
    $branch = RunCommand git rev-parse --abbrev-ref HEAD
    $commit_count = RunCommand git rev-list --count HEAD
    $commit_message = RunCommand git log -1 --pretty=%s
    if ($dirty_check -ne "") {
        $hash_full += "-dirty"
        $hash_short += "-dirty"
    }

    # 转义提交消息以便嵌入 C# 字符串字面量：仅反斜杠与双引号
    # （已只取 subject 首行，不含换行）
    $commit_message = $commit_message.Replace('\', '\\').Replace('"', '\"')

    # EncodingTest "$hostname"

    Pop-Location
}

$build_sequence = $commit_count
if (-not [string]::IsNullOrWhiteSpace($env:DIARY_BUILD_SEQUENCE)) {
    if ($env:DIARY_BUILD_SEQUENCE -notmatch '^[0-9]+$') {
        Write-Error "DIARY_BUILD_SEQUENCE must be a non-negative integer."
        exit 1
    }
    $build_sequence = $env:DIARY_BUILD_SEQUENCE
}
$build_channel = if ([string]::IsNullOrWhiteSpace($env:DIARY_BUILD_CHANNEL)) { "release" } else { $env:DIARY_BUILD_CHANNEL }
if ($build_channel -notmatch '^[a-z0-9][a-z0-9-]{0,31}$') {
    Write-Error "DIARY_BUILD_CHANNEL must use lowercase letters, digits, or hyphens."
    exit 1
}

New-Item -Path $output_dir -ItemType Directory -Force | Out-Null

$content = @"
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
    private const string LastCommitMessage = "${commit_message}";
    
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
"@
$target_path = Join-Path $output_dir $file_name
Write-Output $content
Write-Output $target_path
Write-Output $content | Out-File -FilePath $target_path -Encoding UTF8 -Force
exit 0
