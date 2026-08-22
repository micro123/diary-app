#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [switch] $NoBuild,
    [string] $RedmineSeedProfile = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDirectory = Split-Path -Parent $PSCommandPath
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $scriptDirectory '..'))
$uiTestScript = Join-Path $scriptDirectory 'ui-test.ps1'
$reportDirectory = Join-Path $repositoryRoot '.build-tmp\ui-test\reports'
$node = Get-Command node -ErrorAction Stop

$groups = @(
    [ordered]@{
        Scenario = 'default'; Plugins = $false; Seed = ''
        Suites = @([ordered]@{ Name = 'ui-settings-full'; Script = 'ui-settings-full.mjs' })
    },
    [ordered]@{
        Scenario = 'default'; Plugins = $false; Seed = ''
        Suites = @([ordered]@{ Name = 'ui-smoke'; Script = 'ui-smoke.mjs' })
    },
    [ordered]@{
        Scenario = 'default'; Plugins = $false; Seed = ''
        Suites = @([ordered]@{ Name = 'ui-core-full'; Script = 'ui-core-full.mjs' })
    },
    [ordered]@{
        Scenario = 'extended'; Plugins = $false; Seed = ''
        Suites = @(
            [ordered]@{ Name = 'ui-extended-full'; Script = 'ui-extended-full.mjs' },
            [ordered]@{ Name = 'ui-script-editor'; Script = 'ui-script-editor.mjs' }
        )
    },
    [ordered]@{
        Scenario = 'database-error'; Plugins = $false; Seed = ''
        Suites = @([ordered]@{ Name = 'ui-database-error'; Script = 'ui-database-error.mjs' })
    },
    [ordered]@{
        Scenario = 'survey'; Plugins = $false; Seed = ''
        Suites = @([ordered]@{ Name = 'ui-survey-full'; Script = 'ui-survey-full.mjs' })
    },
    [ordered]@{
        Scenario = 'extra-fields'; Plugins = $false; Seed = ''
        Suites = @([ordered]@{ Name = 'ui-extra-fields-full'; Script = 'ui-extra-fields-full.mjs' })
    },
    [ordered]@{
        Scenario = 'plugins'; Plugins = $true; Seed = $RedmineSeedProfile
        Suites = @([ordered]@{ Name = 'ui-redmine-full'; Script = 'ui-redmine-full.mjs' })
    }
)

function Stop-CurrentUiTest {
    try {
        & $uiTestScript stop | Out-Host
    }
    catch {
        Write-Warning "停止 UI 测试程序失败：$($_.Exception.Message)"
    }
}

function Get-LatestSuiteReport([string] $SuiteName, [DateTime] $StartedUtc) {
    if (-not (Test-Path -LiteralPath $reportDirectory -PathType Container)) {
        return $null
    }
    return Get-ChildItem -LiteralPath $reportDirectory -Filter "$SuiteName-*.json" -File |
        Where-Object LastWriteTimeUtc -ge $StartedUtc.AddSeconds(-1) |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}

function Add-SuiteResult(
    [Collections.Generic.List[object]] $Target,
    [System.Collections.IDictionary] $Suite,
    [DateTime] $StartedUtc,
    [int] $ExitCode,
    [string] $ErrorMessage = '') {
    $reportFile = Get-LatestSuiteReport $Suite.Name $StartedUtc
    $report = if ($null -ne $reportFile) {
        Get-Content -LiteralPath $reportFile.FullName -Raw | ConvertFrom-Json
    }
    else {
        $null
    }
    $durationMs = if ($null -ne $report -and $null -ne $report.PSObject.Properties['durationMs']) {
        $report.durationMs
    }
    else {
        $null
    }
    $summary = if ($null -ne $report -and $null -ne $report.PSObject.Properties['summary']) {
        $report.summary
    }
    else {
        $null
    }
    $Target.Add([ordered]@{
        name = $Suite.Name
        status = if ($null -ne $report) { $report.status } elseif ($ExitCode -eq 0) { 'passed' } else { 'failed' }
        exitCode = $ExitCode
        durationMs = $durationMs
        summary = $summary
        reportPath = if ($null -ne $reportFile) { $reportFile.FullName } else { $null }
        error = if ([string]::IsNullOrWhiteSpace($ErrorMessage)) { $null } else { $ErrorMessage }
    })
}

Stop-CurrentUiTest
if (-not $NoBuild) {
    & dotnet build (Join-Path $repositoryRoot 'Diary.App\Diary.App.csproj') --configuration Debug
    if ($LASTEXITCODE -ne 0) {
        throw "Debug 构建失败：exit=$LASTEXITCODE"
    }
}

$startedAt = [DateTimeOffset]::UtcNow
$results = [Collections.Generic.List[object]]::new()
foreach ($group in $groups) {
    if ($group.Plugins -and [string]::IsNullOrWhiteSpace($group.Seed)) {
        foreach ($suite in $group.Suites) {
            $results.Add([ordered]@{
                name = $suite.Name
                status = 'blocked-external'
                reason = '未提供 Redmine 加密 seed profile'
            })
        }
        continue
    }

    $startParameters = @{
        Command = 'start'
        NoBuild = $true
        Scenario = $group.Scenario
    }
    if ($group.Plugins) {
        $startParameters.WithPlugins = $true
        $startParameters.SeedProfile = $group.Seed
    }

    $completedNames = [Collections.Generic.HashSet[string]]::new()
    try {
        & $uiTestScript @startParameters | Out-Host
        foreach ($suite in $group.Suites) {
            Write-Host "运行 UI 套件：$($suite.Name)"
            $suiteStartedUtc = [DateTime]::UtcNow
            & $node.Source (Join-Path $scriptDirectory $suite.Script)
            $exitCode = $LASTEXITCODE
            Add-SuiteResult $results $suite $suiteStartedUtc $exitCode
            $completedNames.Add([string]$suite.Name) | Out-Null
        }
    }
    catch {
        $message = $_.Exception.Message
        foreach ($suite in $group.Suites) {
            if ($completedNames.Contains([string]$suite.Name)) {
                continue
            }
            Add-SuiteResult $results $suite ([DateTime]::UtcNow) 1 $message
        }
    }
    finally {
        Stop-CurrentUiTest
    }
}

$completedAt = [DateTimeOffset]::UtcNow
$failed = @($results | Where-Object status -eq 'failed')
$blocked = @($results | Where-Object status -eq 'blocked-external')
$aggregate = [ordered]@{
    suite = 'ui-full-test'
    status = if ($failed.Count -gt 0) { 'failed' } elseif ($blocked.Count -gt 0) { 'partial' } else { 'passed' }
    startedAt = $startedAt.ToString('O')
    completedAt = $completedAt.ToString('O')
    durationMs = [int64]($completedAt - $startedAt).TotalMilliseconds
    summary = [ordered]@{
        total = $results.Count
        passed = @($results | Where-Object status -eq 'passed').Count
        failed = $failed.Count
        blockedExternal = $blocked.Count
    }
    results = $results
}

New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$stamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH-mm-ss-fffZ')
$aggregatePath = Join-Path $reportDirectory "ui-full-test-$stamp.json"
$aggregate.reportPath = $aggregatePath
$aggregate | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $aggregatePath -Encoding utf8NoBOM
$aggregate | ConvertTo-Json -Depth 10
if ($aggregate.status -eq 'failed') {
    exit 1
}
