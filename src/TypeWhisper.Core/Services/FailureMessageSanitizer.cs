using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Sanitizes failure messages by redacting whole secrets and partial echoes of at least 24 characters,
/// including JSON-escaped forms, collapsing whitespace, and limiting output to 300 characters.
/// Blank input returns <c>null</c> so callers can substitute a localized fallback.
/// </summary>
public static partial class FailureMessageSanitizer
{
    /// <summary>
    /// Redacts every non-empty secret and its JSON-string-escaped forms, both in full and in matching
    /// 24-character windows. Also redacts whitespace-normalized secrets after collapsing whitespace.
    /// </summary>
    /// <param name="message">The failure message; null or whitespace-only input returns <c>null</c>.</param>
    /// <param name="secrets">Sensitive text to redact; null and empty entries are ignored.</param>
    /// <returns>A trimmed, whitespace-collapsed message capped at 300 characters, including a trailing
    /// ellipsis when truncated, with sensitive spans replaced by "[redacted]"; <c>null</c> for blank input.</returns>
    public static string? Sanitize(string? message, params string?[] secrets)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        var forms = secrets.Where(secret => !string.IsNullOrEmpty(secret))
            .SelectMany(secret => new[]
            {
                secret!,
                JsonEncodedText.Encode(secret!).ToString(),
                JsonEncodedText.Encode(secret!, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString(),
            })
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(secret => secret.Length);
        // ReSharper disable once LoopCanBeConvertedToQuery -- explicit loop kept; the Aggregate form reads worse for a rolling string rewrite.
        foreach (var form in forms)
            message = RedactSecret(message, form);

        message = WhitespaceRun().Replace(message, " ").Trim();
        // ReSharper disable once LoopCanBeConvertedToQuery -- explicit loop kept; the Aggregate form reads worse for a rolling string rewrite.
        foreach (var secret in secrets.Where(secret => !string.IsNullOrWhiteSpace(secret)).OrderByDescending(secret => secret!.Length))
            message = RedactSecret(message, WhitespaceRun().Replace(secret!, " ").Trim());

        return message.Length > 300 ? message[..299] + "…" : message;
    }

    private static string RedactSecret(string message, string secret)
    {
        message = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
        const int windowLength = 24;
        if (secret.Length <= windowLength)
            return message;

        // Mark spans before replacing so overlapping windows cannot leave fragments behind.
        var redacted = new bool[message.Length];
        for (var offset = 0; offset <= secret.Length - windowLength; offset++)
        {
            var window = secret.Substring(offset, windowLength);
            for (var hit = message.IndexOf(window, StringComparison.Ordinal); hit >= 0;
                 hit = message.IndexOf(window, hit + 1, StringComparison.Ordinal))
            {
                var length = windowLength;
                while (hit + length < message.Length && offset + length < secret.Length
                       && message[hit + length] == secret[offset + length])
                    length++;
                Array.Fill(redacted, true, hit, length);
            }
        }

        var result = new System.Text.StringBuilder();
        for (var index = 0; index < message.Length; index++)
        {
            if (!redacted[index])
                result.Append(message[index]);
            else if (index == 0 || !redacted[index - 1])
                result.Append("[redacted]");
        }
        return result.ToString();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
