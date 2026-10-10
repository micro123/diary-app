using System.Net;

namespace Diary.Mcp.Remote;

internal sealed class PeerRequestRateLimiter(int requestsPerMinute)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly Dictionary<IPAddress, Queue<DateTimeOffset>> _requests = [];
    private readonly Lock _sync = new();

    public bool TryAcquire(IPAddress address, DateTimeOffset nowUtc, out TimeSpan retryAfter)
    {
        address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        lock (_sync)
        {
            if (!_requests.TryGetValue(address, out var timestamps))
            {
                timestamps = new Queue<DateTimeOffset>();
                _requests[address] = timestamps;
            }

            while (timestamps.TryPeek(out var timestamp) && nowUtc - timestamp >= Window)
                timestamps.Dequeue();
            if (timestamps.Count >= requestsPerMinute)
            {
                retryAfter = Window - (nowUtc - timestamps.Peek());
                return false;
            }

            timestamps.Enqueue(nowUtc);
            retryAfter = TimeSpan.Zero;
            if (_requests.Count > 256)
                PruneEmptyQueues();
            return true;
        }
    }

    private void PruneEmptyQueues()
    {
        foreach (var address in _requests.Where(item => item.Value.Count == 0).Select(item => item.Key).ToArray())
            _requests.Remove(address);
    }
}
