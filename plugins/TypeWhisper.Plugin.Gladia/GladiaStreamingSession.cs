using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Gladia;

/// <summary>
///     Real-time Gladia transcription over their v2 live API. Two-step handshake:
///     <c>POST /v2/live</c> (with the <c>x-gladia-key</c> header) returns a
///     tokenized WebSocket URL, which we then open. Gladia emits one
///     <c>transcript</c> message per utterance with a <c>data.is_final</c> flag, so
///     this session follows the Deepgram per-message model — finals land in the
///     host coordinator live as utterances complete.
/// </summary>
internal sealed class GladiaStreamingSession : IStreamingSession
{
    private const string InitUrl = "https://api.gladia.io/v2/live";

    private readonly ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _receiveCts = new();
    private readonly TaskCompletionSource _finishedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _receiveTask;

    public event Action<StreamingTranscriptEvent>? TranscriptReceived;

    public static async Task<GladiaStreamingSession> ConnectAsync(
        HttpClient httpClient,
        string apiKey,
        string? language,
        CancellationToken ct
    )
    {
        var session = new GladiaStreamingSession();

        using var request = new HttpRequestMessage(HttpMethod.Post, InitUrl);
        request.Headers.Add("x-gladia-key", apiKey);
        request.Content = new StringContent(
            BuildInitRequest(language, 16000),
            Encoding.UTF8,
            "application/json"
        );

        using var response = await httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Gladia live init failed {(int)response.StatusCode}: {json}"
            );

        var url = ParseSessionUrl(json)
            ?? throw new InvalidOperationException("Gladia live init response missing 'url'.");

        await session._ws.ConnectAsync(new Uri(url), ct);
        session._receiveTask = session.ReceiveLoopAsync(session._receiveCts.Token);
        return session;
    }

    public async Task SendAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken ct)
    {
        if (_ws.State != WebSocketState.Open || pcm16Audio.Length == 0)
            return;
        // Gladia detects raw bytes vs base64; send raw PCM16 as binary frames.
        await _ws.SendAsync(pcm16Audio, WebSocketMessageType.Binary, true, ct);
    }

    public async Task FinalizeAsync(CancellationToken ct)
    {
        if (_ws.State == WebSocketState.Open)
        {
            try
            {
                var stop = Encoding.UTF8.GetBytes("""{"type":"stop_recording"}""");
                await _ws.SendAsync(stop, WebSocketMessageType.Text, true, ct);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // Couldn't signal stop, but still await the post-processing flush below.
            }
        }

        // Gladia post-processes after stop_recording and can emit trailing final
        // utterances before closing (code 1000). Wait for the receive loop to observe
        // that close so a late final lands in the coordinator's final-segment buffer
        // before it snapshots, instead of committing a truncated transcript. Bounded
        // by the caller's ct/timeout; a cancel/timeout is the grace-window backstop.
        try { await _finishedTcs.Task.WaitAsync(ct); }
        catch (OperationCanceledException) { /* coordinator's grace window backstops */ }
    }

    internal static string BuildInitRequest(string? language, int sampleRate)
    {
        var body = new Dictionary<string, object>
        {
            ["encoding"] = "wav/pcm",
            ["bit_depth"] = 16,
            ["sample_rate"] = sampleRate,
            ["channels"] = 1,
            ["messages_config"] = new Dictionary<string, object>
            {
                ["receive_partial_transcripts"] = true,
                ["receive_final_transcripts"] = true,
            },
        };

        if (!string.IsNullOrWhiteSpace(language)
            && !string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
        {
            body["language_config"] = new Dictionary<string, object>
            {
                ["languages"] = new[] { language },
            };
        }

        return JsonSerializer.Serialize(body);
    }

    internal static string? ParseSessionUrl(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("url", out var urlEl)
                && urlEl.ValueKind == JsonValueKind.String
                ? urlEl.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal readonly record struct GladiaMessage(
        string MessageType,
        string? Text,
        bool IsFinal
    );

    /// <summary>Reflection-free, never-throws parse of one Gladia frame.</summary>
    internal static GladiaMessage ParseMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var type = root.TryGetProperty("type", out var typeEl)
                && typeEl.ValueKind == JsonValueKind.String
                ? typeEl.GetString() ?? ""
                : "";

            if (type != "transcript"
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                return new GladiaMessage(type, null, false);
            }

            var isFinal = data.TryGetProperty("is_final", out var finalEl)
                && finalEl.ValueKind == JsonValueKind.True;

            string? text = null;
            if (data.TryGetProperty("utterance", out var utterance)
                && utterance.ValueKind == JsonValueKind.Object
                && utterance.TryGetProperty("text", out var textEl)
                && textEl.ValueKind == JsonValueKind.String)
            {
                text = textEl.GetString();
            }

            return new GladiaMessage(type, text, isFinal);
        }
        catch (JsonException)
        {
            return new GladiaMessage("", null, false);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var messageBuffer = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                messageBuffer.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    messageBuffer.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var json = Encoding.UTF8.GetString(
                    messageBuffer.GetBuffer(),
                    0,
                    (int)messageBuffer.Length
                );

                var message = ParseMessage(json);
                if (message.MessageType == "transcript"
                    && !string.IsNullOrWhiteSpace(message.Text))
                {
                    Emit(new StreamingTranscriptEvent(message.Text!, message.IsFinal));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            Debug.WriteLine($"Gladia realtime WebSocket error: {ex.Message}");
            // A transport drop (not a clean code-1000 close, which the loop handles
            // above) means the stream is incomplete. Fault so the coordinator falls
            // back to a complete batch transcription instead of committing whatever
            // utterances arrived before the drop.
            _finishedTcs.TrySetException(
                new InvalidOperationException("Gladia streaming transport error.", ex)
            );
        }
        finally
        {
            // Unblock FinalizeAsync once the stream has terminated (close / EOF /
            // cancel). No-op if a fault was already set above.
            _finishedTcs.TrySetResult();
        }
    }

    private void Emit(StreamingTranscriptEvent evt)
    {
        try
        {
            TranscriptReceived?.Invoke(evt);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Gladia realtime subscriber failed: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _receiveCts.Cancel();

        if (_ws.State == WebSocketState.Open)
        {
            using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closeCts.Token);
            }
            catch
            {
                try { _ws.Abort(); } catch { /* best effort */ }
            }
        }

        if (_receiveTask is not null)
        {
            try { await _receiveTask; }
            catch { /* expected */ }
        }

        _finishedTcs.TrySetResult();
        // Observe any captured transport fault so it can't surface as an unobserved
        // task exception when FinalizeAsync was never called.
        _ = _finishedTcs.Task.Exception;
        _receiveCts.Dispose();
        _ws.Dispose();
    }
}
