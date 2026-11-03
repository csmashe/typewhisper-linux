using System.Text.Json;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.AppImport;

/// <summary>
///     Reads the custom words from Handy's <c>settings_store.json</c> (tauri-plugin-store,
///     <c>{"settings": {"custom_words": [...]}}</c>). Handy rewrites the file whole while it runs,
///     so a read is only trusted when two consecutive reads are byte-identical.
/// </summary>
public static class HandySettingsReader
{
    public const string AppIdentifier = "com.pais.handy";
    public const string SettingsFileName = "settings_store.json";

    internal const int MaxFileBytes = 8 * 1024 * 1024;
    internal const int MaxWords = 10_000;
    private const int Attempts = 3;
    private const int ChunkBytes = 80 * 1024;
    private static readonly TimeSpan s_retryDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>Handy's custom words as dictionary terms.</summary>
    public static AppImportBatch Load(
        string path,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, byte[]>? read = null
    ) =>
        new([.. ReadCustomWords(path, cancellationToken, read).Select(word => new DictionaryImportCandidate(DictionaryEntryType.Term, word))]);

    /// <summary>Returns the trimmed custom words in file order; a missing or null list is empty, not an error.</summary>
    /// <param name="path">Handy's <c>settings_store.json</c>.</param>
    /// <param name="cancellationToken">Stops reading between chunks and attempts.</param>
    /// <param name="read">Test seam that replaces the bounded file read.</param>
    public static IReadOnlyList<string> ReadCustomWords(
        string path,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, byte[]>? read = null
    )
    {
        read ??= ReadBounded;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            // Handy's rewrite is not atomic; back-to-back retries could all land inside one save.
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt > 1)
            {
                cancellationToken.WaitHandle.WaitOne(s_retryDelay);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var first = read(path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!first.AsSpan().SequenceEqual(read(path, cancellationToken)))
            {
                continue;
            }

            try
            {
                return Decode(first);
            }
            catch (AppImportReadException ex) when (ex.Failure == AppImportReadFailure.Malformed && attempt < Attempts)
            {
                // A torn write can be read identically twice; give Handy a chance to finish it.
            }
        }

        throw new AppImportReadException(
            AppImportReadFailure.Unstable,
            "Could not read a stable copy of the Handy settings file."
        );
    }

    internal static IReadOnlyList<string> Decode(ReadOnlyMemory<byte> utf8)
    {
        if (utf8.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            utf8 = utf8[3..];
        }

        try
        {
            // Parse, unlike ParseValue, rejects anything after the first value.
            using var document = JsonDocument.Parse(utf8);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Malformed("The settings file is not a JSON object.");
            }

            if (!TryGetUniqueProperty(root, "settings", out var settings) || settings.ValueKind == JsonValueKind.Null)
            {
                return [];
            }

            if (settings.ValueKind != JsonValueKind.Object)
            {
                throw Malformed("\"settings\" is not an object.");
            }

            if (!TryGetUniqueProperty(settings, "custom_words", out var words) || words.ValueKind == JsonValueKind.Null)
            {
                return [];
            }

            if (words.ValueKind != JsonValueKind.Array)
            {
                throw Malformed("\"custom_words\" is not an array.");
            }

            if (words.GetArrayLength() > MaxWords)
            {
                throw new AppImportReadException(
                    AppImportReadFailure.TooManyEntries,
                    $"Handy's list has more than {MaxWords} words."
                );
            }

            var result = new List<string>(words.GetArrayLength());
            foreach (var word in words.EnumerateArray())
            {
                if (word.ValueKind != JsonValueKind.String)
                {
                    throw Malformed("A custom word is not a string.");
                }

                result.Add(word.GetString()!.Trim());
            }

            return result;
        }
        catch (JsonException ex)
        {
            throw Malformed("The settings file is not valid JSON.", ex);
        }
    }

    /// <summary>Handy's serde reader would take the last of two same-named fields; refuse rather than guess.</summary>
    private static bool TryGetUniqueProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        var found = false;
        // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator -- the loop must see every same-named field to reject duplicates, which a filtered query would hide behind another enumerator.
        foreach (var property in element.EnumerateObject())
        {
            if (!property.NameEquals(name))
            {
                continue;
            }

            if (found)
            {
                throw Malformed($"Duplicate \"{name}\" field.");
            }

            value = property.Value;
            found = true;
        }

        return found;
    }

    private static AppImportReadException Malformed(string message, Exception? inner = null) =>
        new(AppImportReadFailure.Malformed, message, inner);

    private static byte[] ReadBounded(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1
        );
        using var buffer = new MemoryStream();
        var chunk = new byte[ChunkBytes];
        int count;
        while ((count = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length + count > MaxFileBytes)
            {
                throw new AppImportReadException(
                    AppImportReadFailure.TooLarge,
                    "The Handy settings file is larger than 8 MB."
                );
            }

            buffer.Write(chunk, 0, count);
        }

        return buffer.ToArray();
    }
}
