using Diary.Script.Runtime;
using Diary.Utils;

namespace Diary.App.Models;

[DiAutoRegister(singleton: true)]
public sealed class ScriptDirectoryLoadState(IScriptDirectoryLoader directoryLoader)
{
    private readonly object _sync = new();
    private Task<ScriptDirectoryLoadResult>? _loadTask;
    private string? _rootDirectory;
    private ScriptDirectoryLoadResult? _latestResult;

    public ScriptDirectoryLoadResult? LatestResult
    {
        get
        {
            lock (_sync)
                return _latestResult;
        }
    }

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
            _loadTask = Task.Run(() => LoadAndRememberAsync(normalizedRoot));
            return _loadTask;
        }
    }

    private async Task<ScriptDirectoryLoadResult> LoadAndRememberAsync(string rootDirectory)
    {
        var result = await directoryLoader.LoadAsync(rootDirectory).ConfigureAwait(false);
        lock (_sync)
        {
            if (string.Equals(_rootDirectory, rootDirectory, StringComparison.Ordinal))
                _latestResult = result;
        }
        return result;
    }
}
