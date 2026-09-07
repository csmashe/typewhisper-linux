using System.Runtime.CompilerServices;
using System.Text;

namespace TypeWhisper.PluginSDK.Helpers;

/// <summary>A single dispatched server-sent event.</summary>
public readonly record struct SseEvent(
    string Data,
    string EventType,
    string LastEventId);

/// <summary>The result of applying a provider's stream policy to an SSE event.</summary>
public readonly record struct SsePolicyDecision<TDelta>(
    bool HasDelta = false,
    TDelta? Delta = default,
    bool AcceptTerminal = false,
    bool EndStream = false,
    Exception? Error = null);

/// <summary>Provider-specific interpretation of framed SSE events.</summary>
public interface ISseEventPolicy<TDelta>
{
    string StreamName { get; }
    string ExpectedTerminal { get; }

    SsePolicyDecision<TDelta> Evaluate(SseEvent sseEvent);
}

/// <summary>Raised when an SSE stream ends without its provider's accepted terminal.</summary>
public sealed class IncompleteSseStreamException : InvalidOperationException
{
    public IncompleteSseStreamException(string streamName, string expectedTerminal)
        : base($"Incomplete {streamName}: expected {expectedTerminal} before the stream ended.")
    {
        StreamName = streamName;
        ExpectedTerminal = expectedTerminal;
    }

    public string StreamName { get; }
    public string ExpectedTerminal { get; }
}

/// <summary>WHATWG-compatible SSE framing plus provider-policy validation.</summary>
public static class SseEventDecoder
{
    public static async IAsyncEnumerable<SseEvent> DecodeAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var data = new StringBuilder();
        var eventType = "";
        var lastEventId = "";

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length == 0)
                {
                    // Per WHATWG dispatch, a blank line always resets the pending event
                    // type - an event-only block must not label the next event.
                    eventType = "";
                    continue;
                }

                data.Length--;
                yield return new SseEvent(
                    data.ToString(),
                    eventType.Length == 0 ? "message" : eventType,
                    lastEventId);
                data.Clear();
                eventType = "";
                continue;
            }

            if (line[0] == ':')
                continue;

            var colonIndex = line.IndexOf(':');
            var field = colonIndex < 0 ? line : line[..colonIndex];
            var value = colonIndex < 0 ? "" : line[(colonIndex + 1)..];
            if (value.StartsWith(' '))
                value = value[1..];

            switch (field)
            {
                case "data":
                    data.Append(value);
                    data.Append('\n');
                    break;
                case "event":
                    eventType = value;
                    break;
                case "id" when !value.Contains('\0'):
                    lastEventId = value;
                    break;
            }
        }
    }

    public static async IAsyncEnumerable<TDelta> ReadValidatedAsync<TDelta>(
        TextReader reader,
        ISseEventPolicy<TDelta> policy,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var acceptedTerminal = false;

        await foreach (var sseEvent in DecodeAsync(reader, cancellationToken))
        {
            var decision = policy.Evaluate(sseEvent);
            if (decision.Error is not null)
                throw decision.Error;

            acceptedTerminal |= decision.AcceptTerminal;

            if (decision.HasDelta)
                yield return decision.Delta!;

            if (!decision.EndStream)
                continue;

            if (!acceptedTerminal)
            {
                throw new IncompleteSseStreamException(
                    policy.StreamName,
                    policy.ExpectedTerminal);
            }

            yield break;
        }

        if (!acceptedTerminal)
        {
            throw new IncompleteSseStreamException(
                policy.StreamName,
                policy.ExpectedTerminal);
        }
    }
}
