using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using TypeWhisper.Plugins.Shared.Net;

namespace TypeWhisper.Plugin.GitHubCopilot;

internal sealed record CopilotRuntimeAsset(string Platform, string Sha256, long Bytes)
{
    internal string FileName => $"github-copilot-{CopilotRuntimeInstaller.Version}-{Platform}.tgz";

    internal string Url =>
        $"https://github.com/github/copilot-cli/releases/download/v{CopilotRuntimeInstaller.Version}/{FileName}";
}

/// <summary>
///     Downloads the official Copilot runtime on first use instead of bundling ~107 MB with
///     every package. The archive is pinned to the version GitHub.Copilot.SDK 1.0.13 was built
///     against and to its published SHA-256, extracted into an owner-only directory under the
///     plugin's data folder, and only then trusted for execution.
/// </summary>
internal class CopilotRuntimeInstaller
{
    internal const string Version = "1.0.83";
    private const string ReceiptFileName = ".typewhisper-runtime.json";
    private const string ArchivePrefix = "package/";
    private const long MaximumExtractedBytes = 400L * 1024 * 1024;
    private const long MaximumEntryBytes = 200L * 1024 * 1024;
    private const int MaximumEntries = 5000;
    private const long ExtractedBytes = 160L * 1024 * 1024;
    private static readonly TimeSpan s_idleTimeout = TimeSpan.FromSeconds(60);

    private static readonly CopilotRuntimeAsset[] s_assets =
    [
        new("linux-x64", "888f8fbb4575c335afba4a8863c647ef04f81e5124c7c794bdcaee90c5fa4503", 51_632_715),
        new("linux-arm64", "364224054898291c92055996ae09144359fd18f199f389b8f48809b105c3860d", 57_326_169),
    ];

    private readonly string _root;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal CopilotRuntimeInstaller(string root, HttpClient httpClient, CopilotRuntimeAsset? asset = null)
    {
        _root = root;
        _httpClient = httpClient;
        Asset = asset ?? CurrentAsset();
    }

    /// <summary>Null when the machine is not glibc Linux on x64 or arm64.</summary>
    internal CopilotRuntimeAsset? Asset { get; }

    private string InstallDirectory => Path.Join(_root, $"{Version}-{Asset!.Platform}");

    internal string ExecutablePath =>
        Path.Join(InstallDirectory, "prebuilds", Asset!.Platform, "copilot-runtime");

    internal virtual bool IsInstalled
    {
        get
        {
            if (Asset is null)
            {
                return false;
            }

            try
            {
                return ReadReceipt(InstallDirectory) == Asset.Sha256
                       && PrivateDirectory.IsPrivate(_root)
                       && PrivateDirectory.IsPrivate(InstallDirectory)
                       && IsOwnerExecutable(ExecutablePath)
                       && File.Exists(Path.Join(Path.GetDirectoryName(ExecutablePath)!, "runtime.node"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return false;
            }
        }
    }

    internal virtual async Task InstallAsync(IProgress<double>? progress, CancellationToken ct)
    {
        var asset = Asset ?? throw new PlatformNotSupportedException();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsInstalled)
            {
                return;
            }

            PrivateDirectory.Ensure(_root);
            await using var fileLock = await InterProcessFileLock
                .AcquireAsync(Path.Join(_root, ".install.lock"), ct)
                .ConfigureAwait(false);
            // Another TypeWhisper process may have finished while this one waited.
            if (IsInstalled)
            {
                return;
            }

            var downloads = Path.Join(_root, ".download");
            PrivateDirectory.Ensure(downloads);
            var archive = Path.Join(downloads, asset.FileName);
            try
            {
                await ResilientDownloader.DownloadToFileAsync(
                        _httpClient,
                        asset.Url,
                        archive,
                        asset.Bytes,
                        s_idleTimeout,
                        allowResume: true,
                        onBytesOnDisk: bytes => progress?.Report(0.9 * Math.Min(1.0, (double)bytes / asset.Bytes)),
                        verifyComplete: path => Verify(path, asset),
                        ct,
                        new DownloadSpaceRequirement("GitHub Copilot runtime", ExtractedBytes))
                    .ConfigureAwait(false);
                await ExtractAndPublishAsync(archive, asset, ct).ConfigureAwait(false);
                progress?.Report(1.0);
            }
            finally
            {
                // A verified archive is only needed until it has been published.
                if (File.Exists(archive) && IsInstalled)
                {
                    File.Delete(archive);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal virtual async Task RemoveAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_root))
            {
                return;
            }

            await using var fileLock = await InterProcessFileLock
                .AcquireAsync(Path.Join(_root, ".install.lock"), ct)
                .ConfigureAwait(false);
            // The lock file itself stays so a concurrent installer never loses the file it holds.
            foreach (var entry in Directory.EnumerateFileSystemEntries(_root))
            {
                if (Path.GetFileName(entry) == ".install.lock")
                {
                    continue;
                }

                if (Directory.Exists(entry) && new DirectoryInfo(entry).LinkTarget is null)
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Verify(string path, CopilotRuntimeAsset asset)
    {
        var length = new FileInfo(path).Length;
        if (length != asset.Bytes)
        {
            throw new InvalidDataException("The downloaded Copilot runtime has an unexpected size.");
        }

        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(actual, asset.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The downloaded Copilot runtime does not match its pinned SHA-256.");
        }
    }

    private async Task ExtractAndPublishAsync(string archive, CopilotRuntimeAsset asset, CancellationToken ct)
    {
        var staging = Path.Join(_root, ".staging-" + Guid.NewGuid().ToString("N"));
        PrivateDirectory.Ensure(staging);
        try
        {
            await ExtractAsync(archive, staging, ct).ConfigureAwait(false);
            var executable = Path.Join(staging, "prebuilds", asset.Platform, "copilot-runtime");
            if (!IsOwnerExecutable(executable)
                || !File.Exists(Path.Join(staging, "prebuilds", asset.Platform, "runtime.node")))
            {
                throw new InvalidDataException("The Copilot runtime archive is missing its executable.");
            }

            // The receipt is written last, so a directory without it is never trusted.
            await using (var receipt = new FileStream(
                             Path.Join(staging, ReceiptFileName),
                             FileMode.CreateNew,
                             FileAccess.Write))
            {
                await JsonSerializer.SerializeAsync(receipt, new RuntimeReceipt(Version, asset.Platform, asset.Sha256), cancellationToken: ct)
                    .ConfigureAwait(false);
                receipt.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            var target = InstallDirectory;
            if (Directory.Exists(target))
            {
                var stale = Path.Join(_root, ".stale-" + Guid.NewGuid().ToString("N"));
                Directory.Move(target, stale);
                Directory.Delete(stale, recursive: true);
            }

            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    internal static async Task ExtractAsync(string archive, string destination, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        await using var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var reader = new TarReader(gzip);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        while (await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.Name.Replace('\\', '/');
            if (entry.EntryType is TarEntryType.GlobalExtendedAttributes)
            {
                continue;
            }

            if (!name.StartsWith(ArchivePrefix, StringComparison.Ordinal)
                || name.Contains(':')
                || name.Contains('\0')
                || name.Split('/').Any(segment => segment is ".." or ".")
                || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile
                    or TarEntryType.Directory))
            {
                throw new InvalidDataException("Unsafe entry in the Copilot runtime archive.");
            }

            var relative = name[ArchivePrefix.Length..].TrimEnd('/');
            if (relative.Length == 0)
            {
                continue;
            }

            if (seen.Count >= MaximumEntries || !seen.Add(relative))
            {
                throw new InvalidDataException("Unexpected entries in the Copilot runtime archive.");
            }

            var path = Path.GetFullPath(Path.Join(destination, relative));
            if (!path.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Unsafe entry in the Copilot runtime archive.");
            }

            if (entry.EntryType == TarEntryType.Directory)
            {
                PrivateDirectory.Ensure(path);
                continue;
            }

            if (entry.Length > MaximumEntryBytes || (total += entry.Length) > MaximumExtractedBytes)
            {
                throw new InvalidDataException("The Copilot runtime archive is larger than expected.");
            }

            PrivateDirectory.Ensure(Path.GetDirectoryName(path)!);
            // Archive modes are not trusted: files are owner-only, executable only if marked so.
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if ((entry.Mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
            {
                mode |= UnixFileMode.UserExecute;
            }

            await using (var output = new FileStream(path, new FileStreamOptions
                         {
                             Mode = FileMode.CreateNew,
                             Access = FileAccess.Write,
                             Share = FileShare.None,
                             Options = FileOptions.Asynchronous,
                             UnixCreateMode = mode,
                         }))
            {
                if (entry.DataStream is { } data)
                {
                    await data.CopyToAsync(output, ct).ConfigureAwait(false);
                }

                if (output.Length != entry.Length)
                {
                    throw new InvalidDataException("Incomplete entry in the Copilot runtime archive.");
                }
            }

            File.SetUnixFileMode(path, mode);
        }
    }

    private static string? ReadReceipt(string directory)
    {
        var path = Path.Join(directory, ReceiptFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var receipt = JsonSerializer.Deserialize<RuntimeReceipt>(File.ReadAllText(path));
        return receipt is { Version: Version } ? receipt.Sha256 : null;
    }

    private static bool IsOwnerExecutable(string path)
    {
        var info = new FileInfo(path);
        return info is { Exists: true, LinkTarget: null }
               && (info.UnixFileMode & UnixFileMode.UserExecute) != 0
               && (info.UnixFileMode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0;
    }

    private static CopilotRuntimeAsset? CurrentAsset()
    {
        if (!OperatingSystem.IsLinux() || IsMusl())
        {
            return null;
        }

        var platform = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "linux-x64",
            Architecture.Arm64 => "linux-arm64",
            _ => null,
        };
        return s_assets.FirstOrDefault(asset => asset.Platform == platform);
    }

    // The libc this process runs on, not whether a musl loader happens to be installed alongside glibc.
    internal static bool IsMusl(IEnumerable<string> processMaps) =>
        processMaps.Any(line => line.Contains("/ld-musl-", StringComparison.Ordinal)
                                || line.Contains("/libc.musl-", StringComparison.Ordinal));

    private static bool IsMusl()
    {
        try
        {
            return IsMusl(File.ReadLines("/proc/self/maps"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ReSharper disable once MemberHidesStaticFromOuterClass -- the receipt records the installed Version.
    private sealed record RuntimeReceipt(string Version, string Platform, string Sha256);
}

/// <summary>Directories the runtime runs from must not be writable by anyone else.</summary>
internal static class PrivateDirectory
{
    private const UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    internal static void Ensure(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        // CreateDirectory applies the mode only to the last level, so create each missing one.
        var missing = new Stack<string>();
        for (var current = Path.GetFullPath(path);
             !Directory.Exists(current);
             current = Path.GetDirectoryName(current)!)
        {
            missing.Push(current);
        }

        while (missing.TryPop(out var directory))
        {
            Directory.CreateDirectory(directory, OwnerOnly);
        }

        if (!IsPrivate(path))
        {
            throw new IOException("A GitHub Copilot data directory is shared or a symbolic link.");
        }
    }

    internal static bool IsPrivate(string path)
    {
        var info = new DirectoryInfo(path);
        return info is { Exists: true, LinkTarget: null } && (info.UnixFileMode & ~OwnerOnly) == 0;
    }
}

/// <summary>
///     Bounds connecting and waiting for response headers. The downloader's idle watchdog only
///     starts once headers arrive, and the body must not be limited, so HttpClient.Timeout cannot.
/// </summary>
internal sealed class ResponseHeadersTimeoutHandler(TimeSpan timeout)
    : DelegatingHandler(new SocketsHttpHandler { ConnectTimeout = timeout })
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headers.CancelAfter(timeout);
        try
        {
            return await base.SendAsync(request, headers.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (headers.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }
}
