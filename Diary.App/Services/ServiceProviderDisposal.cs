namespace Diary.App.Services;

internal static class ServiceProviderDisposal
{
    public static async ValueTask DisposeAsync(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        if (serviceProvider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
            return;
        }

        if (serviceProvider is IDisposable disposable)
            disposable.Dispose();
    }
}
