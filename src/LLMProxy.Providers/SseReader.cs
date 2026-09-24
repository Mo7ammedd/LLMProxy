using System.Runtime.CompilerServices;
using System.Text;
using LLMProxy.Domain;

namespace LLMProxy.Providers;

public sealed record SseEvent(string Event, string Data);

public static class SseReader
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream body, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var buffer = new char[4096];
        var line = new StringBuilder();
        var data = new StringBuilder();
        var eventName = "message";
        while (true)
        {
            int read;
            try { read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken); }
            catch (IOException) { throw new ProviderException("provider_connection_error", true); }
            if (read == 0) break;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != '\n')
                {
                    line.Append(buffer[i]);
                    if (line.Length + data.Length > 1_048_576) throw new ProviderException("provider_event_too_large", false);
                    continue;
                }
                var value = line.ToString().TrimEnd('\r');
                line.Clear();
                if (value.Length == 0)
                {
                    if (data.Length > 0)
                    {
                        yield return new SseEvent(eventName, data.ToString(0, data.Length - 1));
                        data.Clear();
                    }
                    eventName = "message";
                }
                else if (value.StartsWith("data:", StringComparison.Ordinal))
                {
                    var field = value.AsSpan(5);
                    if (field.StartsWith(" ")) field = field[1..];
                    data.Append(field).Append('\n');
                }
                else if (value.StartsWith("event:", StringComparison.Ordinal)) eventName = value[6..].TrimStart(' ');
            }
        }
        // SSE dispatches only on blank lines. Adapters treat a missing protocol terminator as an interrupted stream.
    }
}
