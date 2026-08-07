using Diary.ScriptBase;

namespace Diary.Script.Runtime;

public interface IScriptExecutionContextFactory
{
    IScriptExecutionContext Create(ScriptExecutionMetadata metadata);
}

public sealed class ScriptExecutionContextFactory(
    Func<ScriptExecutionMetadata, IScriptExecutionContext> factory) : IScriptExecutionContextFactory
{
    public IScriptExecutionContext Create(ScriptExecutionMetadata metadata) => factory(metadata);
}

public sealed class ScriptExecutionContext(ScriptExecutionMetadata? metadata = null) : IScriptExecutionContext
{
    private readonly Dictionary<Type, ApiRegistration> _apis = [];

    public ScriptExecutionMetadata? Metadata { get; } = metadata;

    public void RegisterApi<TApi>(TApi api)
        where TApi : class
    {
        ArgumentNullException.ThrowIfNull(api);
        var apiType = typeof(TApi);
        if (api is IServiceProvider || typeof(IServiceProvider).IsAssignableFrom(apiType))
            throw new ArgumentException("IServiceProvider cannot be exposed to scripts.", nameof(api));
        if (!_apis.TryAdd(apiType, new ApiRegistration(api)))
            throw new InvalidOperationException($"An API of type '{apiType.Name}' is already registered.");
    }

    public TApi? GetApi<TApi>() where TApi : class
    {
        var apiType = typeof(TApi);
        if (typeof(IServiceProvider).IsAssignableFrom(apiType))
            return null;
        if (!_apis.TryGetValue(apiType, out var registration))
            return null;
        return (TApi)registration.Api;
    }

    private sealed record ApiRegistration(object Api);
}
