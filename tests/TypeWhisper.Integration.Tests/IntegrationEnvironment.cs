using System.Runtime.CompilerServices;

namespace TypeWhisper.Integration.Tests;

internal static class IntegrationEnvironment
{
    private static string? s_root;

    internal static string Root =>
        s_root ?? throw new InvalidOperationException("The integration environment was not initialized.");

    private static string DataHome => Path.Join(Root, "data");
    internal static string ConfigHome => Path.Join(Root, "config");
    private static string CacheHome => Path.Join(Root, "cache");
    private static string RuntimeHome => Path.Join(Root, "runtime");

    internal static void ResetApplicationState()
    {
        DeleteOwnedDirectory(Path.Join(DataHome, "TypeWhisper"));
        DeleteOwnedDirectory(Path.Join(ConfigHome, "typewhisper"));
        DeleteOwnedDirectory(Path.Join(RuntimeHome, "typewhisper"));
    }

    [ModuleInitializer]
    internal static void Initialize()
    {
        // Keep XDG_RUNTIME_DIR short: Linux sockaddr_un paths are limited to 108 bytes.
        var nonce = Guid.NewGuid().ToString("N")[..8];
        s_root = Path.Join(Path.GetTempPath(), $"twi-{Environment.ProcessId}-{nonce}");

        Directory.CreateDirectory(DataHome);
        Directory.CreateDirectory(ConfigHome);
        Directory.CreateDirectory(CacheHome);
        Directory.CreateDirectory(RuntimeHome);

#pragma warning disable CA1416 // This integration project runs only on Linux.
        File.SetUnixFileMode(
            RuntimeHome,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        );
#pragma warning restore CA1416

        Environment.SetEnvironmentVariable("XDG_DATA_HOME", DataHome);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", ConfigHome);
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", CacheHome);
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", RuntimeHome);

        // Best effort: the root is per-process, so without this every local and CI run
        // leaves another one behind. Skipped when the testhost is killed outright, which
        // is the case where the leftovers are worth inspecting anyway.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDeleteRoot();
    }

    private static void TryDeleteRoot()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteOwnedDirectory(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root));
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to clean a path outside {root}: {candidate}");
        }

        if (Directory.Exists(candidate))
        {
            Directory.Delete(candidate, recursive: true);
        }
    }
}
