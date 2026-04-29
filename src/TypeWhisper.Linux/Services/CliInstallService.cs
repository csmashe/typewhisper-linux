using System.Diagnostics;
using TypeWhisper.Core;

namespace TypeWhisper.Linux.Services;

public sealed record CliInstallState(
    bool BundledCliAvailable,
    bool Installed,
    string? BundledPath,
    string InstallPath,
    string LauncherPath,
    bool LauncherDirectoryInPath,
    string StatusText);

public sealed class CliInstallService
{
    private const string CliFileName = "typewhisper";
    private readonly Func<string?> _bundledPathProvider;
    private readonly Func<string> _installDirectoryProvider;
    private readonly Func<string> _launcherDirectoryProvider;

    public CliInstallService()
        : this(FindBundledCliPath, DefaultInstallDirectory, DefaultLauncherDirectory)
    {
    }

    internal CliInstallService(
        Func<string?> bundledPathProvider,
        Func<string> installDirectoryProvider,
        Func<string> launcherDirectoryProvider)
    {
        _bundledPathProvider = bundledPathProvider;
        _installDirectoryProvider = installDirectoryProvider;
        _launcherDirectoryProvider = launcherDirectoryProvider;
    }

    public CliInstallState GetState()
    {
        var installDirectory = _installDirectoryProvider();
        var launcherDirectory = _launcherDirectoryProvider();
        var installPath = Path.Combine(installDirectory, CliFileName);
        var launcherPath = Path.Combine(launcherDirectory, CliFileName);
        var bundledPath = _bundledPathProvider();
        var installed = FileExistsWithExactName(installPath) && FileExistsWithExactName(launcherPath);
        var inPath = IsDirectoryInPath(launcherDirectory);

        var status = installed
            ? inPath
                ? $"Installed at {launcherPath}"
                : $"Installed at {launcherPath}; add {launcherDirectory} to PATH or restart your shell"
            : bundledPath is null
                ? "CLI binary not found in this build"
                : "Not installed";

        return new CliInstallState(
            bundledPath is not null,
            installed,
            bundledPath,
            installPath,
            launcherPath,
            inPath,
            status);
    }

    public CliInstallState Install()
    {
        var state = GetState();
        if (state.BundledPath is null)
            return state;

        var sourceDirectory = Path.GetDirectoryName(state.BundledPath)
            ?? throw new InvalidOperationException("Missing CLI bundle directory.");
        var installDirectory = Path.GetDirectoryName(state.InstallPath)
            ?? throw new InvalidOperationException("Missing CLI install directory.");
        var launcherDirectory = Path.GetDirectoryName(state.LauncherPath)
            ?? throw new InvalidOperationException("Missing CLI launcher directory.");

        Directory.CreateDirectory(installDirectory);
        Directory.CreateDirectory(launcherDirectory);

        File.Copy(state.BundledPath, state.InstallPath, overwrite: true);
        CopyCliPayload(sourceDirectory, installDirectory);
        MarkExecutable(state.InstallPath);

        File.WriteAllText(state.LauncherPath, BuildLauncherScript(state.InstallPath));
        MarkExecutable(state.LauncherPath);

        return GetState();
    }

    public static IReadOnlyList<string> BuildCliExamples(int port) =>
    [
        "export TYPEWHISPER_API_TOKEN=\"paste-token-here\"",
        "typewhisper --help",
        $"typewhisper status --port {port}",
        $"typewhisper models --port {port}",
        $"typewhisper transcribe recording.wav --port {port}",
        $"typewhisper transcribe recording.wav --language de --json --port {port}"
    ];

    public static IReadOnlyList<string> BuildCurlExamples(int port) =>
    [
        "export TYPEWHISPER_API_TOKEN=\"paste-token-here\"",
        $"curl -H \"Authorization: Bearer $TYPEWHISPER_API_TOKEN\" http://localhost:{port}/v1/status",
        $"curl -H \"Authorization: Bearer $TYPEWHISPER_API_TOKEN\" http://localhost:{port}/v1/models",
        $"curl -H \"Authorization: Bearer $TYPEWHISPER_API_TOKEN\" -X POST http://localhost:{port}/v1/transcribe -F \"file=@recording.wav\"",
        $"curl -H \"Authorization: Bearer $TYPEWHISPER_API_TOKEN\" -X POST http://localhost:{port}/v1/dictation/start",
        $"curl -H \"Authorization: Bearer $TYPEWHISPER_API_TOKEN\" -X POST http://localhost:{port}/v1/dictation/stop"
    ];

    private static void CopyCliPayload(string sourceDirectory, string installDirectory)
    {
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "typewhisper.*"))
        {
            var fileName = Path.GetFileName(file);
            File.Copy(file, Path.Combine(installDirectory, fileName), overwrite: true);
        }
    }

    private static string BuildLauncherScript(string installPath) =>
        $"""
        #!/usr/bin/env sh
        exec "{installPath}" "$@"
        """;

    private static string DefaultInstallDirectory() =>
        Path.Combine(TypeWhisperEnvironment.BasePath, "Cli");

    private static string DefaultLauncherDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local",
            "bin");

    private static string? FindBundledCliPath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "Cli", CliFileName),
            Path.Combine(baseDirectory, "..", "TypeWhisper.Cli", CliFileName),
            Path.Combine(baseDirectory, "..", "..", "..", "..", "TypeWhisper.Cli", "bin", "Debug", "net10.0", CliFileName),
            Path.Combine(baseDirectory, "..", "..", "..", "..", "TypeWhisper.Cli", "bin", "Release", "net10.0", CliFileName)
        };

        return candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(IsCliAppHost);
    }

    private static bool IsCliAppHost(string path) =>
        FileExistsWithExactName(path);

    private static bool FileExistsWithExactName(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName) || !Directory.Exists(directory))
            return false;

        return Directory.EnumerateFiles(directory, fileName)
            .Any(candidate => string.Equals(Path.GetFileName(candidate), fileName, StringComparison.Ordinal));
    }

    private static bool IsDirectoryInPath(string directory)
    {
        var full = NormalizeDirectory(directory);
        var processPath = Environment.GetEnvironmentVariable("PATH") ?? "";

        return SplitPath(processPath)
            .Select(NormalizeDirectory)
            .Any(path => string.Equals(path, full, StringComparison.Ordinal));
    }

    private static IEnumerable<string> SplitPath(string value) =>
        value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeDirectory(string directory) =>
        Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static void MarkExecutable(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[CliInstallService] chmod failed for {path}: {ex.Message}");
        }
    }
}
