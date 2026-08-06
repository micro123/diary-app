using System.Diagnostics;

namespace Diary.Script.Runtime;

public sealed record WorkerProcessOptions(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null);

public sealed class ProcessWorkerTransportFactory(WorkerProcessOptions options) : IWorkerTransportFactory
{
    public ValueTask<IWorkerTransport> CreateAsync(CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(options.ExecutablePath))
            throw new ArgumentException("Worker 可执行文件必须使用绝对路径。", nameof(options));
        if (!Path.IsPathFullyQualified(options.WorkingDirectory))
            throw new ArgumentException("Worker 工作目录必须使用绝对路径。", nameof(options));

        var startInfo = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in options.Arguments)
            startInfo.ArgumentList.Add(argument);
        if (options.Environment is not null)
        {
            startInfo.Environment.Clear();
            foreach (var pair in options.Environment)
                startInfo.Environment[pair.Key] = pair.Value;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("无法启动 Worker 进程。");
        return ValueTask.FromResult<IWorkerTransport>(new ProcessWorkerTransport(process));
    }
}

public sealed class ProcessWorkerTransport(Process process) : IWorkerTransport
{
    private readonly Stream _input = process.StandardInput.BaseStream;
    private readonly Stream _output = process.StandardOutput.BaseStream;

    public ValueTask SendAsync<TPayload>(WorkerMessage<TPayload> message, CancellationToken cancellationToken = default) =>
        WorkerMessageCodec.WriteAsync(_input, message, cancellationToken: cancellationToken);

    public ValueTask<WorkerMessage<TPayload>> ReceiveAsync<TPayload>(CancellationToken cancellationToken = default) =>
        WorkerMessageCodec.ReadAsync<TPayload>(_output, cancellationToken: cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }
        if (!process.HasExited)
            await process.WaitForExitAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        process.Dispose();
        return ValueTask.CompletedTask;
    }
}
