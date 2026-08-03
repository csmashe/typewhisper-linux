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
)
{
    // The launcher classification GetState already computed, so Install can reuse it for its
    // pre-copy foreign-entry check instead of re-reading the launcher file. Internal: an
    // implementation detail, not part of the public state contract.
    internal CliInstallService.LauncherEntryClassification LauncherEntry { get; init; }
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

    public CliInstallService()
        : this(
            FindBundledCliPath,
            DefaultInstallDirectory,
            DefaultLauncherDirectory,
            RunCliVerification,
            ReadUnixFileMode
        )
    {
    }

    internal CliInstallService(
        Func<string?> bundledPathProvider,
        Func<string> installDirectoryProvider,
        Func<string> launcherDirectoryProvider,
        Func<string, CliVerificationResult>? verificationRunner = null,
        Func<string, UnixFileMode>? unixFileModeReader = null
    )
    {
        _bundledPathProvider = bundledPathProvider;
        _installDirectoryProvider = installDirectoryProvider;
        _launcherDirectoryProvider = launcherDirectoryProvider;
        _verificationRunner = verificationRunner ?? RunCliVerification;
        _unixFileModeReader = unixFileModeReader ?? ReadUnixFileMode;
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
                launcherEntry
            );
        }

        var installDirectory =
            Path.GetDirectoryName(state.InstallPath)
            ?? throw new InvalidOperationException("Missing CLI install directory.");

        Directory.CreateDirectory(installDirectory);
        Directory.CreateDirectory(launcherDirectory);

        if (
            !string.Equals(
                Path.GetFullPath(state.BundledPath),
                Path.GetFullPath(state.InstallPath),
                StringComparison.Ordinal
            )
        )
        {
            InstallBundledCli(state.BundledPath, state.InstallPath, installDirectory);
        }

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

        WriteLauncherAtomically(
            state.LauncherPath,
            launcherDirectory,
            BuildLauncherScript(state.InstallPath)
        );
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

    private void InstallBundledCli(
        string bundledPath,
        string installPath,
        string installDirectory
    )
    {
        var tempPath = Path.Join(
            installDirectory,
            $".{CliFileName}.{Guid.NewGuid():N}.tmp"
        );
        try
        {
            File.Copy(bundledPath, tempPath);
            SetExecutableAndVerify(tempPath);
            VerifyCliIdentityAndVersion(tempPath);
            File.Move(tempPath, installPath, true);
        }
        finally
        {
            TryDeleteTemporaryFile(tempPath);
        }
    }

    // Commit by rename, like the binary above, so an interrupted install can't leave a
    // truncated script at the name users type. The mode is verified before the rename:
    // a fresh temp file starts non-executable, and committing one whose chmod failed
    // would replace a working launcher with a broken one.
    private void WriteLauncherAtomically(
        string launcherPath,
        string launcherDirectory,
        string script
    )
    {
        var tempPath = Path.Join(
            launcherDirectory,
            $".{CliFileName}.{Guid.NewGuid():N}.tmp"
        );
        try
        {
            File.WriteAllText(tempPath, script);
            SetExecutableAndVerify(tempPath);
            File.Move(tempPath, launcherPath, true);
        }
        finally
        {
            TryDeleteTemporaryFile(tempPath);
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup must never replace the copy/chmod/verify failure that got us here.
            Trace.WriteLine($"[CliInstallService] could not remove {path}: {ex.Message}");
        }
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
        return RunCliVerification(path, s_verificationTimeout);
    }

    // Parameterized so tests can use a short deadline instead of the production one.
    internal static CliVerificationResult RunCliVerification(string path, TimeSpan timeout)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        process.StartInfo.ArgumentList.Add("--version");
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start CLI verification.");
        }

        // One deadline covering process exit *and* both reads. WaitForExit(int) does
        // not drain redirected pipes, so a grandchild inheriting them keeps ReadToEnd
        // blocked forever even after the CLI itself has exited.
        using var deadline = new CancellationTokenSource(timeout);
        var standardOutput = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var standardError = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult();
            return new CliVerificationResult(
                process.ExitCode,
                standardOutput.GetAwaiter().GetResult(),
                standardError.GetAwaiter().GetResult()
            );
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
                // Bounded: the parameterless overload also waits for pipe EOF, which is
                // the very thing that may be stuck.
                process.WaitForExit(5_000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Trace.WriteLine($"[CliInstallService] could not stop CLI verification: {ex.Message}");
            }

            throw new TimeoutException(
                $"CLI verification did not complete within {timeout.TotalSeconds:0} seconds."
            );
        }
    }

    // Earlier versions installed as "typewhisper", shadowing the desktop app's own command.
    // Renaming leaves that launcher behind, so delete it here — but only when it's provably
    // ours; e.g. the desktop app's own symlink at this name is foreign and left untouched.
    private static void RemoveLegacyLauncher(string launcherDirectory, string installDirectory)
    {
        var legacyLauncherPath = Path.Join(launcherDirectory, LegacyCliFileName);
        var legacyInstallPath = Path.Join(installDirectory, LegacyCliFileName);
        if (
            ClassifyLauncherEntry(legacyLauncherPath, legacyInstallPath)
            != LauncherEntryClassification.Owned
        )
        {
            return;
        }

        try
        {
            File.Delete(legacyLauncherPath);
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
        )
        {
            LauncherEntry = launcherEntry,
        };
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
                        AttributesToSkip = 0,
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
