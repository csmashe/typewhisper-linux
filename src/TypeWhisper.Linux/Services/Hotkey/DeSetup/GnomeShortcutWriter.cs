using System.Globalization;
using System.Text;
using TypeWhisper.Core;

namespace TypeWhisper.Linux.Services.Hotkey.DeSetup;

/// <summary>
///     GNOME helper that wires TypeWhisper's dictation toggle into the GNOME
///     custom-keybindings list under
///     <c>org.gnome.settings-daemon.plugins.media-keys</c>.
///     Critical invariant: we never overwrite the user's existing custom
///     keybinding list. We read it, append our path if missing, and write
///     the merged list back. Before any write we snapshot the current list
///     to a timestamped file in <c>~/.config/typewhisper/backups/</c> so a
///     parse bug here can't silently delete the user's other shortcuts.
/// </summary>
public sealed class GnomeShortcutWriter : IDeShortcutWriter
{
    private const int MaxListMutationAttempts = 3;
    private const int MaxSnapshotAttempts = 5;
    private const string MediaKeysSchema = "org.gnome.settings-daemon.plugins.media-keys";

    private const string CustomKeybindingSchema =
        "org.gnome.settings-daemon.plugins.media-keys.custom-keybinding";

    private const string ListKey = "custom-keybindings";

    private static readonly TimeSpan s_gsettingsTimeout = TimeSpan.FromSeconds(5);

    private readonly string _backupDirectory;

    // The writer is a DI singleton and the Shortcuts panel exposes install and remove as
    // separate commands, so one flow could otherwise roll back the other's half-published path.
    // Cross-process edits are separate, handled by the read/confirm/retry loop in MutateListAsync.
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly IProcessRunner _processRunner;

    public GnomeShortcutWriter()
        : this(new ProcessRunner()) { }

    // ReSharper disable once MemberCanBePrivate.Global -- public DI seam: callers inject an IProcessRunner; the parameterless overload chains here with a real ProcessRunner.
    public GnomeShortcutWriter(IProcessRunner processRunner)
        : this(processRunner, Path.Join(TypeWhisperEnvironment.BasePath, "backups")) { }

    internal GnomeShortcutWriter(IProcessRunner processRunner, string backupDirectory)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        _backupDirectory = backupDirectory;
    }

    public string DesktopId => "gnome";
    public string DisplayName => "GNOME";
    public bool SupportsPushToTalk => false;

    // gsettings-daemon applies custom keybindings live.
    public bool RequiresSessionRestartToApply => false;

    public bool IsCurrentDesktop()
    {
        // Accept ubuntu:GNOME and variants; bail if gsettings is absent.
        return DesktopDetector.DetectId() == "gnome" && DesktopDetector.BinaryExists("gsettings");
    }

    public string PreviewLines(DeShortcutSpec spec)
    {
        var path = BuildCustomPath(spec.ShortcutId);
        var binding = FormatGnomeAccel(spec.Trigger);
        return $"gsettings list path: {path}\n"
               + $"  name    = {spec.DisplayName}\n"
               + $"  command = {spec.OnPressCommand}\n"
               + $"  binding = {binding}";
    }

    public async Task<bool> IsInstalledAsync(DeShortcutSpec spec, CancellationToken ct)
    {
        var path = BuildCustomPath(spec.ShortcutId);
        if (!await IsManagedPathListedAsync(path, ct).ConfigureAwait(false))
        {
            return false;
        }

        // Path presence isn't enough — a stale or partial write can list the path
        // while command/binding are wrong. Verify both against what we'd write.
        var schemaWithPath = $"{CustomKeybindingSchema}:{path}";
        var command = await GetStringValueAsync(schemaWithPath, "command", ct).ConfigureAwait(false);
        var binding = await GetStringValueAsync(schemaWithPath, "binding", ct).ConfigureAwait(false);
        return command == spec.OnPressCommand && binding == FormatGnomeAccel(spec.Trigger);
    }

    public Task<bool> IsManagedShortcutPresentAsync(string shortcutId, CancellationToken ct)
    {
        return IsManagedPathListedAsync(BuildCustomPath(shortcutId), ct);
    }

    public async Task<DeShortcutWriteResult> WriteAsync(DeShortcutSpec spec, CancellationToken ct)
    {
        await _mutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await WriteLockedAsync(spec, ct).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<DeShortcutWriteResult> WriteLockedAsync(
        DeShortcutSpec spec,
        CancellationToken ct
    )
    {
        var path = BuildCustomPath(spec.ShortcutId);
        var mutation = await MutateListAsync(path, add: true, ct).ConfigureAwait(false);
        if (mutation.Failure is not null)
        {
            return mutation.Failure;
        }

        var changed = new List<string>();
        if (mutation.BackupPath is not null)
        {
            changed.Add(mutation.BackupPath);
        }

        if (mutation.Changed)
        {
            changed.Add($"{MediaKeysSchema}.{ListKey}");
        }

        // Set name/command/binding only after the complete-list add is stable.
        var schemaWithPath = $"{CustomKeybindingSchema}:{path}";
        try
        {
            foreach (
                var (key, value) in new[]
                {
                    ("name", spec.DisplayName), ("command", spec.OnPressCommand),
                    ("binding", FormatGnomeAccel(spec.Trigger)),
                }
            )
            {
                var (ok, _, err) = await RunAsync(
                        "gsettings",
                        ["set", schemaWithPath, key, value],
                        ct
                    )
                    .ConfigureAwait(false);
                if (ok)
                {
                    continue;
                }

                var cleanup = await TryUnpublishAddedPathAsync(spec, path, mutation.Changed)
                    .ConfigureAwait(false);
                // The cleanup writes its own list snapshot; FilesChanged must report it.
                changed.AddRange(cleanup.FilesChanged);
                return new DeShortcutWriteResult(
                    false,
                    $"Could not set {key}: {err.Trim()}",
                    changed,
                    cleanup.Unpublished ? null : LeftoverEntryWarning(path)
                );
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation has no result to carry a warning; the helper traces a failed cleanup.
            await TryUnpublishAddedPathAsync(spec, path, mutation.Changed).ConfigureAwait(false);
            throw;
        }

        changed.Add(schemaWithPath);

        return new DeShortcutWriteResult(
            true,
            mutation.Changed
                ? "GNOME shortcut installed. It will appear under Settings → Keyboard → Custom Shortcuts."
                : "GNOME shortcut updated.",
            changed
        );
    }

    public async Task<DeShortcutWriteResult> RemoveAsync(string shortcutId, CancellationToken ct)
    {
        await _mutationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RemoveLockedAsync(shortcutId, ct).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<DeShortcutWriteResult> RemoveLockedAsync(
        string shortcutId,
        CancellationToken ct
    )
    {
        var path = BuildCustomPath(shortcutId);
        var mutation = await MutateListAsync(path, add: false, ct).ConfigureAwait(false);
        if (mutation.Failure is not null)
        {
            return mutation.Failure;
        }

        if (!mutation.Changed)
        {
            return new DeShortcutWriteResult(
                true,
                "No GNOME integration to remove.",
                []
            );
        }

        // gsettings has no "reset path" verb; reset individual keys so dconf-editor
        // stops showing stale values. This only runs after the list removal is stable.
        var schemaWithPath = $"{CustomKeybindingSchema}:{path}";
        foreach (var key in new[] { "name", "command", "binding" })
        {
            // Reset failures are non-fatal — the entry is no longer in
            // the list, so GNOME won't honor those values either way.
            await RunAsync("gsettings", ["reset", schemaWithPath, key], ct)
                .ConfigureAwait(false);
        }

        // Mirrors WriteAsync: report the backup only when one was actually written.
        var removed = new List<string>();
        if (mutation.BackupPath is not null)
        {
            removed.Add(mutation.BackupPath);
        }

        removed.Add($"{MediaKeysSchema}.{ListKey}");

        return new DeShortcutWriteResult(
            true,
            "GNOME shortcut removed.",
            removed
        );
    }

    /// <summary>
    ///     Drops a path this call just added when its name/command/binding writes didn't land,
    ///     so a failed or cancelled install leaves no empty custom shortcut in GNOME's Settings
    ///     UI. An already-listed path is left alone — it is the user's entry, not this call's
    ///     litter. Reports <c>Unpublished: false</c> if it is still published, plus any backup
    ///     the cleanup wrote.
    /// </summary>
    private async Task<UnpublishOutcome> TryUnpublishAddedPathAsync(
        DeShortcutSpec spec,
        string path,
        bool addedByThisCall
    )
    {
        if (!addedByThisCall)
        {
            return new UnpublishOutcome(true, []);
        }

        try
        {
            // CancellationToken.None throughout: the caller's token may already be cancelled and
            // this cleanup is what makes that safe. Each call is still bounded by the timeout.
            // A fully configured entry is no longer this call's litter — another writer completed
            // the install between our failed property write and this cleanup, so leave it alone.
            if (await IsInstalledAsync(spec, CancellationToken.None).ConfigureAwait(false))
            {
                return new UnpublishOutcome(true, []);
            }

            var cleanup = await MutateListAsync(path, add: false, CancellationToken.None)
                .ConfigureAwait(false);
            if (cleanup.Failure is null)
            {
                return new UnpublishOutcome(
                    true,
                    cleanup.BackupPath is null ? [] : [cleanup.BackupPath]
                );
            }

            System.Diagnostics.Trace.WriteLine(
                $"[GnomeShortcutWriter] Could not unpublish {path}: {cleanup.Failure.UserMessage}"
            );
            return new UnpublishOutcome(false, cleanup.Failure.FilesChanged);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[GnomeShortcutWriter] Could not unpublish {path} after a failed install: {ex.Message}"
            );
            return new UnpublishOutcome(false, []);
        }
    }

    private sealed record UnpublishOutcome(bool Unpublished, IReadOnlyList<string> FilesChanged);

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[GnomeShortcutWriter] Could not delete partial snapshot {path}: {ex.Message}"
            );
        }
    }

    private static string LeftoverEntryWarning(string path)
    {
        return $"The half-configured entry at {path} is still listed and could not be removed. "
               + "Remove it under Settings → Keyboard → Custom Shortcuts, or retry once the "
               + "settings backend is writable again.";
    }

    private async Task<ListMutationOutcome> MutateListAsync(
        string path,
        bool add,
        CancellationToken ct
    )
    {
        string? candidateRaw = null;
        for (var attempt = 0; attempt < MaxListMutationAttempts; attempt++)
        {
            if (candidateRaw is null)
            {
                var read = await ReadListAsync(ct).ConfigureAwait(false);
                if (!read.Ok)
                {
                    return ListMutationOutcome.Fail(
                        $"Could not read GNOME shortcut list: {read.Error.Trim()}"
                    );
                }

                candidateRaw = read.Raw;
            }

            List<string> list;
            try
            {
                list = ParseGSettingsList(candidateRaw);
            }
            catch (FormatException ex)
            {
                if (!add)
                {
                    return ListMutationOutcome.Fail(
                        $"Could not parse GNOME shortcut list ({ex.Message}). Refusing to modify shortcuts."
                    );
                }

                // Back up the malformed value on add so the user has it while repairing.
                var malformedBackup = await SnapshotListAsync(candidateRaw, ct)
                    .ConfigureAwait(false);
                if (malformedBackup is null)
                {
                    return ListMutationOutcome.Fail(
                        "Could not write GNOME backup file. Refusing to modify shortcuts."
                    );
                }

                return ListMutationOutcome.Fail(
                    $"Could not parse GNOME shortcut list ({ex.Message}). Refusing to modify shortcuts; backup at {malformedBackup}.",
                    [malformedBackup]
                );
            }

            var containsPath = list.Contains(path);
            // ReSharper disable once ConvertIfStatementToSwitchStatement -- the add/contains no-op guard reads clearer as a single boolean condition than a two-tuple switch.
            if ((add && containsPath) || (!add && !containsPath))
            {
                return ListMutationOutcome.NoChange();
            }

            if (add)
            {
                list.Add(path);
            }
            else
            {
                // RemoveAll, not Remove: an externally duplicated entry would otherwise survive
                // removal and leave the shortcut registered.
                list.RemoveAll(entry => string.Equals(entry, path, StringComparison.Ordinal));
            }

            // Compare the exact raw value immediately before replacing the complete list.
            var confirmation = await ReadListAsync(ct).ConfigureAwait(false);
            if (!confirmation.Ok)
            {
                return ListMutationOutcome.Fail(
                    $"Could not read GNOME shortcut list: {confirmation.Error.Trim()}"
                );
            }

            if (
                !string.Equals(candidateRaw, confirmation.Raw, StringComparison.Ordinal)
            )
            {
                candidateRaw = confirmation.Raw;
                continue;
            }

            var backupPath = await SnapshotListAsync(candidateRaw, ct).ConfigureAwait(false);
            if (backupPath is null)
            {
                return ListMutationOutcome.Fail(
                    "Could not write GNOME backup file. Refusing to modify shortcuts."
                );
            }

            var (setOk, _, setErr) = await RunAsync(
                    "gsettings",
                    ["set", MediaKeysSchema, ListKey, FormatGSettingsList(list)],
                    ct
                )
                .ConfigureAwait(false);
            if (!setOk)
            {
                return ListMutationOutcome.Fail(
                    $"Could not update GNOME shortcut list: {setErr.Trim()}",
                    [backupPath]
                );
            }

            return ListMutationOutcome.ChangedList(backupPath);
        }

        return ListMutationOutcome.Fail(
            "GNOME shortcut list kept changing while TypeWhisper was updating it. Please retry."
        );
    }

    /// <summary>
    ///     Parse a <c>gsettings get</c> list-of-strings result. gsettings emits <c>@as []</c> for
    ///     empty or a Python-style <c>['path1', 'path2']</c>; dconf-editor hand-edits may use double
    ///     quotes. Does NOT Split on commas (walks char-by-char). Honors <c>\'</c>, <c>\"</c>,
    ///     <c>\\</c> escapes; throws <see cref="FormatException" /> on anything it can't safely
    ///     round-trip — better to refuse a write than silently wipe the user's other shortcuts.
    /// </summary>
    public static List<string> ParseGSettingsList(string raw)
    {
        var result = new List<string>();
        // Fail closed: blank stdout from gsettings is anomalous, not "empty list".
        // Only literal "@as []" / "[]" mean empty; anything else must throw.
        if (raw is null)
        {
            throw new FormatException("gsettings returned a null list");
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new FormatException(
                "gsettings returned an empty list value (refusing to treat as empty)"
            );
        }

        var s = raw.Trim();
        // Strip "@as " type-annotation prefix if present.
        if (s.StartsWith("@as ", StringComparison.Ordinal))
        {
            s = s[4..].TrimStart();
        }

        if (s.Length < 2 || s[0] != '[' || s[^1] != ']')
        {
            throw new FormatException($"Unexpected gsettings list shape: {raw}");
        }

        // Drop the brackets and trim — an empty body means an empty
        // list, full stop.
        var body = s.Substring(1, s.Length - 2).Trim();
        if (body.Length == 0)
        {
            return result;
        }

        var i = 0;
        while (i < body.Length)
        {
            // Skip whitespace + a leading comma.
            while (i < body.Length && (char.IsWhiteSpace(body[i]) || body[i] == ','))
            {
                i++;
            }

            if (i >= body.Length)
            {
                break;
            }

            var quote = body[i];
            if (quote != '\'' && quote != '"')
            {
                throw new FormatException(
                    $"Expected quoted string at position {i} in gsettings list: {raw}"
                );
            }

            i++;

            var sb = new StringBuilder();
            var closed = false;
            while (i < body.Length)
            {
                var c = body[i];
                if (c == '\\' && i + 1 < body.Length)
                {
                    // Only honor \\, \', \" — the escapes gsettings emits. Any other
                    // escape is a hand-edit we can't safely round-trip; throw rather
                    // than silently drop the backslash and rewrite the user's entry.
                    var next = body[i + 1];
                    if (next != '\\' && next != '\'' && next != '"')
                    {
                        throw new FormatException(
                            $"Unsupported escape \\{next} in gsettings list: {raw}"
                        );
                    }

                    sb.Append(next);
                    i += 2;
                    continue;
                }

                if (c == quote)
                {
                    closed = true;
                    i++;
                    break;
                }

                sb.Append(c);
                i++;
            }

            if (!closed)
            {
                throw new FormatException($"Unterminated quoted string in gsettings list: {raw}");
            }

            result.Add(sb.ToString());
        }

        return result;
    }

    /// <summary>
    ///     Serialize a list-of-strings for <c>gsettings set</c>: single-quoted entries with
    ///     backslash-escaped single quotes. Empty list is <c>[]</c>, not <c>@as []</c> —
    ///     the <c>set</c> verb doesn't accept the type annotation.
    /// </summary>
    public static string FormatGSettingsList(IEnumerable<string> items)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        var first = true;
        foreach (var item in items)
        {
            if (!first)
            {
                sb.Append(", ");
            }

            first = false;
            sb.Append('\'');
            foreach (var c in item)
            {
                if (c is '\\' or '\'')
                {
                    sb.Append('\\');
                }

                sb.Append(c);
            }

            sb.Append('\'');
        }

        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    ///     Converts "Ctrl+Shift+Space" to <c>&lt;Control&gt;&lt;Shift&gt;space</c>.
    ///     Modifiers become angle-bracketed tokens; the terminal key is lower-cased
    ///     (except function keys, which require a capital F for GTK's keysym parser).
    /// </summary>
    public static string FormatGnomeAccel(string trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger))
        {
            return string.Empty;
        }

        var parts = trigger.Split(
            '+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var p = parts[i].ToLowerInvariant();
            var modifier = p switch
            {
                "ctrl" or "control" => "Control",
                "shift" => "Shift",
                "alt" => "Alt",
                "super" or "win" or "windows" or "cmd" or "meta" => "Super",
                _ => null,
            };
            if (modifier is null)
            {
                // Non-modifier in a non-terminal slot is unusual but
                // we don't want to swallow the user's intent — pass
                // it through capitalized as a fallback.
                sb.Append('<').Append(parts[i]).Append('>');
            }
            else
            {
                sb.Append('<').Append(modifier).Append('>');
            }
        }

        var key = parts[^1];
        // GTK expects lowercase ("space", "k"), but function keys (F1..F35) must
        // preserve the leading capital — GTK's keysym parser is case-sensitive there.
        key = IsFunctionKey(key) ? "F" + key[1..] : key.ToLowerInvariant();

        sb.Append(key);
        return sb.ToString();
    }

    /// <summary>
    ///     Read a single string-valued gsettings key and strip the surrounding
    ///     single quotes gsettings prints (unescaping <c>\'</c> and <c>\\</c>).
    ///     Returns null when the key can't be read.
    /// </summary>
    private async Task<string?> GetStringValueAsync(
        string schemaWithPath,
        string key,
        CancellationToken ct
    )
    {
        var (ok, raw, _) = await RunAsync("gsettings", ["get", schemaWithPath, key], ct)
            .ConfigureAwait(false);
        if (!ok)
        {
            return null;
        }

        var s = raw.Trim();
        if (s.Length < 2 || s[0] != '\'' || s[^1] != '\'')
        {
            return s;
        }

        var inner = s.Substring(1, s.Length - 2);
        return inner.Replace("\\'", "'").Replace(@"\\", "\\");
    }

    private static string BuildCustomPath(string shortcutId)
    {
        // Stable hex suffix (FNV-1a) so removal targets our entry precisely
        // and two TypeWhisper shortcuts can't collide. String.GetHashCode is
        // randomized per-process in .NET, so we can't use it for durable paths.
        return
            $"/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/typewhisper-{StableHashHex(shortcutId)}/";
    }

    private static string StableHashHex(string s)
    {
        // FNV-1a 32-bit: deterministic, tiny, sufficient for a path suffix.
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var h = offset;
        foreach (var c in s)
        {
            h ^= c;
            h *= prime;
        }

        return h.ToString("x8", CultureInfo.InvariantCulture);
    }

    private static bool IsFunctionKey(string k)
    {
        if (k.Length < 2 || (k[0] != 'F' && k[0] != 'f'))
        {
            return false;
        }

        for (var i = 1; i < k.Length; i++)
        {
            if (!char.IsDigit(k[i]))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<(bool Ok, string Raw, string Error)> ReadListAsync(
        CancellationToken ct
    )
    {
        var (ok, raw, error) = await RunAsync(
                "gsettings",
                ["get", MediaKeysSchema, ListKey],
                ct
            )
            .ConfigureAwait(false);
        return (ok, raw, error);
    }

    private async Task<bool> IsManagedPathListedAsync(string path, CancellationToken ct)
    {
        if (!DesktopDetector.BinaryExists("gsettings"))
        {
            return false;
        }

        var (ok, listOut, _) = await ReadListAsync(ct).ConfigureAwait(false);
        if (!ok)
        {
            return false;
        }

        try
        {
            return ParseGSettingsList(listOut).Contains(path);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task<string?> SnapshotListAsync(string currentValue, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_backupDirectory);
            var contents = Encoding.UTF8.GetBytes(
                $"# GNOME custom-keybindings list snapshot taken {DateTime.UtcNow:O}\n"
                + $"# Restore with:\n"
                + $"#   gsettings set {MediaKeysSchema} {ListKey} \"<value below>\"\n"
                + $"\n{currentValue.TrimEnd()}\n"
            );

            // CreateNew reserves the name, so a colliding stamp (a second instance, or a clock
            // step) can never overwrite a snapshot the user may still need.
            for (var attempt = 0; attempt < MaxSnapshotAttempts; attempt++)
            {
                var stamp = DateTime.UtcNow.ToString(
                    "yyyyMMdd-HHmmss-fffffff",
                    CultureInfo.InvariantCulture
                );
                var suffix = attempt == 0
                    ? ""
                    : $"-{attempt.ToString(CultureInfo.InvariantCulture)}";
                var file = Path.Join(_backupDirectory, $"gnome-keybindings-{stamp}{suffix}.txt");
                FileStream stream;
                try
                {
                    stream = new FileStream(
                        file,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None
                    );
                }
                catch (IOException) when (File.Exists(file))
                {
                    // Name already taken — regenerate and retry. Only the create step retries:
                    // a write that fails after the file exists is a real failure, not a collision.
                    continue;
                }

                try
                {
                    await using (stream)
                    {
                        await stream.WriteAsync(contents, ct).ConfigureAwait(false);
                    }

                    return file;
                }
                catch
                {
                    // Never leave a truncated file behind — it would read as a valid snapshot.
                    TryDeleteFile(file);
                    throw;
                }
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<(bool ok, string stdout, string stderr)> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        CancellationToken ct
    )
    {
        var result = await _processRunner.RunAsync(
                fileName,
                args,
                timeout: s_gsettingsTimeout,
                ct: ct
            )
            .ConfigureAwait(false);
        // Some runners report cancellation as a result rather than throwing; enforce it either way.
        ct.ThrowIfCancellationRequested();
        var error = result.TimedOut
            ? $"{fileName} timed out after {s_gsettingsTimeout.TotalSeconds:0} seconds."
            : result.StandardError;
        return (result.Succeeded, result.StandardOutput, error);
    }

    private sealed record ListMutationOutcome(
        bool Changed,
        string? BackupPath,
        DeShortcutWriteResult? Failure
    )
    {
        public static ListMutationOutcome ChangedList(string backupPath)
        {
            return new ListMutationOutcome(true, backupPath, null);
        }

        public static ListMutationOutcome NoChange()
        {
            return new ListMutationOutcome(false, null, null);
        }

        public static ListMutationOutcome Fail(
            string message,
            IReadOnlyList<string>? filesChanged = null
        )
        {
            return new ListMutationOutcome(
                false,
                null,
                new DeShortcutWriteResult(false, message, filesChanged ?? [])
            );
        }
    }
}
