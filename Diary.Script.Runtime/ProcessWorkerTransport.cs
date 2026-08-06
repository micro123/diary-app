using System.Diagnostics;

namespace Diary.Script.Runtime;

public sealed record WorkerProcessOptions(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null,
    int MaxMessageBytes = WorkerProtocol.DefaultMaxMessageBytes,
    int MaxStderrBytes = 1 * 1024 * 1024);

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
        return ValueTask.FromResult<IWorkerTransport>(new ProcessWorkerTransport(process, options.MaxMessageBytes, options.MaxStderrBytes));
    }
}

public sealed class ProcessWorkerTransport : IWorkerTransport, IWorkerTerminationNotification, IWorkerBoundedTransport
{
    private readonly Process _process;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Task _stderrDrain;

    public event EventHandler<WorkerTerminatedEventArgs>? Terminated;
    public int MaxMessageBytes { get; }
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;
    public bool StderrLimitExceeded { get; private set; }

    public ProcessWorkerTransport(Process process, int maxMessageBytes = WorkerProtocol.DefaultMaxMessageBytes, int maxStderrBytes = 1 * 1024 * 1024)
    {
        if (maxMessageBytes <= 0 || maxStderrBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
        _process = process;
        _input = process.StandardInput.BaseStream;
        _output = process.StandardOutput.BaseStream;
        MaxMessageBytes = maxMessageBytes;
        _stderrDrain = DrainStderrAsync(process.StandardError.BaseStream, maxStderrBytes);
        process.Exited += OnProcessExited;
    }

    public ValueTask SendAsync<TPayload>(WorkerMessage<TPayload> message, CancellationToken cancellationToken = default) =>
        WorkerMessageCodec.WriteAsync(_input, message, MaxMessageBytes, cancellationToken);

    public ValueTask<WorkerMessage<TPayload>> ReceiveAsync<TPayload>(CancellationToken cancellationToken = default) =>
        WorkerMessageCodec.ReadAsync<TPayload>(_output, MaxMessageBytes, cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _process.StandardInput.FlushAsync(cancellationToken);
            _process.StandardInput.Close();
        }
        catch (IOException)
        {
        }
        if (!_process.HasExited)
            await _process.WaitForExitAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _process.Exited -= OnProcessExited;
        _process.Dispose();
        return ValueTask.CompletedTask;
    }

    private void OnProcessExited(object? sender, EventArgs args) =>
        Terminated?.Invoke(this, new WorkerTerminatedEventArgs(ExitCode));

    private async Task DrainStderrAsync(Stream stderr, int maxBytes)
    {
        var buffer = new byte[8192];
        var total = 0;
        try
        {
            while (true)
            {
                var read = await stderr.ReadAsync(buffer);
                if (read == 0)
                    return;
                total += read;
                if (total > maxBytes)
                {
                    StderrLimitExceeded = true;
                    return;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
