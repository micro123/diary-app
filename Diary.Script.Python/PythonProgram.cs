using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.Script.Py;

public sealed class PythonProgram(
    ScriptDescriptor descriptor,
    string sourcePath,
    string source,
    PythonRuntimeResolution runtime) : IScriptProgramV1
{
    public ScriptDescriptor Descriptor { get; } = descriptor;
    public string SourcePath { get; } = sourcePath;
    public string Source { get; } = source;
    public PythonRuntimeResolution Runtime { get; } = runtime;

    public async ValueTask<ScriptExecutionResult> ExecuteAsync(
        ScriptExecutionRequest request,
        IScriptExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!Runtime.Succeeded || Runtime.ExecutablePath is null)
            return Failure("PYTHON_RUNTIME_NOT_FOUND", "The Python runtime is not available.", ScriptDiagnosticCategory.Runtime);

        var workerDirectory = AppContext.BaseDirectory;
        var supervisor = new WorkerSupervisor(new ProcessWorkerTransportFactory(new WorkerProcessOptions(
            Runtime.ExecutablePath,
            PythonWorkerSource.CreateArguments(),
            workerDirectory)));
        try
        {
            await supervisor.StartAsync(new("python", [ScriptApiVersion.V1, ScriptApiVersion.V2], ["workItems.query"]), cancellationToken);
            var executionId = context.Metadata?.ExecutionId ?? Guid.NewGuid();
            var result = await supervisor.ExecuteAsync(
                Descriptor.Id,
                executionId.ToString("N"),
                new WorkerExecutePayload(Descriptor.Id, SourcePath, Source, request, ApiVersion: Descriptor.ApiVersion),
                cancellationToken: cancellationToken);
            return new ScriptExecutionResult(result.Payload.Status, result.Payload.Diagnostics.ToImmutableArray(), result.Payload.Effects);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ScriptExecutionResult.Cancelled();
        }
        catch (Exception exception)
        {
            return Failure("PYTHON_WORKER_FAILED", exception.Message, ScriptDiagnosticCategory.Runtime);
        }
        finally
        {
            await supervisor.StopAsync(CancellationToken.None);
        }
    }

    private static ScriptExecutionResult Failure(
        string code,
        string message,
        ScriptDiagnosticCategory category) =>
        new(ScriptExecutionStatus.Failed, [new ScriptDiagnostic(
            code,
            message,
            ScriptDiagnosticSeverity.Error,
            category)]);
}

internal sealed record PythonProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal static class PythonProcessRunner
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);

    public static async ValueTask<PythonProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string? standardInput,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        if (standardInput is not null)
            startInfo.StandardInputEncoding = Utf8WithoutBom;
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("The Python process could not be started.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException("The Python process could not be started.", exception);
        }

        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        var token = linkedCancellation.Token;
        var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
        var stderrTask = process.StandardError.ReadToEndAsync(token);
        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), token);
                await process.StandardInput.FlushAsync(token);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(token);
            await Task.WhenAll(stdoutTask, stderrTask);
            return new PythonProcessResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
