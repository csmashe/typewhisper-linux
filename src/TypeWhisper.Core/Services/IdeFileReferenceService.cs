using System.Text.RegularExpressions;

namespace TypeWhisper.Core.Services;

public sealed partial class IdeFileReferenceService
{
    private static readonly string[] AtReferencePrefixes =
    [
        "at ",
        "tag ",
        "file tag ",
        "file reference ",
        "reference "
    ];

    private static readonly string[] PlainReferencePrefixes =
    [
        "file ",
        "open file "
    ];

    private static readonly Dictionary<string, string> ExtensionAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ts"] = "ts",
        ["typescript"] = "ts",
        ["tsx"] = "tsx",
        ["js"] = "js",
        ["javascript"] = "js",
        ["jsx"] = "jsx",
        ["py"] = "py",
        ["python"] = "py",
        ["cs"] = "cs",
        ["c sharp"] = "cs",
        ["md"] = "md",
        ["markdown"] = "md",
        ["json"] = "json",
        ["yaml"] = "yaml",
        ["yml"] = "yml",
        ["env"] = "env"
    };

    public string ToFileReference(string spokenText)
    {
        if (string.IsNullOrWhiteSpace(spokenText))
            return "";

        var normalized = Normalize(spokenText);
        normalized = DotEnvRegex().Replace(normalized, ".env");
        normalized = ExtensionRegex().Replace(normalized, match =>
        {
            var name = Slug(match.Groups["name"].Value);
            var extension = ResolveExtension(match.Groups["extension"].Value);
            return extension is null ? match.Value : $"{name}.{extension}";
        });

        if (normalized.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
            return ".env";

        if (LooksLikeFileName(normalized))
            return normalized;

        return Slug(normalized);
    }

    public string ToAtReference(string spokenText)
    {
        var fileReference = ToFileReference(spokenText);
        return string.IsNullOrWhiteSpace(fileReference) ? "" : "@" + fileReference;
    }

    public string? TryFormatReferenceCommand(string spokenText)
    {
        if (string.IsNullOrWhiteSpace(spokenText))
            return null;

        var normalized = Normalize(spokenText).Trim('.', '!', '?', ',');
        if (normalized.Length == 0)
            return null;

        foreach (var prefix in AtReferencePrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var candidate = normalized[prefix.Length..].Trim();
                return LooksLikeSpokenFileName(candidate) ? ToAtReference(candidate) : null;
            }
        }

        foreach (var prefix in PlainReferencePrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var candidate = normalized[prefix.Length..].Trim();
                return LooksLikeSpokenFileName(candidate) ? ToFileReference(candidate) : null;
            }
        }

        return null;
    }

    private static string Normalize(string value) =>
        RepeatedWhitespaceRegex()
            .Replace(value.Trim().ToLowerInvariant(), " ");

    private static string? ResolveExtension(string value)
    {
        value = value.Trim().Replace(" dot ", ".", StringComparison.OrdinalIgnoreCase);
        return ExtensionAliases.TryGetValue(value, out var extension)
            ? extension
            : value.Length is >= 1 and <= 5 && PlainExtensionRegex().IsMatch(value)
                ? value
                : null;
    }

    private static string Slug(string value) =>
        string.Join('_', WordRegex().Matches(value).Select(match => match.Value.ToLowerInvariant()));

    private static bool LooksLikeFileName(string value) =>
        value.Contains('.', StringComparison.Ordinal)
        || value.Contains('/', StringComparison.Ordinal)
        || value.StartsWith("@", StringComparison.Ordinal);

    private static bool LooksLikeSpokenFileName(string value) =>
        DotEnvRegex().IsMatch(value)
        || ExtensionRegex().IsMatch(value);

    [GeneratedRegex(@"\bdot\s+env\b", RegexOptions.IgnoreCase)]
    private static partial Regex DotEnvRegex();

    [GeneratedRegex(@"^(?<name>.+?)\s+dot\s+(?<extension>[a-z0-9+# ]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ExtensionRegex();

    [GeneratedRegex(@"^[a-z0-9]+$")]
    private static partial Regex PlainExtensionRegex();

    [GeneratedRegex(@"[a-z0-9]+")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex RepeatedWhitespaceRegex();
}
