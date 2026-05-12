using System.Globalization;
using System.Text;

namespace TypeWhisper.Linux.Services.Hotkey.DeSetup;

/// <summary>
/// KDE helper that drops a <c>.desktop</c> entry into
/// <c>~/.local/share/kglobalaccel/</c>. KGlobalAccel scans that
/// directory on session start and picks the file up — the user can
/// then edit the trigger from System Settings → Shortcuts if they want
/// to override what we wrote.
///
/// We deliberately avoid the live D-Bus path
/// (<c>org.kde.kglobalaccel.registerShortcut</c>) because it's more
/// fragile across Plasma versions and a single static toggle doesn't
/// benefit from the immediate-effect property. The cost is a single
/// "log out and back in for KDE to register the shortcut" message in
/// the result.
/// </summary>
public sealed class KdeShortcutWriter : IDeShortcutWriter
{
    public string DesktopId => "kde";
    public string DisplayName => "KDE Plasma";
    public bool SupportsPushToTalk => false;

    public bool IsCurrentDesktop() => DesktopDetector.DetectId() == "kde";

    public string PreviewLines(DeShortcutSpec spec) =>
        $"~/.local/share/kglobalaccel/{FileName(spec.ShortcutId)}\n" +
        BuildDesktopFile(spec);

    public async Task<DeShortcutWriteResult> WriteAsync(DeShortcutSpec spec, CancellationToken ct)
    {
        var (dir, target) = ResolveTargetPath(spec.ShortcutId);
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            return new DeShortcutWriteResult(false, $"Could not create {dir}: {ex.Message}", Array.Empty<string>());
        }

        var contents = BuildDesktopFile(spec);
        try
        {
            await AtomicWriteAsync(target, contents, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new DeShortcutWriteResult(false, $"Could not write {target}: {ex.Message}", Array.Empty<string>());
        }

        return new DeShortcutWriteResult(true,
            "KDE shortcut file written. Log out and back in (or run `kquitapp5 kglobalaccel5 && kglobalaccel5 &`) for Plasma to register it.",
            new[] { target });
    }

    public Task<DeShortcutWriteResult> RemoveAsync(string shortcutId, CancellationToken ct)
    {
        var (_, target) = ResolveTargetPath(shortcutId);
        if (!File.Exists(target))
            return Task.FromResult(new DeShortcutWriteResult(true, "No KDE integration to remove.", Array.Empty<string>()));

        try
        {
            File.Delete(target);
            return Task.FromResult(new DeShortcutWriteResult(true,
                "KDE shortcut file removed. Restart kglobalaccel5 or log out and back in to drop the registration.",
                new[] { target }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new DeShortcutWriteResult(false, $"Could not delete {target}: {ex.Message}", Array.Empty<string>()));
        }
    }

    private static (string dir, string file) ResolveTargetPath(string shortcutId)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataHome = string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".local", "share") : xdg;
        var dir = Path.Combine(dataHome, "kglobalaccel");
        return (dir, Path.Combine(dir, FileName(shortcutId)));
    }

    private static string FileName(string shortcutId)
    {
        // KGlobalAccel reads the file's basename as the identifying
        // component. Stripping non-filename characters guards against
        // a hypothetical shortcut id like "foo/bar".
        var safe = new StringBuilder();
        foreach (var c in shortcutId)
        {
            if (char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_')
                safe.Append(c);
            else
                safe.Append('-');
        }
        return $"{safe}.desktop";
    }

    private static string BuildDesktopFile(DeShortcutSpec spec)
    {
        // KGlobalAccel cares about a small set of keys:
        //   Type=Service — required so it's treated as a service.
        //   Name         — shown in System Settings → Shortcuts.
        //   Exec         — what to run.
        //   X-KDE-Shortcuts — the trigger; comma-separated for alternates.
        //
        // No timestamp here on purpose — two runs with the same spec
        // must produce identical bytes so the atomic-write step is a
        // true no-op on a repeat click. Diagnostic info goes through
        // the result message instead.
        return string.Format(CultureInfo.InvariantCulture,
            "[Desktop Entry]\n" +
            "Type=Service\n" +
            "Name={0}\n" +
            "Exec={1}\n" +
            "X-KDE-Shortcuts={2}\n" +
            "X-KDE-StartupNotify=false\n" +
            "X-TypeWhisper-Managed=true\n" +
            "X-TypeWhisper-ShortcutId={3}\n",
            spec.DisplayName,
            spec.OnPressCommand,
            spec.Trigger,
            spec.ShortcutId);
    }

    private static async Task AtomicWriteAsync(string target, string contents, CancellationToken ct)
    {
        // Write to a sibling temp file then File.Move overwrite — keeps
        // the .desktop file from existing in a half-written state if
        // we crash mid-write.
        var dir = Path.GetDirectoryName(target)!;
        var tmp = Path.Combine(dir, $".{Path.GetFileName(target)}.tmp");
        await File.WriteAllTextAsync(tmp, contents, ct).ConfigureAwait(false);
        File.Move(tmp, target, overwrite: true);
    }
}
