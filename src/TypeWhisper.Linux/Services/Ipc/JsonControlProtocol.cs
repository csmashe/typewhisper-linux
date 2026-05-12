using System.Text.Json;
using System.Text.Json.Serialization;

namespace TypeWhisper.Linux.Services.Ipc;

/// <summary>
/// JSON-line wire protocol for the control socket. Each request is exactly
/// one UTF-8 line of JSON; each response is exactly one UTF-8 line of JSON.
/// The connection closes after the single request/response exchange.
/// </summary>
/// <remarks>
/// The leading byte distinguishes a JSON request from the legacy Phase 4
/// <c>toggle</c> plain-text line: a <c>{</c> means parse as JSON, anything
/// else means treat as the legacy text protocol. Backwards compatibility is
/// load-bearing — a Phase 4 binary in <c>$PATH</c> must still talk to a
/// Phase 5 running app during upgrade windows.
/// </remarks>
internal static class JsonControlProtocol
{
    /// <summary>
    /// Hard cap on a single request line. The server reads byte-by-byte and
    /// rejects with <c>line-too-long</c> on overrun so a hostile or buggy
    /// client cannot exhaust process memory.
    /// </summary>
    public const int MaxLineBytes = 4 * 1024;

    /// <summary>Current protocol version. Bumped only on breaking changes.</summary>
    public const int CurrentVersion = 1;

    public const string CmdRecordStart  = "record.start";
    public const string CmdRecordStop   = "record.stop";
    public const string CmdRecordToggle = "record.toggle";
    public const string CmdRecordCancel = "record.cancel";
    public const string CmdStatus       = "status";

    public const string StateIdle         = "idle";
    public const string StateRecording    = "recording";
    public const string StateTranscribing = "transcribing";
    public const string StateInjecting    = "injecting";

    public const string ErrUnknownCommand    = "unknown-command";
    public const string ErrUnsupportedVersion = "unsupported-version";
    public const string ErrMalformed         = "malformed-request";
    public const string ErrLineTooLong       = "line-too-long";
    public const string ErrInternal          = "internal";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        // The spec uses snake_case in some places (e.g. supports_press_release)
        // but the request fields (v, cmd) are flat lowercase. Use a custom
        // naming policy: properties tagged with [JsonPropertyName] win; the
        // rest are converted to snake_case to match the documented response
        // shape. Default camelCase doesn't match the spec.
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>
    /// Inbound request shape. Only <c>v</c> and <c>cmd</c> are required;
    /// unknown fields are ignored for forward compatibility.
    /// </summary>
    public sealed class Request
    {
        [JsonPropertyName("v")]
        public int Version { get; set; }

        [JsonPropertyName("cmd")]
        public string? Command { get; set; }
    }

    /// <summary>
    /// Outbound success response. <see cref="Prev"/> is the state before the
    /// verb executed; <see cref="State"/> is the state after.
    /// </summary>
    public sealed class ActionResponse
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
    /// Outbound <c>status</c> response. Field names follow the spec
    /// (<c>supports_press_release</c>, <c>active_binding</c>) via
    /// <see cref="JsonPropertyNameAttribute"/>; the snake_case naming policy
    /// in <see cref="JsonOptions"/> handles the rest.
    /// </summary>
    public sealed class StatusResponse
    {
        [JsonPropertyName("v")]
        public int Version { get; set; } = CurrentVersion;

        [JsonPropertyName("ok")]
        public bool Ok { get; set; } = true;

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("backend")]
        public string? Backend { get; set; }

        [JsonPropertyName("supports_press_release")]
        public bool SupportsPressRelease { get; set; }

        [JsonPropertyName("active_binding")]
        public string? ActiveBinding { get; set; }

        [JsonPropertyName("mode")]
        public string? Mode { get; set; }
    }

    public static string SerializeError(string code)
        => JsonSerializer.Serialize(
            new ActionResponse { Version = CurrentVersion, Ok = false, Error = code },
            JsonOptions);

    public static string SerializeAction(string prev, string state)
        => JsonSerializer.Serialize(
            new ActionResponse { Version = CurrentVersion, Ok = true, Prev = prev, State = state },
            JsonOptions);

    public static string SerializeStatus(StatusResponse status)
        => JsonSerializer.Serialize(status, JsonOptions);
}
