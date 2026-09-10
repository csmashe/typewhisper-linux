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

        // Replacements can expand and cascade, so only whole-message processing is exact.
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

        // Build a suffix automaton of the secret, with O(secret.Length) states.
        var states = new List<SuffixState> { new(0, -1) };
        var last = 0;
        foreach (var character in secret)
        {
            var current = states.Count;
            states.Add(new SuffixState(states[last].Length + 1, 0));
            var previous = last;
            while (previous >= 0 && states[previous].Transitions.TryAdd(character, current))
                previous = states[previous].Link;

            if (previous >= 0)
            {
                var next = states[previous].Transitions[character];
                if (states[previous].Length + 1 == states[next].Length)
                    states[current].Link = next;
                else
                {
                    var clone = states.Count;
                    states.Add(new SuffixState(states[previous].Length + 1, states[next].Link,
                        new Dictionary<char, int>(states[next].Transitions)));
                    while (previous >= 0
                           && states[previous].Transitions.TryGetValue(character, out var target) && target == next)
                    {
                        states[previous].Transitions[character] = clone;
                        previous = states[previous].Link;
                    }
                    states[next].Link = clone;
                    states[current].Link = clone;
                }
            }
            last = current;
        }

        // Mark spans before replacing so overlapping windows cannot leave fragments behind.
        var redacted = new bool[message.Length];
        // The old scan unions [i, i + ext(i,o)) for every message position i and secret offset o
        // with equal 24-character windows, where ext is their common-prefix length. This is exactly
        // the union of all common substrings of length >= 24, equivalently [p - L(p) + 1, p] for
        // each L(p) >= 24: L(p) is the longest substring ending at p that occurs in the secret,
        // and every shorter suffix of that match also occurs in the secret.
        var state = 0;
        var length = 0;
        var lastMarked = -1;
        for (var position = 0; position < message.Length; position++)
        {
            var character = message[position];
            while (state != 0 && !states[state].Transitions.ContainsKey(character))
            {
                state = states[state].Link;
                length = states[state].Length;
            }
            if (states[state].Transitions.TryGetValue(character, out var next))
            {
                state = next;
                length++;
            }
            else
                length = 0;

            if (length < windowLength)
                continue;

            // L(p+1) <= L(p) + 1, so starts never decrease. Mark each position at most once;
            // together with amortized suffix-link traversal, the scan is O(message.Length).
            var start = Math.Max(position - length + 1, lastMarked + 1);
            for (var index = start; index <= position; index++)
                redacted[index] = true;
            lastMarked = position;
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

    private sealed class SuffixState(int length, int link, Dictionary<char, int>? transitions = null)
    {
        public int Length { get; } = length;
        public int Link { get; set; } = link;
        public Dictionary<char, int> Transitions { get; } = transitions ?? [];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
