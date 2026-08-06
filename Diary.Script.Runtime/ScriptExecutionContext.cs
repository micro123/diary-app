using Diary.ScriptBase;

namespace Diary.Script.Runtime;

public interface IScriptExecutionContextFactory
{
    IScriptExecutionContext Create(ScriptCapability capabilities);
}

public sealed class ScriptExecutionContextFactory(
    Func<ScriptCapability, IScriptExecutionContext> factory) : IScriptExecutionContextFactory
{
    public IScriptExecutionContext Create(ScriptCapability capabilities) => factory(capabilities);
}

public sealed class ScriptExecutionContext(ScriptCapability capabilities) : IScriptExecutionContext
{
    private readonly Dictionary<Type, ApiRegistration> _apis = [];

    public ScriptCapability Capabilities { get; } = capabilities;

    public void RegisterApi<TApi>(TApi api, ScriptCapability requiredCapability = ScriptCapability.None)
        where TApi : class
    {
        ArgumentNullException.ThrowIfNull(api);
        var apiType = typeof(TApi);
        if (api is IServiceProvider || typeof(IServiceProvider).IsAssignableFrom(apiType))
            throw new ArgumentException("IServiceProvider cannot be exposed to scripts.", nameof(api));
        if ((requiredCapability & ~Capabilities) != 0)
            return;
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
        if ((Capabilities & registration.RequiredCapability) != registration.RequiredCapability)
            return null;
        return (TApi)registration.Api;
    }

    private sealed record ApiRegistration(object Api, ScriptCapability RequiredCapability);
}
