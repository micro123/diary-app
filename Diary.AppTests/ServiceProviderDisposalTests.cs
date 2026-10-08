using Diary.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.AppTests;

[TestClass]
public sealed class ServiceProviderDisposalTests
{
    [TestMethod]
    public async Task DisposeAsync_UsesAsyncContainerDisposalForAsyncOnlyService()
    {
        var services = new ServiceCollection();
        services.AddSingleton<AsyncOnlyService>(_ => new AsyncOnlyService());
        var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<AsyncOnlyService>();

        await ServiceProviderDisposal.DisposeAsync(provider);

        Assert.IsTrue(service.IsDisposed);
    }

    [TestMethod]
    public async Task DisposeAsync_FallsBackToSynchronousDisposable()
    {
        var provider = new SyncOnlyServiceProvider();

        await ServiceProviderDisposal.DisposeAsync(provider);

        Assert.IsTrue(provider.IsDisposed);
    }

    private sealed class AsyncOnlyService : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SyncOnlyServiceProvider : IServiceProvider, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public object? GetService(Type serviceType) => null;

        public void Dispose() => IsDisposed = true;
    }
}
