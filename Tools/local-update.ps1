#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('publish', 'all', 'server-start', 'server-stop', 'status')]
    [string] $Command = 'publish',
    [string] $ServerUrl = $(if ($env:DIARY_UPDATE_SERVER_URL) { $env:DIARY_UPDATE_SERVER_URL } else { 'http://127.0.0.1:18080' }),
    [string] $TokenFile = $(if ($env:DIARY_UPDATE_PUBLISH_TOKEN_FILE) { $env:DIARY_UPDATE_PUBLISH_TOKEN_FILE } else { '' }),
    [Nullable[long]] $Sequence,
    [ValidateSet('standard', 'python313')]
    [string] $Flavor = 'standard',
    [switch] $ReuseExistingManual
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Rid = 'win-x64'
$Channel = 'local'
$PythonVersion = '3.13.15'
$PythonSeries = '313'
$PythonSha256 = 'd1f04d990aee1253d8569e8e5104e30fa9f5fa830899f14843448872d936a2cf'
$ScriptDirectory = Split-Path -Parent $PSCommandPath
$RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $ScriptDirectory '..'))
$UpdateServerDirectory = Join-Path $RepositoryRoot 'UpdateServer'
$LocalStateDirectory = Join-Path $UpdateServerDirectory '.local-windows'
$ServerUrl = $ServerUrl.TrimEnd('/')
if ([string]::IsNullOrWhiteSpace($TokenFile)) {
    $TokenFile = Join-Path $UpdateServerDirectory 'publish_token.txt'
}
$TokenFile = [System.IO.Path]::GetFullPath($TokenFile)
$TemporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("diaryapp-local-update-{0}" -f [guid]::NewGuid().ToString('N'))

function Assert-Command([string] $Name) {
    if ($null -eq (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "缺少必需命令：$Name"
    }
}

function Invoke-Native([string] $FilePath, [string[]] $Arguments) {
    & $FilePath @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "命令执行失败（exit=$LASTEXITCODE）：$FilePath $($Arguments -join ' ')"
    }
}

function Assert-ServerUrl {
    $uri = $null
    if (-not [Uri]::TryCreate($ServerUrl, [UriKind]::Absolute, [ref] $uri) -or $uri.Scheme -notin @('http', 'https')) {
        throw "服务器地址必须是 HTTP/HTTPS 绝对地址：$ServerUrl"
    }
}

function New-PublishToken {
    $bytes = [byte[]]::new(32)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    return [Convert]::ToHexString($bytes).ToLowerInvariant()
}

function Get-PublishToken([switch] $Create) {
    if (-not [string]::IsNullOrWhiteSpace($env:DIARY_UPDATE_PUBLISH_TOKEN)) {
        return $env:DIARY_UPDATE_PUBLISH_TOKEN.Trim()
    }
    if ($Create -and -not (Test-Path -LiteralPath $TokenFile -PathType Leaf)) {
        $tokenDirectory = Split-Path -Parent $TokenFile
        New-Item -ItemType Directory -Path $tokenDirectory -Force | Out-Null
        Set-Content -LiteralPath $TokenFile -Value (New-PublishToken) -Encoding utf8NoBOM -NoNewline
        Write-Host "已生成本机发布 Token：$TokenFile"
    }
    if (-not (Test-Path -LiteralPath $TokenFile -PathType Leaf)) {
        throw "找不到发布 Token：$TokenFile。请先运行 .\Tools\local-update.ps1 server-start"
    }
    $token = (Get-Content -LiteralPath $TokenFile -Raw).Trim()
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw "发布 Token 不能为空：$TokenFile"
    }
    return $token
}

function Get-PythonCommand {
    foreach ($candidate in @('python', 'py')) {
        if ($null -ne (Get-Command $candidate -ErrorAction SilentlyContinue)) {
            return $candidate
        }
    }
    throw '缺少 Python 3.11+。请安装 Python，并确保 python 或 py 位于 PATH。'
}

function Test-ServerReady {
    try {
        $response = Invoke-WebRequest -Uri "$ServerUrl/health/ready" -TimeoutSec 3 -UseBasicParsing
        return $response.StatusCode -eq 200
    }
    catch {
        return $false
    }
}

function Write-ServerConfig {
    New-Item -ItemType Directory -Path $LocalStateDirectory -Force | Out-Null
    $config = [ordered]@{
        repository = 'micro123/diary-app'
        storageDirectory = './data'
        listenHost = '127.0.0.1'
        listenPort = ([Uri] $ServerUrl).Port
        apiBaseUrl = 'https://api.github.com'
        githubTokenEnvironment = 'DIARY_GITHUB_TOKEN'
        syncTokenEnvironment = 'DIARY_UPDATE_SYNC_TOKEN'
        publishTokenEnvironment = 'DIARY_UPDATE_PUBLISH_TOKEN'
        pollIntervalSeconds = 21600
        requestTimeoutSeconds = 60
        allowedChannels = @('stable', 'preview', 'local')
    }
    $configPath = Join-Path $LocalStateDirectory 'config.json'
    $config | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $configPath -Encoding utf8NoBOM
    return $configPath
}

function Start-UpdateServer {
    if (Test-ServerReady) {
        Write-Host "更新服务器已在运行：$ServerUrl"
        return
    }
    $python = Get-PythonCommand
    $versionText = & $python --version 2>&1
    if ($LASTEXITCODE -ne 0 -or "$versionText" -notmatch 'Python (?<major>\d+)\.(?<minor>\d+)') {
        throw "无法识别 Python 版本：$versionText"
    }
    if ([int] $Matches.major -lt 3 -or ([int] $Matches.major -eq 3 -and [int] $Matches.minor -lt 11)) {
        throw "更新服务器要求 Python 3.11+，当前版本：$versionText"
    }
    $token = Get-PublishToken -Create
    $configPath = Write-ServerConfig
    $pidPath = Join-Path $LocalStateDirectory 'server.pid'
    $stdoutPath = Join-Path $LocalStateDirectory 'server.stdout.log'
    $stderrPath = Join-Path $LocalStateDirectory 'server.stderr.log'
    $previousToken = $env:DIARY_UPDATE_PUBLISH_TOKEN
    try {
        $env:DIARY_UPDATE_PUBLISH_TOKEN = $token
        $process = Start-Process -FilePath $python -ArgumentList @('-m', 'diary_update_server', '--config', $configPath, 'serve-local') -WorkingDirectory $UpdateServerDirectory -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -WindowStyle Hidden -PassThru
    }
    finally {
        $env:DIARY_UPDATE_PUBLISH_TOKEN = $previousToken
    }
    Set-Content -LiteralPath $pidPath -Value $process.Id -Encoding ascii -NoNewline
    Write-Host "正在等待 Windows 本地更新服务器就绪：$ServerUrl"
    foreach ($attempt in 1..30) {
        if (Test-ServerReady) {
            Write-Host "更新服务器已就绪（PID $($process.Id)）。"
            return
        }
        if ($process.HasExited) {
            $errorLog = if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath -Raw } else { '' }
            throw "更新服务器提前退出（exit=$($process.ExitCode)）。`n$errorLog"
        }
        Start-Sleep -Seconds 1
    }
    throw "更新服务器在 30 秒内未就绪，请检查：$stderrPath"
}

function Stop-UpdateServer {
    $pidPath = Join-Path $LocalStateDirectory 'server.pid'
    if (-not (Test-Path -LiteralPath $pidPath -PathType Leaf)) {
        Write-Host '没有找到 Windows 本地更新服务器 PID 文件。'
        return
    }
    $serverPid = [int] (Get-Content -LiteralPath $pidPath -Raw).Trim()
    $process = Get-Process -Id $serverPid -ErrorAction SilentlyContinue
    if ($null -ne $process) {
        $processInfo = Get-CimInstance Win32_Process -Filter "ProcessId = $serverPid"
        $expectedConfigPath = Join-Path $LocalStateDirectory 'config.json'
        $commandLine = if ($null -eq $processInfo) { '' } else { $processInfo.CommandLine }
        if (-not $commandLine.Contains('diary_update_server', [StringComparison]::OrdinalIgnoreCase) -or
            -not $commandLine.Contains($expectedConfigPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw "PID $serverPid 不属于当前工作区的更新服务器，拒绝停止：$commandLine"
        }
        Stop-Process -Id $serverPid
        $process.WaitForExit(5000) | Out-Null
        Write-Host "已停止 Windows 本地更新服务器（PID $serverPid）。"
    }
    else {
        Write-Host "PID $serverPid 已不存在，清理陈旧状态文件。"
    }
    Remove-Item -LiteralPath $pidPath -Force
}

function Invoke-JsonGet([string] $Path, [switch] $AllowNotFound) {
    try {
        return Invoke-RestMethod -Uri "$ServerUrl$Path" -TimeoutSec 30
    }
    catch {
        if ($AllowNotFound -and $null -ne $_.Exception.Response -and $_.Exception.Response.StatusCode -eq 404) {
            return $null
        }
        throw
    }
}

function Get-LatestEnvelope {
    return Invoke-JsonGet "/api/v1/updates/latest?channel=$Channel&rid=$Rid&flavor=$Flavor" -AllowNotFound
}

function Resolve-Sequence {
    $latest = Get-LatestEnvelope
    $latestSequence = if ($null -eq $latest) { 0L } else { [long] $latest.manifest.sequence }
    if ($null -ne $Sequence) {
        if ($Sequence.Value -le $latestSequence) {
            throw "sequence 必须大于服务器 local latest（$latestSequence）：$($Sequence.Value)"
        }
        return $Sequence.Value
    }
    $candidate = [long] (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss')
    return [Math]::Max($candidate, $latestSequence + 1)
}

function Get-DataVersion {
    $sourcePath = Join-Path $RepositoryRoot 'Diary.Core\DataVersion.cs'
    $source = Get-Content -LiteralPath $sourcePath -Raw
    $values = foreach ($name in @('Major', 'Minor', 'Patch')) {
        $match = [regex]::Match($source, "private const uint $name = (?<value>\d+);")
        if (-not $match.Success) {
            throw "无法从 $sourcePath 读取 $name。"
        }
        $match.Groups['value'].Value
    }
    return $values -join '.'
}

function Assert-PublishOutput([string] $PublishDirectory) {
    $requiredFiles = @(
        'Diary.App.exe', 'Diary.App.dll', 'Diary.Script.Worker.dll', 'Diary.Script.Worker.exe',
        'Diary.Script.Worker.deps.json', 'Diary.Script.Worker.runtimeconfig.json', 'Diary.Updater.exe',
        'Diary.Mcp.exe', 'Diary.Mcp.dll', 'Diary.Mcp.deps.json', 'Diary.Mcp.runtimeconfig.json',
        'Microsoft.Diagnostics.NETCore.Client.dll', 'Diary.RedMine.dll', 'Diary.RedMine.UI.dll',
        'Diary.RedMine.SQLite.dll', 'Diary.RedMine.PostgreSQL.dll', 'Diary.Jira.dll', 'Diary.Jira.UI.dll',
        'Diary.Jira.SQLite.dll', 'Diary.Jira.PostgreSQL.dll', 'nng.NET.dll', 'nng.NET.Shared.dll',
        'nng.dll', 'mbedcrypto.dll', 'mbedtls.dll', 'mbedx509.dll',
        'Docs\UserManual\DiaryApp-User-Manual.html', 'Docs\UserManual\DiaryApp-User-Manual.pdf'
    )
    foreach ($requiredFile in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $PublishDirectory $requiredFile) -PathType Leaf)) {
            throw "发布目录缺少必需文件：$requiredFile"
        }
    }
}

function Add-UserManual([string] $PublishDirectory) {
    $manualProjectDirectory = Join-Path $RepositoryRoot 'Docs\UserManual'
    $manualOutputDirectory = Join-Path $manualProjectDirectory '_output'
    $htmlSource = Join-Path $manualOutputDirectory 'DiaryApp-User-Manual.html'
    $pdfSource = Join-Path $manualOutputDirectory 'DiaryApp-User-Manual.pdf'
    $manualDestinationDirectory = Join-Path $PublishDirectory 'Docs\UserManual'

    if ($ReuseExistingManual) {
        Write-Warning '正在复用现有用户手册产物；该选项只用于本地升级链路测试，产物可能不包含最新文档修改。'
    }
    else {
        Assert-Command 'quarto'
        Write-Host '正在渲染用户手册……'
        Invoke-Native 'quarto' @('render', $manualProjectDirectory)
    }
    if (-not (Test-Path -LiteralPath $htmlSource -PathType Leaf) -or
        (Get-Item -LiteralPath $htmlSource).Length -eq 0 -or
        (Get-Content -LiteralPath $htmlSource -Raw) -notmatch '(?i)<html') {
        throw "用户手册 HTML 缺失或格式无效：$htmlSource"
    }
    if (-not (Test-Path -LiteralPath $pdfSource -PathType Leaf) -or
        (Get-Item -LiteralPath $pdfSource).Length -lt 5) {
        throw "用户手册 PDF 缺失或格式无效：$pdfSource"
    }
    $pdfStream = [System.IO.File]::OpenRead($pdfSource)
    try {
        $header = [byte[]]::new(5)
        if ($pdfStream.Read($header, 0, $header.Length) -ne $header.Length -or
            [System.Text.Encoding]::ASCII.GetString($header) -ne '%PDF-') {
            throw "用户手册 PDF 缺少有效文件头：$pdfSource"
        }
    }
    finally {
        $pdfStream.Dispose()
    }

    New-Item -ItemType Directory -Path $manualDestinationDirectory -Force | Out-Null
    Copy-Item -LiteralPath $htmlSource -Destination (Join-Path $manualDestinationDirectory 'DiaryApp-User-Manual.html') -Force
    Copy-Item -LiteralPath $pdfSource -Destination (Join-Path $manualDestinationDirectory 'DiaryApp-User-Manual.pdf') -Force
}

function Remove-UnrelatedRuntimeAssets([string] $PublishDirectory) {
    $runtimesDirectory = Join-Path $PublishDirectory 'runtimes'
    foreach ($runtimeName in @($Rid, 'any')) {
        if (-not (Test-Path -LiteralPath (Join-Path $runtimesDirectory $runtimeName) -PathType Container)) {
            throw "发布目录缺少运行时目录：runtimes/$runtimeName"
        }
    }
    Get-ChildItem -LiteralPath $runtimesDirectory -Directory -Force |
        Where-Object { $_.Name -ne $Rid -and $_.Name -ne 'any' } |
        ForEach-Object {
            $runtimePath = [System.IO.Path]::GetFullPath($_.FullName)
            $publishRoot = [System.IO.Path]::GetFullPath($PublishDirectory).TrimEnd('\') + '\'
            if (-not $runtimePath.StartsWith($publishRoot, [StringComparison]::OrdinalIgnoreCase)) {
                throw "拒绝清理发布目录之外的运行时目录：$runtimePath"
            }
            Remove-Item -LiteralPath $runtimePath -Recurse -Force
        }
}

function Add-EmbeddedPython([string] $PublishDirectory) {
    $cacheDirectory = Join-Path $RepositoryRoot 'artifacts\cache\python'
    $archiveName = "python-$PythonVersion-embed-amd64.zip"
    $cachedArchive = Join-Path $cacheDirectory $archiveName
    New-Item -ItemType Directory -Path $cacheDirectory -Force | Out-Null
    $validCache = (Test-Path -LiteralPath $cachedArchive -PathType Leaf) -and
        ((Get-FileHash -LiteralPath $cachedArchive -Algorithm SHA256).Hash.ToLowerInvariant() -eq $PythonSha256)
    if (-not $validCache) {
        $downloadPath = Join-Path $cacheDirectory (".$archiveName.download.{0}" -f [guid]::NewGuid().ToString('N'))
        Write-Host "正在下载 Python $PythonVersion embeddable runtime……"
        try {
            Invoke-WebRequest -Uri "https://www.python.org/ftp/python/$PythonVersion/$archiveName" -OutFile $downloadPath -TimeoutSec 300
            $downloadHash = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($downloadHash -ne $PythonSha256) {
                throw "Python 包 SHA-256 不匹配：$downloadHash"
            }
            Move-Item -LiteralPath $downloadPath -Destination $cachedArchive -Force
        }
        finally {
            if (Test-Path -LiteralPath $downloadPath) {
                Remove-Item -LiteralPath $downloadPath -Force
            }
        }
    }
    $pythonDirectory = Join-Path $PublishDirectory 'python'
    [System.IO.Compression.ZipFile]::ExtractToDirectory($cachedArchive, $pythonDirectory)
    foreach ($requiredFile in @('python.exe', "python$PythonSeries.dll", "python$PythonSeries.zip")) {
        if (-not (Test-Path -LiteralPath (Join-Path $pythonDirectory $requiredFile) -PathType Leaf)) {
            throw "Python embeddable runtime 缺少文件：python/$requiredFile"
        }
    }
}

function Build-UpdatePackage([long] $BuildSequence) {
    Assert-Command 'dotnet'
    Assert-Command 'git'
    $python = Get-PythonCommand
    $packageLabel = "local-$BuildSequence"
    $flavorSuffix = if ($Flavor -eq 'python313') { '-python313' } else { '' }
    $archiveName = "DiaryAppNG-$packageLabel-$Rid$flavorSuffix.zip"
    $outputDirectory = Join-Path $RepositoryRoot 'artifacts\packages'
    $archivePath = Join-Path $outputDirectory $archiveName
    $publishDirectory = Join-Path $TemporaryDirectory 'publish'
    $updaterDirectory = Join-Path $TemporaryDirectory 'updater'
    $temporaryArchive = Join-Path $TemporaryDirectory $archiveName
    New-Item -ItemType Directory -Path $publishDirectory, $updaterDirectory, $outputDirectory -Force | Out-Null

    $previousSequence = $env:DIARY_BUILD_SEQUENCE
    $previousChannel = $env:DIARY_BUILD_CHANNEL
    try {
        $env:DIARY_BUILD_SEQUENCE = "$BuildSequence"
        $env:DIARY_BUILD_CHANNEL = $Channel
        Write-Host "正在还原并发布 $Rid 自包含应用……"
        Invoke-Native 'dotnet' @('restore', (Join-Path $RepositoryRoot 'DiaryApp.sln'), '--runtime', $Rid, '-p:Configuration=Release')
        Invoke-Native 'dotnet' @('restore', (Join-Path $RepositoryRoot 'Diary.Script.Worker\Diary.Script.Worker.csproj'), '--runtime', $Rid, '-p:Configuration=Release')
        Invoke-Native 'dotnet' @('publish', (Join-Path $RepositoryRoot 'Diary.App\Diary.App.csproj'), '--configuration', 'Release', '--runtime', $Rid, '--self-contained', 'true', '--no-restore', '--output', $publishDirectory)
        Invoke-Native 'dotnet' @('publish', (Join-Path $RepositoryRoot 'Diary.Updater\Diary.Updater.csproj'), '--configuration', 'Release', '--runtime', $Rid, '--self-contained', 'true', '--no-restore', '--output', $updaterDirectory)
    }
    finally {
        $env:DIARY_BUILD_SEQUENCE = $previousSequence
        $env:DIARY_BUILD_CHANNEL = $previousChannel
    }

    Copy-Item -LiteralPath (Join-Path $updaterDirectory 'Diary.Updater.exe') -Destination (Join-Path $publishDirectory 'Diary.Updater.exe') -Force
    $updaterIdentity = & (Join-Path $publishDirectory 'Diary.Updater.exe') --machine-version | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $updaterIdentity.protocolVersion -ne 1 -or $updaterIdentity.rid -ne $Rid) {
        throw "更新器身份异常：$($updaterIdentity | ConvertTo-Json -Compress)"
    }
    Get-ChildItem -LiteralPath $publishDirectory -File -Recurse -Filter '*.pdb' | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Force
    }
    Add-UserManual $publishDirectory
    Assert-PublishOutput $publishDirectory
    Remove-UnrelatedRuntimeAssets $publishDirectory
    if ($Flavor -eq 'python313') {
        Add-EmbeddedPython $publishDirectory
    }

    Write-Host '正在生成并校验更新 ZIP……'
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $publishDirectory,
        $temporaryArchive,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
    Invoke-Native $python @((Join-Path $RepositoryRoot 'Tools\validate-release-package.py'), '--archive', $temporaryArchive, '--rid', $Rid, '--flavor', $Flavor, '--require-user-manual', '--require-script-api')
    Move-Item -LiteralPath $temporaryArchive -Destination $archivePath -Force
    return $archivePath
}

function Publish-UpdatePackage {
    if (-not (Test-ServerReady)) {
        throw "更新服务器未就绪：$ServerUrl。请先运行 .\Tools\local-update.ps1 server-start"
    }
    $publishToken = Get-PublishToken
    $buildSequence = Resolve-Sequence
    $dataVersion = Get-DataVersion
    $versionId = "$dataVersion-r$buildSequence"
    Write-Host "开始构建 local 更新：sequence=$buildSequence, version=$versionId, flavor=$Flavor"
    $archivePath = Build-UpdatePackage $buildSequence
    $packageHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()

    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromMinutes(30)
    $stream = [System.IO.File]::OpenRead($archivePath)
    $content = [System.Net.Http.StreamContent]::new($stream)
    $content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new('application/zip')
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "$ServerUrl/api/v1/internal/publish/local")
    $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $publishToken)
    foreach ($header in ([ordered]@{
        'X-Diary-Channel' = $Channel
        'X-Diary-Sequence' = "$buildSequence"
        'X-Diary-Version-Id' = $versionId
        'X-Diary-Data-Version' = $dataVersion
        'X-Diary-Rid' = $Rid
        'X-Diary-Flavor' = $Flavor
        'X-Diary-Sha256' = $packageHash
    }).GetEnumerator()) {
        $request.Headers.TryAddWithoutValidation($header.Key, $header.Value) | Out-Null
    }
    $request.Content = $content
    try {
        Write-Host "正在上传到 $ServerUrl 的 local 通道……"
        $response = $client.Send($request, [System.Net.Http.HttpCompletionOption]::ResponseContentRead)
        $responseText = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "服务器拒绝 local 更新（HTTP $([int] $response.StatusCode)）：$responseText"
        }
        $published = $responseText | ConvertFrom-Json
        if ([long] $published.release.sequence -ne $buildSequence -or $published.fullPackage.sha256 -ne $packageHash) {
            throw '服务器发布响应与本地 sequence/SHA-256 不一致。'
        }
    }
    finally {
        $request.Dispose()
        $content.Dispose()
        $stream.Dispose()
        $client.Dispose()
    }

    $latest = Get-LatestEnvelope
    if ([long] $latest.manifest.sequence -ne $buildSequence -or $latest.fullPackage.sha256 -ne $packageHash) {
        throw 'latest 回读与本地 sequence/SHA-256 不一致。'
    }
    Write-Host ''
    Write-Host 'local 更新发布与回读校验完成。'
    Write-Host "  服务器：$ServerUrl"
    Write-Host "  频道：$Channel"
    Write-Host "  RID：$Rid"
    Write-Host "  包类型：$Flavor"
    Write-Host "  版本：$versionId"
    Write-Host "  sequence：$buildSequence"
    Write-Host "  本地包：$archivePath"
    Write-Host "  下载页：$ServerUrl/downloads"
}

function Show-Status {
    Write-Host '服务状态：'
    Invoke-JsonGet '/health/status' | ConvertTo-Json -Depth 8
    Write-Host ''
    Write-Host "local latest（$Flavor）："
    $latest = Get-LatestEnvelope
    if ($null -eq $latest) {
        Write-Host '尚未发布。'
        return
    }
    [ordered]@{
        versionId = $latest.manifest.versionId
        sequence = $latest.manifest.sequence
        channel = $latest.manifest.channel
        rid = $latest.manifest.rid
        flavor = $latest.manifest.flavor
        fileCount = @($latest.manifest.files).Count
        packageSize = $latest.fullPackage.size
        packageSha256 = $latest.fullPackage.sha256
    } | ConvertTo-Json
}

function Remove-TemporaryDirectory {
    if (-not (Test-Path -LiteralPath $TemporaryDirectory -PathType Container)) {
        return
    }
    $resolved = (Resolve-Path -LiteralPath $TemporaryDirectory).Path
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Split-Path -Leaf $resolved).StartsWith('diaryapp-local-update-', [StringComparison]::Ordinal)) {
        throw "拒绝清理非预期临时目录：$resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

Assert-ServerUrl
New-Item -ItemType Directory -Path $TemporaryDirectory -Force | Out-Null
try {
    switch ($Command) {
        'publish' { Publish-UpdatePackage }
        'all' {
            Start-UpdateServer
            Publish-UpdatePackage
        }
        'server-start' { Start-UpdateServer }
        'server-stop' { Stop-UpdateServer }
        'status' { Show-Status }
    }
}
finally {
    Remove-TemporaryDirectory
}
