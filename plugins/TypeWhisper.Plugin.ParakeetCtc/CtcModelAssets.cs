using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using TypeWhisper.Plugins.Shared.Net;

namespace TypeWhisper.Plugin.ParakeetCtc;

public sealed record CtcAssetSource(string Url, string Sha256, long MaximumBytes);

/// <summary>Prepares verified CTC assets without publishing partial downloads or extracted files.</summary>
public sealed class CtcModelAssets(
    HttpClient http,
    CtcAssetSource? archive = null,
    CtcAssetSource? tokenizer = null,
    Func<string, long?>? spaceProbe = null
)
{
    private const string SpaceDescription = "the dictionary boosting model";

    // The pinned archive expands to a ~132 MB tar holding the ~132 MB model, both
    // staged beside the archive before the tar is discarded.
    private const long ArchiveExtractionBytes = 270L * 1024 * 1024;

    private static CtcAssetSource Archive { get; } =
        new(
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet_tdt_ctc_110m-en-36000-int8.tar.bz2",
            "17f945007b52ccd8b7200ffc7c5652e9e8e961dfdf479cefcabd06cf5703630b",
            512L * 1024 * 1024
        );
    private static CtcAssetSource Tokenizer { get; } =
        new(
            "https://huggingface.co/FluidInference/parakeet-ctc-110m-coreml/resolve/main/tokenizer.json",
            "9f7c517c0bf644b1b690ab037bab4d4c53aecd38e047e7154d011013ab9160db",
            16L * 1024 * 1024
        );
    private readonly CtcAssetSource _archive = archive ?? Archive;
    private readonly CtcAssetSource _tokenizer = tokenizer ?? Tokenizer;
    private static readonly string[] s_required =
    [
        "model.int8.onnx",
        "tokens.txt",
        "tokenizer.json",
    ];

    private sealed record Receipt(
        string ArchiveHash,
        string TokenizerHash,
        Dictionary<string, string> Files
    );

    public async Task EnsureAsync(
        string directory,
        CancellationToken cancellationToken,
        Action<string>? report = null
    )
    {
        directory = Path.GetFullPath(directory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        var parent = Path.GetDirectoryName(directory)!;
        Directory.CreateDirectory(parent);
        await using var lease = await InterProcessFileLock
            .AcquireAsync(Path.Join(parent, ".ctc-assets.lock"), ct)
            .ConfigureAwait(false);
        if (await IsVerifiedAsync(directory, ct).ConfigureAwait(false))
            return;
        var staging = Path.Join(parent, ".ctc-staging-" + Guid.NewGuid().ToString("N"));
        var ready = Path.Join(staging, "model");
        Directory.CreateDirectory(ready);
        try
        {
            report?.Invoke("Downloading NVIDIA dictionary boosting model…");
            var archivePath = Path.Join(staging, "model.archive");
            await DownloadAsync(_archive, archivePath, ArchiveExtractionBytes, ct)
                .ConfigureAwait(false);
            report?.Invoke("Verifying and extracting dictionary boosting model…");
            await ExtractAsync(archivePath, ready, ct).ConfigureAwait(false);
            report?.Invoke("Downloading dictionary tokenizer…");
            await DownloadAsync(_tokenizer, Path.Join(ready, "tokenizer.json"), 0, ct)
                .ConfigureAwait(false);
            if (new CtcTokenizer(Path.Join(ready, "tokens.txt")).BlankId != 1024)
                throw new InvalidDataException(
                    "The downloaded tokenizer does not match the CTC model."
                );
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in s_required)
                hashes[name] = await HashAsync(Path.Join(ready, name), ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                    Path.Join(ready, "verified-assets.json"),
                    JsonSerializer.Serialize(
                        new Receipt(_archive.Sha256, _tokenizer.Sha256, hashes)
                    ),
                    ct
                )
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            Publish(ready, directory, report);
            report?.Invoke("Dictionary boosting assets are ready.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Dictionary boosting setup timed out. Check your connection and enable vocabulary rescoring again to retry."
            );
        }
        catch (Exception ex)
            when (DownloadSpace.TranslateWriteFailure(ex, parent, SpaceDescription) is { } full)
        {
            // Keep the space shortfall visible instead of the generic setup wrapper below.
            if (ReferenceEquals(full, ex))
                throw;
            throw full;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new IOException(
                "Dictionary boosting setup failed. Check your connection and available disk space, then enable vocabulary rescoring again to retry. "
                    + ex.Message,
                ex
            );
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
        }
    }

    private async Task<bool> IsVerifiedAsync(string directory, CancellationToken ct)
    {
        var receiptPath = Path.Join(directory, "verified-assets.json");
        if (!File.Exists(receiptPath))
            return false;
        try
        {
            var receipt = JsonSerializer.Deserialize<Receipt>(
                await File.ReadAllTextAsync(receiptPath, ct).ConfigureAwait(false)
            );
            if (
                receipt is null
                || receipt.ArchiveHash != _archive.Sha256
                || receipt.TokenizerHash != _tokenizer.Sha256
                // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- the receipt is JSON-deserialized; Files can be null at runtime.
                || receipt.Files is null
            )
                return false;
            foreach (var name in s_required)
                if (
                    !File.Exists(Path.Join(directory, name))
                    || !receipt.Files.TryGetValue(name, out var hash)
                    || !string.Equals(
                        hash,
                        await HashAsync(Path.Join(directory, name), ct).ConfigureAwait(false),
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    return false;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private Task DownloadAsync(
        CtcAssetSource source,
        string destination,
        long extractionBytes,
        CancellationToken ct
    ) =>
        ResilientDownloader.DownloadToFileAsync(
            http,
            source.Url,
            destination,
            approxTotalBytes: null,
            idleTimeout: TimeSpan.FromSeconds(60),
            allowResume: true,
            // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local -- callback signature; the byte count is the only input.
            onBytesOnDisk: bytes =>
            {
                if (bytes > source.MaximumBytes)
                    throw new InvalidDataException(
                        $"CTC asset exceeded {source.MaximumBytes} bytes; the download was not installed."
                    );
            },
            verifyComplete: path => VerifyAsset(path, source),
            ct,
            new DownloadSpaceRequirement(SpaceDescription, extractionBytes, spaceProbe)
        );

    private static void VerifyAsset(string path, CtcAssetSource source)
    {
        var length = new FileInfo(path).Length;
        if (length == 0 || length > source.MaximumBytes)
            throw new InvalidDataException(
                $"CTC asset size mismatch: expected 1–{source.MaximumBytes} bytes, got {length}; the download was not installed."
            );
        using var input = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(input));
        if (!string.Equals(source.Sha256, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"CTC asset checksum mismatch: expected {source.Sha256}, got {actual}; the download was not installed."
            );
    }

    private static async Task ExtractAsync(
        string archivePath,
        string destination,
        CancellationToken ct
    )
    {
        // The pinned artifact is a BZip2-compressed TAR. Archive auto-detection can
        // identify TAR without decoding its BZip2 layer. Decode explicitly and
        // bound the entire expanded archive, including entries we do not install.
        var tarPath = archivePath + ".tar";
        await using (
            var compressed = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                true
            )
        )
        await using (
            var decompressed = await BZip2Stream
                .CreateAsync(compressed, CompressionMode.Decompress, false, leaveOpen: true, ct)
                .ConfigureAwait(false)
        )
        await using (
            var expanded = new FileStream(
                tarPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                true
            )
        )
            await CopyBoundedAsync(decompressed, expanded, 600L * 1024 * 1024, ct)
                .ConfigureAwait(false);
        await using var tar = new FileStream(
            tarPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            true
        );
        await using var reader = new TarReader(tar, leaveOpen: true);
        var found = new HashSet<string>(StringComparer.Ordinal);
        while (
            await reader
                .GetNextEntryAsync(copyData: false, cancellationToken: ct)
                .ConfigureAwait(false)
                is { } entry
        )
        {
            ct.ThrowIfCancellationRequested();
            var key = entry.Name.Replace('\\', '/');
            if (
                key.StartsWith('/')
                || key.Contains(':')
                || key.Split('/').Contains("..")
                || entry.EntryType
                    is not (
                        TarEntryType.RegularFile
                        or TarEntryType.V7RegularFile
                        or TarEntryType.Directory
                    )
            )
                throw new InvalidDataException("Unsafe entry in CTC model archive.");
            if (entry.EntryType == TarEntryType.Directory)
                continue;
            var name = key.Split('/').Last();
            if (name is not ("model.int8.onnx" or "tokens.txt"))
                continue;
            if (!found.Add(name))
                throw new InvalidDataException("Duplicate CTC model archive entry.");
            var maximum = name == "tokens.txt" ? 4L * 1024 * 1024 : 512L * 1024 * 1024;
            if (entry.Length <= 0 || entry.Length > maximum || entry.DataStream is null)
                throw new InvalidDataException("Unexpected extracted CTC model size.");
            var input = entry.DataStream;
            await using var output = new FileStream(
                Path.Join(destination, name),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                true
            );
            if (
                await CopyBoundedAsync(input, output, maximum, ct).ConfigureAwait(false)
                != entry.Length
            )
                throw new InvalidDataException("Incomplete CTC model archive entry.");
        }
        if (found.Count != 2)
            throw new InvalidDataException("The CTC model archive is missing required files.");
    }

    private static async Task<long> CopyBoundedAsync(
        Stream input,
        Stream output,
        long maximum,
        CancellationToken ct
    )
    {
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += count;
            if (total > maximum)
                throw new InvalidDataException(
                    "The CTC asset exceeded its download or extraction size limit."
                );
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        return total;
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            true
        );
        return Convert.ToHexString(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false));
    }

    private static void Publish(string ready, string destination, Action<string>? report)
    {
        var backup = destination + ".previous-" + Guid.NewGuid().ToString("N");
        var previous = Directory.Exists(destination);
        var owned =
            previous
            && File.Exists(Path.Join(destination, "verified-assets.json"))
            && !Directory.EnumerateDirectories(destination).Any()
            && Directory
                .EnumerateFiles(destination)
                .All(path =>
                    s_required.Contains(Path.GetFileName(path))
                    || Path.GetFileName(path) == "verified-assets.json"
                );
        if (previous)
            Directory.Move(destination, backup);
        try
        {
            Directory.Move(ready, destination);
        }
        catch
        {
            if (previous)
                Directory.Move(backup, destination);
            throw;
        }
        // ReSharper disable once ConvertIfStatementToSwitchStatement -- a tuple switch over (previous, owned) would hide that the second branch is the fallback for a retained backup.
        if (previous && owned)
        {
            try
            {
                Directory.Delete(backup, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report?.Invoke(
                    "Dictionary assets are installed; an older backup could not be removed: "
                        + backup
                );
            }
        }
        else if (previous)
            report?.Invoke(
                "Previous manually supplied dictionary assets were retained at " + backup
            );
    }
}
