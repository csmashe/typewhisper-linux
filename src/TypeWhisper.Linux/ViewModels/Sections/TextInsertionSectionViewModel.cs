using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.Insertion;

namespace TypeWhisper.Linux.ViewModels.Sections;

/// <summary>
/// Backs the "Text insertion" settings panel. The panel mirrors the
/// Shortcuts panel shape: per-backend status rows + a one-click
/// "Set up automatically" flow, but for the ydotool stack instead of
/// a DE keyboard binding. The interesting state lives on
/// <see cref="LinuxCapabilitySnapshot"/> already; this VM is largely
/// a façade so the UI can bind status strings and run the helper.
/// </summary>
public partial class TextInsertionSectionViewModel : ObservableObject
{
    private readonly SystemCommandAvailabilityService _commands;
    private readonly YdotoolSetupHelper _setup;

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _integrationStatusMessage = "";

    public TextInsertionSectionViewModel(SystemCommandAvailabilityService commands, YdotoolSetupHelper setup)
    {
        _commands = commands;
        _setup = setup;
    }

    private LinuxCapabilitySnapshot Snapshot => _commands.GetSnapshot();
    private YdotoolSetupHelper.Status YdotoolStatus => _setup.IsCurrentlyConfigured();

    public string SessionType => Snapshot.SessionType;
    public string CompositorDisplayName => DesktopDetector.DisplayName();
    public string ClipboardToolStatus => Snapshot.ClipboardStatus;

    /// <summary>True when the running compositor is known to reject wtype
    /// (GNOME / KDE Wayland). Drives the wtype row's warning tone and the
    /// "ydotool recommended" framing.</summary>
    public bool CompositorRejectsWtype => Snapshot.CompositorRejectsWtype;

    /// <summary>True when ydotool is a sensible thing to surface: we're
    /// on Wayland AND there's something the user can actually do here
    /// (install manually, run the one-click setup, or revert a setup we
    /// previously installed). Hides the whole section on X11 and also
    /// hides it once ydotool is fully configured and we don't own any
    /// integration — at that point the page would be all-status,
    /// no-action, which is just noise.</summary>
    public bool ShowYdotoolSetup => Snapshot.SessionType == "Wayland"
        && (ShowManualInstructions || CanSetUpAutomatically || CanRemoveIntegration);

    public string XdotoolStatusText
    {
        get
        {
            if (!Snapshot.HasXdotool) return "Not installed.";
            return Snapshot.SessionType == "Wayland"
                ? "Installed — XWayland windows only."
                : "Installed.";
        }
    }
    public string XdotoolStatusTone => Snapshot.HasXdotool
        ? (Snapshot.SessionType == "Wayland" ? "warn" : "ok")
        : "missing";

    public string WtypeStatusText
    {
        get
        {
            if (!Snapshot.HasWtype) return "Not installed.";
            if (Snapshot.SessionType != "Wayland") return "Installed (Wayland only).";
            return Snapshot.CompositorRejectsWtype
                ? "Installed, but this compositor doesn't support wtype's virtual-keyboard protocol."
                : "Installed.";
        }
    }
    public string WtypeStatusTone => !Snapshot.HasWtype
        ? "missing"
        : Snapshot.CompositorRejectsWtype || Snapshot.SessionType != "Wayland"
            ? "warn"
            : "ok";

    public string YdotoolStatusText
    {
        get
        {
            var status = YdotoolStatus;
            if (!status.BinaryInstalled)
                return "ydotool not installed. Install it via your package manager.";
            // Only flag the missing rule when /dev/uinput isn't already
            // writable — when the kernel grants access directly the rule
            // genuinely isn't needed and the warning would be wrong.
            if (!status.UdevRulePresent && !status.UinputAccessible)
                return "ydotool installed, but the /dev/uinput udev rule is missing.";
            if (!status.SystemdUnitActive)
                return "ydotoold systemd user unit is not active.";
            if (!status.SocketReachable)
                return "ydotoold is active but its socket isn't reachable.";
            // Probe-only failure means the daemon is up but can't write
            // /dev/uinput — usually EACCES. Don't show "Ready" in this
            // state; it directly contradicts the setup-result popup.
            if (!status.ProbeSucceeded)
                return "ydotoold socket reachable, but a test keystroke failed. Check that your user has read/write access to /dev/uinput (run `groups` — you should see `input`; if not, `sudo usermod -aG input $USER` then log out and back in).";
            return $"Ready. Socket: {status.SocketPath}";
        }
    }
    public string YdotoolStatusTone => YdotoolStatus.IsFullyConfigured ? "ok" : "missing";

    public string SetupPreview => _setup.PreviewLines();

    public bool CanSetUpAutomatically => YdotoolStatus.BinaryInstalled && !YdotoolStatus.IsFullyConfigured;

    public bool ShowManualInstructions => !YdotoolStatus.BinaryInstalled;

    /// <summary>True whenever there is anything to revert: either the
    /// udev rule is on disk or the systemd user unit is active. Drives
    /// a separate "Remove integration" block in the view so the button
    /// stays usable after a successful setup — otherwise
    /// <see cref="CanSetUpAutomatically"/> flips false and the entire
    /// set-up panel (including Remove) disappears.</summary>
    public bool CanRemoveIntegration =>
        YdotoolStatus.UdevRulePresent || YdotoolStatus.SystemdUnitActive;

    /// <summary>Distro-specific install hint. We default to dnf / apt since
    /// those cover the majority of users; advanced distros (Arch, Alpine)
    /// will recognise the command shape regardless.</summary>
    public string ManualInstallCommand =>
        "Fedora:        sudo dnf install ydotool\n" +
        "Debian/Ubuntu: sudo apt install ydotool\n" +
        "Arch:          sudo pacman -S ydotool";

    [RelayCommand]
    private async Task SetUpYdotoolAsync()
    {
        IntegrationStatusMessage = "Setting up ydotool… (you may be asked for your admin password)";
        try
        {
            var result = await _setup.SetUpAsync(CancellationToken.None).ConfigureAwait(true);
            IntegrationStatusMessage = string.IsNullOrWhiteSpace(result.Detail)
                ? result.Message
                : $"{result.Message}\n{result.Detail}";
        }
        catch (Exception ex)
        {
            IntegrationStatusMessage = $"Setup failed: {ex.Message}";
        }
        finally
        {
            RefreshDerivedProperties();
        }
    }

    [RelayCommand]
    private async Task RemoveYdotoolAsync()
    {
        IntegrationStatusMessage = "Removing ydotool integration…";
        try
        {
            var result = await _setup.RemoveAsync(CancellationToken.None).ConfigureAwait(true);
            IntegrationStatusMessage = string.IsNullOrWhiteSpace(result.Detail)
                ? result.Message
                : $"{result.Message}\n{result.Detail}";
        }
        catch (Exception ex)
        {
            IntegrationStatusMessage = $"Removal failed: {ex.Message}";
        }
        finally
        {
            RefreshDerivedProperties();
        }
    }

    [RelayCommand]
    private void RefreshStatus()
    {
        _commands.RefreshSnapshot();
        RefreshDerivedProperties();
        StatusMessage = "Status refreshed.";
    }

    /// <summary>
    /// Snapshot-derived properties don't auto-notify because the
    /// underlying <see cref="LinuxCapabilitySnapshot"/> is a value
    /// type rebuilt out-of-band. After any action that could change
    /// the snapshot, raise PropertyChanged for everything the view
    /// binds to so the rows repaint without an app restart.
    /// </summary>
    private void RefreshDerivedProperties()
    {
        OnPropertyChanged(nameof(SessionType));
        OnPropertyChanged(nameof(CompositorDisplayName));
        OnPropertyChanged(nameof(ClipboardToolStatus));
        OnPropertyChanged(nameof(CompositorRejectsWtype));
        OnPropertyChanged(nameof(ShowYdotoolSetup));
        OnPropertyChanged(nameof(XdotoolStatusText));
        OnPropertyChanged(nameof(XdotoolStatusTone));
        OnPropertyChanged(nameof(WtypeStatusText));
        OnPropertyChanged(nameof(WtypeStatusTone));
        OnPropertyChanged(nameof(YdotoolStatusText));
        OnPropertyChanged(nameof(YdotoolStatusTone));
        OnPropertyChanged(nameof(CanSetUpAutomatically));
        OnPropertyChanged(nameof(CanRemoveIntegration));
        OnPropertyChanged(nameof(ShowManualInstructions));
        OnPropertyChanged(nameof(SetupPreview));
    }
}
