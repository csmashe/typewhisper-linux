using System.Text.Json;
using TypeWhisper.Cli.Output;

namespace TypeWhisper.Cli.Services;

/// <summary>Validates successful API response bodies before commands render them.</summary>
internal static class ApiResponseValidator
{
    private const string SupportedApiVersion = "1.0";

    internal sealed record StatusResponse(string Status, string Engine, string Model);

    internal sealed record ModelResponse(
        string Id,
        string Engine,
        string Name,
        string Status,
        bool Selected
    );

    internal sealed record ModelsResponse(IReadOnlyList<ModelResponse> Models);

    internal sealed record TranscribeResponse(string Text);

    internal readonly record struct ValidationResult<T>(T? Value, string? Error)
        where T : class;

    public static ValidationResult<StatusResponse> ValidateStatus(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failure<StatusResponse>("status response must be a JSON object");
            }

            if (!root.TryGetProperty("status", out var status))
            {
                return Failure<StatusResponse>(
                    "status response is missing required field 'status'"
                );
            }

            if (status.ValueKind != JsonValueKind.String)
            {
                return Failure<StatusResponse>("field 'status' must be a string");
            }

            var statusValue = status.GetString()!;
            if (statusValue is not ("ready" or "no_model"))
            {
                return Failure<StatusResponse>(
                    $"unknown status value '{statusValue}'"
                );
            }

            if (root.TryGetProperty("api_version", out var apiVersion))
            {
                if (apiVersion.ValueKind != JsonValueKind.String)
                {
                    return Failure<StatusResponse>(
                        "field 'api_version' must be a string when present"
                    );
                }

                var apiVersionValue = apiVersion.GetString()!;
                if (apiVersionValue != SupportedApiVersion)
                {
                    return Failure<StatusResponse>(
                        $"API version '{apiVersionValue}' is not supported by this CLI, which speaks version {SupportedApiVersion}"
                    );
                }
            }

            var engineResult = ReadOptionalString(root, "engine", "status");
            if (engineResult.Error is not null)
            {
                return Failure<StatusResponse>(engineResult.Error);
            }

            var modelResult = ReadOptionalString(root, "model", "status");
            if (modelResult.Error is not null)
            {
                return Failure<StatusResponse>(modelResult.Error);
            }

            return Success(
                new StatusResponse(
                    statusValue,
                    engineResult.Value!,
                    modelResult.Value!
                )
            );
        }
        catch (JsonException)
        {
            return Failure<StatusResponse>("status response body is not valid JSON");
        }
    }

    public static ValidationResult<ModelsResponse> ValidateModels(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failure<ModelsResponse>("models response must be a JSON object");
            }

            if (!root.TryGetProperty("models", out var models))
            {
                return Failure<ModelsResponse>(
                    "models response is missing required field 'models'"
                );
            }

            if (models.ValueKind != JsonValueKind.Array)
            {
                return Failure<ModelsResponse>("field 'models' must be an array");
            }

            var values = new List<ModelResponse>();
            var index = 0;
            foreach (var model in models.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object)
                {
                    return Failure<ModelsResponse>(
                        $"models[{index}] must be a JSON object"
                    );
                }

                var idResult = ReadOptionalString(model, "id", $"models[{index}]");
                if (idResult.Error is not null)
                {
                    return Failure<ModelsResponse>(idResult.Error);
                }

                var nameResult = ReadOptionalString(model, "name", $"models[{index}]");
                if (nameResult.Error is not null)
                {
                    return Failure<ModelsResponse>(nameResult.Error);
                }

                var engineResult = ReadOptionalString(model, "engine", $"models[{index}]");
                if (engineResult.Error is not null)
                {
                    return Failure<ModelsResponse>(engineResult.Error);
                }

                var statusResult = ReadOptionalString(model, "status", $"models[{index}]");
                if (statusResult.Error is not null)
                {
                    return Failure<ModelsResponse>(statusResult.Error);
                }

                var selected = false;
                if (model.TryGetProperty("selected", out var selectedElement))
                {
                    if (
                        selectedElement.ValueKind
                        is not (JsonValueKind.True or JsonValueKind.False)
                    )
                    {
                        return Failure<ModelsResponse>(
                            $"field 'models[{index}].selected' must be a boolean when present"
                        );
                    }

                    selected = selectedElement.GetBoolean();
                }

                values.Add(
                    new ModelResponse(
                        idResult.Value!,
                        engineResult.Value!,
                        nameResult.Value!,
                        statusResult.Value!,
                        selected
                    )
                );
                index++;
            }

            return Success(new ModelsResponse(values));
        }
        catch (JsonException)
        {
            return Failure<ModelsResponse>("models response body is not valid JSON");
        }
    }

    public static ValidationResult<TranscribeResponse> ValidateTranscribe(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failure<TranscribeResponse>(
                    "transcription response must be a JSON object"
                );
            }

            if (!root.TryGetProperty("text", out var text))
            {
                return Failure<TranscribeResponse>(
                    "transcription response is missing required field 'text'"
                );
            }

            // ReSharper disable once ConvertIfStatementToReturnStatement -- keeps the
            // guard-clause shape the sibling validators use; the ternary would bury the
            // happy path in the else-branch of a negated check.
            if (text.ValueKind != JsonValueKind.String)
            {
                return Failure<TranscribeResponse>("field 'text' must be a string");
            }

            return Success(new TranscribeResponse(text.GetString()!));
        }
        catch (JsonException)
        {
            return Failure<TranscribeResponse>(
                "transcription response body is not valid JSON"
            );
        }
    }

    internal sealed record JsonResponse(JsonElement Root);

    public static ValidationResult<JsonResponse> ValidateHistory(string body) =>
        ValidateObject(body, "history", ("records", JsonValueKind.Array));

    public static ValidationResult<JsonResponse> ValidateDictationStart(string body) =>
        ValidateObject(body, "dictation start", ("session_id", JsonValueKind.Number));

    public static ValidationResult<JsonResponse> ValidateDictationStop(string body) =>
        ValidateObject(body, "dictation stop");

    public static ValidationResult<JsonResponse> ValidateDictationStatus(string body) =>
        ValidateObject(body, "dictation status", ("state", JsonValueKind.String));

    public static ValidationResult<JsonResponse> ValidateDictationResult(string body) =>
        ValidateObject(body, "dictation result", ("state", JsonValueKind.String));

    public static ValidationResult<JsonResponse> ValidateModelOperation(string body) =>
        ValidateObject(body, "model operation", ("status", JsonValueKind.String), ("engine", JsonValueKind.String));

    public static string OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : "";

    private static ValidationResult<JsonResponse> ValidateObject(
        string body, string context, params (string Name, JsonValueKind Kind)[] required
    )
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failure<JsonResponse>($"{context} response must be a JSON object");
            }

            foreach (var (name, kind) in required)
            {
                if (!root.TryGetProperty(name, out var value))
                {
                    return Failure<JsonResponse>($"{context} response is missing required field '{name}'");
                }

                // Unloading with nothing loaded returns a null engine.
                if (name == "engine" && value.ValueKind == JsonValueKind.Null)
                    continue;
                if (value.ValueKind != kind)
                {
                    return Failure<JsonResponse>($"field '{name}' has an invalid type");
                }

                if (name == "session_id" && (!value.TryGetInt64(out var id) || id <= 0))
                {
                    return Failure<JsonResponse>("field 'session_id' must be a positive integer");
                }
            }

            if (context == "history")
            {
                foreach (var record in root.GetProperty("records").EnumerateArray())
                {
                    if (record.ValueKind != JsonValueKind.Object)
                    {
                        return Failure<JsonResponse>("history records must be JSON objects");
                    }

                    foreach (var name in new[] { "timestamp", "text" })
                    {
                        var field = ReadOptionalString(record, name, "history record");
                        if (field.Error is not null)
                            return Failure<JsonResponse>(field.Error);
                    }
                }
            }
            else
            {
                foreach (var name in new[] { "text", "error", "message", "model" })
                {
                    var field = ReadOptionalString(root, name, context);
                    if (field.Error is not null)
                        return Failure<JsonResponse>(field.Error);
                }
            }

            return Success(new JsonResponse(root.Clone()));
        }
        catch (JsonException)
        {
            return Failure<JsonResponse>($"{context} response body is not valid JSON");
        }
    }

    public static int ProtocolError(string detail)
    {
        return ConsoleOutput.Error(
            $"Protocol error: {detail}. The TypeWhisper app and typewhisper-cli may be out of sync.",
            ExitCodes.ServerError
        );
    }

    private static ValidationResult<string> ReadOptionalString(
        JsonElement parent,
        string property,
        string context
    )
    {
        // The app serializes absent optional strings as JSON null, so null reads as "".
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return Success("");
        }

        return value.ValueKind == JsonValueKind.String
            ? Success(value.GetString()!)
            : Failure<string>(
                $"field '{context}.{property}' must be a string when present"
            );
    }

    private static ValidationResult<T> Success<T>(T value)
        where T : class
    {
        return new ValidationResult<T>(value, null);
    }

    private static ValidationResult<T> Failure<T>(string error)
        where T : class
    {
        return new ValidationResult<T>(null, error);
    }
}