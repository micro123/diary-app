using System.Diagnostics;

namespace Diary.Script.Runtime;

public sealed record WorkerProcessOptions(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null,
    int MaxMessageBytes = WorkerProtocol.DefaultMaxMessageBytes,
    int MaxStderrBytes = 1 * 1024 * 1024,
    TimeSpan? ShutdownGracePeriod = null);

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
        return ValueTask.FromResult<IWorkerTransport>(new ProcessWorkerTransport(
            process,
            options.MaxMessageBytes,
            options.MaxStderrBytes,
            options.ShutdownGracePeriod));
    }
}

public sealed class ProcessWorkerTransport : IWorkerTransport, IWorkerTerminationNotification, IWorkerBoundedTransport, IWorkerResourceUsage
{
    private readonly Process _process;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Task _stderrDrain;
    private readonly TimeSpan _shutdownGracePeriod;

    public event EventHandler<WorkerTerminatedEventArgs>? Terminated;
    public int MaxMessageBytes { get; }
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;
    public bool StderrLimitExceeded { get; private set; }
    public long? WorkingSetBytes => _process.HasExited ? null : _process.WorkingSet64;

    public ProcessWorkerTransport(
        Process process,
        int maxMessageBytes = WorkerProtocol.DefaultMaxMessageBytes,
        int maxStderrBytes = 1 * 1024 * 1024,
        TimeSpan? shutdownGracePeriod = null)
    {
        if (maxMessageBytes <= 0 || maxStderrBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
        _process = process;
        _input = process.StandardInput.BaseStream;
        _output = process.StandardOutput.BaseStream;
        MaxMessageBytes = maxMessageBytes;
        _shutdownGracePeriod = shutdownGracePeriod ?? TimeSpan.FromSeconds(2);
        _stderrDrain = DrainStderrAsync(process.StandardError.BaseStream, maxStderrBytes);
        process.Exited += OnProcessExited;
    }

    public async ValueTask SendAsync<TPayload>(WorkerMessage<TPayload> message, CancellationToken cancellationToken = default)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            await WorkerMessageCodec.WriteAsync(_input, message, MaxMessageBytes, cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public ValueTask<WorkerMessage<TPayload>> ReceiveAsync<TPayload>(
        CancellationToken cancellationToken = default,
        int maxMessageBytes = WorkerProtocol.DefaultMaxMessageBytes) =>
        WorkerMessageCodec.ReadAsync<TPayload>(_output, maxMessageBytes, cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _process.StandardInput.FlushAsync(cancellationToken);
            _process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
        }
        if (_process.HasExited)
            return;

        using var graceCancellation = new CancellationTokenSource(_shutdownGracePeriod);
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            graceCancellation.Token);
        try
        {
            await _process.WaitForExitAsync(waitCancellation.Token);
        }
        catch (OperationCanceledException) when (graceCancellation.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(CancellationToken.None);
        }
    }

    public ValueTask DisposeAsync()
    {
        _process.Exited -= OnProcessExited;
        _process.Dispose();
        _sendGate.Dispose();
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
