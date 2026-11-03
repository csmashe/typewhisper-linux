using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TypeWhisper.Core.Services.Diagnostics;

/// <summary>One line of the local diagnostic log. Every field is an allowlisted, content-free value.</summary>
/// <remarks>
///     Events are dotted names, data uses known keys with flag, number or short token values, and errors are
///     exception type names, HResults and method names. None of these can carry a transcript, prompt, clipboard
///     contents, a window title, a path or an exception message; <see cref="DiagnosticLogFile.Admit" /> drops
///     anything else.
/// </remarks>
public sealed record DiagnosticLogLine(
    DateTimeOffset Time,
    string Event,
    Guid? CorrelationId = null,
    long? ElapsedMs = null,
    IReadOnlyDictionary<string, string>? Data = null,
    string? Error = null,
    [property: JsonPropertyName("hresult")] string? HResult = null,
    IReadOnlyList<string>? Stack = null
);

/// <summary>Whether the local diagnostic log is kept, and for how many days. Off unless the user turns it on.</summary>
public sealed record DiagnosticLogPreferences(bool Enabled = false, int RetentionDays = DiagnosticLogPreferences.DefaultRetentionDays)
{
    public const int DefaultRetentionDays = 7;

    private static IReadOnlyList<int> RetentionChoices { get; } = [1, 3, 7, 14, 30];

    public bool IsValid => RetentionChoices.Contains(RetentionDays);

    public static int NormalizeRetentionDays(int days) =>
        RetentionChoices.Contains(days) ? days : DefaultRetentionDays;
}

/// <summary>A size-capped JSON Lines file of content-free diagnostic events with day-based retention.</summary>
/// <remarks>Best-effort: file system failures never reach the caller, so logging cannot break dictation or startup.</remarks>
public sealed partial class DiagnosticLogFile
{
    /// <summary>The file is trimmed to its newest lines once it grows beyond this size.</summary>
    public const long MaximumBytes = 2 * 1024 * 1024;

    private const long TrimmedBytes = 3 * MaximumBytes / 4;
    private const long MaximumElapsedMs = 24 * 60 * 60 * 1000;
    private const int MaximumDataEntries = 16;
    private const int MaximumStackFrames = 12;
    private static readonly TimeSpan s_pruneInterval = TimeSpan.FromHours(1);

    // Only these keys are recorded, each with its own value domain, so a new tag cannot carry content by
    // accident: it is dropped until it is added here.
    private static readonly FrozenDictionary<string, Func<string, bool>> s_dataKeys =
        new Dictionary<string, Func<string, bool>>
        {
            ["debug"] = Flag,
            ["profile.used"] = Flag,
            ["startup.minimized"] = Flag,
            ["audio.duration_s"] = Number,
            ["duration-ms"] = Number,
            ["http.status"] = Number,
            ["retention-days"] = Number,
            ["startup.time_to_window_ms"] = Number,
            ["transcript.chars"] = Number,
            ["arch"] = Token,
            ["cleanup.level"] = Token,
            ["engine.model"] = Token,
            ["engine.provider"] = Token,
            ["install"] = Token,
            ["mode"] = Token,
            ["os"] = Token,
            ["plugin.id"] = Token,
            ["runtime"] = Token,
            ["socket.error"] = Token,
            ["version"] = Token,
            ["outcome"] = value => OutcomeValue().IsMatch(value),
            ["language"] = value => LanguageValue().IsMatch(value),
            ["operation"] = value => EventName().IsMatch(value),
            ["inner"] = value => TypeName().IsMatch(value),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly Lock _lock = new();
    private readonly Func<DateTimeOffset> _clock;
    private DiagnosticLogPreferences _preferences;
    private DateTimeOffset _nextPrune = DateTimeOffset.MinValue;

    /// <summary>Nothing is read or written until the first call.</summary>
    public DiagnosticLogFile(string path, DiagnosticLogPreferences preferences, Func<DateTimeOffset>? clock = null)
    {
        FilePath = Path.GetFullPath(path);
        _preferences = preferences.IsValid ? preferences : preferences with { RetentionDays = DiagnosticLogPreferences.DefaultRetentionDays };
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    private string FilePath { get; }

    public DiagnosticLogPreferences Preferences
    {
        get
        {
            lock (_lock)
            {
                return _preferences;
            }
        }
    }

    /// <summary>Applies new preferences. Turning the log off deletes it; a shorter retention prunes it now.</summary>
    /// <returns>False when the file could not be deleted or pruned.</returns>
    public bool Configure(DiagnosticLogPreferences preferences)
    {
        if (!preferences.IsValid)
        {
            return false;
        }

        lock (_lock)
        {
            _preferences = preferences;
            return BestEffort(() =>
            {
                if (!preferences.Enabled)
                {
                    File.Delete(FilePath);
                }
                else
                {
                    PruneUnsafe();
                }
            });
        }
    }

    /// <summary>Appends one line after reducing it to allowlisted values. Ignored while the log is off.</summary>
    public void Write(DiagnosticLogLine line)
    {
        var admitted = Admit(line);
        lock (_lock)
        {
            if (!_preferences.Enabled)
            {
                return;
            }

            BestEffort(() =>
            {
                if (_clock() >= _nextPrune)
                {
                    PruneUnsafe();
                }

                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                using (var stream = OpenForAppend())
                {
                    stream.Write(s_utf8.GetBytes(JsonSerializer.Serialize(admitted, s_options) + "\n"));
                }

                if (new FileInfo(FilePath).Length > MaximumBytes)
                {
                    RewriteUnsafe(_ => true, TrimmedBytes);
                }
            });
        }
    }

    /// <summary>Applies retention when the hourly prune is due, so entries expire while no events are written.</summary>
    public void PruneIfDue()
    {
        lock (_lock)
        {
            if (_preferences.Enabled && _clock() >= _nextPrune)
            {
                BestEffort(PruneUnsafe);
            }
        }
    }

    /// <summary>Writes the header and every retained line; returns the number of retained lines.</summary>
    /// <exception cref="IOException">The destination could not be written.</exception>
    public int Export(string destination, DiagnosticLogLine header)
    {
        var lines = Snapshot();
        var content = new StringBuilder();
        foreach (var line in new[] { Admit(header) }.Concat(lines))
        {
            content.Append(JsonSerializer.Serialize(line, s_options)).Append('\n');
        }

        AtomicFileWrite.WriteAllText(destination, content.ToString());
        return lines.Count;
    }

    /// <summary>Reads only retained, admitted entries. Disabled logging never exposes a leftover file.</summary>
    /// <exception cref="IOException">The log exists but could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The log exists but could not be read.</exception>
    public IReadOnlyList<DiagnosticLogLine> Snapshot()
    {
        lock (_lock)
        {
            if (!_preferences.Enabled)
            {
                return [];
            }

            BestEffort(PruneUnsafe);
            // Filter again: the prune may have failed to rewrite the file.
            var cutoff = _clock() - TimeSpan.FromDays(_preferences.RetentionDays);
            return ReadUnsafe(line => line.Time >= cutoff);
        }
    }

    /// <summary>Deletes every line. The log continues with new events while it is on.</summary>
    /// <returns>False when the file could not be deleted.</returns>
    public bool Clear()
    {
        lock (_lock)
        {
            return BestEffort(() => File.Delete(FilePath));
        }
    }

    /// <summary>Reduces a line to allowlisted values: invalid fields are dropped and an invalid event is renamed.</summary>
    public static DiagnosticLogLine Admit(DiagnosticLogLine line)
    {
        // Lines read back from disk can hold JSON nulls anywhere; they are invalid values, not errors.
        var data = line.Data?
            .Where(entry => entry.Key is not null
                && s_dataKeys.TryGetValue(entry.Key, out var valid)
                && entry.Value is not null
                && valid(entry.Value))
            .Take(MaximumDataEntries)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        var stack = line.Stack?
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract -- lines read back from disk can hold JSON nulls despite the annotation.
            .Where(frame => frame is not null && StackFrame().IsMatch(frame))
            .Take(MaximumStackFrames)
            .ToArray();
        return new DiagnosticLogLine(
            line.Time.ToUniversalTime(),
            line.Event is { } name && EventName().IsMatch(name) ? name : "diagnostics.invalid-event",
            line.CorrelationId is { } id && id != Guid.Empty ? id : null,
            line.ElapsedMs is >= 0 and <= MaximumElapsedMs ? line.ElapsedMs : null,
            data is { Count: > 0 } ? data : null,
            line.Error is { } error && TypeName().IsMatch(error) ? error : null,
            line.HResult is { } hresult && HResultValue().IsMatch(hresult) ? hresult : null,
            stack is { Length: > 0 } ? stack : null
        );
    }

    private FileStream OpenForAppend()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.ReadWrite | FileShare.Delete,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerOnly;
        }

        return new FileStream(FilePath, options);
    }

    // Retention and the size cap rewrite the file through the same parse and admission as a write,
    // so an edited or corrupted line can never survive a prune or reach an export.
    private void PruneUnsafe()
    {
        // A failed prune is retried with the next write instead of an hour later.
        _nextPrune = DateTimeOffset.MinValue;
        if (File.Exists(FilePath))
        {
            var cutoff = _clock() - TimeSpan.FromDays(_preferences.RetentionDays);
            RewriteUnsafe(line => line.Time >= cutoff, long.MaxValue);
        }

        _nextPrune = _clock() + s_pruneInterval;
    }

    private void RewriteUnsafe(Func<DiagnosticLogLine, bool> keep, long maximumBytes)
    {
        var selected = new List<string>();
        long bytes = 0;
        // Keep the newest lines when the size cap applies.
        foreach (var line in Enumerable.Reverse(ReadUnsafe(keep)))
        {
            var json = JsonSerializer.Serialize(line, s_options);
            bytes += s_utf8.GetByteCount(json) + 1;
            if (bytes > maximumBytes)
            {
                break;
            }

            selected.Add(json);
        }

        selected.Reverse();
        var content = new StringBuilder();
        foreach (var json in selected)
        {
            content.Append(json).Append('\n');
        }

        AtomicFileWrite.WriteAllBytes(FilePath, s_utf8.GetBytes(content.ToString()));
    }

    private List<DiagnosticLogLine> ReadUnsafe(Func<DiagnosticLogLine, bool> keep)
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // A file larger than the cap was edited or left behind; read only its newest part so memory stays
        // bounded, and drop the line the cut lands in.
        var cut = stream.Length > MaximumBytes;
        if (cut)
        {
            stream.Seek(-MaximumBytes, SeekOrigin.End);
        }

        using var reader = new StreamReader(stream, s_utf8);
        if (cut)
        {
            reader.ReadLine();
        }

        var lines = new List<DiagnosticLogLine>();
        while (reader.ReadLine() is { } text)
        {
            if (TryParse(text) is { } line && keep(line))
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private static DiagnosticLogLine? TryParse(string text)
    {
        try
        {
            // ReSharper disable once RedundantAlwaysMatchSubpattern -- a line read back from disk can hold a JSON null event despite the annotation.
            if (JsonSerializer.Deserialize<DiagnosticLogLine>(text, s_options) is not { Event: not null } line)
            {
                return null;
            }

            var admitted = Admit(line);
            // A line that needed any change was not written by this log; drop it rather than guess.
            return JsonSerializer.Serialize(admitted, s_options) == JsonSerializer.Serialize(line, s_options)
                ? admitted
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool BestEffort(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Flag(string value) => value is "true" or "false";

    private static bool Number(string value) => NumberValue().IsMatch(value);

    private static bool Token(string value) => TokenValue().IsMatch(value);

    [GeneratedRegex(@"^(?=.{1,80}$)[a-z0-9]+(?:[.\-][a-z0-9]+)*$")]
    private static partial Regex EventName();

    [GeneratedRegex(@"^[0-9]{1,10}(?:\.[0-9]{1,3})?$")]
    private static partial Regex NumberValue();

    [GeneratedRegex(@"^[A-Za-z0-9_.:+\-]{1,64}$")]
    private static partial Regex TokenValue();

    [GeneratedRegex("^[a-z][a-z_]{0,31}$")]
    private static partial Regex OutcomeValue();

    [GeneratedRegex("^(?:auto|[a-z]{2,3}(?:-[A-Za-z0-9]{2,8}){0,3})$")]
    private static partial Regex LanguageValue();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.+`]{0,127}$")]
    private static partial Regex TypeName();

    [GeneratedRegex("^0x[0-9A-F]{8}$")]
    private static partial Regex HResultValue();

    [GeneratedRegex("^[A-Za-z0-9_.+`<>|$]{1,160}$")]
    private static partial Regex StackFrame();
}
