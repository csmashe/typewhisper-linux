using System.Globalization;
using System.Text;

namespace TypeWhisper.Linux.Services.Hotkey.DeSetup;

/// <summary>
///     Writes a <c>.desktop</c> entry into <c>~/.local/share/kglobalaccel/</c>.
///     KGlobalAccel scans that directory on session start; the user can override the
///     trigger from System Settings → Shortcuts.
///     Existing targets are changed only when the ownership marker and shortcut ID match.
///     The live D-Bus path (<c>org.kde.kglobalaccel.registerShortcut</c>) is avoided
///     because it's fragile across Plasma versions and a static toggle doesn't need
///     the immediate-effect property. Cost: user must log out once to activate.
/// </summary>
public sealed class KdeShortcutWriter : IDeShortcutWriter
{
    public string DesktopId => "kde";
    public string DisplayName => "KDE Plasma";
    public bool SupportsPushToTalk => false;

    // KGlobalAccel only loads a dropped .desktop on the next login / daemon
    // restart, so the bind isn't live the moment we write it.
    public bool RequiresSessionRestartToApply => true;

    public bool IsCurrentDesktop()
    {
        return DesktopDetector.DetectId() == "kde";
    }

    public string PreviewLines(DeShortcutSpec spec)
    {
        return $"~/.local/share/kglobalaccel/{FileName(spec.ShortcutId)}\n" + BuildDesktopFile(spec);
    }

    public Task<bool> IsInstalledAsync(DeShortcutSpec spec, CancellationToken ct)
    {
        var (_, target) = ResolveTargetPath(spec.ShortcutId);
        if (!File.Exists(target))
        {
            return Task.FromResult(false);
        }

        // BuildDesktopFile is deterministic (no timestamp), so an exact byte match confirms
        // this spec is installed; a changed hotkey or partial write reads as not-installed.
        try
        {
            return Task.FromResult(File.ReadAllText(target) == BuildDesktopFile(spec));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> IsManagedShortcutPresentAsync(string shortcutId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (_, target) = ResolveTargetPath(shortcutId);
        return Task.FromResult(
            File.Exists(target) && IsOwnedByTypeWhisper(target, shortcutId)
        );
    }

    public async Task<DeShortcutWriteResult> WriteAsync(DeShortcutSpec spec, CancellationToken ct)
    {
        var (dir, target) = ResolveTargetPath(spec.ShortcutId);
        if (File.Exists(target) && !IsOwnedByTypeWhisper(target, spec.ShortcutId))
        {
            return new DeShortcutWriteResult(
                false,
                $"Left {target} untouched — it doesn't carry TypeWhisper's ownership markers, so we won't overwrite it. Remove or rename it manually, then try again.",
                []
            );
        }

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            return new DeShortcutWriteResult(
                false,
                $"Could not create {dir}: {ex.Message}",
                []
            );
        }

        var contents = BuildDesktopFile(spec);
        try
        {
            await AtomicFileWriter.WriteAsync(target, contents, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new DeShortcutWriteResult(
                false,
                $"Could not write {target}: {ex.Message}",
                []
            );
        }

        return new DeShortcutWriteResult(
            true,
            "KDE shortcut file written. Log out and back in (or restart the KGlobalAccel daemon) for Plasma to register it.",
            [target]
        );
    }

    public Task<DeShortcutWriteResult> RemoveAsync(string shortcutId, CancellationToken ct)
    {
        var (_, target) = ResolveTargetPath(shortcutId);
        if (!File.Exists(target))
        {
            return Task.FromResult(
                new DeShortcutWriteResult(
                    true,
                    "No KDE integration to remove.",
                    []
                )
            );
        }

        if (!IsOwnedByTypeWhisper(target, shortcutId))
        {
            return Task.FromResult(
                new DeShortcutWriteResult(
                    true,
                    "KDE shortcut file left in place.",
                    [],
                    $"Left {target} untouched — it doesn't carry TypeWhisper's ownership markers, so we won't delete it. Remove it manually if you want to."
                )
            );
        }

        try
        {
            File.Delete(target);
            return Task.FromResult(
                new DeShortcutWriteResult(
                    true,
                    "KDE shortcut file removed. Restart the KGlobalAccel daemon or log out and back in to drop the registration.",
                    [target]
                )
            );
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                new DeShortcutWriteResult(
                    false,
                    $"Could not delete {target}: {ex.Message}",
                    []
                )
            );
        }
    }

    private static (string dir, string file) ResolveTargetPath(string shortcutId)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataHome = string.IsNullOrEmpty(xdg) ? Path.Join(home, ".local", "share") : xdg;
        var dir = Path.Join(dataHome, "kglobalaccel");
        return (dir, Path.Join(dir, FileName(shortcutId)));
    }

    private static string FileName(string shortcutId)
    {
        // KGlobalAccel uses the basename as the identifier; sanitize to guard against ids like "foo/bar".
        var safe = new StringBuilder();
        foreach (var c in shortcutId)
        {
            if (char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_')
            {
                safe.Append(c);
            }
            else
            {
                safe.Append('-');
            }
        }

        return $"{safe}.desktop";
    }

    private static string BuildDesktopFile(DeShortcutSpec spec)
    {
        // No timestamp — two runs with the same spec must produce identical bytes so the
        // atomic-write is a no-op on repeat. Diagnostics go through the result message.
        return string.Format(
            CultureInfo.InvariantCulture,
            "[Desktop Entry]\n"
            + "Type=Service\n"
            + "Name={0}\n"
            + "Exec={1}\n"
            + "X-KDE-Shortcuts={2}\n"
            + "X-KDE-StartupNotify=false\n"
            + "X-TypeWhisper-Managed=true\n"
            + "X-TypeWhisper-ShortcutId={3}\n",
            EscapeDesktopValue(spec.DisplayName),
            EscapeDesktopValue(spec.OnPressCommand),
            EscapeDesktopValue(spec.Trigger),
            EscapeDesktopValue(spec.ShortcutId)
        );
    }

    // Require both exact lines: a marker alone could belong to a different shortcut,
    // while full-file equality would reject legitimate trigger updates.
    private static bool IsOwnedByTypeWhisper(string target, string shortcutId)
    {
        string contents;
        try
        {
            contents = File.ReadAllText(target);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Refuse destructive changes when ownership cannot be proven.
            return false;
        }

        var lines = contents.Split('\n').Select(line => line.TrimEnd('\r'));
        var lineSet = lines.ToHashSet(StringComparer.Ordinal);
        const string managedLine = "X-TypeWhisper-Managed=true";
        var idLine = $"X-TypeWhisper-ShortcutId={EscapeDesktopValue(shortcutId)}";
        return lineSet.Contains(managedLine) && lineSet.Contains(idLine);
    }

    private static string EscapeDesktopValue(string value)
    {
        // Desktop Entry Specification escaping: \\ for backslash, \n/\r/\t for control chars,
        // other ASCII controls as \xNN to avoid breaking line-by-line parsers.
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    sb.Append(@"\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20 || c == 0x7f)
                    {
                        sb.Append('\\')
                            .Append('x')
                            .Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }
}
