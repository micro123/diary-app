using Diary.Script.Runtime;

namespace Diary.Script.Py;

public sealed class PythonWorkerTransportFactory(
    PythonRuntimeResolver runtimeResolver) : IWorkerTransportFactory
{
    public async ValueTask<IWorkerTransport> CreateAsync(CancellationToken cancellationToken = default)
    {
        var runtime = await runtimeResolver.ResolveAsync(cancellationToken: cancellationToken);
        if (!runtime.Succeeded || runtime.ExecutablePath is null)
        {
            var diagnostic = runtime.Diagnostics.FirstOrDefault();
            throw new InvalidOperationException(diagnostic?.Message ?? "Python runtime is unavailable.");
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
