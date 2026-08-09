using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.Script.Py;

public sealed class PythonWorkerTransportFactory(
    PythonRuntimeResolver runtimeResolver) : IWorkerTransportFactory
{
    public async ValueTask<IWorkerTransport> CreateAsync(CancellationToken cancellationToken = default)
    {
        var runtime = await runtimeResolver.ResolveAsync(cancellationToken: cancellationToken);
        if (!runtime.Succeeded || runtime.ExecutablePath is null)
        {
            var diagnostic = runtime.Diagnostics.FirstOrDefault() ?? new ScriptDiagnostic(
                "PYTHON_RUNTIME_NOT_FOUND",
                "Python runtime is unavailable.",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Runtime);
            throw new WorkerRuntimeUnavailableException(diagnostic);
        }

        return await new ProcessWorkerTransportFactory(new WorkerProcessOptions(
            runtime.ExecutablePath,
            PythonWorkerSource.CreateArguments(),
            AppContext.BaseDirectory,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PYTHONIOENCODING"] = "utf-8",
                ["PYTHONUNBUFFERED"] = "1",
            })).CreateAsync(cancellationToken);
    }
}
