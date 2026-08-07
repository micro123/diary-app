using Diary.ScriptBase;

namespace Diary.Script.Runtime;

public interface IScriptExecutionContextFactory
{
    IScriptExecutionContext Create(ScriptCapability capabilities, ScriptExecutionMetadata metadata);
}

public sealed class ScriptExecutionContextFactory(
    Func<ScriptCapability, ScriptExecutionMetadata, IScriptExecutionContext> factory) : IScriptExecutionContextFactory
{
    public IScriptExecutionContext Create(ScriptCapability capabilities, ScriptExecutionMetadata metadata) =>
        factory(capabilities, metadata);
}

public sealed class ScriptExecutionContext(
    ScriptCapability capabilities,
    ScriptExecutionMetadata? metadata = null) : IScriptExecutionContext
{
    private readonly Dictionary<Type, ApiRegistration> _apis = [];

    public ScriptCapability Capabilities { get; } = capabilities;
    public ScriptExecutionMetadata? Metadata { get; } = metadata;

    public void RegisterApi<TApi>(TApi api, ScriptCapability requiredCapability = ScriptCapability.None)
        where TApi : class
    {
        ArgumentNullException.ThrowIfNull(api);
        var apiType = typeof(TApi);
        if (api is IServiceProvider || typeof(IServiceProvider).IsAssignableFrom(apiType))
            throw new ArgumentException("IServiceProvider cannot be exposed to scripts.", nameof(api));
        if (!_apis.TryAdd(apiType, new ApiRegistration(api, requiredCapability)))
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

    private sealed record ApiRegistration(object Api, ScriptCapability RequiredCapability);
}
