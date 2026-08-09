using Diary.ScriptBase;

namespace Diary.ScriptHost;

public sealed class WorkerDiaryApiProxy(
    IWorkItemQueryScriptApi query,
    ILogItemScriptApi logItems,
    ITemplateLogItemScriptApi templateLogItems,
    ITemplateScriptApi? templates = null,
    IHostCapabilitiesScriptApi? host = null) : IDiaryApi
{
    private readonly DiaryApi _inner = new(query, logItems, templateLogItems, templates, host);
    public ITemplateScriptApi Templates => _inner.Templates;
    public IHostCapabilitiesScriptApi Host => _inner.Host;
    public ValueTask<ScriptWorkItemQueryResult> QueryAsync(ScriptWorkItemQuery request, CancellationToken cancellationToken = default) => _inner.QueryAsync(request, cancellationToken);
    public IAsyncEnumerable<ScriptWorkItem> StreamAsync(ScriptWorkItemQuery request, int pageSize = 500, CancellationToken cancellationToken = default) => _inner.StreamAsync(request, pageSize, cancellationToken);
    public ValueTask<ScriptLogItemResult> CreateLogItemAsync(ScriptLogItemRequest request, CancellationToken cancellationToken = default) => _inner.CreateLogItemAsync(request, cancellationToken);
    public ValueTask<ScriptLogItemResult> CreateFromTemplateAsync(ScriptTemplateLogItemRequest request, CancellationToken cancellationToken = default) => _inner.CreateFromTemplateAsync(request, cancellationToken);
}
