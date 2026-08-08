using Diary.ScriptBase;

namespace Diary.ScriptHost;

public sealed class WorkerDiaryApiProxy(
    IWorkItemQueryScriptApi query,
    ILogItemScriptApi logItems,
    ITemplateLogItemScriptApi templateLogItems) : IDiaryApi
{
    private readonly DiaryApi _inner = new(query, logItems, templateLogItems);
    public ValueTask<ScriptWorkItemQueryResult> QueryAsync(ScriptWorkItemQuery request, CancellationToken cancellationToken = default) => _inner.QueryAsync(request, cancellationToken);
    public IAsyncEnumerable<ScriptWorkItem> StreamAsync(ScriptWorkItemQuery request, int pageSize = 500, CancellationToken cancellationToken = default) => _inner.StreamAsync(request, pageSize, cancellationToken);
    public ValueTask<ScriptLogItemResult> CreateLogItemAsync(ScriptLogItemRequest request, CancellationToken cancellationToken = default) => _inner.CreateLogItemAsync(request, cancellationToken);
    public ValueTask<ScriptLogItemResult> CreateFromTemplateAsync(ScriptTemplateLogItemRequest request, CancellationToken cancellationToken = default) => _inner.CreateFromTemplateAsync(request, cancellationToken);
}
