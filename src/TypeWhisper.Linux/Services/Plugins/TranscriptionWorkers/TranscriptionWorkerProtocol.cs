using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Linux.Services.Plugins.TranscriptionWorkers;

/// <summary>
///     One frame is an 8-byte little-endian prefix (header length, payload length), a UTF-8 JSON
///     header and an optional binary payload. Audio travels as payload so it never passes through
///     JSON, and the protocol has its own socket so a native library writing to stdout or stderr
///     cannot corrupt it.
/// </summary>
internal static class TranscriptionWorkerProtocol
{
    internal const int Version = 1;

    // Responses carry the full result, including segments and token timings of long files, in the header.
    private const int MaximumHeaderBytes = 64 << 20;

    // Two hours of 16 kHz PCM16 WAV is about 230 MB; anything near this ceiling is not dictation.
    internal const int MaximumAudioBytes = 1 << 30;

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static async Task WriteAsync(
        Stream stream,
        TranscriptionWorkerMessage message,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct
    )
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(message, s_json);
        // Checked before anything is written, so the stream stays usable for an error response.
        if (header.Length > MaximumHeaderBytes)
        {
            throw new InvalidDataException("The transcription result is too large to return.");
        }

        if (payload.Length > MaximumAudioBytes)
        {
            throw new InvalidDataException("The audio is too large for the transcription worker.");
        }

        var prefix = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, header.Length);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(4), payload.Length);
        await stream.WriteAsync(prefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        if (!payload.IsEmpty)
        {
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        }

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Returns null at a clean end of stream. A frame cut short throws
    ///     <see cref="EndOfStreamException" />; one with a bad length or header throws
    ///     <see cref="InvalidDataException" /> before its payload is allocated.
    /// </summary>
    internal static async Task<TranscriptionWorkerFrame?> ReadAsync(
        Stream stream,
        int maximumPayloadBytes,
        CancellationToken ct
    )
    {
        var prefix = new byte[8];
        var read = await stream
            .ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, ct)
            .ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < prefix.Length)
        {
            throw new EndOfStreamException("The transcription worker connection closed mid-frame.");
        }

        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(4));
        if (
            headerLength is <= 0 or > MaximumHeaderBytes
            || payloadLength < 0
            || payloadLength > maximumPayloadBytes
        )
        {
            throw new InvalidDataException("The transcription worker connection sent an invalid frame.");
        }

        var header = new byte[headerLength];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var payload = payloadLength == 0 ? [] : new byte[payloadLength];
        if (payloadLength > 0)
        {
            await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        }

        TranscriptionWorkerMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<TranscriptionWorkerMessage>(header, s_json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The transcription worker connection sent an unreadable frame.", ex);
        }

        return message is null
            ? throw new InvalidDataException("The transcription worker connection sent an empty frame.")
            : new TranscriptionWorkerFrame(message, payload);
    }

    // Engine exceptions keep their kind and message across the process boundary, because the host
    // shows the message and some callers distinguish unsupported requests from failures.
    internal static TranscriptionWorkerError ToError(Exception exception)
    {
        return new TranscriptionWorkerError(
            exception switch
            {
                OperationCanceledException => "canceled",
                NotSupportedException => "not-supported",
                ArgumentException => "argument",
                FileNotFoundException or DirectoryNotFoundException => "file-not-found",
                InvalidDataException => "invalid-data",
                UnauthorizedAccessException => "unauthorized",
                IOException => "io",
                TimeoutException => "timeout",
                _ => "invalid-operation",
            },
            exception.Message
        );
    }

    internal static Exception ToException(TranscriptionWorkerError error)
    {
        return error.Kind switch
        {
            "canceled" => new OperationCanceledException(error.Message),
            "not-supported" => new NotSupportedException(error.Message),
            "argument" => new ArgumentException(error.Message),
            "file-not-found" => new FileNotFoundException(error.Message),
            "invalid-data" => new InvalidDataException(error.Message),
            "unauthorized" => new UnauthorizedAccessException(error.Message),
            "io" => new IOException(error.Message),
            "timeout" => new TimeoutException(error.Message),
            _ => new InvalidOperationException(error.Message),
        };
    }

    internal static TranscriptionWorkerResult ToResult(PluginTranscriptionResult result)
    {
        return new TranscriptionWorkerResult(
            result.Text,
            result.DetectedLanguage,
            result.DurationSeconds,
            result.NoSpeechProbability,
            [
                .. result.Segments.Select(segment => new TranscriptionWorkerSegment(
                    segment.Text,
                    segment.Start,
                    segment.End,
                    segment.NoSpeechProbability
                )),
            ],
            [
                .. result.TokenTimings.Select(timing => new TranscriptionWorkerTiming(
                    timing.Text,
                    timing.StartSeconds,
                    timing.EndSeconds
                )),
            ]
        );
    }

    internal static PluginTranscriptionResult FromResult(TranscriptionWorkerResult result)
    {
        return new PluginTranscriptionResult(
            result.Text,
            result.DetectedLanguage,
            result.DurationSeconds,
            result.NoSpeechProbability
        )
        {
            Segments =
            [
                .. (result.Segments ?? []).Select(segment => new PluginTranscriptionSegment(
                    segment.Text,
                    segment.Start,
                    segment.End
                ) { NoSpeechProbability = segment.NoSpeechProbability }),
            ],
            TokenTimings =
            [
                .. (result.TokenTimings ?? []).Select(timing => new VocabularyTokenTiming(
                    timing.Text,
                    timing.Start,
                    timing.End
                )),
            ],
        };
    }
}

internal sealed record TranscriptionWorkerFrame(TranscriptionWorkerMessage Message, byte[] Payload);

// Both readers ignore frame types they do not know, so a type added here needs no protocol version.
internal static class TranscriptionWorkerMessageTypes
{
    internal const string Hello = "hello";
    internal const string Request = "request";
    internal const string Cancel = "cancel";
    internal const string Response = "response";
    internal const string Log = "log";

    /// <summary>Sent by the worker with the request id while that request runs; carries nothing else.</summary>
    internal const string Heartbeat = "heartbeat";
}

internal static class TranscriptionWorkerCommands
{
    internal const string Load = "load";
    internal const string Transcribe = "transcribe";
    internal const string Shutdown = "shutdown";
}

/// <summary>The single envelope of every frame; unused fields stay null.</summary>
internal sealed record TranscriptionWorkerMessage
{
    public string Type { get; init; } = "";
    public long Id { get; init; }
    public int ProtocolVersion { get; init; }
    public int ProcessId { get; init; }

    /// <summary>In <see cref="TranscriptionWorkerMessageTypes.Hello" />: the launch token, proving the peer is the worker this host started.</summary>
    public string? Token { get; init; }

    public string? Command { get; init; }
    public string? ModelId { get; init; }
    public string? Language { get; init; }
    public string[]? LanguageHints { get; init; }
    public bool Translate { get; init; }
    public string? Prompt { get; init; }
    public TranscriptionWorkerResult? Result { get; init; }
    public TranscriptionWorkerError? Error { get; init; }
    public TranscriptionWorkerState? State { get; init; }
    public PluginLogLevel? LogLevel { get; init; }
    public string? LogMessage { get; init; }

    /// <summary>In <see cref="TranscriptionWorkerMessageTypes.Hello" />: how often the worker beats while a request runs; null when it never does.</summary>
    public TimeSpan? HeartbeatInterval { get; init; }
}

internal sealed record TranscriptionWorkerError(string Kind, string Message);

/// <summary>The worker engine's own view after each command, so the host can report what actually ran.</summary>
internal sealed record TranscriptionWorkerState(
    string? SelectedModelId,
    string? LoadedModelId,
    TranscriptionAccelerationStatus Status
);

internal sealed record TranscriptionWorkerResult(
    string Text,
    string? DetectedLanguage,
    double DurationSeconds,
    float? NoSpeechProbability,
    TranscriptionWorkerSegment[]? Segments,
    TranscriptionWorkerTiming[]? TokenTimings
);

internal sealed record TranscriptionWorkerSegment(
    string Text,
    double Start,
    double End,
    float? NoSpeechProbability = null
);

internal sealed record TranscriptionWorkerTiming(string Text, double Start, double End);
