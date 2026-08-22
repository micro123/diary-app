#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('start', 'stop', 'status', 'smoke')]
    [string] $Command = 'start',
    [ValidateRange(1024, 65535)]
    [int] $Port = 9222,
    [switch] $NoBuild,
    [switch] $WithPlugins,
    [ValidateSet('default', 'extended', 'survey', 'database-error', 'extra-fields', 'plugins')]
    [string] $Scenario = 'default',
    [string] $SeedProfile = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDirectory = Split-Path -Parent $PSCommandPath
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $scriptDirectory '..'))
$stateDirectory = Join-Path $repositoryRoot '.build-tmp\ui-test'
$statePath = Join-Path $stateDirectory 'current.json'
$appPath = Join-Path $repositoryRoot 'Diary.App\bin\Debug\net10.0\Diary.App.exe'

function Get-TestState {
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
        return $null
    }
    return Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
}

function Get-OwnedProcess($state) {
    if ($null -eq $state -or [int] $state.processId -le 0) {
        return $null
    }
    $process = Get-Process -Id ([int] $state.processId) -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        return $null
    }
    $expectedPath = [IO.Path]::GetFullPath([string] $state.appPath)
    if (-not [string]::Equals($process.Path, $expectedPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "PID $($state.processId) 不属于当前 UI 测试程序：$($process.Path)"
    }
    return $process
}

function Wait-CdpReady([int] $TargetPort) {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        try {
            $targets = Invoke-RestMethod -Uri "http://127.0.0.1:$TargetPort/json" -TimeoutSec 2
            if (@($targets).Count -gt 0) {
                return $targets
            }
        }
        catch {
            Start-Sleep -Milliseconds 200
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "CDP 服务 60 秒内未就绪：http://127.0.0.1:$TargetPort"
}

function Start-UiTest {
    $existing = Get-TestState
    $owned = Get-OwnedProcess $existing
    if ($null -ne $owned) {
        throw "UI 测试程序已运行：PID=$($owned.Id)，端口=$($existing.port)"
    }
    if (-not $NoBuild) {
        & dotnet build (Join-Path $repositoryRoot 'Diary.App\Diary.App.csproj') --configuration Debug
        if ($LASTEXITCODE -ne 0) {
            throw "Debug 构建失败：exit=$LASTEXITCODE"
        }
    }
    if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) {
        throw "找不到 Debug App：$appPath"
    }

    New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
    $runId = "{0}-{1}" -f (Get-Date -Format 'yyyyMMddHHmmss'), [guid]::NewGuid().ToString('N').Substring(0, 8)
    $profile = Join-Path $stateDirectory "profiles\$runId"
    New-Item -ItemType Directory -Path $profile -Force | Out-Null

    if (-not [string]::IsNullOrWhiteSpace($SeedProfile)) {
        $seedRoot = [IO.Path]::GetFullPath($SeedProfile)
        if (-not (Test-Path -LiteralPath $seedRoot -PathType Container)) {
            throw "UI 测试种子 profile 不存在：$seedRoot"
        }
        $seedConfig = Join-Path $seedRoot 'config'
        if (-not (Test-Path -LiteralPath $seedConfig -PathType Container)) {
            throw "UI 测试种子 profile 缺少 config 目录：$seedRoot"
        }
        $targetConfig = Join-Path $profile 'config'
        New-Item -ItemType Directory -Path $targetConfig -Force | Out-Null
        Get-ChildItem -LiteralPath $seedConfig -File | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $targetConfig $_.Name) -Force
        }
    }

    $previousPort = $env:DIARY_CDP_PORT
    $previousRoot = $env:DIARY_UI_TEST_ROOT
    $previousScenario = $env:DIARY_UI_TEST_SCENARIO
    try {
        $env:DIARY_CDP_PORT = "$Port"
        $env:DIARY_UI_TEST_ROOT = $profile
        $env:DIARY_UI_TEST_SCENARIO = $(if ($Scenario -eq 'default') { $null } else { $Scenario })
        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        $startParameters = @{
            FilePath = $appPath
            WorkingDirectory = Split-Path -Parent $appPath
            WindowStyle = 'Normal'
            PassThru = $true
        }
        if (-not $WithPlugins) {
            $startParameters.ArgumentList = '--core-only'
        }
        $process = Start-Process @startParameters
    }
    finally {
        $env:DIARY_CDP_PORT = $previousPort
        $env:DIARY_UI_TEST_ROOT = $previousRoot
        $env:DIARY_UI_TEST_SCENARIO = $previousScenario
    }

    try {
        $targets = Wait-CdpReady $Port
        $stopwatch.Stop()
        $state = [ordered]@{
            processId = $process.Id
            appPath = $appPath
            port = $Port
            profile = $profile
            startedAt = [DateTimeOffset]::Now.ToString('O')
            startupReadyMs = $stopwatch.ElapsedMilliseconds
            withPlugins = [bool] $WithPlugins
            scenario = $Scenario
            seeded = -not [string]::IsNullOrWhiteSpace($SeedProfile)
        }
        $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8NoBOM
        [ordered]@{
            status = 'ready'
            processId = $process.Id
            port = $Port
            startupReadyMs = $stopwatch.ElapsedMilliseconds
            withPlugins = [bool] $WithPlugins
            scenario = $Scenario
            seeded = -not [string]::IsNullOrWhiteSpace($SeedProfile)
            targetCount = @($targets).Count
            title = $targets[0].title
            webSocketDebuggerUrl = $targets[0].webSocketDebuggerUrl
            profile = $profile
        } | ConvertTo-Json
    }
    catch {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
        }
        throw
    }
}

function Stop-UiTest {
    $state = Get-TestState
    $process = Get-OwnedProcess $state
    if ($null -eq $process) {
        Write-Host 'UI 测试程序未运行。'
        return
    }
    Stop-Process -Id $process.Id -Force
    $process.WaitForExit(10000) | Out-Null
    Remove-Item -LiteralPath $statePath -Force
    Write-Host "UI 测试程序已停止：PID=$($process.Id)"
}

function Invoke-UiSmoke {
    $state = Get-TestState
    $process = Get-OwnedProcess $state
    if ($null -eq $process) {
        throw 'UI 测试程序未运行，请先执行 Tools/ui-test.ps1 start。'
    }
    $node = Get-Command node -ErrorAction SilentlyContinue
    if ($null -eq $node) {
        throw '找不到 Node.js；UI smoke 需要 Node.js 22 或更高版本。'
    }
    & $node.Source (Join-Path $scriptDirectory 'ui-smoke.mjs') --state $statePath
    if ($LASTEXITCODE -ne 0) {
        throw "UI smoke 失败：exit=$LASTEXITCODE"
    }
}

function Show-UiTestStatus {
    $state = Get-TestState
    $process = Get-OwnedProcess $state
    if ($null -eq $process) {
        Write-Host 'UI 测试程序未运行。'
        return
    }
    $targets = Invoke-RestMethod -Uri "http://127.0.0.1:$($state.port)/json" -TimeoutSec 3
    [ordered]@{
        status = 'ready'
        processId = $process.Id
        responding = $process.Responding
        port = $state.port
        startupReadyMs = $state.startupReadyMs
        profile = $state.profile
        targets = @($targets).Count
    } | ConvertTo-Json
}

switch ($Command) {
    'start' { Start-UiTest }
    'stop' { Stop-UiTest }
    'status' { Show-UiTestStatus }
    'smoke' { Invoke-UiSmoke }
}
