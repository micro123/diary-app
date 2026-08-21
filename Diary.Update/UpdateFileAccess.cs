using System.ComponentModel;

namespace Diary.Update;

internal static class UpdateFileAccess
{
    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
    ];

    public static async ValueTask<T> ExecuteWithSharingRetryAsync<T>(
        Func<ValueTask<T>> operation,
        CancellationToken cancellationToken = default,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var delays = retryDelays ?? DefaultRetryDelays;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception exception) when (IsSharingViolation(exception) && attempt < delays.Count)
            {
                await Task.Delay(delays[attempt], cancellationToken);
            }
        }
    }

    public static bool IsSharingViolation(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            Win32Exception win32 => win32.NativeErrorCode is 32 or 33,
            IOException io => (io.HResult & 0xFFFF) is 32 or 33,
            _ => false,
        };
    }
}
