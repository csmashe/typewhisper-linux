using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypeWhisper.Linux.Services.Ipc;

/// <summary>
///     JSON-line wire protocol for the control socket. Each request is exactly
///     one UTF-8 line of JSON; each response is exactly one UTF-8 line of JSON.
///     The connection closes after the single request/response exchange.
/// </summary>
/// <remarks>
///     The leading byte distinguishes a JSON request from the legacy Phase 4
///     <c>toggle</c> plain-text line: a <c>{</c> means parse as JSON, anything
///     else means treat as the legacy text protocol. Backwards compatibility is
///     load-bearing — a Phase 4 binary in <c>$PATH</c> must still talk to a
///     Phase 5 running app during upgrade windows.
/// </remarks>
internal static class JsonControlProtocol
{
    /// <summary>
    ///     Hard cap on a single request line. The server reads byte-by-byte and
    ///     rejects with <c>line-too-long</c> on overrun so a hostile or buggy
    ///     client cannot exhaust process memory.
    /// </summary>
    public const int MaxLineBytes = 4 * 1024;

    /// <summary>Current protocol version. Bumped only on breaking changes.</summary>
    public const int CurrentVersion = 1;

    public const string CmdRecordStart = "record.start";
    public const string CmdRecordStop = "record.stop";
    public const string CmdRecordToggle = "record.toggle";
    public const string CmdRecordCancel = "record.cancel";
    public const string CmdStatus = "status";

    public const string StateIdle = "idle";
    public const string StateRecording = "recording";
    // ReSharper disable once UnusedMember.Global  IPC control-protocol state string (status wire vocabulary, mirrors StateIdle/StateRecording); part of the protocol surface even if not emitted in-tree
    public const string StateTranscribing = "transcribing";

    // ReSharper disable once UnusedMember.Global  IPC control-protocol state string (status wire vocabulary, mirrors StateIdle/StateRecording); part of the protocol surface even if not emitted in-tree
    public const string StateInjecting = "injecting";

    public const string ErrUnknownCommand = "unknown-command";
    public const string ErrUnsupportedVersion = "unsupported-version";
    public const string ErrMalformed = "malformed-request";
    public const string ErrLineTooLong = "line-too-long";
    public const string ErrInternal = "internal";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        // [JsonPropertyName] overrides win; remaining properties use snake_case to match
        // the documented response shape (camelCase would not match the spec).
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static string SerializeError(string code)
    {
        return JsonSerializer.Serialize(
            new ActionResponse { Version = CurrentVersion, Ok = false, Error = code },
            JsonOptions
        );
    }

    public static string SerializeAction(string prev, string state)
    {
        return JsonSerializer.Serialize(
            new ActionResponse { Version = CurrentVersion, Ok = true, Prev = prev, State = state },
            JsonOptions
        );
    }

    public static string SerializeStatus(StatusResponse status)
    {
        return JsonSerializer.Serialize(status, JsonOptions);
    }

    /// <summary>
    ///     Inbound request shape. Only <c>v</c> and <c>cmd</c> are required;
    ///     unknown fields are ignored for forward compatibility.
    /// </summary>
    public sealed class Request
    {
        [JsonPropertyName("v")]
        public int Version { get; set; }

        [JsonPropertyName("cmd")]
        public string? Command { get; set; }
    }

    /// <summary>
    ///     Outbound success response. <see cref="Prev" /> is the state before the
    ///     verb executed; <see cref="State" /> is the state after.
    /// </summary>
    private sealed class ActionResponse
    {
        [JsonPropertyName("v")]
        public int Version { get; set; } = CurrentVersion;

        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("prev")]
        public string? Prev { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }

    /// <summary>
    ///     Outbound <c>status</c> response. Fields that deviate from snake_case
    ///     (e.g. <c>supports_press_release</c>) use <see cref="JsonPropertyNameAttribute" />;
    ///     <see cref="JsonOptions" /> handles the rest.
    /// </summary>
    public sealed class StatusResponse
    {
        [JsonPropertyName("v")]
        // ReSharper disable once UnusedMember.Global  get read by the reflection JSON serializer (JsonControlProtocol.JsonOptions) in SerializeStatus; part of the status response wire shape
        public int Version { get; set; } = CurrentVersion;

        [JsonPropertyName("ok")]
        // ReSharper disable once UnusedAutoPropertyAccessor.Global  read by the reflection JSON serializer (JsonControlProtocol.JsonOptions) in SerializeStatus
        public bool Ok { get; set; } = true;

        [JsonPropertyName("state")]
        // ReSharper disable once UnusedAutoPropertyAccessor.Global  read by the reflection JSON serializer (JsonControlProtocol.JsonOptions) in SerializeStatus
        public string? State { get; set; }

        [JsonPropertyName("backend")]
        // ReSharper disable once UnusedAutoPropertyAccessor.Global  read by the reflection JSON serializer (JsonControlProtocol.JsonOptions) in SerializeStatus
        public string? Backend { get; set; }

        [JsonPropertyName("supports_press_release")]
        // ReSharper disable once UnusedAutoPropertyAccessor.Global  read by the reflection JSON serializer (JsonControlProtocol.JsonOptions) in SerializeStatus
        public bool SupportsPressRelease { get; set; }

        [JsonPropertyName("active_binding")]
        // ReSharper disable once UnusedAutoPropertyAccessor.Global  read by the reflection JSON serializer (JsonControlProtocol.JsonOptions) in SerializeStatus
        public string? ActiveBinding { get; set; }

        [JsonPropertyName("mode")]
        // ReSharper disable once UnusedAutoPropertyAccessor.Global  read by the reflection JSON serializer (JsonControlProtocol.JsonOptions) in SerializeStatus
        public string? Mode { get; set; }
    }
}