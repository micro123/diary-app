using System.Runtime.CompilerServices;
using System.Text;

namespace Diary.Agent.Protocols;

internal sealed record SseEvent(string? Event, string Data);

internal static class SseEventReader
{
    public const int DefaultMaxEventBytes = 1024 * 1024;

    public static async IAsyncEnumerable<SseEvent> ReadAsync(
        Stream stream,
        int maxEventBytes = DefaultMaxEventBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);
        string? eventName = null;
        var data = new StringBuilder();
        var eventBytes = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            eventBytes = checked(eventBytes + Encoding.UTF8.GetByteCount(line) + 1);
            if (eventBytes > maxEventBytes)
            {
                throw new AiModelException(
                    AiModelErrorCategory.Protocol,
                    "sse_event_too_large",
                    "模型流式事件超过大小限制。");
            }
            if (line.Length == 0)
            {
                if (data.Length > 0)
                    yield return new SseEvent(eventName, data.ToString().TrimEnd('\r', '\n'));
                eventName = null;
                data.Clear();
                eventBytes = 0;
                continue;
            }
            if (line.StartsWith(':'))
                continue;
            var separator = line.IndexOf(':');
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? string.Empty : line[(separator + 1)..].TrimStart(' ');
            if (field == "event")
                eventName = value;
            else if (field == "data")
                data.AppendLine(value);
        }
        if (data.Length > 0)
            yield return new SseEvent(eventName, data.ToString().TrimEnd('\r', '\n'));
    }
}
