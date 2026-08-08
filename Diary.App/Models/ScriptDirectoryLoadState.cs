using Diary.Script.Runtime;
using Diary.Utils;

namespace Diary.App.Models;

[DiAutoRegister(singleton: true)]
public sealed class ScriptDirectoryLoadState(IScriptDirectoryLoader directoryLoader)
{
    private readonly object _sync = new();
    private Task<ScriptDirectoryLoadResult>? _loadTask;
    private string? _rootDirectory;

    public Task<ScriptDirectoryLoadResult> EnsureLoadedAsync(string rootDirectory)
        => GetOrStart(rootDirectory, forceReload: false);

    public Task<ScriptDirectoryLoadResult> ReloadAsync(string rootDirectory)
        => GetOrStart(rootDirectory, forceReload: true);

    private Task<ScriptDirectoryLoadResult> GetOrStart(string rootDirectory, bool forceReload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var normalizedRoot = Path.GetFullPath(rootDirectory);
        lock (_sync)
        {
            if (_loadTask is not null
                && string.Equals(_rootDirectory, normalizedRoot, StringComparison.Ordinal)
                && !_loadTask.IsFaulted
                && !_loadTask.IsCanceled
                && (!forceReload || !_loadTask.IsCompleted))
            {
                return _loadTask;
            }

            _rootDirectory = normalizedRoot;
            _loadTask = Task.Run(async () =>
                await directoryLoader.LoadAsync(normalizedRoot).ConfigureAwait(false));
            return _loadTask;
        }
    }
}
