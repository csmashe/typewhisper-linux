using System.Runtime.CompilerServices;
using TypeWhisper.Linux.Services.Localization;

namespace TypeWhisper.UiTests;

/// <summary>
///     Points the app at a private temporary data root before any production type reads it,
///     and loads the source-tree localization catalogs.
/// </summary>
internal static class UiEnvironment
{
    internal static string Root { get; private set; } = "";

    internal static string LocalizationDir { get; private set; } = "";

    /// <summary>Where rendered PNGs and findings go. Override with TW_UI_SNAPSHOT_DIR.</summary>
    internal static string SnapshotDir { get; private set; } = "";

    [ModuleInitializer]
    internal static void Initialize()
    {
        // Short root: the runtime directory hosts Unix sockets limited to 108 bytes.
        Root = Path.Join(Path.GetTempPath(), $"twui-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}");
        foreach (var name in new[] { "data", "config", "cache", "state", "runtime" })
            Directory.CreateDirectory(Path.Join(Root, name));
#pragma warning disable CA1416 // Linux-only test project.
        File.SetUnixFileMode(Path.Join(Root, "runtime"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Join(Root, "data"));
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Join(Root, "config"));
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Path.Join(Root, "cache"));
        Environment.SetEnvironmentVariable("XDG_STATE_HOME", Path.Join(Root, "state"));
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", Path.Join(Root, "runtime"));

        var repo = RepositoryRoot();
        LocalizationDir = Path.Join(repo, "src", "TypeWhisper.Linux", "Resources", "Localization");
        Loc.Instance.Initialize(LocalizationDir);
        // dotnet test runs from the test's bin folder, so relative paths are taken from the repository root.
        SnapshotDir = Environment.GetEnvironmentVariable("TW_UI_SNAPSHOT_DIR") is { Length: > 0 } custom
            ? Path.GetFullPath(custom, repo)
            : Path.Join(repo, "artifacts", "ui-snapshots", "latest");
        Directory.CreateDirectory(SnapshotDir);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
    }

    private static string RepositoryRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Join(Path.GetDirectoryName(thisFile)!, "..", ".."));
}
