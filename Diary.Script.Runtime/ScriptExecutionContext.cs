using System.Collections.Immutable;
using Diary.ScriptBase;

namespace Diary.Script.Runtime;

public interface IScriptExecutionContextFactory
{
    IScriptExecutionContext Create(
        ScriptExecutionMetadata metadata,
        ScriptExecutionRequest request);
}

public sealed class ScriptExecutionContextFactory(
    Func<ScriptExecutionMetadata, ScriptExecutionRequest, IScriptExecutionContext> factory) : IScriptExecutionContextFactory
{
    public IScriptExecutionContext Create(
        ScriptExecutionMetadata metadata,
        ScriptExecutionRequest request) => factory(metadata, request);
}

public sealed class ScriptExecutionContext(
    ScriptExecutionMetadata? metadata = null,
    ScriptEditorTarget? target = null,
    ImmutableDictionary<string, string>? arguments = null,
    Func<ScriptDateRange, CancellationToken, IAsyncEnumerable<ScriptWorkItem>>? streamItems = null)
    : IScriptApplicationContext, IScriptEditorContext
{
    private readonly Dictionary<Type, ApiRegistration> _apis = [];

    public ScriptExecutionMetadata? Metadata { get; } = metadata;

    public ScriptEditorTarget? EditorTarget { get; } = target;

    ScriptEditorTarget IScriptEditorContext.Target => EditorTarget
        ?? throw new InvalidOperationException("编辑器上下文必须提供目标。");

    public ScriptWorkItem? WorkItem => EditorTarget?.WorkItem;

    public IReadOnlyDictionary<string, string> Arguments { get; } =
        arguments ?? ImmutableDictionary<string, string>.Empty;

    public ScriptDateRange? GetDateRange() => EditorTarget is null
        ? null
        : ScriptEditorTargetResolver.GetDateRange(EditorTarget);

    public IAsyncEnumerable<ScriptWorkItem> StreamItemsAsync(
        CancellationToken cancellationToken = default)
    {
        var range = GetDateRange()
            ?? throw new InvalidOperationException("当前脚本目标没有日期范围。");
        return streamItems is null
            ? throw new InvalidOperationException("当前脚本上下文未配置事项迭代 API。")
            : streamItems(range, cancellationToken);
    }

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
