using System.Text.Json;
using System.Text.RegularExpressions;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.AuthenticatedCli;

internal sealed record OpenCodeCatalogModel(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Variants,
    bool IsFree
);

internal sealed record OpenCodeModelCatalog(
    IReadOnlyList<OpenCodeCatalogModel> Models,
    DateTimeOffset RefreshedAt
);

internal sealed record OpenCodeCachedModel(
    string Id,
    string DisplayName,
    List<string> Variants
);

internal sealed record OpenCodeModelCatalogCache(
    int Version,
    DateTimeOffset RefreshedAt,
    List<OpenCodeCachedModel> Models
);

internal sealed record OpenCodeCatalogStatus(
    int FreeModelCount,
    DateTimeOffset? RefreshedAt,
    string? LastRefreshError,
    bool IsLastKnownGood
);

internal sealed partial class OpenCodeModelCatalogLoader(ICliProcessRunner runner)
{
    private const int MaximumStandardOutputBytes = 2 * 1024 * 1024;
    private const int MaximumStandardErrorBytes = 64 * 1024;
    private const string ModelIdPrefix = "opencode/";
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(20);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeVariantRegex();

    /// <summary>The one definition of an acceptable model id, shared with the cache-restore path.</summary>
    internal static bool IsSafeModelId(string? modelId) =>
        modelId is not null
        && modelId.StartsWith(ModelIdPrefix, StringComparison.Ordinal)
        && IsSafeModelSuffix(modelId[ModelIdPrefix.Length..]);

    /// <summary>The one definition of an acceptable variant name.</summary>
    internal static bool IsSafeVariant(string? variant) =>
        variant is not null && SafeVariantRegex().IsMatch(variant);

    private static bool IsSafeModelSuffix(string modelId) =>
        !string.IsNullOrWhiteSpace(modelId)
        && !modelId.Contains('#')
        && !modelId.Any(char.IsWhiteSpace)
        && !modelId.Any(char.IsControl);

    internal async Task<OpenCodeModelCatalog> LoadAsync(
        string executablePath,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environmentOverrides,
        CancellationToken cancellationToken
    )
    {
        var result = await runner.RunAsync(
            new CliProcessRequest(
                executablePath,
                ["models", "opencode", "--verbose", "--pure"],
                "",
                workingDirectory,
                ["XDG_DATA_HOME"],
                s_timeout,
                MaximumStandardOutputBytes,
                MaximumStandardErrorBytes,
                environmentOverrides
            ),
            cancellationToken
        ).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new PluginRequestException(
                "The OpenCode Zen model catalog could not be refreshed.",
                PluginRequestFailureKind.Configuration,
                isTransient: true
            );
        }

        return Parse(result.StandardOutput, DateTimeOffset.UtcNow);
    }

    internal static OpenCodeModelCatalog Parse(string output, DateTimeOffset refreshedAt)
    {
        var lines = CliAnsi.Strip(output)
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .ToArray();
        var models = new List<OpenCodeCatalogModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var headerCount = 0;
        var parsedMetadataCount = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            if (!TryParseHeader(lines[index], out var fullId, out var modelId))
            {
                continue;
            }

            headerCount++;
            var nextHeader = index + 1;
            while (nextHeader < lines.Length && !TryParseHeader(lines[nextHeader], out _, out _))
            {
                nextHeader++;
            }

            var metadata = string.Join('\n', lines[(index + 1)..nextHeader]).Trim();
            index = nextHeader - 1;

            if (!seen.Add(fullId))
            {
                // A repeated id is ambiguous, so the first parse of it is withdrawn too.
                models.RemoveAll(model => string.Equals(model.Id, fullId, StringComparison.Ordinal));
                continue;
            }

            if (string.IsNullOrWhiteSpace(metadata))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(metadata);
                parsedMetadataCount++;
                if (!TryCreateModel(fullId, modelId, document.RootElement, out var model))
                {
                    continue;
                }

                models.Add(model);
            }
            catch (JsonException)
            {
                // A malformed entry cannot contribute a model to the fail-closed catalog.
            }
        }

        if (headerCount == 0 || parsedMetadataCount == 0)
        {
            throw new CliProtocolException(
                "The OpenCode model catalog did not contain parseable verbose metadata."
            );
        }

        return new OpenCodeModelCatalog(models, refreshedAt);
    }

    private static bool TryParseHeader(string line, out string fullId, out string modelId)
    {
        fullId = "";
        modelId = "";
        if (!string.Equals(line, line.Trim(), StringComparison.Ordinal) || !IsSafeModelId(line))
        {
            return false;
        }

        modelId = line[ModelIdPrefix.Length..];
        fullId = line;
        return true;
    }

    private static bool TryCreateModel(
        string fullId,
        string modelId,
        JsonElement metadata,
        out OpenCodeCatalogModel model
    )
    {
        model = null!;
        if (metadata.ValueKind != JsonValueKind.Object
            || !TryGetString(metadata, "id", out var metadataId)
            || !(string.Equals(metadataId, modelId, StringComparison.Ordinal)
                 || string.Equals(metadataId, fullId, StringComparison.Ordinal))
            || !TryGetString(metadata, "providerID", out var providerId)
            || !string.Equals(providerId, "opencode", StringComparison.Ordinal)
            || !TryGetString(metadata, "name", out var name)
            || string.IsNullOrWhiteSpace(name)
            || IsDeprecated(metadata)
            || !SupportsTextInputAndOutput(metadata)
            || !metadata.TryGetProperty("cost", out var cost)
            || cost.ValueKind != JsonValueKind.Object
            || !TryGetNumber(cost, "input")
            || !TryGetNumber(cost, "output"))
        {
            return false;
        }

        model = new OpenCodeCatalogModel(
            fullId,
            name.Trim(),
            ReadSafeVariants(metadata),
            IsEntireCostObjectZero(cost)
        );
        return true;
    }

    private static bool IsDeprecated(JsonElement metadata)
    {
        if (!metadata.TryGetProperty("status", out var status))
        {
            return false;
        }

        return status.ValueKind != JsonValueKind.String
               || string.Equals(status.GetString(), "deprecated", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SupportsTextInputAndOutput(JsonElement metadata)
    {
        if (metadata.TryGetProperty("modalities", out var modalities)
            && modalities.ValueKind == JsonValueKind.Object)
        {
            return ContainsTextCapability(modalities, "input")
                   && ContainsTextCapability(modalities, "output");
        }

        if (metadata.TryGetProperty("capabilities", out var capabilities)
            && capabilities.ValueKind == JsonValueKind.Object)
        {
            return ContainsTextCapability(capabilities, "input")
                   && ContainsTextCapability(capabilities, "output");
        }

        return false;
    }

    private static bool ContainsTextCapability(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var capability))
        {
            return false;
        }

        if (capability.ValueKind == JsonValueKind.Array)
        {
            return capability.EnumerateArray().Any(item =>
                item.ValueKind == JsonValueKind.String
                && string.Equals(item.GetString(), "text", StringComparison.OrdinalIgnoreCase));
        }

        return capability.ValueKind == JsonValueKind.Object
               && capability.TryGetProperty("text", out var text)
               && text.ValueKind == JsonValueKind.True;
    }

    private static bool TryGetNumber(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number;

    private static bool IsEntireCostObjectZero(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Number => IsJsonNumberZero(value),
            JsonValueKind.Object => value.EnumerateObject()
                .All(property => IsEntireCostObjectZero(property.Value)),
            JsonValueKind.Array => value.EnumerateArray().All(IsEntireCostObjectZero),
            _ => false,
        };

    // Read from the raw token rather than a parsed double: 1e-400 underflows to 0.0 but is a
    // real price, so any significant digit before the exponent means the model is not free.
    private static bool IsJsonNumberZero(JsonElement value) =>
        !value.GetRawText()
            .TakeWhile(character => character is not ('e' or 'E'))
            .Any(character => character is >= '1' and <= '9');

    private static List<string> ReadSafeVariants(JsonElement metadata)
    {
        if (!metadata.TryGetProperty("variants", out var variants))
        {
            return [];
        }

        // ReSharper disable once SuggestVarOrType_Elsewhere
        // The empty collection expression below has no natural type, so the switch needs a target.
        IEnumerable<string?> candidates = variants.ValueKind switch
        {
            JsonValueKind.Object => variants.EnumerateObject().Select(property => property.Name),
            JsonValueKind.Array => variants.EnumerateArray().Select(item =>
                item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : item.ValueKind == JsonValueKind.Object && TryGetString(item, "id", out var id)
                        ? id
                        : null),
            _ => [],
        };

        return candidates
            .Where(IsSafeVariant)
            .Select(candidate => candidate!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool TryGetString(JsonElement parent, string propertyName, out string value)
    {
        value = "";
        if (!parent.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? "";
        return true;
    }
}
