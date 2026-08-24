#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, [int]::MaxValue)]
    [int] $TargetProcessId,
    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class DiaryNativeWindowCapture
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(
        IntPtr window,
        int attribute,
        out Rect value,
        int valueSize);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);
}
'@

$process = Get-Process -Id $TargetProcessId -ErrorAction Stop
$window = $process.MainWindowHandle
if ($window -eq [IntPtr]::Zero) {
    throw "进程 $TargetProcessId 没有可截图的主窗口"
}

$targetPath = [IO.Path]::GetFullPath($OutputPath)
$targetDirectory = Split-Path -Parent $targetPath
New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null

$rect = New-Object DiaryNativeWindowCapture+Rect
$rectSize = [Runtime.InteropServices.Marshal]::SizeOf([type][DiaryNativeWindowCapture+Rect])
$dwmResult = [DiaryNativeWindowCapture]::DwmGetWindowAttribute($window, 9, [ref] $rect, $rectSize)
if ($dwmResult -ne 0 -and -not [DiaryNativeWindowCapture]::GetWindowRect($window, [ref] $rect)) {
    throw "无法读取进程 $TargetProcessId 的窗口边界"
}

$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -le 0 -or $height -le 0) {
    throw "进程 $TargetProcessId 的窗口尺寸无效：${width}x${height}"
}

$bitmap = New-Object Drawing.Bitmap $width, $height, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
try {
    $deviceContext = $graphics.GetHdc()
    try {
        if (-not [DiaryNativeWindowCapture]::PrintWindow($window, $deviceContext, 2)) {
            throw "PrintWindow 无法捕获进程 $TargetProcessId"
        }
    }
    finally {
        $graphics.ReleaseHdc($deviceContext)
    }
    $bitmap.SetResolution(96, 96)
    $bitmap.Save($targetPath, [Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

[ordered]@{
    processId = $TargetProcessId
    path = $targetPath
    width = $width
    height = $height
    capture = 'PrintWindow'
} | ConvertTo-Json -Compress
