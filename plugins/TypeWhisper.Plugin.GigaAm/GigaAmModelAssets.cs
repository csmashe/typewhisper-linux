using System.Security.Cryptography;
using TypeWhisper.Plugins.Shared.Net;

namespace TypeWhisper.Plugin.GigaAm;

/// <summary>One model file, pinned to the size and SHA-256 it has at the pinned revision.</summary>
internal sealed record GigaAmModelFile(string Name, long Size, string Sha256);

/// <summary>Downloads the GigaAM v3 model files and publishes each one only after its hash matched.</summary>
internal sealed class GigaAmModelAssets(
    HttpClient http,
    string baseUrl,
    IReadOnlyList<GigaAmModelFile> files,
    Func<string, long?>? availableBytes = null
)
{
    // A commit, not main: main can serve different bytes at the same URL. The SHA-256 values are
    // the LFS object ids at that commit (2026-02-18, as upstream pinned it); the vocabulary, not in
    // LFS, was hashed from a download.
    internal const string Revision = "322c3b29492673eb7d0b434bfa9dfb8653e34d02";
    internal const string DefaultBaseUrl =
        "https://huggingface.co/istupakov/gigaam-v3-onnx/resolve/" + Revision;

    internal static IReadOnlyList<GigaAmModelFile> DefaultFiles { get; } =
    [
        new(
            GigaAmModelFiles.Encoder,
            224_570_477,
            "4e0e076a6076cd110277e529b8ac8f32cd5297f7fbebad5341ae8ddb7d00817b"
        ),
        new(
            GigaAmModelFiles.Decoder,
            1_159_170,
            "89014e134865615b91e037157e46e389b1271e6072460efc010ea08e61e23146"
        ),
        new(
            GigaAmModelFiles.Joint,
            687_791,
            "ade116563dbf66e503b0994efab6b5861412743e52bf31c39fc3fffa3783d5d1"
        ),
        new(
            GigaAmModelFiles.Vocabulary,
            13_354,
            "39abae20e692998290c574e606f11a9edef2902a1995463fcff63d1490cf22b7"
        ),
    ];

    private const string SpaceDescription = "the GigaAM model";

    internal long TotalBytes => files.Sum(file => file.Size);

    /// <summary>
    ///     Whether every file is present at its final name with its pinned size. A file only
    ///     gets that name after its hash matched, so the size check is enough here.
    /// </summary>
    internal bool IsReady(string directory) => files.All(file => IsPresent(directory, file));

    internal async Task DownloadAsync(
        string directory,
        IProgress<double>? progress,
        CancellationToken ct
    )
    {
        Directory.CreateDirectory(directory);
        // Two app instances, or the app and its transcription worker, share this directory.
        await using var lease = await InterProcessFileLock
            .AcquireAsync(Path.Join(directory, ".download.lock"), ct)
            .ConfigureAwait(false);

        var missing = files.Where(file => !IsPresent(directory, file)).ToList();
        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator -- the loop deletes files; a LINQ projection would hide that side effect.
        foreach (var file in missing)
        {
            // A wrong-sized file at the final name is not ours to resume; the downloader's own
            // .partial file is, and its hash check decides whether the resumed bytes are kept.
            var path = Path.Join(directory, file.Name);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        DownloadSpace.EnsureAvailable(
            directory,
            missing.Sum(file => file.Size - PartialBytes(directory, file)),
            SpaceDescription,
            availableBytes
        );

        double total = TotalBytes;
        double completed = files.Except(missing).Sum(file => file.Size);
        progress?.Report(completed / total);
        foreach (var file in missing)
        {
            ct.ThrowIfCancellationRequested();
            var start = completed;
            await ResilientDownloader
                .DownloadToFileAsync(
                    http,
                    baseUrl + "/" + file.Name,
                    Path.Join(directory, file.Name),
                    approxTotalBytes: file.Size,
                    idleTimeout: TimeSpan.FromSeconds(60),
                    allowResume: true,
                    onBytesOnDisk: bytes =>
                    {
                        if (bytes > file.Size)
                        {
                            throw new InvalidDataException(
                                $"The downloaded {file.Name} is larger than expected."
                            );
                        }

                        progress?.Report((start + bytes) / total);
                    },
                    verifyComplete: path => Verify(file, path),
                    ct,
                    new DownloadSpaceRequirement(SpaceDescription, 0, availableBytes)
                )
                .ConfigureAwait(false);
            completed += file.Size;
            progress?.Report(completed / total);
        }
    }

    private static bool IsPresent(string directory, GigaAmModelFile file) =>
        new FileInfo(Path.Join(directory, file.Name)) is { Exists: true } info
        && info.Length == file.Size;

    private static long PartialBytes(string directory, GigaAmModelFile file) =>
        new FileInfo(Path.Join(directory, file.Name + ".partial")) is { Exists: true } info
            ? Math.Min(info.Length, file.Size)
            : 0;

    private static void Verify(GigaAmModelFile file, string path)
    {
        long length;
        string actual;
        using (var stream = File.OpenRead(path))
        {
            length = stream.Length;
            actual = Convert.ToHexString(SHA256.HashData(stream));
        }

        if (
            length == file.Size
            && string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase)
        )
        {
            return;
        }

        throw new InvalidDataException(
            $"The downloaded {file.Name} does not match its expected checksum."
        );
    }
}
