#!/usr/bin/env pwsh
# 确保有三个参数传入
# 用法: .\gen_version.ps1 <param1> <param2> <param3>

# 支持通过 $args 传入，也支持命名参数。优先使用显式参数，如果未通过 param 提供则使用 $args。

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
        
        return ($output).Trim()
    }
    catch {
        Write-Error "执行命令时出错: $($_.Exception.Message)"
        return ""
    }
}

# values
$timestamp = Get-Date -UFormat "%Y/%m/%d %H:%M:%S"
$hash_full = "unknown"
$hash_short = "unknown"
$branch = "unknown"
$commit_count = "0"
$commit_message = "unknown"
$commit_date = "unknown"
$hostname = RunCommand hostname


$repo_dir = RunCommand git rev-parse --show-toplevel

if ($repo_dir -ne "") {
    Set-Location $repo_dir

    $dirty_check = RunCommand git status --porcelain
    $hash_full = RunCommand git rev-parse HEAD
    $hash_short = RunCommand git rev-parse --short HEAD
    $branch = RunCommand git rev-parse --abbrev-ref HEAD
    $commit_count = RunCommand git rev-list --count HEAD
    $commit_message = RunCommand git log -1 --pretty=%B
    $commit_date = RunCommand git log -1 --pretty=%cd  --date=format:'%Y-%m-%d %H:%M:%S'
    if ($dirty_check -ne "") {
        $hash_full += "-dirty"
        $hash_short += "-dirty"
    }
}

New-Item -Path $output_dir -ItemType Directory -Force | Out-Null

$content = @"
namespace ${project};

internal static class VersionInfo
{
    public static readonly string BuildTime = "$timestamp";
    public static readonly string GitVersionFull = "${hash_full}";
    public static readonly string GitVersionShort = "${hash_short}";
    public static readonly string CommitCount = "${commit_count}";
    public static readonly string Branch = "${branch}";
    public static readonly string LastCommitMessage = "${commit_message}";
    public static readonly string LastCommitDate = "${commit_date}";
    public static readonly string HostName = "${hostname}";
}
"@
Write-Output $content
Write-Output $content | Out-File -FilePath (Join-Path $output_dir $file_name) -Encoding UTF8 -Force
exit 0
