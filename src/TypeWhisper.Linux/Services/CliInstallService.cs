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
    string StatusText
);

public sealed class CliInstallService
{
    private const string CliFileName = "typewhisper";
    private const string LauncherShebang = "#!/usr/bin/env sh";
    private const string LauncherOwnershipMarker = "# Installed by TypeWhisper";
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
        Func<string> launcherDirectoryProvider
    )
    {
        _bundledPathProvider = bundledPathProvider;
        _installDirectoryProvider = installDirectoryProvider;
        _launcherDirectoryProvider = launcherDirectoryProvider;
    }

    public CliInstallState GetState()
    {
        var installDirectory = _installDirectoryProvider();
        var launcherDirectory = _launcherDirectoryProvider();
        var installPath = Path.Join(installDirectory, CliFileName);
        var launcherPath = Path.Join(launcherDirectory, CliFileName);
        var bundledPath = _bundledPathProvider();
        var launcherEntry = ClassifyLauncherEntry(launcherPath, installPath);

        return CreateState(
            bundledPath,
            installPath,
            launcherPath,
            launcherDirectory,
            launcherEntry
        );
    }

    public CliInstallState Install()
    {
        var state = GetState();
        if (state.BundledPath is null)
        {
            return state;
        }

        var launcherDirectory =
            Path.GetDirectoryName(state.LauncherPath)
            ?? throw new InvalidOperationException("Missing CLI launcher directory.");
        var launcherEntry = ClassifyLauncherEntry(state.LauncherPath, state.InstallPath);
        if (launcherEntry == LauncherEntryClassification.Foreign)
        {
            return CreateState(
                state.BundledPath,
                state.InstallPath,
                state.LauncherPath,
                launcherDirectory,
                launcherEntry
            );
        }

        var sourceDirectory =
            Path.GetDirectoryName(state.BundledPath)
            ?? throw new InvalidOperationException("Missing CLI bundle directory.");
        var installDirectory =
            Path.GetDirectoryName(state.InstallPath)
            ?? throw new InvalidOperationException("Missing CLI install directory.");

        Directory.CreateDirectory(installDirectory);
        Directory.CreateDirectory(launcherDirectory);

        File.Copy(state.BundledPath, state.InstallPath, true);
        CopyCliPayload(sourceDirectory, installDirectory);
        MarkExecutable(state.InstallPath);

        launcherEntry = ClassifyLauncherEntry(state.LauncherPath, state.InstallPath);
        if (launcherEntry == LauncherEntryClassification.Foreign)
        {
            return CreateState(
                state.BundledPath,
                state.InstallPath,
                state.LauncherPath,
                launcherDirectory,
                launcherEntry
            );
        }

        File.WriteAllText(state.LauncherPath, BuildLauncherScript(state.InstallPath));
        MarkExecutable(state.LauncherPath);

        return GetState();
    }

    public static IReadOnlyList<string> BuildCliExamples(int port)
    {
        return
        [
            "export TYPEWHISPER_API_TOKEN=\"paste-token-here\"",
            "typewhisper --help",
            $"typewhisper status --port {port}",
            $"typewhisper models --port {port}",
            $"typewhisper transcribe recording.wav --port {port}",
            $"typewhisper transcribe recording.wav --language de --json --port {port}"
        ];
    }

    public static IReadOnlyList<string> BuildCurlExamples(int port)
    {
        return
        [
            "export TYPEWHISPER_API_TOKEN=\"paste-token-here\"",
            $"curl -H \"Authorization: Bearer $TYPEWHISPER_API_TOKEN\" http://localhost:{port}/v1/status",
            $"curl -H \"Authorization: Bearer $TYPEWHISPER_API_TOKEN\" http://localhost:{port}/v1/models",
            $"curl -H \"Authorization: Bearer $TYPEWHISPER_API_TOKEN\" -X POST http://localhost:{port}/v1/transcribe -F \"file=@recording.wav\"",
            $"curl -H \"Authorization: Bearer $TYPEWHISPER_API_TOKEN\" -X POST http://localhost:{port}/v1/dictation/start",
            $"curl -H \"Authorization: Bearer $TYPEWHISPER_API_TOKEN\" -X POST http://localhost:{port}/v1/dictation/stop"
        ];
    }

    private static void CopyCliPayload(string sourceDirectory, string installDirectory)
    {
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "typewhisper.*"))
        {
            var fileName = Path.GetFileName(file);
            File.Copy(file, Path.Join(installDirectory, fileName), true);
        }
    }

    private static string BuildLauncherScript(string installPath)
    {
        return $"{LauncherShebang}\n{LauncherOwnershipMarker}\n{BuildLauncherExecLine(installPath)}";
    }

    private static string BuildLegacyLauncherScript(string installPath)
    {
        return $"{LauncherShebang}\n{BuildLauncherExecLine(installPath)}";
    }

    private static string BuildLauncherExecLine(string installPath)
    {
        return $"exec \"{installPath}\" \"$@\"";
    }

    private static string DefaultInstallDirectory()
    {
        return Path.Join(TypeWhisperEnvironment.BasePath, "Cli");
    }

    private static string DefaultLauncherDirectory()
    {
        // ~/.local/bin is the XDG-recommended per-user bin dir. Most distros
        // add it to PATH via /etc/profile.d or ~/.profile; if it's not there
        // yet the status text tells the user to add it.
        return Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local",
            "bin"
        );
    }

    private static string? FindBundledCliPath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Join(baseDirectory, "Cli", CliFileName),
            Path.Join(baseDirectory, "..", "TypeWhisper.Cli", CliFileName), Path.Join(
                baseDirectory,
                "..",
                "..",
                "..",
                "..",
                "TypeWhisper.Cli",
                "bin",
                "Debug",
                "net10.0",
                CliFileName
            ),
            Path.Join(
                baseDirectory,
                "..",
                "..",
                "..",
                "..",
                "TypeWhisper.Cli",
                "bin",
                "Release",
                "net10.0",
                CliFileName
            )
        };

        return candidates.Select(Path.GetFullPath).FirstOrDefault(IsCliAppHost);
    }

    private static bool IsCliAppHost(string path)
    {
        return FileExistsWithExactName(path);
    }

    private static CliInstallState CreateState(
        string? bundledPath,
        string installPath,
        string launcherPath,
        string launcherDirectory,
        LauncherEntryClassification launcherEntry
    )
    {
        var launcherExists = launcherEntry != LauncherEntryClassification.Absent;
        var launcherOwned = launcherEntry == LauncherEntryClassification.Owned;
        var installed = launcherOwned && FileExistsWithExactName(installPath);
        var inPath = IsDirectoryInPath(launcherDirectory);

        var status = launcherExists && !launcherOwned
            ? $"Left {launcherPath} untouched — it is not managed by TypeWhisper and will not be overwritten."
            : installed
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
            status
        );
    }

    private static LauncherEntryClassification ClassifyLauncherEntry(
        string launcherPath,
        string installPath
    )
    {
        try
        {
            var directory = Path.GetDirectoryName(launcherPath);
            var fileName = Path.GetFileName(launcherPath);
            if (
                string.IsNullOrWhiteSpace(directory)
                || string.IsNullOrWhiteSpace(fileName)
                || !Directory.Exists(directory)
            )
            {
                return LauncherEntryClassification.Absent;
            }

            // Enumerate case-insensitively so a differently-cased alias is returned even
            // when the launcher directory sits on a case-folded filesystem (the process
            // default casing follows the temp/root filesystem, not this directory). We then
            // pick the ordinal-exact entry ourselves; if only an aliasing variant exists we
            // refuse to overwrite it even though it is not our exact name.
            var candidates = Directory
                .EnumerateFileSystemEntries(
                    directory,
                    fileName,
                    new EnumerationOptions
                    {
                        MatchCasing = MatchCasing.CaseInsensitive,
                        AttributesToSkip = 0
                    }
                )
                .ToArray();
            var entry = candidates.FirstOrDefault(candidate =>
                string.Equals(Path.GetFileName(candidate), fileName, StringComparison.Ordinal)
            );
            if (entry is null)
            {
                return candidates.Length == 0
                    ? LauncherEntryClassification.Absent
                    : LauncherEntryClassification.Foreign;
            }

            var attributes = File.GetAttributes(entry);
            if (
                (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                || new FileInfo(entry).LinkTarget is not null
            )
            {
                return LauncherEntryClassification.Foreign;
            }

            var contents = File.ReadAllText(entry);
            return HasMarkedOwnershipHeader(contents) || IsLegacyOwnedLauncher(contents, installPath)
                ? LauncherEntryClassification.Owned
                : LauncherEntryClassification.Foreign;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Refuse destructive changes when the entry cannot be inspected safely.
            return LauncherEntryClassification.Foreign;
        }
    }

    private static bool HasMarkedOwnershipHeader(string contents)
    {
        using var reader = new StringReader(contents);
        return string.Equals(reader.ReadLine(), LauncherShebang, StringComparison.Ordinal)
            && string.Equals(
                reader.ReadLine(),
                LauncherOwnershipMarker,
                StringComparison.Ordinal
            );
    }

    private static bool IsLegacyOwnedLauncher(string contents, string installPath)
    {
        var expected = BuildLegacyLauncherScript(installPath);
        var expectedWindows = expected.Replace("\n", "\r\n", StringComparison.Ordinal);
        return string.Equals(contents, expected, StringComparison.Ordinal)
            || string.Equals(contents, expected + "\n", StringComparison.Ordinal)
            || string.Equals(contents, expectedWindows, StringComparison.Ordinal)
            || string.Equals(contents, expectedWindows + "\r\n", StringComparison.Ordinal);
    }

    private static bool FileExistsWithExactName(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);
        if (
            string.IsNullOrWhiteSpace(directory)
            || string.IsNullOrWhiteSpace(fileName)
            || !Directory.Exists(directory)
        )
        {
            return false;
        }

        // Use EnumerateFiles + exact name comparison rather than File.Exists
        // to guard against case-insensitive filesystems (FAT32, case-folded
        // ext4 directories) that would treat "TypeWhisper" == "typewhisper".
        return Directory
            .EnumerateFiles(directory, fileName)
            .Any(candidate =>
                string.Equals(Path.GetFileName(candidate), fileName, StringComparison.Ordinal)
            );
    }

    private static bool IsDirectoryInPath(string directory)
    {
        var full = NormalizeDirectory(directory);
        var processPath = Environment.GetEnvironmentVariable("PATH") ?? "";

        return SplitPath(processPath)
            .Select(NormalizeDirectory)
            .Any(path => string.Equals(path, full, StringComparison.Ordinal));
    }

    private static string[] SplitPath(string value)
    {
        return value.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
    }

    private static string NormalizeDirectory(string directory)
    {
        return Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void MarkExecutable(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherExecute
            );
        }
        catch (Exception ex)
            when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[CliInstallService] chmod failed for {path}: {ex.Message}");
        }
    }

    private enum LauncherEntryClassification
    {
        Absent,
        Owned,
        Foreign
    }
}
