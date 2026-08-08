using Diary.App.Models;
using Diary.Script.Runtime;

namespace Diary.AppTests;

[TestClass]
public sealed class ScriptDirectoryLoadStateTests
{
    [TestMethod]
    public async Task EnsureLoadedAsyncSharesInFlightAndCompletedResults()
    {
        var loader = new DelayedDirectoryLoader();
        var state = new ScriptDirectoryLoadState(loader);
        var root = Path.Combine(Path.GetTempPath(), "diary-script-state-tests");

        var first = state.EnsureLoadedAsync(root);
        var second = state.EnsureLoadedAsync(root);
        Assert.AreSame(first, second);

        await loader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        loader.Result.TrySetResult(new ScriptDirectoryLoadResult([], []));
        await first;

        Assert.AreSame(first, state.EnsureLoadedAsync(root));
        var reloaded = state.ReloadAsync(root);
        Assert.AreNotSame(first, reloaded);
        await reloaded;
        Assert.AreEqual(2, loader.Calls);
    }

    private sealed class DelayedDirectoryLoader : IScriptDirectoryLoader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ScriptDirectoryLoadResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;

        public async ValueTask<ScriptDirectoryLoadResult> LoadAsync(
            string rootDirectory,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            return await Result.Task.WaitAsync(cancellationToken);
        }
    }
}
