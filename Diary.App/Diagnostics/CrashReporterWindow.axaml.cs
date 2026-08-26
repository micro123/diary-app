using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Diary.Utils;

namespace Diary.App.Diagnostics;

internal sealed partial class CrashReporterWindow : Window
{
    private readonly CrashReportResult _result;

    public CrashReporterWindow(CrashReportResult result)
    {
        _result = result;
        InitializeComponent();
        var request = result.Request;
        var occurredAtLocal = request.OccurredAtUtc.ToLocalTime();
        ExceptionTypeText.Text = $"{request.ExceptionType} · {occurredAtLocal:yyyy-MM-dd HH:mm:ss}";
        ExceptionMessageText.Text = string.IsNullOrWhiteSpace(request.ExceptionMessage)
            ? "未提供异常消息。"
            : request.ExceptionMessage;
        DumpStatusText.Text = result.DumpSucceeded
            ? $"Dump 已生成（{FormatSize(result.DumpSizeBytes ?? 0)}）"
            : $"Dump 生成失败：{result.ErrorMessage ?? "未知错误"}";
        DumpPathText.Text = result.DumpSucceeded
            ? request.DumpPath
            : $"诊断目录：{request.DumpDirectory}";
        LogStatusText.Text = result.LogArchiveSucceeded
            ? $"滚动日志已归档（{FormatSize(result.LogArchiveSizeBytes ?? 0)}）"
            : $"日志归档失败：{result.LogArchiveErrorMessage ?? "未知错误"}";
        LogPathText.Text = result.LogArchiveSucceeded
            ? request.LogArchivePath
            : $"日志目录：{request.LogDirectory}";
    }

    private void OnBeginMoveDrag(object? sender, PointerPressedEventArgs args)
    {
        if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(args);
    }

    private void OnOpenDumpFolder(object? sender, RoutedEventArgs args)
    {
        Directory.CreateDirectory(_result.Request.DumpDirectory);
        ProcUtils.OpenDirectoryCrossPlatform(_result.Request.DumpDirectory);
    }

    private void OnClose(object? sender, RoutedEventArgs args) => Close();

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }
        return $"{value:0.##} {units[unitIndex]}";
    }
}
