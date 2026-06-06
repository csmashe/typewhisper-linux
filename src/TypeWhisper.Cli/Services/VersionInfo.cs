using System.Reflection;

namespace TypeWhisper.Cli.Services;

/// <summary>
///     Resolves the CLI's display version from assembly metadata, preferring
///     the informational version (stripped of any <c>+build</c> suffix) and
///     falling back to the assembly version or <c>"dev"</c>.
/// </summary>
internal static class VersionInfo
{
    public static string Current
    {
        get
        {
            var info = Assembly
                .GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+');
                return plus > 0 ? info[..plus] : info;
            }

            return Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "dev";
        }
    }
}