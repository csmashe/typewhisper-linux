using System.Text.Json;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Linux.Services.SpokenCommand;

/// <summary>
///     Builds the classifier system prompt and parses its reply for spoken command
///     routing. Pure and testable — the LLM call itself is made by the orchestrator via
///     <c>PromptProcessingService.ProcessSystemPromptAsync</c>. The model returns a
///     compact JSON verdict; <see cref="Parse" /> extracts it defensively and falls back
///     to a caller-supplied default kind on any malformed reply. Classification happens
///     before the orchestrator touches the clipboard (a selection probe sends Ctrl+C =
///     SIGINT in terminals), so the prompt intentionally does not know the selection state.
/// </summary>
public static class SpokenCommandClassifier
{
    private const int ActionSummaryMaxLength = 160;

    /// <summary>
    ///     Composes the system prompt that asks the model to route
    ///     <paramref name="command" />: edit vs create, and which of the user's
    ///     <paramref name="actions" /> (if any) fits.
    /// </summary>
    public static string BuildPrompt(string command, IReadOnlyList<PromptAction> actions)
    {
        var actionLines = actions.Count == 0
            ? "(the user has no saved actions)"
            : string.Join(
                "\n",
                actions.Select(action =>
                    $"- id: {action.Id}\n  name: {action.Name}\n  purpose: {Summarize(action.SystemPrompt)}"
                )
            );

        return $$"""
                 You route spoken commands for a voice dictation app. The user just spoke a command.
                 Decide how to carry it out and reply with ONLY a compact JSON object — no prose,
                 no explanation, no markdown code fences.

                 JSON shape (exactly these keys):
                 {"kind": "edit" | "create", "actionId": "<saved action id>" | null}

                 Rules:
                 - "edit": the command changes EXISTING highlighted text — rewrite, fix, shorten,
                   lengthen, change tone, translate, summarize "this"/"the selection", etc.
                 - "create": the command asks for NEW text produced from scratch — write a note,
                   draft an email, answer a question, generate a snippet.
                 - "actionId": if one of the saved actions below clearly matches the command, use
                   its exact id; otherwise null. Prefer null over a weak or uncertain match.

                 Saved actions:
                 {{actionLines}}

                 Command:
                 {{command}}

                 Reply with only the JSON object.
                 """;
    }

    /// <summary>
    ///     Parses the classifier reply into a <see cref="SpokenCommandDecision" />.
    ///     Tolerates surrounding prose/code fences by extracting the first JSON object.
    ///     Any missing/unrecognized field or malformed reply falls back to
    ///     <paramref name="fallbackKind" /> with a null action id.
    /// </summary>
    public static SpokenCommandDecision Parse(string llmReply, CommandKind fallbackKind)
    {
        if (string.IsNullOrWhiteSpace(llmReply))
        {
            return new SpokenCommandDecision(fallbackKind, null);
        }

        var json = ExtractJsonObject(llmReply);
        if (json is null)
        {
            return new SpokenCommandDecision(fallbackKind, null);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new SpokenCommandDecision(fallbackKind, null);
            }

            var kind = ReadKind(root, fallbackKind);
            var actionId = ReadActionId(root);
            return new SpokenCommandDecision(kind, actionId);
        }
        catch (JsonException)
        {
            return new SpokenCommandDecision(fallbackKind, null);
        }
    }

    private static CommandKind ReadKind(JsonElement root, CommandKind fallback)
    {
        if (!root.TryGetProperty("kind", out var kindElement)
            || kindElement.ValueKind != JsonValueKind.String)
        {
            return fallback;
        }

        var kind = kindElement.GetString();
        if (string.Equals(kind, "edit", StringComparison.OrdinalIgnoreCase))
        {
            return CommandKind.Edit;
        }

        return string.Equals(kind, "create", StringComparison.OrdinalIgnoreCase)
            ? CommandKind.Create
            : fallback;
    }

    private static string? ReadActionId(JsonElement root)
    {
        if (!root.TryGetProperty("actionId", out var actionElement)
            || actionElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var actionId = actionElement.GetString()?.Trim();
        return string.IsNullOrEmpty(actionId) ? null : actionId;
    }

    // Grabs the substring from the first '{' to the last '}' so a reply wrapped in prose
    // or ```json fences still yields parseable JSON. Returns null when no braces are present.
    private static string? ExtractJsonObject(string reply)
    {
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        return start >= 0 && end > start ? reply[start..(end + 1)] : null;
    }

    private static string Summarize(string systemPrompt)
    {
        var collapsed = string.Join(' ', systemPrompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= ActionSummaryMaxLength
            ? collapsed
            : collapsed[..ActionSummaryMaxLength] + "…";
    }
}
