using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Linux.Services;
using TypeWhisper.Linux.Services.Hotkey.DeSetup;
using TypeWhisper.Linux.Services.Insertion;

namespace TypeWhisper.Linux.ViewModels.Sections;

// Façade over LinuxCapabilitySnapshot + YdotoolSetupHelper so the panel
// can bind status strings and run the one-click setup.
public partial class TextInsertionSectionViewModel : ObservableObject
{
    private readonly SystemCommandAvailabilityService _commands;
    private readonly YdotoolSetupHelper _setup;

    [ObservableProperty]
    private string _integrationStatusMessage = "";

    [ObservableProperty]
    private string _statusMessage = "";

    public TextInsertionSectionViewModel(
        SystemCommandAvailabilityService commands,
        YdotoolSetupHelper setup
    )
    {
        _commands = commands;
        _setup = setup;
    }

    private LinuxCapabilitySnapshot Snapshot => _commands.GetSnapshot();
    private YdotoolSetupHelper.Status YdotoolStatus => _setup.IsCurrentlyConfigured();

    public string SessionType => Snapshot.SessionType;
    public string CompositorDisplayName => DesktopDetector.DisplayName();
    public string ClipboardToolStatus => Snapshot.ClipboardStatus;

    public bool CompositorRejectsWtype => Snapshot.CompositorRejectsWtype;

    // Hidden on X11, and hidden once fully configured with nothing left to act on.
    public bool ShowYdotoolSetup =>
        Snapshot.SessionType == "Wayland"
        && (ShowManualInstructions || CanSetUpAutomatically || CanRemoveIntegration);

    public string XdotoolStatusText
    {
        get
        {
            if (!Snapshot.HasXdotool)
            {
                return "Not installed.";
            }

            return Snapshot.SessionType == "Wayland"
                ? "Installed — XWayland windows only."
                : "Installed.";
        }
    }

    public string XdotoolStatusTone =>
        Snapshot.HasXdotool ? Snapshot.SessionType == "Wayland" ? "warn" : "ok" : "missing";

    public string WtypeStatusText
    {
        get
        {
            if (!Snapshot.HasWtype)
            {
                return "Not installed.";
            }

            if (Snapshot.SessionType != "Wayland")
            {
                return "Installed (Wayland only).";
            }

            return Snapshot.CompositorRejectsWtype
                ? "Installed, but this compositor doesn't support wtype's virtual-keyboard protocol."
                : "Installed.";
        }
    }

    public string WtypeStatusTone =>
        !Snapshot.HasWtype ? "missing"
        : Snapshot.CompositorRejectsWtype || Snapshot.SessionType != "Wayland" ? "warn"
        : "ok";

    public string YdotoolStatusText
    {
        get
        {
            var status = YdotoolStatus;
            if (!status.BinaryInstalled)
            {
                return "ydotool not installed. Install it via your package manager.";
            }

            // Only flag the missing udev rule when /dev/uinput isn't already
            // accessible — if the kernel grants it directly the rule isn't needed.
            if (!status.UdevRulePresent && !status.UinputAccessible)
            {
                return "ydotool installed, but the /dev/uinput udev rule is missing.";
            }

            if (!status.SystemdUnitActive)
            {
                return "ydotoold systemd user unit is not active.";
            }

            if (!status.SocketReachable)
            {
                return "ydotoold is active but its socket isn't reachable.";
            }

            // Probe failed: daemon is up but /dev/uinput is unwritable (usually EACCES).
            // Showing "Ready" here would contradict the setup-result popup.
            if (!status.ProbeSucceeded)
            {
                return
                    "ydotoold socket reachable, but a test keystroke failed. Check that your user has read/write access to /dev/uinput (run `groups` — you should see `input`; if not, `sudo usermod -aG input $USER` then log out and back in).";
            }

            return $"Ready. Socket: {status.SocketPath}";
        }
    }

    public string YdotoolStatusTone => YdotoolStatus.IsFullyConfigured ? "ok" : "missing";

    public string SetupPreview => _setup.PreviewLines();

    public bool CanSetUpAutomatically =>
        YdotoolStatus.BinaryInstalled && !YdotoolStatus.IsFullyConfigured;

    public bool ShowManualInstructions => !YdotoolStatus.BinaryInstalled;

    // Kept as a separate block so Remove stays reachable after a successful
    // setup (when CanSetUpAutomatically is false the setup panel would vanish).
    public bool CanRemoveIntegration =>
        YdotoolStatus.UdevRulePresent || YdotoolStatus.SystemdUnitActive;

    public string ManualInstallCommand =>
        "Fedora:        sudo dnf install ydotool\n"
        + "Debian/Ubuntu: sudo apt install ydotool\n"
        + "Arch:          sudo pacman -S ydotool";

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

    // LinuxCapabilitySnapshot is a value type rebuilt out-of-band and can't
    // auto-notify, so raise PropertyChanged manually after mutating actions.
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