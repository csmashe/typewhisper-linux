using System.Diagnostics;
using TypeWhisper.Core;
using TypeWhisper.Linux.Services.ManagedArtifacts;
using TypeWhisper.PluginSDK.Processes;

namespace TypeWhisper.Linux.Services;

public sealed record CliInstallState(
    bool BundledCliAvailable,
    bool Installed,
    string? BundledPath,
    string InstallPath,
    string LauncherPath,
    bool LauncherDirectoryInPath,
    string StatusText
)
{
    // The launcher classification GetState already computed, so Install can reuse it for its
    // pre-copy foreign-entry check instead of re-reading the launcher file. Internal: an
    // implementation detail, not part of the public state contract.
    internal CliInstallService.LauncherEntryClassification LauncherEntry { get; init; }
    internal bool BinaryOwned { get; init; }
}

public sealed class CliInstallService
{
    private const string CliFileName = "typewhisper-cli";
    private const UnixFileMode CliExecutableMode =
        UnixFileMode.UserRead
        | UnixFileMode.UserWrite
        | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead
        | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead
        | UnixFileMode.OtherExecute;

    private static readonly TimeSpan s_verificationTimeout = TimeSpan.FromSeconds(10);

    // Old install name, kept only so RemoveLegacyLauncher can find and retire it.
    private const string LegacyCliFileName = "typewhisper";
    private const string LauncherShebang = "#!/usr/bin/env sh";
    private const string LauncherOwnershipMarker = "# Installed by TypeWhisper";
    private readonly Func<string?> _bundledPathProvider;
    private readonly Func<string> _installDirectoryProvider;
    private readonly Func<string> _launcherDirectoryProvider;
    private readonly Func<string, UnixFileMode> _unixFileModeReader;
    private readonly Func<string, CliVerificationResult> _verificationRunner;
    private readonly ManagedFileTransaction _managedFiles;

    public CliInstallService(IProcessRunner processRunner)
        : this(
            FindBundledCliPath,
            DefaultInstallDirectory,
            DefaultLauncherDirectory,
            path => RunCliVerification(processRunner, path, s_verificationTimeout),
            ReadUnixFileMode,
            ManagedFileTransaction.DefaultStateRoot
        ) { }

    internal CliInstallService(
        Func<string?> bundledPathProvider,
        Func<string> installDirectoryProvider,
        Func<string> launcherDirectoryProvider,
        Func<string, CliVerificationResult>? verificationRunner = null,
        Func<string, UnixFileMode>? unixFileModeReader = null,
        string? managedArtifactStateRoot = null
    )
    {
        _bundledPathProvider = bundledPathProvider;
        _installDirectoryProvider = installDirectoryProvider;
        _launcherDirectoryProvider = launcherDirectoryProvider;
        _verificationRunner = verificationRunner ?? RunCliVerification;
        _unixFileModeReader = unixFileModeReader ?? ReadUnixFileMode;
        _managedFiles = new ManagedFileTransaction(
            managedArtifactStateRoot ?? TestStateRoot(installDirectoryProvider())
        );
    }

    // Only reached from tests, which install into a temp tree and want the manifest to live
    // beside it rather than in the real per-user state directory.
    private static string TestStateRoot(string installDirectory)
    {
        var full = Path.GetFullPath(installDirectory);
        return Path.Join(Path.GetDirectoryName(full) ?? full, "managed-artifacts-test");
    }

    public CliInstallState GetState()
    {
        var installDirectory = _installDirectoryProvider();
        var launcherDirectory = _launcherDirectoryProvider();
        var installPath = Path.Join(installDirectory, CliFileName);
        var launcherPath = Path.Join(launcherDirectory, CliFileName);
        var bundledPath = _bundledPathProvider();
        var launcherEntry = ClassifyLauncherEntry(launcherPath, installPath);
        var binaryOwned = ClassifyBinaryEntry(installPath, launcherPath, bundledPath);

        return CreateState(
            bundledPath,
            installPath,
            launcherPath,
            launcherDirectory,
            launcherEntry,
            binaryOwned
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
        // Reuse the classification GetState already computed — nothing has touched the launcher
        // between GetState and here, so re-reading it would only repeat the same file probe.
        var launcherEntry = state.LauncherEntry;
        if (launcherEntry == LauncherEntryClassification.Foreign)
        {
            return CreateState(
                state.BundledPath,
                state.InstallPath,
                state.LauncherPath,
                launcherDirectory,
                launcherEntry,
                state.BinaryOwned
            );
        }

        var installDirectory =
            Path.GetDirectoryName(state.InstallPath)
            ?? throw new InvalidOperationException("Missing CLI install directory.");

        var bundledBytes = File.ReadAllBytes(state.BundledPath);
        var binaryResult = _managedFiles
            .InstallAsync(BuildBinarySpec(state.InstallPath, state.LauncherPath, bundledBytes))
            .GetAwaiter()
            .GetResult();
        if (!binaryResult.OwnsDestination)
        {
            return CreateState(
                state.BundledPath,
                state.InstallPath,
                state.LauncherPath,
                launcherDirectory,
                launcherEntry,
                binaryOwned: false
            );
        }

        var launcherResult = _managedFiles
            .InstallAsync(BuildLauncherSpec(state.LauncherPath, state.InstallPath))
            .GetAwaiter()
            .GetResult();
        if (!launcherResult.OwnsDestination)
        {
            return CreateState(
                state.BundledPath,
                state.InstallPath,
                state.LauncherPath,
                launcherDirectory,
                LauncherEntryClassification.Foreign,
                binaryOwned: true
            );
        }

        RemoveLegacyLauncher(launcherDirectory, installDirectory);

        return GetState();
    }

    public static IReadOnlyList<string> BuildCliExamples(int port)
    {
        _ = port;
        return
        [
            "export TYPEWHISPER_API_TOKEN=\"paste-token-here\"",
            "typewhisper-cli --help",
            "typewhisper-cli status",
            "typewhisper-cli models",
            "typewhisper-cli transcribe recording.wav",
            "typewhisper-cli transcribe recording.wav --language de --json",
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
            $"curl -H \"Authorization: Bearer $TYPEWHISPER_API_TOKEN\" -X POST http://localhost:{port}/v1/dictation/stop",
        ];
    }

    private void VerifyCliIdentityAndVersion(string path)
    {
        var result = _verificationRunner(path);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"CLI verification failed with exit code {result.ExitCode}: {result.StandardError.Trim()}"
            );
        }

        var expected = $"{CliFileName} {AppVersion.Display}";
        var actual = result.StandardOutput.TrimEnd('\r', '\n');
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"CLI verification returned '{actual}'; expected '{expected}'."
            );
        }
    }

    private static CliVerificationResult RunCliVerification(string path)
    {
        return RunCliVerification(new ProcessRunner(), path, s_verificationTimeout);
    }

    // Parameterized so tests can use a short deadline instead of the production one.
    internal static CliVerificationResult RunCliVerification(string path, TimeSpan timeout)
    {
        return RunCliVerification(new ProcessRunner(), path, timeout);
    }

    private static CliVerificationResult RunCliVerification(
        IProcessRunner processRunner,
        string path,
        TimeSpan timeout
    )
    {
        // One deadline covering process exit and both reads: a grandchild inheriting the
        // redirected pipes would otherwise keep the drain blocked long after the CLI exited.
        var result = processRunner.RunProbe(
            new ProcessCommand(path, ["--version"]),
            new ProcessOneShotOptions(Timeout: timeout)
        );
        // ReSharper disable once ConvertIfStatementToSwitchStatement -- guard chain reads
        // better than a switch here.
        if (result.Status == ProcessRunStatus.StartFailed)
        {
            throw new InvalidOperationException(
                result.StartError ?? "Could not start CLI verification."
            );
        }

        if (result.Status == ProcessRunStatus.TimedOut)
        {
            throw new TimeoutException(
                $"CLI verification did not complete within {timeout.TotalSeconds:0} seconds."
            );
        }

        return new CliVerificationResult(
            result.ExitCode ?? -1,
            result.StandardOutputText,
            result.StandardErrorText
        );
    }

    // Earlier versions installed as "typewhisper", shadowing the desktop app's own command.
    // Renaming leaves that launcher behind, so delete it here — but only when it's provably
    // ours; e.g. the desktop app's own symlink at this name is foreign and left untouched.
    private void RemoveLegacyLauncher(string launcherDirectory, string installDirectory)
    {
        var legacyLauncherPath = Path.Join(launcherDirectory, LegacyCliFileName);
        var legacyInstallPath = Path.Join(installDirectory, LegacyCliFileName);
        try
        {
            _managedFiles
                .RemoveAsync(BuildLegacyLauncherSpec(legacyLauncherPath, legacyInstallPath))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine(
                $"[CliInstallService] could not remove legacy launcher {legacyLauncherPath}: {ex.Message}"
            );
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
            ),
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
        LauncherEntryClassification launcherEntry,
        bool binaryOwned
    )
    {
        var launcherExists = launcherEntry != LauncherEntryClassification.Absent;
        var launcherOwned = launcherEntry == LauncherEntryClassification.Owned;
        var installed = launcherOwned && binaryOwned;
        var inPath = IsDirectoryInPath(launcherDirectory);

        var status = launcherExists && !launcherOwned
            ? $"Left {launcherPath} untouched — it is not managed by TypeWhisper and will not be overwritten."
            : FileExistsWithExactName(installPath) && !binaryOwned
                ? $"Left {installPath} untouched — it is not managed by TypeWhisper and will not be overwritten."
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
        )
        {
            LauncherEntry = launcherEntry,
            BinaryOwned = binaryOwned,
        };
    }

    private LauncherEntryClassification ClassifyLauncherEntry(
        string launcherPath,
        string installPath
    )
    {
        try
        {
            var classification = _managedFiles.Probe(
                BuildLauncherSpec(launcherPath, installPath)
            );
            if (classification == ManagedFileClassification.Absent)
            {
                return LauncherEntryClassification.Absent;
            }

            return classification is ManagedFileClassification.CurrentOwned
                    or ManagedFileClassification.StaleOwned
                ? LauncherEntryClassification.Owned
                : LauncherEntryClassification.Foreign;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Refuse destructive changes when the entry cannot be inspected safely.
            return LauncherEntryClassification.Foreign;
        }
    }

    private bool ClassifyBinaryEntry(string installPath, string launcherPath, string? bundledPath)
    {
        try
        {
            var desired = bundledPath is null ? [] : File.ReadAllBytes(bundledPath);
            var classification = _managedFiles.Probe(
                BuildBinarySpec(installPath, launcherPath, desired)
            );
            return classification is ManagedFileClassification.CurrentOwned
                or ManagedFileClassification.StaleOwned;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Whether our public launcher vouches for the binary at <paramref name="installPath" />.
    ///     A previous release's binary is an opaque ELF carrying no marker of its own, and its
    ///     bytes differ every version, so the launcher is the only ownership evidence available
    ///     for a CLI installed before the manifest existed.
    /// </summary>
    private static bool LauncherClaimsInstallPath(string launcherPath, string installPath)
    {
        try
        {
            if (!FileExistsWithExactName(launcherPath))
            {
                return false;
            }

            var contents = File.ReadAllText(launcherPath);
            return (
                    HasMarkedOwnershipHeader(contents)
                    || IsLegacyOwnedLauncher(contents, installPath)
                )
                && contents.Contains(
                    BuildLauncherExecLine(installPath),
                    StringComparison.Ordinal
                );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private ManagedFileSpec BuildBinarySpec(
        string installPath,
        string launcherPath,
        byte[] desiredBytes
    )
    {
        return new ManagedFileSpec
        {
            ArtifactId = "cli-private-binary",
            DestinationPath = installPath,
            DesiredBytes = desiredBytes,
            CreateMode = CliExecutableMode,
            OwnershipProbe = bytes => bytes.Span.SequenceEqual(desiredBytes),
            // Without this the first upgrade after the manifest lands sees a
            // previous-version binary, matches no probe, calls it foreign, and refuses
            // to update every existing CLI installation until the user deletes it by hand.
            LegacyOwnershipProbe = _ => LauncherClaimsInstallPath(launcherPath, installPath),
            StagedFileValidator = (path, _) =>
            {
                SetExecutableAndVerify(path);
                VerifyCliIdentityAndVersion(path);
                return Task.CompletedTask;
            },
        };
    }

    private static ManagedFileSpec BuildLauncherSpec(
        string launcherPath,
        string installPath
    )
    {
        var desired = ManagedFileSpec.Utf8(BuildLauncherScript(installPath));
        return new ManagedFileSpec
        {
            ArtifactId = "cli-public-launcher",
            DestinationPath = launcherPath,
            DesiredBytes = desired,
            CreateMode = CliExecutableMode,
            OwnershipProbe = bytes => HasMarkedOwnershipHeader(
                System.Text.Encoding.UTF8.GetString(bytes.Span)
            ),
            LegacyOwnershipProbe = bytes => IsLegacyOwnedLauncher(
                System.Text.Encoding.UTF8.GetString(bytes.Span),
                installPath
            ),
        };
    }

    private static ManagedFileSpec BuildLegacyLauncherSpec(
        string launcherPath,
        string installPath
    )
    {
        return BuildLauncherSpec(launcherPath, installPath) with
        {
            ArtifactId = "cli-legacy-launcher",
        };
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
        // ext4 directories) that would treat "TypeWhisper-Cli" == "typewhisper-cli".
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

    private void SetExecutableAndVerify(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path, CliExecutableMode);
        }
        else
        {
            throw new PlatformNotSupportedException(
                "CLI executable permissions can only be verified on Unix."
            );
        }

        var actualMode = _unixFileModeReader(path);
        if (actualMode != CliExecutableMode)
        {
            throw new InvalidOperationException(
                $"CLI executable mode verification failed for {path}: expected {CliExecutableMode}, found {actualMode}."
            );
        }
    }

    private static UnixFileMode ReadUnixFileMode(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return File.GetUnixFileMode(path);
        }

        throw new PlatformNotSupportedException(
            "CLI executable permissions can only be verified on Unix."
        );
    }

    internal readonly record struct CliVerificationResult(
        int ExitCode,
        string StandardOutput,
        string StandardError
    );

    internal enum LauncherEntryClassification
    {
        Absent,
        Owned,
        Foreign,
    }
}
