using System.Diagnostics;
using System.Text.Json;

namespace Diary.Update;

public static class UpdateProcessServices
{
    public static async ValueTask WaitForExitAsync(
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0 || processId == Environment.ProcessId)
            throw new ArgumentOutOfRangeException(nameof(processId));
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return;
        }
        using (process)
        using (var timeoutCancellation = new CancellationTokenSource(timeout))
        using (var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token))
        {
            try
            {
                await process.WaitForExitAsync(linkedCancellation.Token);
            }
            catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"等待主程序退出超时：PID={processId}");
            }
        }
    }

    public static async ValueTask<UpdateMachineVersion> ProbeUpdaterAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--machine-version");
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("无法启动目标更新器版本探针。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderr = process.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"目标更新器版本探针失败：{await stderr}");
        return JsonSerializer.Deserialize(await stdout, UpdateJson.Context.UpdateMachineVersion)
            ?? throw new InvalidDataException("目标更新器版本探针没有返回有效 JSON。");
    }

    public static Process StartUpdater(
        string executablePath,
        string planPath,
        string handoffToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--apply");
        startInfo.ArgumentList.Add(planPath);
        startInfo.ArgumentList.Add("--handoff-token");
        startInfo.ArgumentList.Add(handoffToken);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动目标更新器。");
    }

    public static Process StartApply(string executablePath, string planPath, int waitProcessId)
    {
        if (waitProcessId <= 0)
            throw new ArgumentOutOfRangeException(nameof(waitProcessId));
        var startInfo = CreateUpdaterStartInfo(executablePath, "--apply", planPath);
        startInfo.ArgumentList.Add("--wait-pid");
        startInfo.ArgumentList.Add(waitProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动更新器。");
    }

    public static Process StartRecovery(string executablePath, string planPath, int waitProcessId)
    {
        if (waitProcessId <= 0)
            throw new ArgumentOutOfRangeException(nameof(waitProcessId));
        var startInfo = CreateUpdaterStartInfo(executablePath, "--recover", planPath);
        startInfo.ArgumentList.Add("--wait-pid");
        startInfo.ArgumentList.Add(waitProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动更新恢复程序。");
    }

    private static ProcessStartInfo CreateUpdaterStartInfo(
        string executablePath,
        string command,
        string planPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add(planPath);
        return startInfo;
    }
}
