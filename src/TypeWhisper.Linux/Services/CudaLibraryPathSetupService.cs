using System.Text;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.ManagedArtifacts;

namespace TypeWhisper.Linux.Services;

/// <summary>
///     Owns the two user artifacts that make a host CUDA 12 installation visible to
///     TypeWhisper: a strict environment.d file for desktop launches and a sentinel
///     fragment in the current shell profile for terminal launches.
/// </summary>
public sealed class CudaLibraryPathSetupService
{
    private const int MaxProfileWriteAttempts = 3;
    private const string ArtifactId = "cuda-library-path-environment";
    private const string LegacyEnvironmentArtifactId =
        "cuda-library-path-environment-legacy";
    private const string EnvironmentFileName = "typewhisper-cuda.conf";
    private const string OwnershipMarker =
        "# Installed by TypeWhisper - CUDA 12 runtime library path";
    private const string LegacyComment = "# TypeWhisper CUDA 12 runtime libraries";
    private const string OpenSentinel =
        "# >>> typewhisper:cuda-library-path (managed; do not edit between sentinels)";
    private const string OpenSentinelPrefix = "# >>> typewhisper:cuda-library-path";
    private const string CloseSentinel = "# <<< typewhisper:cuda-library-path";

    private const UnixFileMode PrivateConfigMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly Func<
        AtomicFileSnapshot,
        string,
        CancellationToken,
        Task<bool>
    > _conditionalWriteAsync;
    private readonly Func<string?> _cudaLibraryPathFinder;
    private readonly Func<string> _homeDirectoryProvider;
    private readonly Func<string?> _shellProvider;
    private readonly Func<string?> _xdgConfigHomeProvider;
    private readonly ManagedFileTransaction _transaction;

    public event EventHandler? InstalledChangesChanged;

    public CudaLibraryPathSetupService()
        : this(
            new ManagedFileTransaction(),
            () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            () => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
            () => Environment.GetEnvironmentVariable("SHELL"),
            SystemCommandAvailabilityService.FindCuda12RuntimeDirectory,
            AtomicFileWriter.WriteIfUnchangedAsync
        ) { }

    internal CudaLibraryPathSetupService(
        ManagedFileTransaction transaction,
        Func<string> homeDirectoryProvider,
        Func<string?> xdgConfigHomeProvider,
        Func<string?> shellProvider,
        Func<string?> cudaLibraryPathFinder,
        Func<AtomicFileSnapshot, string, CancellationToken, Task<bool>>? conditionalWriteAsync = null
    )
    {
        _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
        _homeDirectoryProvider = homeDirectoryProvider
                                 ?? throw new ArgumentNullException(nameof(homeDirectoryProvider));
        _xdgConfigHomeProvider = xdgConfigHomeProvider
                                 ?? throw new ArgumentNullException(nameof(xdgConfigHomeProvider));
        _shellProvider = shellProvider ?? throw new ArgumentNullException(nameof(shellProvider));
        _cudaLibraryPathFinder = cudaLibraryPathFinder
                                 ?? throw new ArgumentNullException(nameof(cudaLibraryPathFinder));
        _conditionalWriteAsync = conditionalWriteAsync
                                 ?? AtomicFileWriter.WriteIfUnchangedAsync;
    }

    public string? FindCuda12LibraryPath() => _cudaLibraryPathFinder();

    public async Task<CudaLibraryPathSetupResult> SetUpAsync(
        CancellationToken ct = default
    )
    {
        var home = _homeDirectoryProvider();
        if (string.IsNullOrWhiteSpace(home))
        {
            return CudaLibraryPathSetupResult.Failed(
                CudaLibraryPathSetupFailure.HomeDirectoryUnavailable,
                "The user home directory is unavailable."
            );
        }

        var cudaLibraryPath = FindCuda12LibraryPath();
        if (string.IsNullOrWhiteSpace(cudaLibraryPath))
        {
            return CudaLibraryPathSetupResult.Failed(
                CudaLibraryPathSetupFailure.CudaLibrariesUnavailable,
                "No CUDA 12 runtime library directory was found."
            );
        }

        var environmentSpec = BuildEnvironmentSpec(home, cudaLibraryPath);
        var environmentClassification = await _transaction
            .ProbeAsync(environmentSpec, ct)
            .ConfigureAwait(false);
        if (!CanInstallEnvironment(environmentClassification))
        {
            return CudaLibraryPathSetupResult.Failed(
                CudaLibraryPathSetupFailure.EnvironmentFileRefused,
                DescribeEnvironmentRefusal(environmentSpec.DestinationPath),
                environmentClassification
            );
        }

        var profilePath = ResolveShellProfilePath(home, _shellProvider());
        AtomicFileSnapshot profileSnapshot;
        try
        {
            profileSnapshot = await AtomicFileWriter.CaptureAsync(profilePath, ct)
                .ConfigureAwait(false);
            var scan = ScanFragment(profileSnapshot.Contents);
            if (scan.Mismatched)
            {
                return CudaLibraryPathSetupResult.Failed(
                    CudaLibraryPathSetupFailure.ShellProfileRefused,
                    $"'{profilePath}' has an unbalanced TypeWhisper CUDA managed block. {scan.Reason}",
                    environmentClassification,
                    profilePath,
                    environmentSpec.DestinationPath
                );
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CudaLibraryPathSetupResult.Failed(
                CudaLibraryPathSetupFailure.ShellProfileRefused,
                ex.Message,
                environmentClassification,
                profilePath,
                environmentSpec.DestinationPath
            );
        }

        // Both destinations have been classified before either is changed. The strict
        // whole-file publication goes first so a foreign environment.d entry can never
        // leave behind a newly-added shell fragment.
        var environmentInstall = await _transaction.InstallAsync(environmentSpec, ct)
            .ConfigureAwait(false);
        if (!environmentInstall.OwnsDestination)
        {
            return CudaLibraryPathSetupResult.Failed(
                CudaLibraryPathSetupFailure.EnvironmentFileRefused,
                DescribeEnvironmentRefusal(environmentSpec.DestinationPath),
                environmentInstall.Classification,
                profilePath,
                environmentSpec.DestinationPath
            );
        }

        bool? profileChanged;
        try
        {
            profileChanged = await UpsertProfileFragmentAsync(
                    profilePath,
                    cudaLibraryPath,
                    profileSnapshot,
                    ct
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The environment file is already published, so report what changed rather
            // than letting a read-only profile throw out of the command.
            if (environmentInstall.Changed)
            {
                InstalledChangesChanged?.Invoke(this, EventArgs.Empty);
            }

            return CudaLibraryPathSetupResult.Failed(
                CudaLibraryPathSetupFailure.ShellProfileRefused,
                ex.Message,
                environmentInstall.Classification,
                profilePath,
                environmentSpec.DestinationPath,
                environmentInstall.Changed
            );
        }

        if (profileChanged is null)
        {
            if (environmentInstall.Changed)
            {
                InstalledChangesChanged?.Invoke(this, EventArgs.Empty);
            }
            return CudaLibraryPathSetupResult.Failed(
                CudaLibraryPathSetupFailure.ConcurrentShellProfileEdit,
                $"'{profilePath}' kept changing while TypeWhisper was updating it. Please retry.",
                environmentInstall.Classification,
                profilePath,
                environmentSpec.DestinationPath,
                environmentInstall.Changed
            );
        }

        var legacySweep = await SweepLegacyEnvironmentFileAsync(home, cudaLibraryPath, ct)
            .ConfigureAwait(false);
        var result = new CudaLibraryPathSetupResult(
            true,
            environmentInstall.Changed || profileChanged.Value || legacySweep.Changed,
            CudaLibraryPathSetupFailure.None,
            null,
            environmentInstall.Classification,
            profilePath,
            environmentSpec.DestinationPath,
            legacySweep.Notice
        );
        if (result.Changed)
        {
            InstalledChangesChanged?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    /// <summary>
    ///     Removes only the owned CUDA sentinel from every supported shell profile and
    ///     deletes the environment file only when its journaled publication is unchanged.
    /// </summary>
    public async Task<CudaLibraryPathSetupResult> RemoveAsync(
        CancellationToken ct = default
    )
    {
        var home = _homeDirectoryProvider();
        if (string.IsNullOrWhiteSpace(home))
        {
            return CudaLibraryPathSetupResult.Failed(
                CudaLibraryPathSetupFailure.HomeDirectoryUnavailable,
                "The user home directory is unavailable."
            );
        }

        var environmentSpec = BuildEnvironmentSpec(
            home,
            FindCuda12LibraryPath() ?? string.Empty
        );
        var environmentClassification = await _transaction
            .ProbeAsync(environmentSpec, ct)
            .ConfigureAwait(false);
        if (
            environmentClassification is ManagedFileClassification.CustomizedOwned
                or ManagedFileClassification.UnsupportedEntry
        )
        {
            return CudaLibraryPathSetupResult.Failed(
                CudaLibraryPathSetupFailure.EnvironmentFileRefused,
                DescribeEnvironmentRefusal(environmentSpec.DestinationPath),
                environmentClassification,
                environmentPath: environmentSpec.DestinationPath
            );
        }

        var snapshots = new List<(string Path, AtomicFileSnapshot Snapshot)>();
        foreach (var profilePath in GetAllShellProfilePaths(home))
        {
            AtomicFileSnapshot snapshot;
            try
            {
                snapshot = await AtomicFileWriter.CaptureAsync(profilePath, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return CudaLibraryPathSetupResult.Failed(
                    CudaLibraryPathSetupFailure.ShellProfileRefused,
                    ex.Message,
                    environmentClassification,
                    profilePath,
                    environmentSpec.DestinationPath
                );
            }

            if (!snapshot.Existed)
            {
                continue;
            }

            var scan = ScanFragment(snapshot.Contents);
            if (scan.Mismatched)
            {
                return CudaLibraryPathSetupResult.Failed(
                    CudaLibraryPathSetupFailure.ShellProfileRefused,
                    $"'{profilePath}' has an unbalanced TypeWhisper CUDA managed block. {scan.Reason}",
                    environmentClassification,
                    profilePath,
                    environmentSpec.DestinationPath
                );
            }

            snapshots.Add((profilePath, snapshot));
        }

        var environmentRemoval = await _transaction.RemoveAsync(environmentSpec, ct)
            .ConfigureAwait(false);
        if (
            environmentRemoval.Classification is ManagedFileClassification.CustomizedOwned
                or ManagedFileClassification.UnsupportedEntry
        )
        {
            return CudaLibraryPathSetupResult.Failed(
                CudaLibraryPathSetupFailure.EnvironmentFileRefused,
                DescribeEnvironmentRefusal(environmentSpec.DestinationPath),
                environmentRemoval.Classification,
                environmentPath: environmentSpec.DestinationPath
            );
        }

        var changed = environmentRemoval.Changed;
        foreach (var (profilePath, initialSnapshot) in snapshots)
        {
            bool? profileChanged;
            try
            {
                profileChanged = await RemoveProfileFragmentAsync(
                        profilePath,
                        initialSnapshot,
                        ct
                    )
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return CudaLibraryPathSetupResult.Failed(
                    CudaLibraryPathSetupFailure.ShellProfileRefused,
                    ex.Message,
                    environmentRemoval.Classification,
                    profilePath,
                    environmentSpec.DestinationPath,
                    changed
                );
            }

            if (profileChanged is null)
            {
                return CudaLibraryPathSetupResult.Failed(
                    CudaLibraryPathSetupFailure.ConcurrentShellProfileEdit,
                    $"'{profilePath}' kept changing while TypeWhisper was removing its managed block. Please retry.",
                    environmentRemoval.Classification,
                    profilePath,
                    environmentSpec.DestinationPath,
                    changed
                );
            }

            changed |= profileChanged.Value;
        }

        var legacySweep = await SweepLegacyEnvironmentFileAsync(
                home,
                FindCuda12LibraryPath() ?? string.Empty,
                ct
            )
            .ConfigureAwait(false);
        var result = new CudaLibraryPathSetupResult(
            true,
            changed || legacySweep.Changed,
            CudaLibraryPathSetupFailure.None,
            null,
            environmentRemoval.Classification,
            null,
            environmentSpec.DestinationPath,
            legacySweep.Notice
        );
        if (result.Changed)
        {
            InstalledChangesChanged?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    public bool HasInstalledChanges()
    {
        var home = _homeDirectoryProvider();
        if (string.IsNullOrWhiteSpace(home))
        {
            return false;
        }

        try
        {
            var spec = BuildEnvironmentSpec(home, FindCuda12LibraryPath() ?? string.Empty);
            if (
                _transaction.Probe(spec)
                    is ManagedFileClassification.CurrentOwned
                        or ManagedFileClassification.StaleOwned
                        or ManagedFileClassification.CustomizedOwned
            )
            {
                return true;
            }

            return GetAllShellProfilePaths(home).Any(path =>
            {
                var snapshot = AtomicFileWriter
                    .CaptureAsync(path, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                return snapshot.Existed
                       && (ScanFragment(snapshot.Contents).OpenLine is not null
                           || FindLegacyFragment(SplitLines(snapshot.Contents)) >= 0);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidDataException)
        {
            // An unreadable or unsafe entry is not a removal affordance the UI can complete.
            // Narrow on purpose: a journal conflict arrives as an IOException, while anything
            // else here is a defect that must not be reported as "nothing installed".
            return false;
        }
    }

    private static string ResolveShellProfilePath(string home, string? shell)
    {
        if (shell?.EndsWith("/zsh", StringComparison.Ordinal) == true)
        {
            return Path.Join(home, ".zshrc");
        }

        return shell?.EndsWith("/fish", StringComparison.Ordinal) == true
            ? Path.Join(home, ".config", "fish", "config.fish")
            : Path.Join(home, ".bashrc");
    }

    internal static string GetCudaLibraryPathExport(
        string profilePath,
        string cudaLibraryPath
    )
    {
        return profilePath.EndsWith("config.fish", StringComparison.Ordinal)
            ? $"set -gx LD_LIBRARY_PATH {cudaLibraryPath} $LD_LIBRARY_PATH"
            : $"export LD_LIBRARY_PATH={cudaLibraryPath}:${{LD_LIBRARY_PATH:-}}";
    }

    internal static string EnvironmentFileContent(string cudaLibraryPath)
    {
        return OwnershipMarker
               + "\n"
               + $"LD_LIBRARY_PATH={cudaLibraryPath}:${{LD_LIBRARY_PATH:-}}\n";
    }

    private async Task<bool?> UpsertProfileFragmentAsync(
        string profilePath,
        string cudaLibraryPath,
        AtomicFileSnapshot initialSnapshot,
        CancellationToken ct
    )
    {
        var snapshot = initialSnapshot;
        for (var attempt = 0; attempt < MaxProfileWriteAttempts; attempt++)
        {
            var scan = ScanFragment(snapshot.Contents);
            if (scan.Mismatched)
            {
                throw new IOException(
                    $"'{profilePath}' acquired an unbalanced TypeWhisper CUDA managed block."
                );
            }

            var withoutLegacy = RemoveLegacyFragment(snapshot.Contents);
            var updated = ReplaceOrAppendFragment(
                withoutLegacy,
                [
                    LegacyComment,
                    GetCudaLibraryPathExport(profilePath, cudaLibraryPath),
                ]
            );
            if (string.Equals(updated, snapshot.Contents, StringComparison.Ordinal))
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
            if (
                await _conditionalWriteAsync(snapshot, updated, ct).ConfigureAwait(false)
            )
            {
                return true;
            }

            snapshot = await AtomicFileWriter.CaptureAsync(profilePath, ct)
                .ConfigureAwait(false);
        }

        return null;
    }

    private async Task<bool?> RemoveProfileFragmentAsync(
        string profilePath,
        AtomicFileSnapshot initialSnapshot,
        CancellationToken ct
    )
    {
        var snapshot = initialSnapshot;
        for (var attempt = 0; attempt < MaxProfileWriteAttempts; attempt++)
        {
            var scan = ScanFragment(snapshot.Contents);
            if (scan.Mismatched)
            {
                throw new IOException(
                    $"'{profilePath}' acquired an unbalanced TypeWhisper CUDA managed block."
                );
            }

            // Strip both shapes: an upgraded profile that never re-ran setup still carries
            // only the pre-manifest pair, and leaving it behind would keep LD_LIBRARY_PATH
            // exported by a shell whose environment.d file we just deleted.
            var updated = RemoveLegacyFragment(RemoveFragment(snapshot.Contents));
            if (string.Equals(updated, snapshot.Contents, StringComparison.Ordinal))
            {
                return false;
            }

            if (
                await _conditionalWriteAsync(snapshot, updated, ct).ConfigureAwait(false)
            )
            {
                return true;
            }

            snapshot = await AtomicFileWriter.CaptureAsync(profilePath, ct)
                .ConfigureAwait(false);
            if (!snapshot.Existed)
            {
                return false;
            }
        }

        return null;
    }

    private ManagedFileSpec BuildEnvironmentSpec(string home, string cudaLibraryPath)
    {
        var configHome = _xdgConfigHomeProvider();
        // The XDG spec says a relative value must be ignored. Honoring that also keeps a
        // badly-set variable from throwing out of the spec validator during view-model creation.
        if (string.IsNullOrWhiteSpace(configHome) || !Path.IsPathFullyQualified(configHome))
        {
            configHome = Path.Join(home, ".config");
        }

        var desired = EnvironmentFileContent(cudaLibraryPath);
        return new ManagedFileSpec
        {
            ArtifactId = ArtifactId,
            DestinationPath = Path.Join(configHome, "environment.d", EnvironmentFileName),
            DesiredBytes = ManagedFileSpec.Utf8(desired),
            CreateMode = PrivateConfigMode,
            OwnershipProbe = bytes =>
            {
                using var reader = new StringReader(Encoding.UTF8.GetString(bytes.Span));
                return string.Equals(reader.ReadLine(), OwnershipMarker, StringComparison.Ordinal);
            },
            // Matched by shape, not against the discovered path: removal runs after the
            // runtime may have moved or been uninstalled, and an exact-bytes probe would
            // then fail to recognize our own pre-manifest file and abandon it as foreign.
            LegacyOwnershipProbe = bytes =>
            {
                using var reader = new StringReader(Encoding.UTF8.GetString(bytes.Span));
                return string.Equals(reader.ReadLine(), LegacyComment, StringComparison.Ordinal)
                    && reader.ReadLine()?.StartsWith(
                        "LD_LIBRARY_PATH=",
                        StringComparison.Ordinal
                    ) == true;
            },
            ExistingPolicy = ManagedFileExistingPolicy.RefuseForeign,
        };
    }

    /// <summary>
    ///     The pre-XDG service always wrote <c>~/.config/environment.d</c>. When
    ///     XDG_CONFIG_HOME points elsewhere the canonical path no longer covers that
    ///     file, so it is swept separately. Returns null when both paths agree.
    /// </summary>
    private ManagedFileSpec? BuildLegacyEnvironmentSpec(string home, string cudaLibraryPath)
    {
        var canonical = BuildEnvironmentSpec(home, cudaLibraryPath);
        var legacyPath = Path.Join(home, ".config", "environment.d", EnvironmentFileName);
        if (string.Equals(canonical.DestinationPath, legacyPath, StringComparison.Ordinal))
        {
            return null;
        }

        return canonical with
        {
            ArtifactId = LegacyEnvironmentArtifactId,
            DestinationPath = legacyPath,
        };
    }

    /// <summary>
    ///     Deletes the pre-XDG environment file only when it still carries TypeWhisper's
    ///     marker. Anything else is another program's file and is reported, not touched.
    /// </summary>
    private async Task<(bool Changed, string? Notice)> SweepLegacyEnvironmentFileAsync(
        string home,
        string cudaLibraryPath,
        CancellationToken ct
    )
    {
        var spec = BuildLegacyEnvironmentSpec(home, cudaLibraryPath);
        if (spec is null)
        {
            return (false, null);
        }

        ManagedFileClassification classification;
        try
        {
            classification = await _transaction.ProbeAsync(spec, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidDataException)
        {
            return (false, $"'{spec.DestinationPath}' could not be inspected: {ex.Message}");
        }

        if (classification == ManagedFileClassification.Absent)
        {
            return (false, null);
        }

        if (
            classification is not (ManagedFileClassification.CurrentOwned
                or ManagedFileClassification.StaleOwned)
        )
        {
            return (false, DescribeEnvironmentRefusal(spec.DestinationPath));
        }

        var removal = await _transaction.RemoveAsync(spec, ct).ConfigureAwait(false);
        return removal.Changed
            ? (true, null)
            : (false, DescribeEnvironmentRefusal(spec.DestinationPath));
    }

    private static bool CanInstallEnvironment(ManagedFileClassification classification)
    {
        return classification is ManagedFileClassification.Absent
            or ManagedFileClassification.CurrentOwned
            or ManagedFileClassification.StaleOwned;
    }

    private static string DescribeEnvironmentRefusal(string path)
    {
        return $"'{path}' is foreign, customized, symlinked, or otherwise unsafe. TypeWhisper left it untouched.";
    }

    private static IReadOnlyList<string> GetAllShellProfilePaths(string home)
    {
        return
        [
            Path.Join(home, ".bashrc"),
            Path.Join(home, ".zshrc"),
            Path.Join(home, ".config", "fish", "config.fish"),
        ];
    }

    private static FragmentScan ScanFragment(string contents)
    {
        var lines = SplitLines(contents);
        var opens = new List<int>();
        var closes = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.StartsWith(OpenSentinelPrefix, StringComparison.Ordinal))
            {
                opens.Add(i);
            }
            else if (string.Equals(line, CloseSentinel, StringComparison.Ordinal))
            {
                closes.Add(i);
            }
        }

        return (opens.Count, closes.Count) switch
        {
            (0, 0) => new FragmentScan(false, null, null, null),
            (1, 1) when opens[0] < closes[0] =>
                new FragmentScan(false, opens[0], closes[0], null),
            _ => new FragmentScan(
                true,
                opens.Count > 0 ? opens[0] : null,
                closes.Count > 0 ? closes[0] : null,
                $"Found {opens.Count} open sentinel(s) and {closes.Count} close sentinel(s)."
            ),
        };
    }

    private static string ReplaceOrAppendFragment(
        string contents,
        IEnumerable<string> managedLines
    )
    {
        var scan = ScanFragment(contents);
        if (scan.Mismatched)
        {
            throw new InvalidOperationException(scan.Reason);
        }

        var lines = SplitLines(contents);
        var block = new List<string> { OpenSentinel };
        block.AddRange(managedLines);
        block.Add(CloseSentinel);
        if (scan is { OpenLine: { } open, CloseLine: { } close })
        {
            var replaced = lines.Take(open).ToList();
            replaced.AddRange(block);
            replaced.AddRange(lines.Skip(close + 1));
            return JoinLines(replaced, contents);
        }

        var appended = new List<string>(lines);
        if (appended.Count > 0 && !string.IsNullOrEmpty(appended[^1]))
        {
            appended.Add(string.Empty);
        }

        appended.AddRange(block);
        return JoinLines(appended, contents);
    }

    private static string RemoveFragment(string contents)
    {
        var scan = ScanFragment(contents);
        if (scan.Mismatched)
        {
            throw new InvalidOperationException(scan.Reason);
        }

        if (scan is not { OpenLine: { } open, CloseLine: { } close })
        {
            return contents;
        }

        var lines = SplitLines(contents);
        var stripped = lines.Take(open).ToList();
        if (stripped.Count > 0 && string.IsNullOrWhiteSpace(stripped[^1]))
        {
            stripped.RemoveAt(stripped.Count - 1);
        }

        stripped.AddRange(lines.Skip(close + 1));
        return JoinLines(stripped, contents);
    }

    /// <summary>
    ///     Locates the unsentinelized comment/export pair the pre-manifest release appended,
    ///     or -1. The export line is matched by shape rather than against the discovered CUDA
    ///     path: removal runs after the runtime may already be gone, and a pair carrying our
    ///     own comment is ours whatever path it names.
    /// </summary>
    private static int FindLegacyFragment(List<string> lines, FragmentScan? managedBlock = null)
    {
        for (var i = 0; i + 1 < lines.Count; i++)
        {
            // Our own managed block repeats the legacy comment/export pair verbatim, so a
            // scan that counted it would strip the block's body and leave the real
            // pre-manifest pair further down the profile untouched.
            if (
                managedBlock is { OpenLine: { } open, CloseLine: { } close }
                && i >= open
                && i <= close
            )
            {
                continue;
            }

            var next = lines[i + 1].TrimEnd();
            if (
                string.Equals(lines[i].TrimEnd(), LegacyComment, StringComparison.Ordinal)
                && (next.StartsWith("export LD_LIBRARY_PATH=", StringComparison.Ordinal)
                    || next.StartsWith("set -gx LD_LIBRARY_PATH ", StringComparison.Ordinal))
            )
            {
                return i;
            }
        }

        return -1;
    }

    private static string RemoveLegacyFragment(string contents)
    {
        var lines = SplitLines(contents);
        var scan = ScanFragment(contents);
        var index = FindLegacyFragment(lines, scan.Mismatched ? null : scan);
        if (index < 0)
        {
            return contents;
        }

        var start = index > 0 && string.IsNullOrWhiteSpace(lines[index - 1]) ? index - 1 : index;
        lines.RemoveRange(start, index + 2 - start);
        return JoinLines(lines, contents);
    }

    private static List<string> SplitLines(string contents)
    {
        return contents.Length == 0
            ? []
            : contents.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
    }

    private static string JoinLines(List<string> lines, string original)
    {
        var separator = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var joined = string.Join(separator, lines);
        var originalEndsWithNewline = original.EndsWith('\n');
        var joinedEndsWithNewline = joined.EndsWith(separator, StringComparison.Ordinal);
        if (originalEndsWithNewline && !joinedEndsWithNewline)
        {
            return joined + separator;
        }

        return !originalEndsWithNewline && joinedEndsWithNewline
            ? joined[..^separator.Length]
            : joined;
    }

    private sealed record FragmentScan(
        bool Mismatched,
        int? OpenLine,
        int? CloseLine,
        string? Reason
    );
}

public enum CudaLibraryPathSetupFailure
{
    None,
    HomeDirectoryUnavailable,
    CudaLibrariesUnavailable,
    EnvironmentFileRefused,
    ShellProfileRefused,
    ConcurrentShellProfileEdit,
}

// The paths we touched are carried in the result's data shape for callers and logs; the UI
// currently renders only Detail and the classification.
// ReSharper disable NotAccessedPositionalProperty.Global
public sealed record CudaLibraryPathSetupResult(
    bool Success,
    bool Changed,
    CudaLibraryPathSetupFailure Failure,
    string? Detail,
    ManagedFileClassification? EnvironmentClassification = null,
    string? ShellProfilePath = null,
    string? EnvironmentPath = null,
    string? LegacyEnvironmentNotice = null
)
{
    internal static CudaLibraryPathSetupResult Failed(
        CudaLibraryPathSetupFailure failure,
        string detail,
        ManagedFileClassification? environmentClassification = null,
        string? shellProfilePath = null,
        string? environmentPath = null,
        bool changed = false
    )
    {
        return new CudaLibraryPathSetupResult(
            false,
            changed,
            failure,
            detail,
            environmentClassification,
            shellProfilePath,
            environmentPath
        );
    }
}

// ReSharper restore NotAccessedPositionalProperty.Global
