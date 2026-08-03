using System.Diagnostics;
using System.Text;
using TypeWhisper.Linux.Services.Localization;
using TypeWhisper.Linux.Services.ManagedArtifacts;

namespace TypeWhisper.Linux.Services;

public sealed record StartupOperationResult(bool Success, bool IsEnabled, string StatusText);

/// <summary>
///     XDG Autostart integration. Manages ~/.config/autostart/typewhisper.desktop
///     only when TypeWhisper can prove ownership from its contents.
/// </summary>
public static class StartupService
{
    private const string DesktopFileName = "typewhisper.desktop";
    private const string ManagedLine = "X-TypeWhisper-Managed=true";
    private const UnixFileMode DesktopMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite
        | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    internal static string? ManagedArtifactStateRootOverride { get; set; }

    private static string AutostartDir
    {
        get
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(configHome))
            {
                configHome = Path.Join(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config"
                );
            }

            return Path.Join(configHome, "autostart");
        }
    }

    private static string DesktopFilePath => Path.Join(AutostartDir, DesktopFileName);

    public static bool IsEnabled
    {
        get
        {
            try
            {
                var spec = BuildManagedSpec();
                var classification = CreateTransaction().Probe(spec);
                return classification is ManagedFileClassification.CurrentOwned
                    or ManagedFileClassification.StaleOwned;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Trace.WriteLine($"[StartupService] could not inspect autostart entry: {ex.Message}");
                return false;
            }
        }
    }

    public static StartupOperationResult Enable()
    {
        var result = CreateTransaction().InstallAsync(BuildManagedSpec())
            .GetAwaiter()
            .GetResult();
        return result.OwnsDestination ? SuccessResult(isEnabled: true) : RefusedResult();
    }

    public static StartupOperationResult Disable()
    {
        var result = CreateTransaction().RemoveAsync(BuildManagedSpec())
            .GetAwaiter()
            .GetResult();
        return result.Classification is ManagedFileClassification.Absent
            || result.Changed
            ? SuccessResult(isEnabled: false)
            : RefusedResult();
    }

    internal static string BuildDesktopFile(
        string execPath,
        string iconPath,
        bool includeManagedMarker
    )
    {
        var content =
            "[Desktop Entry]\n"
            + "Type=Application\n"
            + "Name=TypeWhisper\n"
            + "GenericName=Voice-to-text dictation\n"
            + $"Exec=\"{execPath}\" --minimized\n"
            + $"Icon={iconPath}\n"
            + "Terminal=false\n"
            + "Categories=Utility;Accessibility;\n"
            + "X-GNOME-Autostart-enabled=true";
        return includeManagedMarker ? $"{content}\n{ManagedLine}" : content;
    }

    private static bool HasManagedLine(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            var contents = Encoding.UTF8.GetString(bytes.Span);
            return contents
                .Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Contains(ManagedLine, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private static ManagedFileSpec BuildManagedSpec()
    {
        var execPath = ResolveExecutablePath();
        var iconPath = ResolveIconPath();
        var desired = BuildDesktopFile(execPath, iconPath, includeManagedMarker: true);
        var legacy = ManagedFileSpec.Utf8(
            BuildDesktopFile(execPath, iconPath, includeManagedMarker: false)
        );
        return new ManagedFileSpec
        {
            ArtifactId = "xdg-autostart",
            DestinationPath = DesktopFilePath,
            DesiredBytes = ManagedFileSpec.Utf8(desired),
            CreateMode = DesktopMode,
            OwnershipProbe = HasManagedLine,
            LegacyOwnershipProbe = bytes => bytes.Span.SequenceEqual(legacy),
        };
    }

    private static ManagedFileTransaction CreateTransaction()
    {
        return ManagedArtifactStateRootOverride is { } stateRoot
            ? new ManagedFileTransaction(stateRoot)
            : new ManagedFileTransaction();
    }

    private static string ResolveExecutablePath()
    {
        return Process.GetCurrentProcess().MainModule?.FileName
               ?? throw new InvalidOperationException("Cannot determine executable path.");
    }

    private static string ResolveIconPath()
    {
        // Prefer an absolute path to the bundled PNG so the entry works even
        // when no icon theme on the system defines "typewhisper". Falls back
        // to the theme name if the PNG is missing for any reason.
        var iconPath = Path.Join(AppContext.BaseDirectory, "Resources", "typewhisper-128.png");
        if (!File.Exists(iconPath))
        {
            iconPath = Path.Join(AppContext.BaseDirectory, "Resources", "typewhisper-64.png");
        }

        return File.Exists(iconPath) ? iconPath : "typewhisper";
    }

    private static StartupOperationResult SuccessResult(bool isEnabled)
    {
        return new StartupOperationResult(
            true,
            isEnabled,
            Loc.Instance["General.AutostartHint"]
        );
    }

    private static StartupOperationResult RefusedResult()
    {
        return new StartupOperationResult(
            false,
            false,
            Loc.Instance.GetString("General.AutostartEntryPreserved", DesktopFilePath)
        );
    }
}
