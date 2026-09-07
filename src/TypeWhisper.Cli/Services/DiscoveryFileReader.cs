using System.Text.Json;
using TypeWhisper.Cli.Models;

namespace TypeWhisper.Cli.Services;

/// <summary>
///     Reads the running app's discovery file
///     (<c>$XDG_CONFIG_HOME/typewhisper/api-discovery.json</c>, falling back to
///     <c>~/.config</c>) so the CLI can auto-pick up the Unix socket and token.
///     Any read/parse failure is treated as
///     "no discovery file" and returns <c>null</c>.
/// </summary>
internal static class DiscoveryFileReader
{
    public static DiscoveryFile? TryRead()
    {
        try
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(configHome))
            {
                configHome = Path.Join(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config"
                );
            }

            var path = Path.Join(configHome, "typewhisper", "api-discovery.json");
            if (!File.Exists(path))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            int? port = null;
            int? version = null;
            string? token = null;
            string? socketPath = null;
            if (
                root.TryGetProperty("version", out var versionEl)
                && versionEl.ValueKind == JsonValueKind.Number
                && versionEl.TryGetInt32(out var versionValue)
            )
            {
                version = versionValue;
            }

            if (root.TryGetProperty("port", out var portEl)
                && portEl.ValueKind == JsonValueKind.Number
                && portEl.TryGetInt32(out var portValue)
                && portValue is >= 1 and <= 65535)
            {
                port = portValue;
            }

            if (root.TryGetProperty("token", out var tokenEl) && tokenEl.ValueKind == JsonValueKind.String)
            {
                token = tokenEl.GetString();
            }

            if (
                root.TryGetProperty("socket_path", out var socketPathEl)
                && socketPathEl.ValueKind == JsonValueKind.String
            )
            {
                socketPath = socketPathEl.GetString();
            }

            return port is null
                ? null
                : new DiscoveryFile(port.Value, token, socketPath, version);
        }
        catch
        {
            return null;
        }
    }
}
