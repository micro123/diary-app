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
$timestamp = Get-Date -UFormat "%Y/%m/%d %H:%M:%S"
$hash_full = "unknown"
$hash_short = "unknown"
$branch = "unknown"
$commit_count = "0"
$commit_message = "unknown"
$commit_date = "unknown"
$hostname = $([System.Environment]::MachineName)


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
    $commit_message = RunCommand git log -1 --pretty=%B
    $commit_date = RunCommand git log -1 --pretty=%cd  --date=format:'%Y/%m/%d %H:%M:%S'
    if ($dirty_check -ne "") {
        $hash_full += "-dirty"
        $hash_short += "-dirty"
    }
    
    # EncodingTest "$hostname"

    Pop-Location
}

New-Item -Path $output_dir -ItemType Directory -Force | Out-Null

$content = @"
using Diary.Core;
namespace ${project};

internal static partial class VersionInfo
{
    private const string BuildTime = "$timestamp";
    private const string GitVersionFull = "${hash_full}";
    private const string GitVersionShort = "${hash_short}";
    private const string CommitCount = "${commit_count}";
    private const string Branch = "${branch}";
    private const string LastCommitMessage = "${commit_message}";
    private const string LastCommitDate = "${commit_date}";
    private const string HostName = "${hostname}";
    
    static partial void GetVersionStringImpl(ref string versionString)
    {
        versionString = $"{DataVersion.VersionString}.{CommitCount}-{GitVersionShort}";
    }

    static partial void GetVersionDetailImpl(ref string versionString)
    {
        versionString =
              $"""
               数据版本：{DataVersion.VersionString} (0x{DataVersion.VersionCode:X8})
               编译增量：{CommitCount}
               Git分支：{Branch}
               Git提交：{GitVersionShort}
               提交消息：{LastCommitMessage}
               提交时间：{LastCommitDate}
               编译时间：{BuildTime}
               编译主机：{HostName}
               """;
    }
}
"@
$target_path = Join-Path $output_dir $file_name
Write-Output $content
Write-Output $target_path
Write-Output $content | Out-File -FilePath $target_path -Encoding UTF8 -Force
exit 0
