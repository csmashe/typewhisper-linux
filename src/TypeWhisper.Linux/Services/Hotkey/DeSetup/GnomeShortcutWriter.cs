using System.Diagnostics;
using System.Globalization;
using System.Text;
using TypeWhisper.Core;

namespace TypeWhisper.Linux.Services.Hotkey.DeSetup;

/// <summary>
/// GNOME helper that wires TypeWhisper's dictation toggle into the GNOME
/// custom-keybindings list under
/// <c>org.gnome.settings-daemon.plugins.media-keys</c>.
///
/// Critical invariant: we never overwrite the user's existing custom
/// keybinding list. We read it, append our path if missing, and write
/// the merged list back. Before any write we snapshot the current list
/// to a timestamped file in <c>~/.config/typewhisper/backups/</c> so a
/// parse bug here can't silently delete the user's other shortcuts.
/// </summary>
public sealed class GnomeShortcutWriter : IDeShortcutWriter
{
    private const string MediaKeysSchema = "org.gnome.settings-daemon.plugins.media-keys";
    private const string CustomKeybindingSchema = "org.gnome.settings-daemon.plugins.media-keys.custom-keybinding";
    private const string ListKey = "custom-keybindings";

    public string DesktopId => "gnome";
    public string DisplayName => "GNOME";
    public bool SupportsPushToTalk => false;

    public bool IsCurrentDesktop()
    {
        // We accept anything XDG calls GNOME (Ubuntu's "ubuntu:GNOME"
        // included) but bail if gsettings itself is missing, because
        // every actual write goes through it.
        if (DesktopDetector.DetectId() != "gnome") return false;
        return DesktopDetector.BinaryExists("gsettings");
    }

    public string PreviewLines(DeShortcutSpec spec)
    {
        var path = BuildCustomPath(spec.ShortcutId);
        var binding = FormatGnomeAccel(spec.Trigger);
        return
            $"gsettings list path: {path}\n" +
            $"  name    = {spec.DisplayName}\n" +
            $"  command = {spec.OnPressCommand}\n" +
            $"  binding = {binding}";
    }

    public async Task<DeShortcutWriteResult> WriteAsync(DeShortcutSpec spec, CancellationToken ct)
    {
        var path = BuildCustomPath(spec.ShortcutId);

        // 1. Snapshot the current list before touching anything. If the
        //    backup fails (disk full, perms) we refuse to proceed — the
        //    whole point of the backup is to be able to recover from a
        //    bad write.
        var (listOk, listOut, listErr) = await RunAsync("gsettings", new[] { "get", MediaKeysSchema, ListKey }, ct).ConfigureAwait(false);
        if (!listOk)
            return new DeShortcutWriteResult(false, $"Could not read GNOME shortcut list: {listErr.Trim()}", Array.Empty<string>());

        var backupPath = await SnapshotListAsync(listOut, ct).ConfigureAwait(false);
        if (backupPath is null)
            return new DeShortcutWriteResult(false, "Could not write GNOME backup file. Refusing to modify shortcuts.", Array.Empty<string>());

        List<string> list;
        try
        {
            list = ParseGSettingsList(listOut);
        }
        catch (FormatException ex)
        {
            // The backup is already on disk — surface its path so the
            // user knows where to look if they need to recover.
            return new DeShortcutWriteResult(false,
                $"Could not parse GNOME shortcut list ({ex.Message}). Refusing to modify shortcuts; backup at {backupPath}.",
                new[] { backupPath });
        }
        var added = false;
        if (!list.Contains(path))
        {
            list.Add(path);
            added = true;
        }

        // 2. Write merged list back only if we actually added a path.
        //    A repeat invocation is a no-op except for the three
        //    field-level sets below (which themselves may be no-ops).
        var changed = new List<string> { backupPath };
        if (added)
        {
            var (ok, _, err) = await RunAsync("gsettings", new[] { "set", MediaKeysSchema, ListKey, FormatGSettingsList(list) }, ct).ConfigureAwait(false);
            if (!ok)
                return new DeShortcutWriteResult(false, $"Could not update GNOME shortcut list: {err.Trim()}", new[] { backupPath });
            changed.Add($"{MediaKeysSchema}.{ListKey}");
        }

        // 3. Set name / command / binding on the relative path. The
        //    schema-with-path form is "schema:path" — gsettings then
        //    treats the trailing path as the dconf prefix.
        var schemaWithPath = $"{CustomKeybindingSchema}:{path}";
        foreach (var (key, value) in new[]
                 {
                     ("name", spec.DisplayName),
                     ("command", spec.OnPressCommand),
                     ("binding", FormatGnomeAccel(spec.Trigger)),
                 })
        {
            var (ok, _, err) = await RunAsync("gsettings", new[] { "set", schemaWithPath, key, value }, ct).ConfigureAwait(false);
            if (!ok)
                return new DeShortcutWriteResult(false, $"Could not set {key}: {err.Trim()}", changed);
        }
        changed.Add(schemaWithPath);

        return new DeShortcutWriteResult(true, added
            ? "GNOME shortcut installed. It will appear under Settings → Keyboard → Custom Shortcuts."
            : "GNOME shortcut updated.", changed);
    }

    public async Task<DeShortcutWriteResult> RemoveAsync(string shortcutId, CancellationToken ct)
    {
        var path = BuildCustomPath(shortcutId);
        var (listOk, listOut, listErr) = await RunAsync("gsettings", new[] { "get", MediaKeysSchema, ListKey }, ct).ConfigureAwait(false);
        if (!listOk)
            return new DeShortcutWriteResult(false, $"Could not read GNOME shortcut list: {listErr.Trim()}", Array.Empty<string>());

        List<string> list;
        try
        {
            list = ParseGSettingsList(listOut);
        }
        catch (FormatException ex)
        {
            return new DeShortcutWriteResult(false,
                $"Could not parse GNOME shortcut list ({ex.Message}). Refusing to modify shortcuts.",
                Array.Empty<string>());
        }
        if (!list.Contains(path))
            return new DeShortcutWriteResult(true, "No GNOME integration to remove.", Array.Empty<string>());

        var backupPath = await SnapshotListAsync(listOut, ct).ConfigureAwait(false);
        if (backupPath is null)
            return new DeShortcutWriteResult(false, "Could not write GNOME backup file. Refusing to modify shortcuts.", Array.Empty<string>());

        list.Remove(path);
        var (setOk, _, setErr) = await RunAsync("gsettings", new[] { "set", MediaKeysSchema, ListKey, FormatGSettingsList(list) }, ct).ConfigureAwait(false);
        if (!setOk)
            return new DeShortcutWriteResult(false, $"Could not update GNOME shortcut list: {setErr.Trim()}", new[] { backupPath });

        // gsettings has no "reset path" verb for the path schema form;
        // resetting the individual keys is the closest thing and will
        // make dconf-editor stop showing stale name/command/binding.
        var schemaWithPath = $"{CustomKeybindingSchema}:{path}";
        foreach (var key in new[] { "name", "command", "binding" })
        {
            // Reset failures are non-fatal — the entry is no longer in
            // the list, so GNOME won't honor those values either way.
            await RunAsync("gsettings", new[] { "reset", schemaWithPath, key }, ct).ConfigureAwait(false);
        }

        return new DeShortcutWriteResult(true, "GNOME shortcut removed.", new[] { backupPath, $"{MediaKeysSchema}.{ListKey}" });
    }

    private static string BuildCustomPath(string shortcutId)
    {
        // Bake a short suffix derived from the shortcut id so removal
        // can target exactly the entry we created and so two
        // TypeWhisper-managed shortcuts don't collide. We use the
        // SHA-derived hex to stay deterministic across runs (string
        // GetHashCode is randomized per-process in .NET).
        return $"/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/typewhisper-{StableHashHex(shortcutId)}/";
    }

    private static string StableHashHex(string s)
    {
        // FNV-1a 32-bit — tiny, deterministic, plenty of entropy for a
        // disambiguation suffix. Anything cryptographic is overkill.
        const uint offset = 2166136261;
        const uint prime = 16777619;
        uint h = offset;
        foreach (var c in s) { h ^= c; h *= prime; }
        return h.ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Parse a <c>gsettings get</c> result for a list-of-strings key.
    /// gsettings prints either <c>@as []</c> for empty or a Python-style
    /// list <c>['path1', 'path2']</c> for populated values. Single and
    /// double quotes are both possible; gsettings emits singles but a
    /// user editing dconf-editor by hand can produce doubles.
    ///
    /// Implementation notes — and this is the parser the phase spec
    /// flagged as the highest-risk surface in the whole phase:
    /// <list type="bullet">
    ///   <item>We do not <c>Split(',')</c>. A quoted string is the only
    ///   place commas can appear; we walk character-by-character.</item>
    ///   <item>Backslash escapes (<c>\'</c>, <c>\"</c>, <c>\\</c>) are
    ///   honored — gsettings escapes single quotes inside
    ///   single-quoted strings.</item>
    ///   <item>Empty (<c>@as []</c>, <c>[]</c>) returns an empty list,
    ///   never null.</item>
    ///   <item>Mismatched quotes throw — better to refuse the write
    ///   than to silently wipe entries.</item>
    /// </list>
    /// </summary>
    public static List<string> ParseGSettingsList(string raw)
    {
        var result = new List<string>();
        // Fail closed on blank input: a successful gsettings call that
        // somehow yields an empty stdout is anomalous, not "the list is
        // empty". Treating it as empty would let us overwrite the
        // user's other custom shortcuts on the very next set — exactly
        // the data-loss path the phase spec called out. Only literal
        // "@as []" / "[]" mean empty.
        if (raw is null) throw new FormatException("gsettings returned a null list");
        if (string.IsNullOrWhiteSpace(raw))
            throw new FormatException("gsettings returned an empty list value (refusing to treat as empty)");

        var s = raw.Trim();
        // Strip the gsettings "@as " type-annotation prefix when present.
        if (s.StartsWith("@as ", StringComparison.Ordinal)) s = s.Substring(4).TrimStart();

        if (s.Length < 2 || s[0] != '[' || s[^1] != ']')
            throw new FormatException($"Unexpected gsettings list shape: {raw}");

        // Drop the brackets and trim — an empty body means an empty
        // list, full stop.
        var body = s.Substring(1, s.Length - 2).Trim();
        if (body.Length == 0) return result;

        var i = 0;
        while (i < body.Length)
        {
            // Skip whitespace + a leading comma.
            while (i < body.Length && (char.IsWhiteSpace(body[i]) || body[i] == ',')) i++;
            if (i >= body.Length) break;

            var quote = body[i];
            if (quote != '\'' && quote != '"')
                throw new FormatException($"Expected quoted string at position {i} in gsettings list: {raw}");
            i++;

            var sb = new StringBuilder();
            var closed = false;
            while (i < body.Length)
            {
                var c = body[i];
                if (c == '\\' && i + 1 < body.Length)
                {
                    // Honor only the escapes gsettings actually emits:
                    // \\, \', \". Anything else means a hand-edit we
                    // can't safely round-trip — silently dropping the
                    // backslash would let us write back a different
                    // string and effectively rewrite the user's entry.
                    var next = body[i + 1];
                    if (next != '\\' && next != '\'' && next != '"')
                        throw new FormatException($"Unsupported escape \\{next} in gsettings list: {raw}");
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
                throw new FormatException($"Unterminated quoted string in gsettings list: {raw}");
            result.Add(sb.ToString());
        }
        return result;
    }

    /// <summary>
    /// Render a list-of-strings in the exact form gsettings expects on
    /// the <c>set</c> side. Single-quoted entries with backslash-escaped
    /// single quotes inside — matching the gsettings reader's
    /// expectations. Empty list serializes as <c>[]</c>, not
    /// <c>@as []</c>: the <c>set</c> verb does not want the type
    /// annotation.
    /// </summary>
    public static string FormatGSettingsList(IEnumerable<string> items)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        var first = true;
        foreach (var item in items)
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append('\'');
            foreach (var c in item)
            {
                if (c == '\\' || c == '\'') sb.Append('\\');
                sb.Append(c);
            }
            sb.Append('\'');
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// Convert TypeWhisper's display accelerator format
    /// ("Ctrl+Shift+Space") into the GNOME / GTK accelerator format
    /// (<c>&lt;Control&gt;&lt;Shift&gt;space</c>). Modifiers go first
    /// as angle-bracketed tokens; the final key is lower-cased except
    /// for printable single characters which gsettings is happy with
    /// either way. Unknown tokens pass through as-is.
    /// </summary>
    public static string FormatGnomeAccel(string trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger)) return string.Empty;
        var parts = trigger.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return string.Empty;

        var sb = new StringBuilder();
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var p = parts[i].ToLowerInvariant();
            var modifier = p switch
            {
                "ctrl" or "control" => "Control",
                "shift" => "Shift",
                "alt" or "meta" => "Alt",
                "super" or "win" or "windows" or "cmd" => "Super",
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
        // GTK accelerators expect "space", "comma", etc. for named
        // keys, and lowercase printable letters (uppercase "K" is the
        // keysym for shifted-K, which would force Shift implicitly).
        // Function keys (F1..F35) are special — they're a fixed
        // mixed-case sigil that GTK's keysym parser only matches
        // case-sensitively, so we preserve the leading capital.
        if (IsFunctionKey(key)) key = "F" + key.Substring(1);
        else key = key.ToLowerInvariant();
        sb.Append(key);
        return sb.ToString();
    }

    private static bool IsFunctionKey(string k)
    {
        if (k.Length < 2 || (k[0] != 'F' && k[0] != 'f')) return false;
        for (var i = 1; i < k.Length; i++)
            if (!char.IsDigit(k[i])) return false;
        return true;
    }

    private static async Task<string?> SnapshotListAsync(string currentValue, CancellationToken ct)
    {
        try
        {
            var dir = Path.Combine(TypeWhisperEnvironment.BasePath, "backups");
            Directory.CreateDirectory(dir);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var file = Path.Combine(dir, $"gnome-keybindings-{stamp}.txt");
            var contents =
                $"# GNOME custom-keybindings list snapshot taken {DateTime.UtcNow:O}\n" +
                $"# Restore with:\n" +
                $"#   gsettings set {MediaKeysSchema} {ListKey} \"<value below>\"\n" +
                $"\n{currentValue.TrimEnd()}\n";
            await File.WriteAllTextAsync(file, contents, ct).ConfigureAwait(false);
            return file;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(bool ok, string stdout, string stderr)> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return (false, string.Empty, $"Could not start {fileName}");
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return (proc.ExitCode == 0, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
        }
    }
}
