using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TypeWhisper.Linux.Services.ManagedArtifacts;

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
    private const UnixFileMode DesktopMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite
        | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private readonly ManagedFileTransaction _managedFiles;

    public KdeShortcutWriter()
        : this(new ManagedFileTransaction()) { }

    internal KdeShortcutWriter(string managedArtifactStateRoot)
        : this(new ManagedFileTransaction(managedArtifactStateRoot)) { }

    private KdeShortcutWriter(ManagedFileTransaction managedFiles)
    {
        _managedFiles = managedFiles;
    }

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

    public async Task<bool> IsInstalledAsync(DeShortcutSpec spec, CancellationToken ct)
    {
        try
        {
            return await _managedFiles.ProbeAsync(BuildManagedSpec(spec), ct)
                    .ConfigureAwait(false)
                == ManagedFileClassification.CurrentOwned;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<bool> IsManagedShortcutPresentAsync(
        string shortcutId,
        CancellationToken ct
    )
    {
        try
        {
            var classification = await _managedFiles
                .ProbeAsync(BuildManagedSpec(shortcutId, []), ct)
                .ConfigureAwait(false);
            if (
                classification is ManagedFileClassification.CurrentOwned
                    or ManagedFileClassification.StaleOwned
            )
            {
                return true;
            }

            // CustomizedOwned is reached without consulting the ownership probe, so a file
            // replaced outright lands here too. The markers decide whether it is still ours.
            return classification == ManagedFileClassification.CustomizedOwned
                && DestinationCarriesMarkers(shortcutId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<DeShortcutWriteResult> WriteAsync(DeShortcutSpec spec, CancellationToken ct)
    {
        var target = ResolveTargetPath(spec.ShortcutId);
        try
        {
            var result = await _managedFiles.InstallAsync(BuildManagedSpec(spec), ct)
                .ConfigureAwait(false);
            if (!result.OwnsDestination)
            {
                return new DeShortcutWriteResult(
                    false,
                    $"Left {target} untouched — it doesn't carry TypeWhisper's ownership markers, so we won't overwrite it. Remove or rename it manually, then try again.",
                    []
                );
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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

    public async Task<DeShortcutWriteResult> RemoveAsync(
        string shortcutId,
        CancellationToken ct
    )
    {
        var target = ResolveTargetPath(shortcutId);
        try
        {
            var result = await _managedFiles
                .RemoveAsync(BuildManagedSpec(shortcutId, []), ct)
                .ConfigureAwait(false);
            if (result.Classification == ManagedFileClassification.Absent)
            {
                return new DeShortcutWriteResult(true, "No KDE integration to remove.", []);
            }

            if (!result.Changed)
            {
                return new DeShortcutWriteResult(
                    true,
                    "KDE shortcut file left in place.",
                    [],
                    $"Left {target} untouched — it doesn't carry TypeWhisper's ownership markers, so we won't delete it. Remove it manually if you want to."
                );
            }

            return new DeShortcutWriteResult(
                true,
                "KDE shortcut file removed. Restart the KGlobalAccel daemon or log out and back in to drop the registration.",
                [target]
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DeShortcutWriteResult(
                false,
                $"Could not delete {target}: {ex.Message}",
                []
            );
        }
    }

    private static bool DestinationCarriesMarkers(string shortcutId)
    {
        try
        {
            // The transaction already refused symlinks and non-regular entries before this
            // classification, so a plain read is safe here.
            return IsOwnedByTypeWhisper(
                File.ReadAllBytes(ResolveTargetPath(shortcutId)),
                shortcutId
            );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ResolveTargetPath(string shortcutId)
    {
        return Path.Join(XdgPaths.ResolveDataHome(), "kglobalaccel", FileName(shortcutId));
    }

    private static string FileName(string shortcutId)
    {
        return $"{SanitizeId(shortcutId)}.desktop";
    }

    private static string SanitizeId(string shortcutId)
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

        return safe.ToString();
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

    private static ManagedFileSpec BuildManagedSpec(DeShortcutSpec spec)
    {
        return BuildManagedSpec(spec.ShortcutId, ManagedFileSpec.Utf8(BuildDesktopFile(spec)));
    }

    private static ManagedFileSpec BuildManagedSpec(string shortcutId, byte[] desiredBytes)
    {
        var target = ResolveTargetPath(shortcutId);
        return new ManagedFileSpec
        {
            ArtifactId = $"kde-shortcut-{ArtifactSuffix(shortcutId)}",
            DestinationPath = target,
            DesiredBytes = desiredBytes,
            CreateMode = DesktopMode,
            OwnershipProbe = bytes => IsOwnedByTypeWhisper(bytes, shortcutId),
            // Shortcuts written before the managed-artifact manifest carry no recorded
            // state, and removal probes with empty desired bytes, so the marker match
            // is the only ownership evidence there is. Without this a pre-upgrade
            // shortcut classifies as customized and can be neither rewritten (new
            // trigger) nor removed — both of which the previous release allowed.
            LegacyOwnershipProbe = bytes => IsOwnedByTypeWhisper(bytes, shortcutId),
        };
    }

    // Require both exact lines: a marker alone could belong to a different shortcut.
    private static bool IsOwnedByTypeWhisper(
        ReadOnlyMemory<byte> bytes,
        string shortcutId
    )
    {
        var contents = Encoding.UTF8.GetString(bytes.Span);
        var lines = contents.Split('\n').Select(line => line.TrimEnd('\r'));
        var lineSet = lines.ToHashSet(StringComparer.Ordinal);
        const string managedLine = "X-TypeWhisper-Managed=true";
        var idLine = $"X-TypeWhisper-ShortcutId={EscapeDesktopValue(shortcutId)}";
        return lineSet.Contains(managedLine) && lineSet.Contains(idLine);
    }

    // Hashes the sanitized id, not the raw one: "foo/bar" and "foo-bar" resolve to the same
    // destination file, so they must also resolve to the same managed-artifact identity.
    private static string ArtifactSuffix(string shortcutId)
    {
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(SanitizeId(shortcutId)))
            )
            .ToLowerInvariant();
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
